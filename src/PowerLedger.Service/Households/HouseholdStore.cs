using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PowerLedger.Core.Households;
using PowerLedger.Storage;

namespace PowerLedger.Service.Households;

/// <summary>
/// The household's state in the service's settings table (households design §1): this PC's keys, the household it is in and
/// that household's key for each epoch, whether it can be found, the random name it is announced under, its name in the
/// household, how far relay sync has got, and N2's session. Every local user can read the database, so the private keys,
/// the household keys and the session are kept encrypted with DPAPI for the account the service runs as, as sharing's
/// install key is. Anything kept that this account can't decrypt, as in a database from another PC, counts as never kept.
/// </summary>
/// <param name="machineName">This PC's Windows name, the name it has in the household until renamed; null for the real one.</param>
internal sealed class HouseholdStore(SettingsRepository settings, Func<string>? machineName = null)
{
    internal const string SignKey = "household.device-sign";
    internal const string DhKey = "household.device-dh";
    internal const string IdKey = "household.id";
    internal const string EpochKey = "household.epoch";
    internal const string KeysKey = "household.keys";
    internal const string DiscoverableKey = "household.discoverable";
    internal const string InstanceKey = "household.instance";
    internal const string NameKey = "household.name";
    internal const string CursorKey = "household.relay-cursor";
    internal const string SequenceKey = "household.next-seq";
    internal const string SessionKey = "household.session";
    internal const string PostedThroughKey = "household.posted-through";
    internal const string ConfirmedKey = "household.relay-confirmed";
    internal const string MembersCheckedKey = "household.members-checked";
    internal const string PendingKey = "household.pending";
    internal const string ProblemKey = "household.problem";
    internal const string WaitingKey = "household.waiting-since";
    internal const string RecoveryKeyKey = "household.recovery-key";
    internal const string AskedToJoinKey = "household.asked-to-join";
    internal const string AccountKey = "household.account";
    internal const string PostedHourKey = "household.posted-hour";
    internal const string SnapshotEpochKey = "household.snapshot-epoch";
    internal const string SnapshotAtKey = "household.snapshot-at";
    internal const string SnapshotWantedKey = "household.snapshot-wanted";
    internal const string SnapshotFromKey = "household.snapshot-from";
    internal const string ServerMembersKey = "household.server-members";
    internal const string RemovalClaimsKey = "household.removal-claims";
    internal const string RotationKeyKey = "household.rotation-key";
    internal const string RotationPostKey = "household.rotation-post";
    internal const string RecoveryCodeKey = "household.recovery-code";
    internal const string LaggingKey = "household.lagging";
    internal const string ApprovingKey = "household.approving";
    internal const string AnsweringKey = "household.answering";
    internal const string CanAskAgainKey = "household.can-ask-again";
    internal const string ApprovalsStartedKey = "household.approvals-started";
    internal const string RecoveringKey = "household.recovering";

    /// <summary>What belongs to the household, not to this PC: forgotten on leaving, and before entering another. What the
    /// server still has to be told stays: it names its household.</summary>
    private static readonly string[] OfTheHousehold =
        [
            IdKey, EpochKey, KeysKey, CursorKey, SequenceKey, PostedThroughKey, PostedHourKey, SnapshotEpochKey, SnapshotAtKey, SnapshotWantedKey,
            SnapshotFromKey, ConfirmedKey, MembersCheckedKey, ProblemKey, WaitingKey, ServerMembersKey, RemovalClaimsKey, RotationKeyKey, RotationPostKey, LaggingKey,
            ApprovingKey,
        ];

    /// <summary>Mixed into every encryption, so no other program running as the same account reads them back by chance.</summary>
    private static readonly byte[] Entropy = "PowerLedger household keys"u8.ToArray();

    private readonly Func<string> _machineName = machineName ?? (() => Environment.MachineName);

    /// <summary>This PC's keys (households design §1), made the first time they are asked for and kept. Keys this account
    /// can't read are made anew; the household knew this PC by the old ones, so it is forgotten, and so is the session.
    /// The caller disposes them.</summary>
    public DeviceKeys DeviceKeys()
    {
        var sign = Unprotect(settings.Get(SignKey));
        var dh = Unprotect(settings.Get(DhKey));
        if (sign is not null && dh is not null)
        {
            try
            {
                return Core.Households.DeviceKeys.FromPrivate(sign, dh);
            }
            catch (CryptographicException)
            {
            }
        }
        if (settings.Get(SignKey) is not null || settings.Get(DhKey) is not null)
        {
            LeaveHousehold();
            Session = null;
            AskedToJoin = null;
        }
        var made = Core.Households.DeviceKeys.Create();
        var (newSign, newDh) = made.ExportPrivate();
        settings.Set(DhKey, Protect(newDh));
        settings.Set(SignKey, Protect(newSign));
        return made;
    }

