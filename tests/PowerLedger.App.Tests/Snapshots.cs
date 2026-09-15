using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.App.Tests;

/// <summary>History as the mockup shows it: a Tuesday afternoon eight days into September.</summary>
internal static class Snapshots
{
    public static HistorySnapshot Typical(DateTimeOffset now, IReadOnlyList<DaySlot>? slots = null)
    {
        var dayStart = new DateTimeOffset(now.Date, TimeSpan.Zero);
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var today = new RangeTotals(
            From: dayStart, To: now, EnergyKwh: 0.284, Cost: 0.0483m, Currency: "USD", CostIsPartial: false,
            AvgW: 41, PeakW: 68, PeakAt: dayStart.AddHours(14),
            OnHours: 7 + 5 / 60.0, IdleOnHours: 0.75, IdleOffHours: 0, AsleepHours: 7.5, UnmonitoredHours: 0,
            CpuKwh: 0.12, GpuKwh: 0.03, DisplayKwh: 0.028, RestKwh: 0.106, IdleOnKwh: 0.011, IdleOffKwh: 0,
            MeasuredShare: 0.62, CalibratedShare: 0.2, EstimatedShare: 0.18);
        var month = today with { From = monthStart, EnergyKwh = 2.74, Cost = 0.47m, IdleOnKwh = 0.15, IdleOffKwh = 0.06 };
        var days = Enumerable.Range(1, now.Day)
            .Select(d => new DayTotals(new DateOnly(now.Year, now.Month, d), 0.2 + d % 4 * 0.1, 0.05m, "USD", false, 8, 60, 0.01, 0))
            .ToList();
        return new HistorySnapshot(
            today, month, days, slots ?? DaySlots.Build([], dayStart, now), dayStart,
            new Tariff(now.AddDays(-30), 0.17m, "USD"),
            new MachineNames("11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", "NVIDIA GeForce MX330", 15.3));
    }
}
