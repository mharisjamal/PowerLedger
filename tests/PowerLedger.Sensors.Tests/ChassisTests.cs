using PowerLedger.Contracts;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class ChassisTests
{
    [Theory]
    [InlineData(9, true, ChassisKind.Laptop)]      // Laptop
    [InlineData(10, true, ChassisKind.Laptop)]     // Notebook
    [InlineData(14, true, ChassisKind.Laptop)]     // Sub-notebook
    [InlineData(31, true, ChassisKind.Laptop)]     // Convertible
    [InlineData(3, false, ChassisKind.Desktop)]    // Desktop
    [InlineData(7, false, ChassisKind.Desktop)]    // Tower
    [InlineData(23, false, ChassisKind.Desktop)]   // Rack mount
    public void The_enclosure_type_decides_the_chassis(int enclosure, bool battery, ChassisKind expected)
        => HardwareInventory.ChassisFrom(enclosure, batteryPresent: battery).ShouldBe(expected);

    [Fact]
    public void A_battery_outvotes_an_enclosure_that_claims_to_be_a_desktop()
    {
        // Some laptops report "Other" or "Unknown"; a fitted battery settles it.
        HardwareInventory.ChassisFrom(enclosureType: 2, batteryPresent: true).ShouldBe(ChassisKind.Laptop);
        HardwareInventory.ChassisFrom(enclosureType: 3, batteryPresent: true).ShouldBe(ChassisKind.Laptop);
    }

    [Fact]
    public void No_enclosure_information_falls_back_to_the_battery()
    {
        HardwareInventory.ChassisFrom(null, batteryPresent: true).ShouldBe(ChassisKind.Laptop);
        HardwareInventory.ChassisFrom(null, batteryPresent: false).ShouldBe(ChassisKind.Desktop);
    }
}
