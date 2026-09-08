using System.Text.Json;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class MachineProfileTests
{
    [Fact]
    public void DefaultLaptop_is_a_laptop_with_a_panel_and_no_external_monitors()
    {
        var p = MachineProfile.DefaultLaptop;
        p.Chassis.ShouldBe(ChassisKind.Laptop);
        p.DisplayDiagonalInches.ShouldBe(15.6);
        p.IncludeMonitors.ShouldBeFalse();
        p.PsuTier.ShouldBe(PsuTier.Bronze);
    }

    [Fact]
    public void DefaultDesktop_has_no_internal_panel()
    {
        MachineProfile.DefaultDesktop.Chassis.ShouldBe(ChassisKind.Desktop);
        MachineProfile.DefaultDesktop.DisplayDiagonalInches.ShouldBe(0);
    }

    [Fact]
    public void Quality_integer_values_are_pinned_because_they_are_stored_in_sqlite()
    {
        ((int)Quality.Estimated).ShouldBe(0);
        ((int)Quality.Calibrated).ShouldBe(1);
        ((int)Quality.Measured).ShouldBe(2);
    }

    [Fact]
    public void Json_missing_properties_fall_back_to_documented_defaults_not_zero()
    {
        var p = JsonSerializer.Deserialize<MachineProfile>("""{"Chassis":0,"DisplayDiagonalInches":0}""")!;
        p.Chassis.ShouldBe(ChassisKind.Desktop);
        p.PsuTier.ShouldBe(PsuTier.Bronze);
        p.MonitorWatts.ShouldBe(25);
        p.RamSticks.ShouldBe(1);
        p.CpuTdpOverrideW.ShouldBeNull();
    }
}
