using PowerLedger.Contracts;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.App.Tests;

public class NowMathTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 8, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_live_window_keeps_the_last_sixty_seconds()
    {
        var window = new LiveWindow(TimeSpan.FromSeconds(60));
        for (var s = 0; s <= 90; s++) window.Add(T0.AddSeconds(s), s);
        window.Values.Count.ShouldBe(61);
        window.Values[0].ShouldBe(30);
        window.Values[^1].ShouldBe(90);
    }

    [Fact]
    public void The_live_window_ignores_readings_that_are_not_numbers()
    {
        var window = new LiveWindow(TimeSpan.FromSeconds(60));
        window.Add(T0, double.NaN);
        window.Add(T0, -3);
        window.Values.ShouldBe(new[] { 0.0 });
    }

    [Theory]
    [InlineData(0, 25, 5, 1)]
    [InlineData(68, 75, 25, 5)]
    [InlineData(130, 150, 25, 5)]
    [InlineData(140, 200, 50, 10)]
    [InlineData(5000, 2000, 500, 100)]
    public void The_meter_scale_is_the_smallest_round_size_ten_percent_above_the_highest_value(double highest, double max, double major, double minor)
        => MeterRange.For(highest).ShouldBe(new MeterRange(max, major, minor));

    [Fact]
    public void Meter_ticks_run_from_zero_to_full_scale_with_labelled_majors()
    {
        var ticks = MeterRange.For(130).Ticks().ToList();
        ticks.Count.ShouldBe(31);
        ticks.Where(t => t.Major).Select(t => t.Value).ShouldBe(new double[] { 0, 25, 50, 75, 100, 125, 150 });
    }

    [Fact]
    public void The_budget_is_four_bands_and_a_negative_rest_counts_as_zero()
    {
        var shares = Budget.Of(Frames.At(T0, totalW: 34.2, cpu: 14.6, gpu: 4.1, display: 4.0));
        shares.Select(s => s.Part).ShouldBe(new[] { Part.Cpu, Part.Gpu, Part.Display, Part.Rest });
        shares[3].Watts.ShouldBe(11.5, 1e-9);
        shares.Sum(s => s.Share).ShouldBe(1, 1e-9);

        var over = Budget.Of(Frames.At(T0, totalW: 20, cpu: 14.6, gpu: 4.1, display: 4.0));
        over[3].Watts.ShouldBe(0);
        over.Sum(s => s.Share).ShouldBe(1, 1e-9);
    }

    [Fact]
    public void The_month_projects_from_the_daily_average_and_prices_it_at_the_month_s_own_rate()
    {
        var days = Enumerable.Range(1, 8).Select(d => Day(d, 0.3 + d * 0.01)).ToList();
        var month = Month(energyKwh: days.Sum(d => d.EnergyKwh), cost: 0.50m, idleOnKwh: 0.15, idleOffKwh: 0.06);
        var outlook = MonthOutlook.From(month, days, new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));

        outlook.DailyAverageKwh.ShouldNotBeNull().ShouldBe(month.EnergyKwh / 7.5, 1e-9);
        outlook.ProjectedKwh.ShouldNotBeNull().ShouldBe(month.EnergyKwh / 7.5 * 30, 1e-9);
        var price = 0.50m / (decimal)month.EnergyKwh;
        outlook.ProjectedCost.ShouldBe(decimal.Round((decimal)outlook.ProjectedKwh!.Value * price, 2));
        outlook.IdleWasteKwh.ShouldBe(0.21, 1e-9);
        outlook.Lowest!.Day.ShouldBe(new DateOnly(2026, 9, 1));
        outlook.Highest!.Day.ShouldBe(new DateOnly(2026, 9, 7));             // the 8th is today, not yet complete
        outlook.NextReport.ShouldBe(new DateOnly(2026, 10, 1));
        outlook.DaysRecorded.ShouldBe(8);
    }

    [Fact]
    public void Before_a_full_day_is_recorded_there_is_no_projection()
    {
        var outlook = MonthOutlook.From(Month(energyKwh: 0.2, cost: 0.03m), [Day(8, 0.2)], new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        outlook.DailyAverageKwh.ShouldBeNull();
        outlook.ProjectedKwh.ShouldBeNull();
        outlook.Lowest.ShouldBeNull();
    }

    private static DayTotals Day(int day, double kwh)
        => new(new DateOnly(2026, 9, day), kwh, 0, "USD", false, OnHours: 8, PeakW: 60, IdleOnKwh: 0, IdleOffKwh: 0);

    private static RangeTotals Month(double energyKwh, decimal cost, double idleOnKwh = 0, double idleOffKwh = 0) => new(
        T0, T0, energyKwh, cost, "USD", false, AvgW: 30, PeakW: 68, PeakAt: T0,
        OnHours: 60, IdleOnHours: 1, IdleOffHours: 0.5, AsleepHours: 50, UnmonitoredHours: 0,
        CpuKwh: 0, GpuKwh: 0, DisplayKwh: 0, RestKwh: 0, idleOnKwh, idleOffKwh,
        MeasuredShare: 0.62, CalibratedShare: 0.2, EstimatedShare: 0.18);
}