    /// <summary>This PC's device ID, making its keys if there are none yet.</summary>
    public string DeviceId
    {
        get
        {
            using var keys = DeviceKeys();
            return keys.DeviceId;
        }
    }

    /// <summary>The household this PC is in; null while in none.</summary>
    public string? HouseholdId => settings.Get(IdKey);

    /// <summary>The current epoch: the household key's, going up each time it is replaced; 0 while in no household.</summary>
    public int Epoch => int.TryParse(settings.Get(EpochKey), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) ? epoch : 0;

    /// <summary>The household key for the current epoch, or null.</summary>
    public byte[]? CurrentKey => KeyFor(Epoch);

    /// <summary>The household key for an epoch, kept to read what was sent under it; null when this PC never had it.</summary>
    public byte[]? KeyFor(int epoch) =>
        Keys().TryGetValue(epoch.ToString(CultureInfo.InvariantCulture), out var key) ? Convert.FromBase64String(key) : null;

    /// <summary>Every epoch this PC holds the key of, oldest first.</summary>
    public IReadOnlyList<int> Epochs => [.. Keys().Keys.Select(epoch => int.Parse(epoch, CultureInfo.InvariantCulture)).Order()];

    /// <summary>Enters a household with its key, forgetting any other: relay sync starts from its beginning.</summary>
    public void EnterHousehold(string householdId, int epoch, byte[] key)
    {
        LeaveHousehold();
        WriteKeys(new Dictionary<string, string> { [epoch.ToString(CultureInfo.InvariantCulture)] = Convert.ToBase64String(key) });
        settings.Set(EpochKey, epoch.ToString(CultureInfo.InvariantCulture));
        settings.Set(IdKey, householdId);
    }

