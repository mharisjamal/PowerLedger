using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using PowerLedger.Contracts;
using PowerLedger.Storage;

namespace PowerLedger.Service.Sharing;

/// <summary>The user's answer as the service keeps it.</summary>
/// <param name="AtMs">When it was given.</param>
/// <param name="DiagnosticsSinceMs">When Crash and sensor reports was last turned on, so a crash from before is never
/// recorded; null while it is off.</param>
internal sealed record StoredConsent(Consent Consent, long AtMs, long? DiagnosticsSinceMs = null);

/// <summary>The last upload the server accepted: when, and its size as sent.</summary>
internal sealed record LastSent(long AtMs, long Bytes);

/// <summary>Why the last try failed, in words the App can show; <paramref name="Rejected"/> when the server refused a day
/// rather than not being reached.</summary>
internal sealed record SendProblem(string Text, bool Rejected, bool Lasts = false);

/// <summary>Failed runs in a row, and when the next may start.</summary>
internal sealed record Backoff(int Failures, long NextMs);

/// <summary>
/// Sharing's state in the service's settings table (data-sharing design §4), outside the service settings, so a settings
/// save can never overwrite it. The install key never leaves the service except to the server, and is kept encrypted with
/// DPAPI for the account the service runs as: every local user can read the database, but only LocalSystem can read the key.
/// </summary>
/// <param name="pickMinute">Chooses the send minute, once; by default at random from 10 to 59, 00:10 to 00:59.</param>
internal sealed class SharingStore(SettingsRepository settings, Func<int>? pickMinute = null)
{
    internal const string ConsentKey = "sharing.consent";
    internal const string IdKey = "sharing.id";
    internal const string KeyKey = "sharing.key";
    internal const string MinuteKey = "sharing.minute";
    internal const string CollectedToKey = "sharing.collected-to";
    internal const string LastSentKey = "sharing.last-sent";
    internal const string ProblemKey = "sharing.problem";
    internal const string BackoffKey = "sharing.backoff";
    internal const string HardwareHashKey = "sharing.hardware-hash";
    internal const string HardwareDayKey = "sharing.hardware-day";
    internal const string ConsentPendingKey = "sharing.consent-pending";
    internal const string ConsentBackoffKey = "sharing.consent-backoff";
    internal const string LastRunKey = "sharing.last-run";
    internal const string SentThroughKey = "sharing.sent-through";
    internal const string PreviousIdKey = "sharing.previous-id";
    internal const string LastPartialRunKey = "sharing.last-partial";
    internal const string HistoryUntilKey = "sharing.history-until";
    internal const string HistoryThroughKey = "sharing.history-through";
    internal const string HistoryBackoffKey = "sharing.history-backoff";

    public const int FirstSendMinute = 10;

    /// <summary>The last send minute, 00:59 (Plan Q §1): a complete day goes within the first hour. A minute kept from before,
    /// up to 05:59, is chosen again.</summary>
    public const int LastSendMinute = 59;

    /// <summary>What forgetting removes: everything the server knew this PC by and all progress. The send minute stays, and
    /// so does the <see cref="PreviousId"/>, which only writing in can have the server forget.</summary>
    private static readonly string[] Forgotten =
    [
        IdKey, KeyKey, CollectedToKey, LastSentKey, ProblemKey, BackoffKey, HardwareHashKey, HardwareDayKey, ConsentPendingKey, ConsentBackoffKey,
        LastRunKey, SentThroughKey, LastPartialRunKey, HistoryUntilKey, HistoryThroughKey, HistoryBackoffKey,
    ];

    /// <summary>What a new ID starts without: what was kept of the one before about the server. What this PC collected, and
    /// when it last ran, stay.</summary>
    private static readonly string[] OfTheId =
        [LastSentKey, ProblemKey, BackoffKey, HardwareHashKey, HardwareDayKey, ConsentPendingKey, ConsentBackoffKey];

