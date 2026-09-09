using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class DownsamplerTests
{
    private static Reading At(int second, double totalW, double delta = 1.0, Quality q = Quality.Measured, bool idle = false)
        => new(TestData.T0.AddSeconds(second), delta, totalW, q,
               new Components(Cpu: totalW * 0.4, Gpu: totalW * 0.1, Display: 4, 0, 0, 0, 0, 0, 0, Rest: totalW * 0.5 - 4),
               OnBattery: q == Quality.Measured, DisplayOn: true, UserIdle: idle, SessionLocked: false, 0.3, 0.3, 0.6, false);

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
}
