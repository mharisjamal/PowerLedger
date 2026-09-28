using PowerLedger.Core;

namespace PowerLedger.App.Tests;

/// <summary>
/// Golden series for the Insights maths (Aero look design §7): hour rows and whole days built from a rule, in any zone, so
/// a flat week, weekday and weekend patterns, a spike, missing hours and clock changes are each one line in a test.
/// </summary>
internal static class InsightsSeries
{
    public static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");

    /// <summary>Hour rows from local midnight on <paramref name="first"/> for <paramref name="days"/> local days, an hour of
    /// absolute time apart, as <c>ReportQueries.Series</c> cuts them. <paramref name="kwh"/> gives an hour's energy from
    /// its local start, or null for an hour with no rows at all; <paramref name="idle"/> its idle energy and seconds.</summary>
    public static List<HourUse> Hours(
        DateOnly first, int days, TimeZoneInfo zone, Func<DateTime, double?> kwh, Func<DateTime, (double Kwh, double Seconds)>? idle = null)
    {
        var from = Ranges.Midnight(first, zone);
        var to = Ranges.Midnight(first.AddDays(days), zone);
        var hours = new List<HourUse>();
        for (var start = from; start < to; start += TimeSpan.FromHours(1))
        {
            var local = TimeZoneInfo.ConvertTime(start, zone).DateTime;
            var used = kwh(local);
            var (idleKwh, idleSeconds) = used is null ? (0, 0) : idle?.Invoke(local) ?? (0, 0);
            hours.Add(new HourUse(start, local, used ?? 0, idleKwh, idleSeconds, used is not null));
        }
        return hours;
    }

    /// <summary>The same hours as the history hands them over: aggregates with energy, idle energy and time on.</summary>
    public static List<Aggregate> Rows(IEnumerable<HourUse> hours) => [.. hours.Select(h => h.Seen
        ? Aggregate.Empty(h.Start) with
        {
            EnergyWh = h.Kwh * 1000, IdleOnWh = h.IdleKwh * 1000, IdleOnSeconds = h.IdleSeconds, OnSeconds = 3600, SampleCount = 3600,
            MeasuredSeconds = 3600,
        }
        : Aggregate.Empty(h.Start))];

    /// <summary>Whole days from <paramref name="first"/>, one a day, each's energy from <paramref name="kwh"/>.</summary>
    public static List<DayUse> Days(DateOnly first, int count, Func<DateOnly, double> kwh)
        => [.. Enumerable.Range(0, count).Select(i => first.AddDays(i)).Select(d => new DayUse(d, kwh(d)))];

    /// <summary>A normal draw from <paramref name="random"/> (Box and Muller), for noise that is seeded and so the same on
    /// every run.</summary>
    public static double Gaussian(this Random random)
    {
        var u = 1 - random.NextDouble();
        var v = random.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v);
    }

    public static bool Weekend(DayOfWeek day) => day is DayOfWeek.Saturday or DayOfWeek.Sunday;
}
