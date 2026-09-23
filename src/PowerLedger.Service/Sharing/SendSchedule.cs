namespace PowerLedger.Service.Sharing;

/// <summary>
/// When the day's upload goes (data-sharing design §4). Pure. It is due when a complete day waits and either tonight's
/// send minute has passed without a run since, a back-off has passed, or the user asked to send now.
/// </summary>
internal static class SendSchedule
{
    private static readonly int[] BackoffHours = [1, 2, 4, 8, 16, 24];

    /// <summary>When <paramref name="day"/>'s run goes: <paramref name="sendMinute"/> minutes after its midnight, or the first
    /// local time after that when a clock change skips it.</summary>
    public static DateTimeOffset SendTime(DateOnly day, int sendMinute, TimeZoneInfo zone) =>
        LocalDays.At(day, TimeSpan.FromMinutes(sendMinute), zone);

    /// <param name="lastRunMs">When the last run started.</param>
    /// <param name="completeDayWaits">Whether a day before today, fully collected, waits in the outbox.</param>
    public static bool Due(
        DateTimeOffset now, TimeZoneInfo zone, int sendMinute, long? lastRunMs, Backoff? backoff, bool completeDayWaits, bool sendNowAsked = false)
    {
        if (!completeDayWaits) return false;
        if (sendNowAsked) return true;
        var tonight = SendTime(LocalDays.Of(now, zone), sendMinute, zone);
        if (now >= tonight && (lastRunMs is not { } last || last < tonight.ToUnixTimeMilliseconds())) return true;
        return backoff is { } waiting && now.ToUnixTimeMilliseconds() >= waiting.NextMs;
    }

    /// <summary>The back-off after one more failed run: 1, 2, 4, 8, 16, then 24 hours.</summary>
    public static Backoff After(Backoff? backoff, DateTimeOffset now)
    {
        var failures = (backoff?.Failures ?? 0) + 1;
        var wait = TimeSpan.FromHours(BackoffHours[Math.Min(failures, BackoffHours.Length) - 1]);
        return new Backoff(failures, (now + wait).ToUnixTimeMilliseconds());
    }
}
