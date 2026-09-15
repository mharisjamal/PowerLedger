using System.Globalization;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>A span of local days to show.</summary>
/// <param name="From">Where reading starts: a local midnight.</param>
/// <param name="To">Where reading stops: now, for a range that includes today.</param>
/// <param name="Through">Where the chart ends: the end of the range's last day, so the rest of today shows empty.</param>
/// <param name="Title">What the screen calls it.</param>
/// <param name="Bucket">The chart bucket that suits its length.</param>
internal sealed record DateRange(DateTimeOffset From, DateTimeOffset To, DateTimeOffset Through, string Title, TimeSpan Bucket)
{
    /// <summary>How many buckets the chart's width holds.</summary>
    public int Capacity => Math.Max(1, (int)Math.Ceiling((Through - From) / Bucket));
}

/// <summary>The ranges the history screens offer, and the local-clock arithmetic behind them.</summary>
internal static class Ranges
{
    public static DateRange Today(DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        var day = LocalDay(now, zone);
        return Build(day, day, now, zone, "Today");
    }

    /// <summary>The last <paramref name="days"/> days, today among them.</summary>
    public static DateRange LastDays(int days, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        var today = LocalDay(now, zone);
        return Build(today.AddDays(1 - days), today, now, zone, $"Last {days.ToString(culture)} days");
    }

    public static DateRange ThisMonth(DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        var today = LocalDay(now, zone);
        return Month(today.Year, today.Month, now, zone, culture);
    }

    public static DateRange LastMonth(DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        var today = LocalDay(now, zone);
        var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        return Month(first.Year, first.Month, now, zone, culture);
    }

    /// <summary>A calendar month, stopping at now while it is under way.</summary>
    public static DateRange Month(int year, int month, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        var first = new DateOnly(year, month, 1);
        return Build(first, first.AddMonths(1).AddDays(-1), now, zone, first.ToString("MMMM yyyy", culture));
    }

    /// <summary>Whole local days from one date to another, in either order, stopping at now.</summary>
    public static DateRange Days(DateOnly first, DateOnly last, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        if (last < first) (first, last) = (last, first);
        return Build(first, last, now, zone, Span(first, last, culture));
    }

    /// <summary>"Thu 3 Sep 2026", "1 Sep – 30 Sep 2026", "28 Dec 2025 – 3 Jan 2026".</summary>
    public static string Span(DateOnly first, DateOnly last, CultureInfo culture)
    {
        if (first == last) return first.ToString("ddd d MMM yyyy", culture);
        return first.Year == last.Year
            ? $"{first.ToString("d MMM", culture)} – {last.ToString("d MMM yyyy", culture)}"
            : $"{first.ToString("d MMM yyyy", culture)} – {last.ToString("d MMM yyyy", culture)}";
    }

    /// <summary>
    /// Five minutes for a day, fifteen for up to three days, an hour for a week, six hours for a month and a day beyond.
    /// A bucket under an hour needs minute rows, which only a range within <see cref="ReportQueries.MinuteResolutionLimit"/> reads.
    /// </summary>
    public static TimeSpan BucketFor(TimeSpan span)
    {
        if (span <= TimeSpan.FromHours(25)) return TimeSpan.FromMinutes(5);
        if (span <= ReportQueries.MinuteResolutionLimit) return TimeSpan.FromMinutes(15);
        if (span <= TimeSpan.FromHours(8 * 24 + 1)) return TimeSpan.FromHours(1);
        if (span <= TimeSpan.FromHours(32 * 24 + 1)) return TimeSpan.FromHours(6);
        return TimeSpan.FromDays(1);
    }

    /// <summary>A chart heading's name for its bucket: "5-min", "hourly", "6-hour", "daily".</summary>
    public static string BucketName(TimeSpan bucket) => bucket.TotalMinutes switch
    {
        < 60 => bucket.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + "-min",
        60 => "hourly",
        < 1440 => bucket.TotalHours.ToString("0", CultureInfo.InvariantCulture) + "-hour",
        _ => "daily",
    };

    /// <summary>What one bucket is, after "per": "15 min", "hour", "6 hours", "day".</summary>
    public static string BucketLength(TimeSpan bucket) => bucket.TotalMinutes switch
    {
        < 60 => bucket.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + " min",
        60 => "hour",
        < 1440 => bucket.TotalHours.ToString("0", CultureInfo.InvariantCulture) + " hours",
        _ => "day",
    };

    /// <summary>The local calendar day at <paramref name="instant"/>.</summary>
    public static DateOnly LocalDay(DateTimeOffset instant, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    /// <summary>Midnight local time at the start of <paramref name="day"/>.</summary>
    public static DateTimeOffset Midnight(DateOnly day, TimeZoneInfo zone) => At(day.ToDateTime(TimeOnly.MinValue), zone);

    /// <summary>The instant a local clock shows <paramref name="wall"/>, or the first valid time after it where a clock change skips it.</summary>
    public static DateTimeOffset At(DateTime wall, TimeZoneInfo zone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(wall)) wall = wall.AddMinutes(15);
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }

    private static DateRange Build(DateOnly first, DateOnly last, DateTimeOffset now, TimeZoneInfo zone, string title)
    {
        var from = Midnight(first, zone);
        var through = Midnight(last.AddDays(1), zone);
        var to = now < through ? now : through;
        return new DateRange(from, to < from ? from : to, through, title, BucketFor(through - from));
    }
}
