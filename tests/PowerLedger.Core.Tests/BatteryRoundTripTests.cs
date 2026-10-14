using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

/// <summary>Accuracy A9: energy from a laptop's battery counts at the wall it was charged from, about 1.22 times its discharge.</summary>
public class BatteryRoundTripTests
{
    private static Reading Tick(double watts, bool onBattery, double delta = 3600)
        => new(TestData.T0, delta, watts, Quality.Measured, new Components(watts / 2, 0, 1, 0, 0, 0, 0, 0, 0, watts / 2 - 1),
               onBattery, DisplayOn: true, UserIdle: false, SessionLocked: false, 0.3, null, 0.5, Suspect: false);

    [Fact]
    public void A_watt_hour_from_the_battery_cost_about_1_22_at_the_wall()
    {
        BatteryRoundTrip.Factor.ShouldBe(1 / (AdapterEfficiency.At(0.5) * BatteryRoundTrip.ChargerEfficiency * BatteryRoundTrip.CellEfficiency), 1e-12);
        BatteryRoundTrip.Factor.ShouldBeInRange(1.2, 1.27);
    }

    [Fact]
    public void A_tick_on_battery_books_its_energy_as_from_the_battery_and_one_on_ac_does_not()
    {
        var onBattery = EnergyIntegrator.Integrate(Tick(10, onBattery: true), maxDeltaSeconds: 7200);
        onBattery.Wh.ShouldBe(10, 1e-9);                                       // at the pack, as the live watts
        onBattery.BatteryWh.ShouldBe(10, 1e-9);
        EnergyIntegrator.Integrate(Tick(10, onBattery: false), maxDeltaSeconds: 7200).BatteryWh.ShouldBe(0);
        EnergyIntegrator.Integrate(Tick(10, onBattery: true)).BatteryWh.ShouldBe(0);   // a gap
    }

    [Fact]
    public void A_row_counts_its_battery_energy_at_the_wall_and_its_sleep_as_booked()
    {
        var minute = Downsampler.ToMinute(TestData.T0, [Tick(10, onBattery: true, delta: 1), Tick(20, onBattery: false, delta: 1)]);
        minute.BatteryWh.ShouldBe(10 / 3600.0, 1e-12);
        minute.EnergyWh.ShouldBe(30 / 3600.0, 1e-12);
        minute.AvgW.ShouldBe(15, 1e-9);                                         // live watts stay at the pack
        (minute with { AsleepWh = 1 }).CountedWh.ShouldBe((30 / 3600.0) + (10 / 3600.0 * (BatteryRoundTrip.Factor - 1)) + 1, 1e-12);
        Downsampler.ToHour(TestData.T0, [minute, minute]).BatteryWh.ShouldBe(20 / 3600.0, 1e-12);
    }
}