    /// <summary>Mixed into the key's encryption, so no other program running as the same account reads it back by chance.</summary>
    private static readonly byte[] KeyEntropy = "PowerLedger data sharing install key"u8.ToArray();

    private readonly Func<int> _pickMinute = pickMinute ?? (() => RandomNumberGenerator.GetInt32(FirstSendMinute, LastSendMinute + 1));

    public StoredConsent? StoredConsent => Read(ConsentKey, SharingJson.Default.StoredConsent);

    /// <summary>What the user agreed to, or <see cref="Consent.Unanswered"/> before they answered.</summary>
    public Consent Consent => StoredConsent?.Consent ?? Consent.Unanswered;

    public void SaveConsent(StoredConsent value) => Write(ConsentKey, value, SharingJson.Default.StoredConsent);

    public string? InstallId => settings.Get(IdKey);

    /// <summary>The ID this PC sent under before its key couldn't be read and <see cref="Identity"/> made a new one, or null.
    /// The server keeps what was sent under it, and only writing in, quoting it, can have that deleted.</summary>
    public string? PreviousId => settings.Get(PreviousIdKey);

    /// <summary>The install key, or null when there is none or the one kept can't be decrypted by this account, as when the
    /// database came from another PC; without its key an ID is no use, so the next <see cref="Identity"/> makes both anew.</summary>
    public string? Key => Unprotect(settings.Get(KeyKey));

    /// <summary>The install's ID, a random GUID, and its key, 32 random bytes in base64url: made the first time a switch is
    /// turned on and kept until forgotten. An ID whose key can't be read is replaced: it is kept as the
    /// <see cref="PreviousId"/>, and the new one starts without what was kept about the server for the old.</summary>
    public (string Id, string Key) Identity()
    {
        if (InstallId is { } old)
        {
            if (Key is { } kept) return (old, kept);
            settings.Set(PreviousIdKey, old);
            foreach (var name in OfTheId) settings.Remove(name);
            settings.Remove(IdKey);
        }
        // The ID is written last, so a crash between never leaves one beside a key made for another.
        var id = Guid.NewGuid().ToString("D");
        var key = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        settings.Set(KeyKey, Protect(key));
        settings.Set(IdKey, id);
        return (id, key);
    }

    /// <summary>Minutes after local midnight at which the day's upload goes, chosen once per install.</summary>
    public int SendMinute
    {
        get
        {
            if (int.TryParse(settings.Get(MinuteKey), NumberStyles.None, CultureInfo.InvariantCulture, out var minute)
                && minute is >= FirstSendMinute and <= LastSendMinute)
            {
                return minute;
            }
            minute = Math.Clamp(_pickMinute(), FirstSendMinute, LastSendMinute);
            settings.Set(MinuteKey, minute.ToString(CultureInfo.InvariantCulture));
            return minute;
        }
    }

    /// <summary>Readings before this, UTC milliseconds, are in the outbox; null while Hardware and power is off.</summary>
    public long? CollectedTo
    {
        get => ReadLong(CollectedToKey);
        set => WriteLong(CollectedToKey, value);
    }

    public LastSent? LastSent
    {
        get => Read(LastSentKey, SharingJson.Default.LastSent);
        set => Write(LastSentKey, value, SharingJson.Default.LastSent);
    }

    public SendProblem? Problem
    {
        get => Read(ProblemKey, SharingJson.Default.SendProblem);
        set => Write(ProblemKey, value, SharingJson.Default.SendProblem);
    }

    /// <summary>The uploads' back-off.</summary>
    public Backoff? Backoff
    {
        get => Read(BackoffKey, SharingJson.Default.Backoff);
        set => Write(BackoffKey, value, SharingJson.Default.Backoff);
    }

    /// <summary>The back-off of a consent change the server hasn't heard, apart from the uploads', so neither pushes the
    /// other's next try back.</summary>
    public Backoff? ConsentBackoff
    {
        get => Read(ConsentBackoffKey, SharingJson.Default.Backoff);
        set => Write(ConsentBackoffKey, value, SharingJson.Default.Backoff);
    }

