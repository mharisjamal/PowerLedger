using System.Globalization;
using PowerLedger.Core;

namespace PowerLedger.App;

/// <summary>Turns a range's series into what the stacked chart draws.</summary>
internal static class Charts
{
    /// <summary>The chart for a range: its buckets in the unit, the time axis, where now is, and what it shows in words.</summary>
    public static ChartModel Build(DateRange range, IReadOnlyList<Aggregate> series, ChartUnit unit, TimeZoneInfo zone, CultureInfo culture)
    {
        var buckets = Buckets(series, unit);
        double? nowAt = range.To > range.From && range.To < range.Through ? (range.To - range.From) / range.Bucket : null;
        var peak = buckets.Count > 0 ? buckets.Max(b => b.Total) : 0;
        var what = unit == ChartUnit.Watts ? "watts" : "watt-hours per " + Ranges.BucketLength(range.Bucket);
        var description = $"{range.Title}: power by component in {what}, stacked from the rest of the system up to the CPU. "
            + (peak > 0 ? $"Peak {Format.Scale(peak, Geometry.ChartScale(peak, Floor(unit)).Step, culture)} {Symbol(unit)}." : "No readings in this range.");
        return new ChartModel(buckets, range.Capacity, range.Bucket, Ticks(range, zone, culture), nowAt, unit, description);
    }

    /// <summary>Each bucket of a series in the unit. Watts are the bucket's energy over its time on; a bucket with no time on is zero.</summary>
    public static IReadOnlyList<ChartBucket> Buckets(IReadOnlyList<Aggregate> series, ChartUnit unit)
        => [.. series.Select(a =>
        {
            var scale = unit == ChartUnit.WattHours ? 1 : a.OnSeconds > 0 ? 3600 / a.OnSeconds : 0;
            return new ChartBucket(a.CpuWh * scale, a.GpuWh * scale, a.DisplayWh * scale, a.RestWh * scale, a.OnSeconds, a.GapSeconds);
        })];

    /// <summary>The lowest top a chart's scale may have: 20 W, or 1 Wh.</summary>
    public static double Floor(ChartUnit unit) => unit == ChartUnit.Watts ? 20 : 1;

    public static string Symbol(ChartUnit unit) => unit == ChartUnit.Watts ? "W" : "Wh";

    /// <summary>
    /// The time axis. A single day is marked every six hours. Longer ranges mark local midnights: every day up to eight
    /// days, every second day up to sixteen, every week up to eight weeks, every fortnight up to four months, and month
    /// starts beyond that. A range of a few hours, the Dashboard's last hour, is marked at each quarter hour inside it.
    /// </summary>
    public static IReadOnlyList<AxisTick> Ticks(DateRange range, TimeZoneInfo zone, CultureInfo culture)
    {
        var first = Ranges.LocalDay(range.From, zone);
        var last = Ranges.LocalDay(range.Through.AddTicks(-1), zone);
        var days = last.DayNumber - first.DayNumber + 1;
        var ticks = new List<AxisTick>();
        if (range.Through - range.From <= TimeSpan.FromHours(3))
        {
            var quarter = TimeSpan.FromMinutes(15);
            var wall = TimeZoneInfo.ConvertTime(range.From, zone).DateTime;
            for (wall = wall.AddTicks(-(wall.Ticks % quarter.Ticks)) + quarter; Ranges.At(wall, zone) < range.Through; wall += quarter)
                ticks.Add(Tick(range, Ranges.At(wall, zone), wall.ToString("HH:mm", culture)));
            return ticks;
        }
        if (days <= 1)
        {
            for (var hour = 0; hour < 24; hour += 6)
                ticks.Add(Tick(range, Ranges.At(first.ToDateTime(new TimeOnly(hour, 0)), zone), hour.ToString("00", CultureInfo.InvariantCulture) + ":00"));
            return ticks;
        }
        if (days > 120)
        {
            var month = new DateOnly(first.Year, first.Month, 1);
            if (month < first) month = month.AddMonths(1);
            for (; month <= last; month = month.AddMonths(1))
                ticks.Add(Tick(range, Ranges.Midnight(month, zone), month.ToString(month.Month == 1 || ticks.Count == 0 ? "MMM yyyy" : "MMM", culture)));
            return ticks;
        }
        var step = days <= 8 ? 1 : days <= 16 ? 2 : days <= 56 ? 7 : 14;
        for (var day = first; day <= last; day = day.AddDays(step))
            ticks.Add(Tick(range, Ranges.Midnight(day, zone), day.ToString(days <= 8 ? "ddd d" : "d MMM", culture)));
        return ticks;
    }

    private static AxisTick Tick(DateRange range, DateTimeOffset at, string label) => new((at - range.From) / range.Bucket, label);
}
