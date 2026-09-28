using System.Globalization;
using PowerLedger.Core;

namespace PowerLedger.App;

/// <summary>
/// The <see cref="IInsights"/> over the PC's own history (Aero look design §4): one read of the last 8 weeks of hour rows,
/// with their days and costs, and all four findings worked from it, on the PC, sending nothing anywhere. Only the
/// carbon figure since the start of the history needs more, and then only the totals before those 8 weeks.
/// <para>It never throws for want of data: with no database, or no rows, each finding says it has nothing yet.</para>
/// </summary>
/// <param name="co2KgPerKwh">Settings' CO₂ per kWh as it stands at each read, so a change there reaches the next one.</param>
/// <param name="region">Windows' region (<see cref="GridFactors.WindowsRegion"/>), for naming where the factor came from.</param>
internal sealed class Insights(IRangeHistory history, Func<double> co2KgPerKwh, string? region) : IInsights
{
    /// <summary>The longest look back any finding takes: the forecast's and unusual use's 8 weeks.</summary>
    public const int Days = 7 * 8;

    public InsightsReport Read(DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = Ranges.LocalDay(now, zone);
        var from = today.AddDays(-Days);
        var range = Ranges.Days(from, today, now, zone, CultureInfo.InvariantCulture) with { Bucket = TimeSpan.FromHours(1) };
        var kgPerKwh = co2KgPerKwh();
        if (history.Read(range, zone) is not { } report) return Empty(null, kgPerKwh);

        var currency = report.Totals.Currency;
        var month = report.Days.Where(d => d.Day.Year == today.Year && d.Day.Month == today.Month).ToList();
        var monthKwh = month.Sum(d => d.EnergyKwh);
        var price = currency is null ? 0m : Price(month.Sum(d => d.Cost), monthKwh) ?? Price(report.Totals.Cost, report.Totals.EnergyKwh) ?? 0m;

        var hours = report.Series.Select(row => HourUse.From(row, zone)).ToList();
        var midnight = Ranges.Midnight(today, zone);
        var tomorrow = Ranges.Midnight(today.AddDays(1), zone);
        var todayLeft = (tomorrow - now) / (tomorrow - midnight);

        var forecast = BillForecasts.From(HourUse.WholeDays(hours, today), today, todayLeft, monthKwh, price, currency);
        var anomalies = UsageAnomalies.Find(hours, now, zone);
        var habits = Habits.From(hours, today, price, currency);
        var carbon = Carbon.Estimate(monthKwh, report.Totals.EnergyKwh + Before(Ranges.Midnight(from, zone), now, zone), kgPerKwh, region);
        return new InsightsReport(forecast, anomalies, habits, carbon);
    }

    /// <summary>The report with nothing to say yet.</summary>
    public InsightsReport Empty(string? currency, double kgPerKwh)
        => new(BillForecast.NotReady(0, currency), [], null, Carbon.Estimate(0, 0, kgPerKwh, region));

    /// <summary>The energy before the 8 weeks read, when the history goes back further; 0 when it doesn't or can't be read.</summary>
    private double Before(DateTimeOffset start, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (history.FirstDay(zone) is not { } first || Ranges.Midnight(first, zone) >= start) return 0;
        var earlier = Ranges.Days(first, Ranges.LocalDay(start, zone).AddDays(-1), now, zone, CultureInfo.InvariantCulture) with { Bucket = TimeSpan.FromDays(1) };
        return history.Read(earlier, zone)?.Totals.EnergyKwh ?? 0;
    }

    private static decimal? Price(decimal cost, double kwh) => kwh > 0 ? cost / (decimal)kwh : null;
}

/// <summary>One hour row as the Insights maths reads it.</summary>
/// <param name="Start">The hour's start, an absolute instant.</param>
/// <param name="Local">The same instant on the local clock, for its weekday, hour and day.</param>
/// <param name="IdleKwh">Energy used awake and idle, display on or off.</param>
/// <param name="IdleSeconds">How long the PC was awake and idle in the hour.</param>
/// <param name="Seen">Whether the history holds anything for the hour, even sleep; an hour before the first row, or lost,
/// is not seen, and counts for nothing either way.</param>
internal readonly record struct HourUse(DateTimeOffset Start, DateTime Local, double Kwh, double IdleKwh, double IdleSeconds, bool Seen)
{
    public static HourUse From(Aggregate row, TimeZoneInfo zone) => new(
        row.Start, TimeZoneInfo.ConvertTime(row.Start, zone).DateTime, row.EnergyWh / 1000, (row.IdleOnWh + row.IdleOffWh) / 1000,
        row.IdleOnSeconds + row.IdleOffSeconds, row.SampleCount > 0 || row.OnSeconds > 0 || row.GapSeconds > 0 || row.EnergyWh > 0);

    /// <summary>
    /// The whole local days before <paramref name="today"/> the history covers, oldest first: from the first day whose
    /// first hour is seen (a PC that joined at ten in the morning has its first day short, which would drag a median down),
    /// and only days with any hour seen (a day the history lost says nothing, where a day asleep says 0). A clock change's
    /// day is simply 23 or 25 hours long.
    /// </summary>
    public static List<DayUse> WholeDays(IReadOnlyList<HourUse> hours, DateOnly today)
    {
        var first = -1;
        for (var i = 0; i < hours.Count && first < 0; i++)
            if (hours[i].Seen) first = i;
        if (first < 0) return [];

        var start = DateOnly.FromDateTime(hours[first].Local);
        if (first > 0 && DateOnly.FromDateTime(hours[first - 1].Local) == start) start = start.AddDays(1);
        return [.. hours
            .Select(h => (Day: DateOnly.FromDateTime(h.Local), Hour: h))
            .Where(x => x.Day >= start && x.Day < today)
            .GroupBy(x => x.Day)
            .Where(day => day.Any(x => x.Hour.Seen))
            .Select(day => new DayUse(day.Key, day.Sum(x => x.Hour.Kwh)))
            .OrderBy(d => d.Day)];
    }
}

/// <summary>A whole local day's energy.</summary>
internal readonly record struct DayUse(DateOnly Day, double Kwh);

/// <summary>The robust statistics the Insights lean on.</summary>
internal static class Stats
{
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>The <paramref name="q"/> quantile of already sorted values, linear between neighbours.</summary>
    public static double Quantile(IReadOnlyList<double> sorted, double q)
    {
        if (sorted.Count == 0) return 0;
        var at = q * (sorted.Count - 1);
        var below = (int)Math.Floor(at);
        var above = Math.Min(below + 1, sorted.Count - 1);
        return sorted[below] + (sorted[above] - sorted[below]) * (at - below);
    }
}
