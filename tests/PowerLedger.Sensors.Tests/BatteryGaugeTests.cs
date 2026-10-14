using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

/// <summary>Accuracy A10: the charge Windows reports in SYSTEM_BATTERY_STATE, in watt-hours, for what a sleep costs.</summary>
public class BatteryGaugeTests
{
    private static Win32.BatteryState State(bool ac = false, bool present = true, bool shortTerm = false, uint remaining = 41_250, uint full = 52_000)
        => new(ac, present, Charging: false, Discharging: !ac, RateMilliwatts: -5000, shortTerm, remaining, full);

    [Fact]
    public void The_charge_comes_in_watt_hours_with_whether_the_machine_is_on_ac()
    {
        BatteryGauge.From(State(), relativeUnits: false).ShouldBe(new BatteryCharge(OnAc: false, 41.25, 52));
        BatteryGauge.From(State(ac: true), relativeUnits: false).ShouldBe(new BatteryCharge(OnAc: true, 41.25, 52));
    }

    [Fact]
    public void No_battery_of_its_own_or_a_charge_that_is_not_watt_hours_gives_none()
    {
        BatteryGauge.From(null, relativeUnits: false).ShouldBeNull();
        BatteryGauge.From(State(present: false), relativeUnits: false).ShouldBeNull();
        BatteryGauge.From(State(shortTerm: true), relativeUnits: false).ShouldBeNull();         // a UPS
        BatteryGauge.From(State(), relativeUnits: true).ShouldBeNull();
        BatteryGauge.From(State(remaining: Win32.UnknownCapacity), relativeUnits: false).ShouldBeNull();
        BatteryGauge.From(State(full: Win32.UnknownCapacity), relativeUnits: false).ShouldBeNull();
        BatteryGauge.From(State(full: 0), relativeUnits: false).ShouldBeNull();
    }
}
