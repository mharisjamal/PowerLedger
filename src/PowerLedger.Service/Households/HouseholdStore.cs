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
    internal const string HistoryKey = "household.history-posted";
    internal const string ConfirmedKey = "household.relay-confirmed";
    internal const string MembersCheckedKey = "household.members-checked";
    internal const string PendingKey = "household.pending";
    internal const string ProblemKey = "household.problem";
    internal const string WaitingKey = "household.waiting-since";
    internal const string RecoveryKeyKey = "household.recovery-key";
    internal const string AskedToJoinKey = "household.asked-to-join";
    internal const string AccountKey = "household.account";
    internal const string PostedHourKey = "household.posted-hour";
    internal const string HistoryHourKey = "household.history-hour";
    internal const string TombstonesKey = "household.tombstones";
    internal const string RotationKeyKey = "household.rotation-key";
    internal const string RecoveryCodeKey = "household.recovery-code";

    /// <summary>What belongs to the household, not to this PC: forgotten on leaving, and before entering another. What the
    /// server still has to be told stays: it names its household.</summary>
    private static readonly string[] OfTheHousehold =
        [
            IdKey, EpochKey, KeysKey, CursorKey, SequenceKey, PostedThroughKey, PostedHourKey, HistoryKey, HistoryHourKey, ConfirmedKey,
            MembersCheckedKey, ProblemKey, WaitingKey, TombstonesKey, RotationKeyKey,
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
    /// PC never moves to an epoch the server doesn't have. Null while there is none.</summary>
    public (int Epoch, byte[] Key)? RotationKey
    {
        get => Unprotect(settings.Get(RotationKeyKey)) is { Length: 4 + HouseholdCrypto.KeyLength } kept
            ? (BinaryPrimitives.ReadInt32BigEndian(kept), kept[4..])
            : null;
        set
        {
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

    /// <summary>The hour the post of this PC's year for new members has reached, when it stopped part way through; null otherwise.</summary>
    public long? HistoryHour
    {
        get => long.TryParse(settings.Get(HistoryHourKey), NumberStyles.None, CultureInfo.InvariantCulture, out var hour) ? hour : null;
        set => WriteText(HistoryHourKey, value?.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The members this PC has posted its year of rows for, or that were already members when it joined.</summary>
    public IReadOnlyList<string> HistoryPosted
    {
        get => HouseholdJson.Read(settings.Get(HistoryKey), HouseholdJson.Default.ListString) ?? [];
        set => settings.Set(HistoryKey, HouseholdJson.Write([.. value], HouseholdJson.Default.ListString));
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

    /// <summary>The PCs removed from the household that this PC knows of, by device ID (plan 0.8): kept when their rows go, so
    /// none is taken back on the word of a PC that hasn't heard.</summary>
    public IReadOnlyDictionary<string, Tombstone> Tombstones
    {
        get => HouseholdJson.Read(settings.Get(TombstonesKey), HouseholdJson.Default.DictionaryStringTombstone) ?? [];
        set => WriteText(TombstonesKey, value.Count == 0 ? null : HouseholdJson.Write(new Dictionary<string, Tombstone>(value), HouseholdJson.Default.DictionaryStringTombstone));
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
