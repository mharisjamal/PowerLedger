namespace PowerLedger.Service.Sharing;

/// <summary>
/// When the day's upload goes (data-sharing design §4). Pure. It is due when a complete day waits and either tonight's
/// send minute has passed without a run since, a back-off has passed, or the user asked to send now. Today so far goes
/// once an hour besides (Plan Q §1).
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

    /// <summary>The latest time at or before <paramref name="now"/> at which today so far goes (Plan Q §1): the local hour's
    /// start plus <paramref name="sendMinute"/> modulo 60 minutes, so installs spread over the hour.</summary>
    public static DateTimeOffset HourSendTime(DateTimeOffset now, TimeZoneInfo zone, int sendMinute)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var slot = new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, 0, 0, local.Offset).AddMinutes(sendMinute % 60);
        return slot <= now ? slot : slot.AddHours(-1);
    }

    /// <summary>Whether today so far is due: this hour's <see cref="HourSendTime"/> has passed without a partial upload since,
    /// and no back-off holds uploads back.</summary>
    /// <param name="lastPartialMs">When the last partial upload was tried.</param>
    public static bool PartialDue(DateTimeOffset now, TimeZoneInfo zone, int sendMinute, long? lastPartialMs, Backoff? backoff)
    {
        if (backoff is { } waiting && now.ToUnixTimeMilliseconds() < waiting.NextMs) return false;
        return lastPartialMs is not { } last || last < HourSendTime(now, zone, sendMinute).ToUnixTimeMilliseconds();
    }

    /// <summary>The back-off after one more failed run: 1, 2, 4, 8, 16, then 24 hours.</summary>
    public static Backoff After(Backoff? backoff, DateTimeOffset now)
    {
        var failures = (backoff?.Failures ?? 0) + 1;
        var wait = TimeSpan.FromHours(BackoffHours[Math.Min(failures, BackoffHours.Length) - 1]);
        return new Backoff(failures, (now + wait).ToUnixTimeMilliseconds());
    }
}
