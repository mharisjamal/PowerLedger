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
}
