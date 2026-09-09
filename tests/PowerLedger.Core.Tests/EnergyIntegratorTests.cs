using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class EnergyIntegratorTests
{
    private static Reading Reading(double totalW = 34.2, double delta = 1.0, bool idle = false, bool displayOn = true, bool onBattery = false)
        => new(TestData.T0, delta, totalW, Quality.Measured,
               new Components(Cpu: 14.6, Gpu: 4.1, Display: 4.2, 0, 0, 0, 0, 0, 0, Rest: 11.3),
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
    public void Non_positive_deltas_contribute_nothing_and_are_not_gaps()
    {
        var e = EnergyIntegrator.Integrate(Reading(delta: 0));
        e.Wh.ShouldBe(0);
        e.Gap.ShouldBeFalse();
        e.GapSeconds.ShouldBe(0);
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
}