    /// <summary>The hardware section last sent, as <see cref="ReportJson.Hash"/> gives it.</summary>
    public string? HardwareHash
    {
        get => settings.Get(HardwareHashKey);
        set => WriteText(HardwareHashKey, value);
    }

    /// <summary>The day whose accepted upload last carried the hardware section. The server keeps only a day's last upload,
    /// so every later upload of that day carries it too.</summary>
    public string? HardwareDay
    {
        get => settings.Get(HardwareDayKey);
        set => WriteText(HardwareDayKey, value);
    }

    /// <summary>True while a consent change hasn't reached the server.</summary>
    public bool ConsentPending
    {
        get => settings.Get(ConsentPendingKey) == "1";
        set => WriteText(ConsentPendingKey, value ? "1" : null);
    }

    /// <summary>When the last send run started, UTC milliseconds.</summary>
    public long? LastRun
    {
        get => ReadLong(LastRunKey);
        set => WriteLong(LastRunKey, value);
    }

    /// <summary>The newest day sent, or refused by the server: a day at or before it is closed, so nothing more is filed under it.</summary>
    public string? SentThrough
    {
        get => settings.Get(SentThroughKey);
        set => WriteText(SentThroughKey, value);
    }

    /// <summary>When today so far was last tried, UTC milliseconds (Plan Q §1).</summary>
    public long? LastPartialRun
    {
        get => ReadLong(LastPartialRunKey);
        set => WriteLong(LastPartialRunKey, value);
    }

    /// <summary>Where the history ends, UTC milliseconds (Plan Q §2): the start of the hour Hardware and power was turned on in,
    /// under consent version 2. The hours before it go once. Null when there is no history to send.</summary>
    public long? HistoryUntilMs
    {
        get => ReadLong(HistoryUntilKey);
        set => WriteLong(HistoryUntilKey, value);
    }

    /// <summary>The history's progress, UTC milliseconds: the hours before this have gone. Null before the first chunk.</summary>
    public long? HistoryThroughMs
    {
        get => ReadLong(HistoryThroughKey);
        set => WriteLong(HistoryThroughKey, value);
    }

    /// <summary>The history's back-off, apart from the uploads', so an old server without it holds nothing else back.</summary>
    public Backoff? HistoryBackoff
    {
        get => Read(HistoryBackoffKey, SharingJson.Default.Backoff);
        set => Write(HistoryBackoffKey, value, SharingJson.Default.Backoff);
    }

    /// <summary>Forgets the ID, the key and every state about sending, and turns every switch off, as an answer to the
    /// current wording. The send minute and the <see cref="PreviousId"/> stay.</summary>
    public void Forget(long nowMs)
    {
        foreach (var key in Forgotten) settings.Remove(key);
        SaveConsent(new StoredConsent(new Consent(ConsentText.Version, false, false, false, false), nowMs));
    }

    private static string Protect(string key) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), KeyEntropy, DataProtectionScope.CurrentUser));

    private static string? Unprotect(string? kept)
    {
        if (string.IsNullOrEmpty(kept)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(kept), KeyEntropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            return null;
        }
    }

    private T? Read<T>(string key, JsonTypeInfo<T> type) where T : class => SharingJson.Read(settings.Get(key), type);

    private void Write<T>(string key, T? value, JsonTypeInfo<T> type) where T : class =>
        WriteText(key, value is null ? null : SharingJson.Write(value, type));

    private long? ReadLong(string key) =>
        long.TryParse(settings.Get(key), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;

    private void WriteLong(string key, long? value) => WriteText(key, value?.ToString(CultureInfo.InvariantCulture));

    private void WriteText(string key, string? value)
    {
        if (value is null) settings.Remove(key);
        else settings.Set(key, value);
    }
}