    /// <summary>Keeps the key of another epoch; the current epoch becomes the newest this PC holds. A key already kept for the
    /// epoch stays, unless <paramref name="replace"/>: another member's rotation to the same epoch reached the server first.</summary>
    public void AddKey(int epoch, byte[] key, bool replace = false)
    {
        var keys = Keys();
        var name = epoch.ToString(CultureInfo.InvariantCulture);
        if (replace) keys[name] = Convert.ToBase64String(key);
        else keys.TryAdd(name, Convert.ToBase64String(key));
        WriteKeys(keys);
        if (epoch > Epoch) settings.Set(EpochKey, epoch.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>A key this PC made for the next epoch, kept until the server takes it (plan 0.8): it isn't used before, so this
    /// PC never moves to an epoch the server doesn't have. Null while there is none. Setting it forgets the envelopes sealed
    /// for the one before (<see cref="RotationPost"/>).</summary>
    public (int Epoch, byte[] Key)? RotationKey
    {
        get => Unprotect(settings.Get(RotationKeyKey)) is { Length: 4 + HouseholdCrypto.KeyLength } kept
            ? (BinaryPrimitives.ReadInt32BigEndian(kept), kept[4..])
            : null;
        set
        {
            settings.Remove(RotationPostKey);
            if (value is not { } pending)
            {
                settings.Remove(RotationKeyKey);
                return;
            }
            var kept = new byte[4 + pending.Key.Length];
            BinaryPrimitives.WriteInt32BigEndian(kept, pending.Epoch);
            pending.Key.CopyTo(kept, 4);
            settings.Set(RotationKeyKey, Protect(kept));
        }
    }

    /// <summary>The envelopes the waiting new key was sealed in, as last posted (plan 0.9): posted again as they are, so a
    /// retry after an answer that was lost is the very same request, which the server takes as done. Null before the first
    /// post.</summary>
    public Relay.PostKeysBody? RotationPost
    {
        get => HouseholdJson.Read(settings.Get(RotationPostKey), HouseholdJson.Default.PostKeysBody);
        set => WriteText(RotationPostKey, value is null ? null : HouseholdJson.Write(value, HouseholdJson.Default.PostKeysBody));
    }

    /// <summary>Forgets the household: its ID, its keys and the progress of relay sync. This PC's own keys stay.</summary>
    public void LeaveHousehold()
    {
        foreach (var key in OfTheHousehold) settings.Remove(key);
    }

    /// <summary>Whether other PCs on a Private network can find this one; on until the user turns it off.</summary>
    public bool Discoverable
    {
        get => settings.Get(DiscoverableKey) != "0";
        set => settings.Set(DiscoverableKey, value ? "1" : "0");
    }

    /// <summary>The random name this PC is announced under on the network, made once: never its Windows name.</summary>
    public string InstanceId
    {
        get
        {
            if (settings.Get(InstanceKey) is { Length: 32 } kept) return kept;
            var made = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            settings.Set(InstanceKey, made);
            return made;
        }
    }

    /// <summary>This PC's name in the household: the user's, or its Windows name.</summary>
    public string Name
    {
        get => settings.Get(NameKey) is { Length: > 0 } name ? name : _machineName();
        set => settings.Set(NameKey, value);
    }

    /// <summary>The server's sequence number of the last batch read from the relay.</summary>
    public long RelayCursor
    {
        get => long.TryParse(settings.Get(CursorKey), NumberStyles.None, CultureInfo.InvariantCulture, out var cursor) ? cursor : 0;
        set => settings.Set(CursorKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The sequence number for this PC's next batch, from 1, each used once in the household.</summary>
    public long NextSequence()
    {
        var next = long.TryParse(settings.Get(SequenceKey), NumberStyles.None, CultureInfo.InvariantCulture, out var kept) ? kept : 1;
        settings.Set(SequenceKey, (next + 1).ToString(CultureInfo.InvariantCulture));
        return next;
    }

    /// <summary>This PC's rows that changed up to this time, unix milliseconds, have gone to the server.</summary>
    public long PostedThrough
    {
        get => long.TryParse(settings.Get(PostedThroughKey), NumberStyles.None, CultureInfo.InvariantCulture, out var through) ? through : 0;
        set => settings.Set(PostedThroughKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Where a post of this PC's rows that stopped part way through goes on: after the row for this hour among those
    /// changed at <see cref="PostedThrough"/>; null when every row changed then has gone.</summary>
    public long? PostedHour
    {
        get => long.TryParse(settings.Get(PostedHourKey), NumberStyles.None, CultureInfo.InvariantCulture, out var hour) ? hour : null;
        set => WriteText(PostedHourKey, value?.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The epoch this PC last posted all its rows under (plan 0.9); null before it has in this household.</summary>
    public int? SnapshotEpoch
    {
        get => int.TryParse(settings.Get(SnapshotEpochKey), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) ? epoch : null;
        set => WriteText(SnapshotEpochKey, value?.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>When this PC last finished posting all its rows, unix milliseconds; null before it has in this household.</summary>
    public long? SnapshotAt
    {
        get => long.TryParse(settings.Get(SnapshotAtKey), NumberStyles.None, CultureInfo.InvariantCulture, out var at) ? at : null;
        set => WriteText(SnapshotAtKey, value?.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>True once a member has joined since this PC last posted all its rows, which it then does again.</summary>
    public bool SnapshotWanted
    {
        get => settings.Get(SnapshotWantedKey) == "1";
        set => WriteText(SnapshotWantedKey, value ? "1" : null);
    }

    /// <summary>Where a post of all this PC's rows that stopped part way through goes on: under that epoch, after that hour;
    /// null when none did.</summary>
    public (int Epoch, long Hour)? SnapshotFrom
    {
        get => settings.Get(SnapshotFromKey)?.Split(':') is [var epoch, var hour]
            && int.TryParse(epoch, NumberStyles.None, CultureInfo.InvariantCulture, out var e) && long.TryParse(hour, NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            ? (e, h)
            : null;
        set => WriteText(SnapshotFromKey, value is { } from ? string.Create(CultureInfo.InvariantCulture, $"{from.Epoch}:{from.Hour}") : null);
    }

    /// <summary>True once the server has taken a request from this PC as a member of this household: a refusal after that
    /// means this PC was removed, where before it only means it hasn't been added yet.</summary>
    public bool RelayConfirmed
    {
        get => settings.Get(ConfirmedKey) == "1";
        set => WriteText(ConfirmedKey, value ? "1" : null);
    }

    /// <summary>When the server's member list was last read, unix milliseconds.</summary>
    public long? MembersCheckedAt
    {
        get => long.TryParse(settings.Get(MembersCheckedKey), NumberStyles.None, CultureInfo.InvariantCulture, out var at) ? at : null;
        set => WriteText(MembersCheckedKey, value?.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The server's member list as this PC last read it, by device ID (plan 0.10), which says who is in and gives the
    /// epochs; null before this PC has read it for its household.</summary>
    public IReadOnlyDictionary<string, ServerEntry>? ServerMembers
    {
        get => HouseholdJson.Read(settings.Get(ServerMembersKey), HouseholdJson.Default.DictionaryStringServerEntry);
        set => WriteText(ServerMembersKey, value is null ? null : HouseholdJson.Write(new Dictionary<string, ServerEntry>(value), HouseholdJson.Default.DictionaryStringServerEntry));
    }

    /// <summary>The removals in the members' latest lists, by the PC whose list it is, <see cref="MemberBook.Own"/> for this
    /// PC's own: each removed PC's device ID with the epoch it was removed at (plan 0.10).</summary>
    public IReadOnlyDictionary<string, Dictionary<string, int>> RemovalClaims
    {
        get => HouseholdJson.Read(settings.Get(RemovalClaimsKey), HouseholdJson.Default.DictionaryStringDictionaryStringInt32) ?? [];
        set => WriteText(RemovalClaimsKey, value.Count == 0 ? null
            : HouseholdJson.Write(new Dictionary<string, Dictionary<string, int>>(value), HouseholdJson.Default.DictionaryStringDictionaryStringInt32));
    }

    /// <summary>Current members whose batches still come under an older epoch than this PC's, by device ID: since when, unix
    /// milliseconds, and the epoch this PC made a new key at for them, if it did (plan 0.8).</summary>
    public IReadOnlyDictionary<string, Lag> Lagging
    {
        get => HouseholdJson.Read(settings.Get(LaggingKey), HouseholdJson.Default.DictionaryStringLag) ?? [];
        set => WriteText(LaggingKey, value.Count == 0 ? null : HouseholdJson.Write(new Dictionary<string, Lag>(value), HouseholdJson.Default.DictionaryStringLag));
    }

    /// <summary>What the server still has to be told, oldest first: kept across leaving, since each names its household.</summary>
    public IReadOnlyList<Relay.PendingOp> Pending
    {
        get => HouseholdJson.Read(settings.Get(PendingKey), HouseholdJson.Default.ListPendingOp) ?? [];
        set => WriteText(PendingKey, value.Count == 0 ? null : HouseholdJson.Write([.. value], HouseholdJson.Default.ListPendingOp));
    }

    public void AddPending(Relay.PendingOp op) => Pending = [.. Pending, op];

    /// <summary>Since when relay sync has waited at its cursor for the key of a newer epoch, unix milliseconds; null while it
    /// isn't waiting.</summary>
    public long? WaitingSince
    {
        get => long.TryParse(settings.Get(WaitingKey), NumberStyles.None, CultureInfo.InvariantCulture, out var since) ? since : null;
        set => WriteText(WaitingKey, value?.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The last problem syncing, in words the App can show; null while all goes well.</summary>
    public string? Problem
    {
        get => settings.Get(ProblemKey);
        set => WriteText(ProblemKey, value);
    }

    /// <summary>N2: the key made from the recovery code, kept by the PC that made the code or recovered with it, so a new
    /// household key can be sealed for recovery too; null when this PC has none or it can't be read.</summary>
    /// <summary>N2: the recovery this PC opened and is taking the household back with (plan 0.10), kept encrypted before the
    /// server is asked, so a recover whose answer was lost goes again; null while none is under way.</summary>
    public Recovering? Recovering
    {
        get => Unprotect(settings.Get(RecoveringKey)) is { } kept ? HouseholdJson.Read(kept, HouseholdJson.Default.Recovering) : null;
        set
        {
            if (value is null) settings.Remove(RecoveringKey);
            else settings.Set(RecoveringKey, Protect(HouseholdJson.Bytes(value, HouseholdJson.Default.Recovering)));
        }
    }

    public byte[]? RecoveryKey
    {
        get => Unprotect(settings.Get(RecoveryKeyKey));
        set
        {
            if (value is null) settings.Remove(RecoveryKeyKey);
            else settings.Set(RecoveryKeyKey, Protect(value));
        }
    }

    /// <summary>N2: a recovery code made on this PC that the App hasn't yet said it showed, with the ID of the notice that
    /// shows it (plan 0.8): kept encrypted until then, and forgotten once seen.</summary>
    public (string PromptId, string Code)? RecoveryCodeToShow
    {
        get => Unprotect(settings.Get(RecoveryCodeKey)) is { } kept && Encoding.UTF8.GetString(kept).Split('\n') is [var promptId, var code]
            ? (promptId, code)
            : null;
        set
        {
            if (value is not { } waiting) settings.Remove(RecoveryCodeKey);
            else settings.Set(RecoveryCodeKey, Protect(Encoding.UTF8.GetBytes($"{waiting.PromptId}\n{waiting.Code}")));
        }
    }

    /// <summary>N2: the opaque ID of the account this PC is signed in as; null while signed out.</summary>
    public string? Account
    {
        get => settings.Get(AccountKey);
        set => WriteText(AccountKey, value);
    }

    /// <summary>N2: the household this PC, signed in, has asked to join and waits to be approved into; null otherwise.</summary>
    public string? AskedToJoin
    {
        get => settings.Get(AskedToJoinKey);
        set => WriteText(AskedToJoinKey, value);
    }

    /// <summary>N2: this PC's answer to the member that committed to approving it (plan 0.9); null before one has.</summary>
    public Answering? Answering
    {
        get => HouseholdJson.Read(settings.Get(AnsweringKey), HouseholdJson.Default.Answering);
        set => WriteText(AnsweringKey, value is null ? null : HouseholdJson.Write(value, HouseholdJson.Default.Answering));
    }

    /// <summary>N2: true once this PC's request to join was refused or lapsed: its user may ask again (plan 0.9).</summary>
    public bool CanAskAgain
    {
        get => settings.Get(CanAskAgainKey) == "1";
        set => WriteText(CanAskAgainKey, value ? "1" : null);
    }

    /// <summary>N2: the approval of a waiting PC this PC runs (plan 0.9); null while it runs none.</summary>
    public Approving? Approving
    {
        get => HouseholdJson.Read(settings.Get(ApprovingKey), HouseholdJson.Default.Approving);
        set => WriteText(ApprovingKey, value is null ? null : HouseholdJson.Write(value, HouseholdJson.Default.Approving));
    }

    /// <summary>N2: how many approvals this PC started on the UTC day given, as <c>yyyy-MM-dd:count</c>.</summary>
    public (DateOnly Day, int Count)? ApprovalsStarted
    {
        get => settings.Get(ApprovalsStartedKey)?.Split(':') is [var day, var count]
            && DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            && int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? (d, n)
            : null;
        set => WriteText(ApprovalsStartedKey, value is { } started
            ? string.Create(CultureInfo.InvariantCulture, $"{started.Day:yyyy-MM-dd}:{started.Count}")
            : null);
    }

    /// <summary>N2: the session token the server gave this PC at sign-in; null while signed out.</summary>
    public string? Session
    {
        get => Unprotect(settings.Get(SessionKey)) is { } token ? Encoding.UTF8.GetString(token) : null;
        set
        {
            if (value is null) settings.Remove(SessionKey);
            else settings.Set(SessionKey, Protect(Encoding.UTF8.GetBytes(value)));
        }
    }

    private Dictionary<string, string> Keys()
    {
        if (Unprotect(settings.Get(KeysKey)) is not { } json) return new(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize(json, HouseholdJson.Default.DictionaryStringString) ?? new(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    private void WriteText(string key, string? value)
    {
        if (value is null) settings.Remove(key);
        else settings.Set(key, value);
    }

    private void WriteKeys(Dictionary<string, string> keys) =>
        settings.Set(KeysKey, Protect(JsonSerializer.SerializeToUtf8Bytes(keys, HouseholdJson.Default.DictionaryStringString)));

    private static string Protect(byte[] secret) =>
        Convert.ToBase64String(ProtectedData.Protect(secret, Entropy, DataProtectionScope.CurrentUser));

    private static byte[]? Unprotect(string? kept)
    {
        if (string.IsNullOrEmpty(kept)) return null;
        try
        {
            return ProtectedData.Unprotect(Convert.FromBase64String(kept), Entropy, DataProtectionScope.CurrentUser);
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
