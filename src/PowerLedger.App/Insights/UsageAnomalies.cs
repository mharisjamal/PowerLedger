namespace PowerLedger.App;

/// <summary>
/// Unusual use's maths (Aero look design §4). What is normal depends on when: a PC busy every weekday afternoon is not
/// unusual for being busy on Tuesday afternoon. So each hour is held against the same hour on the same weekday over the
/// last 8 weeks, by the median and the median absolute deviation, which one earlier spike cannot drag up as a mean and
/// standard deviation would. An hour is flagged only when it is far out (over the median by 3.5 deviations, scaled by
/// 1.4826 to match a standard deviation on normal noise), matters (over it by more than 30 Wh, so a PC that idles at 5 W
/// is not flagged for 20 W), and the normal rests on at least 4 other hours. The last week's hours are checked, and at
/// most 3 a day are kept, the largest first, so the bell never floods.
/// </summary>
internal static class UsageAnomalies
{
    public const int Weeks = 8;

    public const int DaysChecked = 7;

    public const double Deviations = 3.5;

    /// <summary>The MAD of normal noise times this is its standard deviation.</summary>
    public const double Consistency = 1.4826;

    public const double MinExcessKwh = 0.030;

    public const int MinSamples = 4;

    public const int PerDay = 3;

    /// <summary>The flagged hours of the last <see cref="DaysChecked"/> days, newest first, at most <see cref="PerDay"/> a
    /// local day. The hour under way is never judged: it is not over.</summary>
    public static IReadOnlyList<UsageAnomaly> Find(IReadOnlyList<HourUse> hours, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = Ranges.LocalDay(now, zone);
        var since = Ranges.Midnight(today.AddDays(-7 * Weeks), zone);
        var checkFrom = Ranges.Midnight(today.AddDays(1 - DaysChecked), zone);
        var seen = hours.Where(h => h.Seen && h.Start >= since && h.Start + TimeSpan.FromHours(1) <= now).ToList();
        var byKey = seen.ToLookup(h => (h.Local.DayOfWeek, h.Local.Hour));

        var found = new List<UsageAnomaly>();
        foreach (var hour in seen.Where(h => h.Start >= checkFrom))
        {
            var others = byKey[(hour.Local.DayOfWeek, hour.Local.Hour)].Where(o => o.Start != hour.Start).Select(o => o.Kwh).ToList();
            if (others.Count < MinSamples) continue;
            var median = Stats.Median(others);
            var mad = Stats.Median(others.Select(k => Math.Abs(k - median)).ToList());
            if (hour.Kwh > median + Deviations * Consistency * mad && hour.Kwh - median > MinExcessKwh)
                found.Add(new UsageAnomaly(TimeZoneInfo.ConvertTime(hour.Start, zone), hour.Kwh, median, median > 0 ? hour.Kwh / median : double.PositiveInfinity));
        }

        return [.. found
            .GroupBy(a => a.Hour.Date)
            .SelectMany(day => day.OrderByDescending(a => a.Kwh - a.NormalKwh).Take(PerDay))
            .OrderByDescending(a => a.Hour)];
    }

    /// <summary>The bell's alerts (design §4): today's flagged hours, newest first, at most <see cref="PerDay"/>.</summary>
    public static IReadOnlyList<UsageAnomaly> Today(IReadOnlyList<UsageAnomaly> anomalies, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = Ranges.LocalDay(now, zone);
        return [.. anomalies.Where(a => Ranges.LocalDay(a.Hour, zone) == today).OrderByDescending(a => a.Hour).Take(PerDay)];
    }
}
