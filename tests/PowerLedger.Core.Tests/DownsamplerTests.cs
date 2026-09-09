using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class DownsamplerTests
{
    private static Reading At(int second, double totalW, double delta = 1.0, Quality q = Quality.Measured, bool idle = false, bool displayOn = true)
        => new(TestData.T0.AddSeconds(second), delta, totalW, q,
               new Components(Cpu: totalW * 0.4, Gpu: totalW * 0.1, Display: 4, 0, 0, 0, 0, 0, 0, Rest: totalW * 0.5 - 4),
               OnBattery: q == Quality.Measured, DisplayOn: displayOn, UserIdle: idle, SessionLocked: false, 0.3, 0.3, 0.6, false);

    [Fact]
    public void Sixty_seconds_at_thirty_watts_is_half_a_watt_hour()
    {
        var readings = Enumerable.Range(0, 60).Select(i => At(i, 30)).ToList();
        var m = Downsampler.ToMinute(TestData.T0, readings);
        m.Start.ShouldBe(TestData.T0);
        m.EnergyWh.ShouldBe(0.5, 1e-9);
        m.AvgW.ShouldBe(30, 1e-9);
        m.MaxW.ShouldBe(30);
        m.OnSeconds.ShouldBe(60);
        m.SampleCount.ShouldBe(60);
        m.GapSeconds.ShouldBe(0);
    }

    [Fact]
    public void Minute_energy_equals_the_sum_of_per_tick_integration_even_with_gaps()
    {
        var rng = new Random(7);
        var readings = Enumerable.Range(0, 60).Select(i => At(i, 20 + rng.NextDouble() * 40, delta: i == 30 ? 12.0 : 1.0)).ToList();
        var m = Downsampler.ToMinute(TestData.T0, readings);
        var expected = readings.Sum(r => EnergyIntegrator.Integrate(r).Wh);
        m.EnergyWh.ShouldBe(expected, 1e-9);
        m.GapSeconds.ShouldBe(12.0);
        m.OnSeconds.ShouldBe(59);
    }

    [Fact]
    public void Gap_ticks_do_not_affect_max_or_average()
    {
        var readings = new List<Reading> { At(0, 30), At(1, 30), At(2, 999, delta: 60) };
        var m = Downsampler.ToMinute(TestData.T0, readings);
        m.MaxW.ShouldBe(30);
        m.AvgW.ShouldBe(30, 1e-9);
    }

    [Fact]
    public void Quality_seconds_are_tallied_and_the_dominant_one_reported()
    {
        var readings = Enumerable.Range(0, 40).Select(i => At(i, 30))
            .Concat(Enumerable.Range(40, 20).Select(i => At(i, 30, q: Quality.Estimated))).ToList();
        var m = Downsampler.ToMinute(TestData.T0, readings);
        m.MeasuredSeconds.ShouldBe(40);
        m.EstimatedSeconds.ShouldBe(20);
        m.CalibratedSeconds.ShouldBe(0);
        m.DominantQuality.ShouldBe(Quality.Measured);
        m.BatterySeconds.ShouldBe(40);
    }

    [Fact]
    public void Idle_energy_carries_into_the_minute()
    {
        var readings = Enumerable.Range(0, 60).Select(i => At(i, 30, idle: i >= 30)).ToList();
        var m = Downsampler.ToMinute(TestData.T0, readings);
        m.IdleOnWh.ShouldBe(0.25, 1e-9);
        m.IdleOnSeconds.ShouldBe(30);
        m.IdleOffSeconds.ShouldBe(0);
    }

    [Fact]
    public void An_empty_minute_is_all_zeros()
    {
        var m = Downsampler.ToMinute(TestData.T0, []);
        m.EnergyWh.ShouldBe(0);
        m.AvgW.ShouldBe(0);
        m.SampleCount.ShouldBe(0);
        m.DominantQuality.ShouldBe(Quality.Estimated);
    }

    [Fact]
    public void Hours_sum_minutes_and_average_by_on_time()
    {
        var minutes = Enumerable.Range(0, 60).Select(i =>
            Downsampler.ToMinute(TestData.T0.AddMinutes(i), Enumerable.Range(0, 60).Select(s => At(i * 60 + s, i < 30 ? 20 : 40)).ToList())).ToList();
        var h = Downsampler.ToHour(TestData.T0, minutes);
        h.EnergyWh.ShouldBe(minutes.Sum(m => m.EnergyWh), 1e-9);
        h.EnergyWh.ShouldBe(30.0, 1e-6);
        h.AvgW.ShouldBe(30, 1e-6);
        h.MaxW.ShouldBe(40);
        h.OnSeconds.ShouldBe(3600);
        h.SampleCount.ShouldBe(3600);
        h.DominantQuality.ShouldBe(Quality.Measured);
    }

    [Fact]
    public void Zero_delta_and_non_finite_ticks_do_not_set_the_peak()
    {
        var readings = new List<Reading> { At(0, 30), At(1, 999, delta: 0), At(2, double.NaN) };
        var m = Downsampler.ToMinute(TestData.T0, readings);
        m.MaxW.ShouldBe(30);
        m.EnergyWh.ShouldBe(30.0 / 3600, 1e-9);
        m.OnSeconds.ShouldBe(1);
        m.SampleCount.ShouldBe(3);
    }

    [Fact]
    public void Idle_with_the_display_off_lands_in_the_off_bucket()
    {
        var readings = Enumerable.Range(0, 60).Select(i => At(i, 30, idle: true, displayOn: false)).ToList();
        var m = Downsampler.ToMinute(TestData.T0, readings);
        m.IdleOffWh.ShouldBe(0.5, 1e-9);
        m.IdleOffSeconds.ShouldBe(60);
        m.IdleOnSeconds.ShouldBe(0);
    }

    [Theory]
    [InlineData(60, 0, 0, Quality.Measured)]
    [InlineData(0, 60, 0, Quality.Calibrated)]
    [InlineData(0, 0, 60, Quality.Estimated)]
    [InlineData(30, 30, 0, Quality.Measured)]
    [InlineData(0, 30, 30, Quality.Calibrated)]
    [InlineData(0, 0, 0, Quality.Estimated)]
    public void Dominant_quality_prefers_the_higher_quality_on_ties(double measured, double calibrated, double estimated, Quality expected)
        => (Aggregate.Empty(TestData.T0) with { MeasuredSeconds = measured, CalibratedSeconds = calibrated, EstimatedSeconds = estimated })
            .DominantQuality.ShouldBe(expected);

    [Fact]
    public void An_empty_hour_is_all_zeros()
    {
        var h = Downsampler.ToHour(TestData.T0, []);
        h.Start.ShouldBe(TestData.T0);
        h.EnergyWh.ShouldBe(0);
        h.AvgW.ShouldBe(0);
        h.SampleCount.ShouldBe(0);
    }

    [Fact]
    public void Hours_sum_gap_seconds_and_keep_the_peak_from_real_ticks()
    {
        var first = Downsampler.ToMinute(TestData.T0, [At(0, 30), At(1, 999, delta: 60)]);
        var second = Downsampler.ToMinute(TestData.T0.AddMinutes(1), [At(60, 40)]);
        var h = Downsampler.ToHour(TestData.T0, [first, second]);
        h.GapSeconds.ShouldBe(60);
        h.MaxW.ShouldBe(40);
        h.OnSeconds.ShouldBe(2);
        h.SampleCount.ShouldBe(3);
        h.EnergyWh.ShouldBe((30.0 + 40.0) / 3600, 1e-9);
    }
}
