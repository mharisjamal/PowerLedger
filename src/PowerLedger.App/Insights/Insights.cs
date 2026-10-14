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
/// <param name="idle">The service's idle threshold and what the PC draws asleep, for the Habits saving, as they stand at
/// each read; <see cref="IdleSetup.Default"/> without it.</param>
internal sealed class Insights(IRangeHistory history, Func<double> co2KgPerKwh, string? region, Func<IdleSetup>? idle = null) : IInsights
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
        var monthCost = currency is null ? 0m : month.Sum(d => d.Cost);
        var price = currency is null ? 0m : Price(monthCost, monthKwh) ?? Price(report.Totals.Cost, report.Totals.EnergyKwh) ?? 0m;
        // The energy still to come is priced at today's tariff (report finding 4); the averages only stand in without one.
        var todayPrice = report.Tariff is { } tariff && tariff.Currency == currency ? tariff.PricePerKwh : price;

        var hours = report.Series.Select(row => HourUse.From(row, zone)).ToList();
        var midnight = Ranges.Midnight(today, zone);
        var tomorrow = Ranges.Midnight(today.AddDays(1), zone);
        // What recent days used after this time of day (report finding 19); an even spread only without any.
        var todayLeft = 1 - (DayShape.ShareBy(hours, today, TimeZoneInfo.ConvertTime(now, zone).TimeOfDay) ?? (now - midnight) / (tomorrow - midnight));

        var forecast = BillForecasts.From(HourUse.WholeDays(hours, today), today, todayLeft, monthKwh, monthCost, todayPrice, currency);
        var anomalies = UsageAnomalies.Find(hours, now, zone);
        var habits = HabitsFinder.From(hours, today, todayPrice, currency, idle?.Invoke());
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
/// <param name="Kwh">The hour's energy as the totals count it (<see cref="Aggregate.CountedWh"/>): what came from the battery
/// at the wall equivalent, and what the machine drew asleep, laid back over the hours it slept, so the days the forecast
/// and its band are worked from add up as the month so far does.</param>
/// <param name="IdleKwh">Energy used awake and idle, display on or off, on the scale of <paramref name="Kwh"/>: idle on
/// battery at its wall equivalent.</param>
/// <param name="IdleSeconds">How long the PC was awake and idle in the hour.</param>
/// <param name="Seen">Whether the history holds anything for the hour, even sleep; an hour before the first row, or lost,
/// is not seen, and counts for nothing either way.</param>
internal readonly record struct HourUse(DateTimeOffset Start, DateTime Local, double Kwh, double IdleKwh, double IdleSeconds, bool Seen)
{
    public static HourUse From(Aggregate row, TimeZoneInfo zone) => new(
        row.Start, TimeZoneInfo.ConvertTime(row.Start, zone).DateTime, row.CountedWh / 1000, (row.IdleOnWh + row.IdleOffWh) * row.CountedPerWh / 1000,
        row.IdleOnSeconds + row.IdleOffSeconds, row.SampleCount > 0 || row.OnSeconds > 0 || row.GapSeconds > 0 || row.EnergyWh > 0);

    /// <summary>
    /// The whole local days before <paramref name="today"/> the history covers, oldest first: from the first day whose
    /// first hour is seen (a PC that joined at ten in the morning has its first day short, which would drag a median down),
    /// and every day after it, a day with no rows at all counting as a day that used nothing (report finding 4: a PC shut
    /// down at weekends leaves no rows then, and skipping those days forecast its weekends at its weekdays' use). A clock
    /// change's day is simply 23 or 25 hours long.
    /// </summary>
    public static List<DayUse> WholeDays(IReadOnlyList<HourUse> hours, DateOnly today)
    {
        var first = -1;
        for (var i = 0; i < hours.Count && first < 0; i++)
            if (hours[i].Seen) first = i;
        if (first < 0) return [];

        var start = DateOnly.FromDateTime(hours[first].Local);
        if (first > 0 && DateOnly.FromDateTime(hours[first - 1].Local) == start) start = start.AddDays(1);
        var used = hours
            .GroupBy(h => DateOnly.FromDateTime(h.Local))
            .ToDictionary(day => day.Key, day => day.Sum(h => h.Kwh));
        var days = new List<DayUse>();
        for (var day = start; day < today; day = day.AddDays(1)) days.Add(new DayUse(day, used.GetValueOrDefault(day)));
        return days;
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
