using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.Service;

/// <summary>
/// Minute and hour rows (spec §7). A minute is folded from the raw rows in storage, never from the loop's own buffer:
/// UpsertMinute replaces the row, so a restart inside a minute must add to what the earlier run wrote (Plan B handoff).
/// An hour is folded only once its last minute has been, because the report reads minute rows for any hour that has no
/// hour row yet (Plan A handoff). Folding is idempotent: folding a range again rewrites the same rows.
/// </summary>
internal sealed class Rollups(RawSampleRepository raw, AggregateRepository aggregates)
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <summary>Folds every minute in [from, to) that has raw rows, then every hour that overlaps the range and ends by <paramref name="to"/>.</summary>
    public void FoldRange(DateTimeOffset from, DateTimeOffset to, double gapThresholdSeconds)
    {
        from = Floor(from, Minute);
        to = Floor(to, Minute);
        for (var chunk = Floor(from, Hour); chunk < to; chunk += Hour)
        {
            var start = chunk < from ? from : chunk;
            var end = chunk + Hour < to ? chunk + Hour : to;
            foreach (var minute in raw.Read(start, end).GroupBy(r => Floor(r.Timestamp, Minute)))
            {
                aggregates.UpsertMinute(Downsampler.ToMinute(minute.Key, [.. minute], gapThresholdSeconds));
            }
        }
        for (var hour = Floor(from, Hour); hour + Hour <= to; hour += Hour) FoldHour(hour);
    }

    /// <summary>At start: whatever a previous run left unfolded, from the newest minute row (or the raw-retention horizon on a
    /// fresh database) up to the current minute, and any complete hour since the newest hour row.</summary>
    public void CatchUp(DateTimeOffset now, TimeSpan horizon, double gapThresholdSeconds)
    {
        var current = Floor(now, Minute);
        var from = aggregates.LastMinuteStart() is { } last && last > current - horizon ? last : current - horizon;
        FoldRange(from, current, gapThresholdSeconds);
        if (aggregates.LastHourStart() is not { } lastHour) return;
        for (var hour = lastHour; hour < Floor(from, Hour); hour += Hour) FoldHour(hour);
    }

    private void FoldHour(DateTimeOffset hourStart)
    {
        var minutes = aggregates.ReadMinutes(hourStart, hourStart + Hour);
        if (minutes.Count > 0) aggregates.UpsertHour(Downsampler.ToHour(hourStart, minutes));
    }

    /// <summary>The start of the minute or hour containing <paramref name="t"/>, in UTC like every stored row.</summary>
    internal static DateTimeOffset Floor(DateTimeOffset t, TimeSpan unit) => new(t.UtcTicks - t.UtcTicks % unit.Ticks, TimeSpan.Zero);
}
