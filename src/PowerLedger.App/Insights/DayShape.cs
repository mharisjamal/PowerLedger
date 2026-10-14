using PowerLedger.Core;

namespace PowerLedger.App;

/// <summary>
/// How this PC's day runs (report finding 19): the share of a whole day's energy recent days had used by a time of day,
/// from their hour rows. "By now" then means by now on this PC: a PC used in the evening has used little of its day at
/// noon, where an even spread through the day would expect half, and read today as far over and its rest as half done.
/// The Dashboard's Today trend and the Bill forecast's rest of today both follow it.
/// </summary>
internal static class DayShape
{
    /// <summary>How many whole days before today the shape is taken from: recent enough to follow a change of habit.</summary>
    public const int Days = 28;

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <summary>The share, 0 to 1, of their energy the <see cref="Days"/> whole days before <paramref name="today"/> had
    /// used by <paramref name="timeOfDay"/> on the local clock, an hour under way counting for its part gone; null when
    /// they used nothing, so there is no shape to follow.</summary>
    public static double? ShareBy(IReadOnlyList<HourUse> hours, DateOnly today, TimeSpan timeOfDay)
    {
        var since = today.AddDays(-Days);
        double before = 0, all = 0;
        foreach (var hour in hours)
        {
            var day = DateOnly.FromDateTime(hour.Local);
            if (day < since || day >= today || !(hour.Kwh > 0)) continue;
            all += hour.Kwh;
            before += hour.Kwh * Math.Clamp((timeOfDay - hour.Local.TimeOfDay) / Hour, 0, 1);
        }
        return all > 0 ? before / all : null;
    }

    /// <summary>The same from a history's hour series, at <paramref name="now"/> in <paramref name="zone"/>.</summary>
    public static double? ShareBy(IReadOnlyList<Aggregate> series, DateTimeOffset now, TimeZoneInfo zone)
        => ShareBy([.. series.Select(row => HourUse.From(row, zone))], Ranges.LocalDay(now, zone), TimeZoneInfo.ConvertTime(now, zone).TimeOfDay);
}
