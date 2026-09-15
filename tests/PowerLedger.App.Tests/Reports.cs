using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.App.Tests;

/// <summary>Range reports for view model tests: a range with readings in every bucket up to now, and an empty one.</summary>
internal static class Reports
{
    public static RangeTotals Totals(DateRange range, double kwh = 2.74) => new(
        From: range.From, To: range.To, EnergyKwh: kwh, Cost: (decimal)kwh * 0.17m, Currency: "USD", CostIsPartial: false,
        AvgW: 41, PeakW: 68, PeakAt: range.From.AddHours(14),
        OnHours: 60, IdleOnHours: 6, IdleOffHours: 2, AsleepHours: 50, UnmonitoredHours: 5,
        CpuKwh: kwh * 0.43, GpuKwh: kwh * 0.11, DisplayKwh: kwh * 0.1, RestKwh: kwh * 0.36,
        IdleOnKwh: kwh * 0.1, IdleOffKwh: kwh * 0.04, MeasuredShare: 0.62, CalibratedShare: 0.2, EstimatedShare: 0.18);

    public static RangeReport Typical(DateRange range) => Typical(range, 2.74);

    public static RangeReport Typical(DateRange range, double kwh)
    {
        var count = Math.Max(1, (int)Math.Ceiling((range.To - range.From) / range.Bucket));
        var perBucket = kwh * 1000 / count;
        var series = Enumerable.Range(0, count).Select(i => Aggregate.Empty(range.From + i * range.Bucket) with
        {
            EnergyWh = perBucket, CpuWh = perBucket * 0.43, GpuWh = perBucket * 0.11, DisplayWh = perBucket * 0.1, RestWh = perBucket * 0.36,
            OnSeconds = range.Bucket.TotalSeconds,
        }).ToList();
        var first = DateOnly.FromDateTime(range.From.UtcDateTime);
        var last = DateOnly.FromDateTime(range.To.AddTicks(-1).UtcDateTime);
        var dayCount = Math.Max(1, last.DayNumber - first.DayNumber + 1);
        var days = Enumerable.Range(0, dayCount)
            .Select(d => new DayTotals(first.AddDays(d), kwh / dayCount, 0.05m, "USD", false, 8, 60, 0.01, 0))
            .ToList();
        return new RangeReport(range, Totals(range, kwh), days, series, new Tariff(range.From.AddDays(-30), 0.17m, "USD"));
    }

    public static RangeReport Empty(DateRange range) => new(
        range,
        Totals(range, 0) with
        {
            Cost = 0, AvgW = 0, PeakW = 0, PeakAt = null, OnHours = 0, IdleOnHours = 0, IdleOffHours = 0, AsleepHours = 0,
            UnmonitoredHours = (range.To - range.From).TotalHours, MeasuredShare = 0, CalibratedShare = 0, EstimatedShare = 0,
        },
        [], []);
}
