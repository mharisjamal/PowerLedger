using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class EnergyIntegratorTests
{
    private static Reading Reading(double totalW = 34.2, double delta = 1.0, bool idle = false, bool displayOn = true, bool onBattery = false, double monitors = 0, double rest = 11.3)
        => new(TestData.T0, delta, totalW, Quality.Measured,
               new Components(Cpu: 14.6, Gpu: 4.1, Display: 4.2, 0, 0, 0, 0, Monitors: monitors, 0, Unattributed: rest),
               onBattery, displayOn, idle, false, 0.3, 0.3, 0.6, false);

    [Fact]
    public void One_second_at_34_2_watts_is_0_0095_watt_hours_split_by_component()
    {
        var e = EnergyIntegrator.Integrate(Reading());
        e.Wh.ShouldBe(34.2 / 3600, 1e-9);
        e.CpuWh.ShouldBe(14.6 / 3600, 1e-9);
        e.GpuWh.ShouldBe(4.1 / 3600, 1e-9);
        e.DisplayWh.ShouldBe(4.2 / 3600, 1e-9);
        e.RestWh.ShouldBe(11.3 / 3600, 1e-9);
        e.OnSeconds.ShouldBe(1.0);
        e.Gap.ShouldBeFalse();
    }

    [Fact]
    public void Ticks_up_to_five_seconds_count_in_full()
    {
        var e = EnergyIntegrator.Integrate(Reading(delta: 5.0));
        e.Wh.ShouldBe(34.2 * 5 / 3600, 1e-9);
        e.OnSeconds.ShouldBe(5.0);
    }

    [Fact]
    public void Ticks_longer_than_five_seconds_are_gaps_with_zero_energy()
    {
        var e = EnergyIntegrator.Integrate(Reading(delta: 5.01));
        e.Gap.ShouldBeTrue();
        e.Wh.ShouldBe(0);
        e.OnSeconds.ShouldBe(0);
        e.GapSeconds.ShouldBe(5.01);
    }

    [Fact]
    public void A_gap_tick_counts_no_idle_or_battery_time()
    {
        var e = EnergyIntegrator.Integrate(Reading(delta: 9, idle: true, onBattery: true));
        e.Gap.ShouldBeTrue();
        e.IdleOnSeconds.ShouldBe(0);
        e.IdleOffSeconds.ShouldBe(0);
        e.IdleOnWh.ShouldBe(0);
        e.BatterySeconds.ShouldBe(0);
        e.OnSeconds.ShouldBe(0);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Non_positive_deltas_contribute_nothing_and_are_not_gaps(double delta)
    {
        var e = EnergyIntegrator.Integrate(Reading(delta: delta));
        e.ShouldBe(EnergySlice.Nothing);
        e.Gap.ShouldBeFalse();
    }

    [Theory]
    [InlineData(double.NaN, 34.2)]
    [InlineData(1.0, double.NaN)]
    [InlineData(1.0, double.PositiveInfinity)]
    public void Non_finite_readings_contribute_nothing(double delta, double totalW)
        => EnergyIntegrator.Integrate(Reading(totalW: totalW, delta: delta)).ShouldBe(EnergySlice.Nothing);

    [Fact]
    public void The_gap_threshold_is_configurable_and_scales_with_the_sample_interval()
    {
        var e = EnergyIntegrator.Integrate(Reading(delta: 7), maxDeltaSeconds: 10);
        e.Gap.ShouldBeFalse();
        e.OnSeconds.ShouldBe(7);
        EnergyIntegrator.GapThresholdFor(1).ShouldBe(5);
        EnergyIntegrator.GapThresholdFor(4).ShouldBe(8);
        EnergyIntegrator.GapThresholdFor(5).ShouldBe(10);
    }

    [Fact]
    public void Idle_energy_is_split_by_display_state()
    {
        EnergyIntegrator.Integrate(Reading(idle: true, displayOn: true)).IdleOnWh.ShouldBe(34.2 / 3600, 1e-9);
        EnergyIntegrator.Integrate(Reading(idle: true, displayOn: true)).IdleOnSeconds.ShouldBe(1.0);
        EnergyIntegrator.Integrate(Reading(idle: true, displayOn: false)).IdleOffWh.ShouldBe(34.2 / 3600, 1e-9);
        EnergyIntegrator.Integrate(Reading(idle: true, displayOn: false)).IdleOffSeconds.ShouldBe(1.0);
        EnergyIntegrator.Integrate(Reading(idle: false)).IdleOnWh.ShouldBe(0);
        EnergyIntegrator.Integrate(Reading(idle: false)).IdleOnSeconds.ShouldBe(0);
    }

    [Fact]
    public void Battery_seconds_are_counted_only_on_battery()
    {
        EnergyIntegrator.Integrate(Reading(onBattery: true)).BatterySeconds.ShouldBe(1.0);
        EnergyIntegrator.Integrate(Reading(onBattery: false)).BatterySeconds.ShouldBe(0);
    }

    [Fact]
    public void Display_energy_includes_opted_in_monitors()
    {
        var e = EnergyIntegrator.Integrate(Reading(totalW: 59.2, monitors: 25));
        e.DisplayWh.ShouldBe((4.2 + 25) / 3600, 1e-9);
        e.RestWh.ShouldBe(11.3 / 3600, 1e-9);
        (e.CpuWh + e.GpuWh + e.DisplayWh + e.RestWh).ShouldBe(e.Wh, 1e-9);
    }

    [Fact]
    public void Over_reporting_parts_give_negative_rest_energy_that_still_adds_up()
    {
        var e = EnergyIntegrator.Integrate(Reading(totalW: 20, rest: 20 - 14.6 - 4.1 - 4.2));
        e.RestWh.ShouldBeLessThan(0);
        e.RestWh.ShouldBe((20 - 14.6 - 4.1 - 4.2) / 3600, 1e-9);
        (e.CpuWh + e.GpuWh + e.DisplayWh + e.RestWh).ShouldBe(e.Wh, 1e-9);
    }
}
