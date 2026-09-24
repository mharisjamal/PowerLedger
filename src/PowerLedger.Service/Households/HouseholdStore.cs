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

    /// <summary>What belongs to the household, not to this PC: forgotten on leaving, and before entering another. What the
    /// server still has to be told stays: it names its household.</summary>
    private static readonly string[] OfTheHousehold =
        [IdKey, EpochKey, KeysKey, CursorKey, SequenceKey, PostedThroughKey, HistoryKey, ConfirmedKey, MembersCheckedKey, ProblemKey];

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

    /// <summary>What the server still has to be told, oldest first: kept across leaving, since each names its household.</summary>
    public IReadOnlyList<Relay.PendingOp> Pending
    {
        get => HouseholdJson.Read(settings.Get(PendingKey), HouseholdJson.Default.ListPendingOp) ?? [];
        set => WriteText(PendingKey, value.Count == 0 ? null : HouseholdJson.Write([.. value], HouseholdJson.Default.ListPendingOp));
    }

    public void AddPending(Relay.PendingOp op) => Pending = [.. Pending, op];

    /// <summary>The last problem syncing, in words the App can show; null while all goes well.</summary>
    public string? Problem
    {
        get => settings.Get(ProblemKey);
        set => WriteText(ProblemKey, value);
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
