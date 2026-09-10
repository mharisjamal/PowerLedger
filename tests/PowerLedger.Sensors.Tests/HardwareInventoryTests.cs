using PowerLedger.Contracts;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class HardwareInventoryTests
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

    [Fact]
    public void Only_drives_fitted_inside_the_machine_are_counted()
    {
        var drives = new[]
        {
            (MediaType: 4, BusType: 17),   // NVMe SSD
            (MediaType: 3, BusType: 11),   // SATA hard disk
            (MediaType: 0, BusType: 13),   // eMMC on a budget laptop, media type unknown
            (MediaType: 4, BusType: 7),    // USB stick
            (MediaType: 3, BusType: 7),    // USB backup disk
            (MediaType: 0, BusType: 12),   // SD card
            (MediaType: 0, BusType: 15),   // mounted ISO
        };

        HardwareInventory.CountDrives(drives).ShouldBe((2, 1));
    }

    [Theory]
    [InlineData(0x80000000u)]   // internal
    [InlineData(11u)]           // embedded DisplayPort
    [InlineData(6u)]            // LVDS, on older laptops
    public void The_panel_size_comes_from_the_built_in_panel_wherever_windows_lists_it(uint builtIn)
    {
        var connections = new Dictionary<string, uint>
        {
            [@"DISPLAY\DEL41A8\1"] = 10,          // DisplayPort desk monitor, listed first
            [@"DISPLAY\AUO4199\2"] = builtIn,     // the laptop's own panel
        };
        (string, double, double)[] sizes = [(@"DISPLAY\DEL41A8\1", 60, 34), (@"DISPLAY\AUO4199\2", 34, 19)];

        HardwareInventory.BuiltInDiagonal(connections, sizes).ShouldBe(15.3);
    }

    [Fact]
    public void A_laptop_shut_on_its_dock_reports_no_panel_size_rather_than_the_desk_monitor()
    {
        var connections = new Dictionary<string, uint> { [@"DISPLAY\DEL41A8\1"] = 10 };
        (string, double, double)[] sizes = [(@"DISPLAY\DEL41A8\1", 60, 34)];

        HardwareInventory.BuiltInDiagonal(connections, sizes).ShouldBe(0);
    }
}
