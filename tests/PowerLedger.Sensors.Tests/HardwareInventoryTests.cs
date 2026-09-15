using PowerLedger.Contracts;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class HardwareInventoryTests
{
    [Theory]
    [InlineData(8)]     // Portable
    [InlineData(9)]     // Laptop
    [InlineData(10)]    // Notebook
    [InlineData(11)]    // Hand held
    [InlineData(12)]    // Docking station
    [InlineData(14)]    // Sub-notebook
    [InlineData(18)]    // Expansion chassis
    [InlineData(21)]    // Peripheral chassis
    [InlineData(30)]    // Tablet
    [InlineData(31)]    // Convertible
    [InlineData(32)]    // Detachable
    public void A_portable_enclosure_is_a_laptop_with_or_without_a_battery(int enclosure)
    {
        HardwareInventory.ChassisFrom(enclosure, batteryPresent: true).ShouldBe(ChassisKind.Laptop);
        HardwareInventory.ChassisFrom(enclosure, batteryPresent: false).ShouldBe(ChassisKind.Laptop);
    }

    [Theory]
    [InlineData(3)]     // Desktop
    [InlineData(4)]     // Low-profile desktop
    [InlineData(5)]     // Pizza box
    [InlineData(6)]     // Mini tower
    [InlineData(7)]     // Tower
    [InlineData(13)]    // All in one
    [InlineData(15)]    // Space-saving
    [InlineData(16)]    // Lunch box
    [InlineData(17)]    // Main server chassis
    [InlineData(19)]    // Sub-chassis
    [InlineData(20)]    // Bus expansion chassis
    [InlineData(22)]    // RAID chassis
    [InlineData(23)]    // Rack mount
    [InlineData(24)]    // Sealed-case PC
    [InlineData(25)]    // Multi-system chassis
    [InlineData(26)]    // Compact PCI
    [InlineData(27)]    // Advanced TCA
    [InlineData(28)]    // Blade
    [InlineData(29)]    // Blade enclosure
    [InlineData(33)]    // IoT gateway
    [InlineData(34)]    // Embedded PC
    [InlineData(35)]    // Mini PC
    [InlineData(36)]    // Stick PC
    public void A_known_desktop_enclosure_stays_a_desktop_when_a_battery_shows(int enclosure)
    {
        // A UPS on USB that Windows doesn't mark short-term looks just like the machine's own battery.
        HardwareInventory.ChassisFrom(enclosure, batteryPresent: true).ShouldBe(ChassisKind.Desktop);
        HardwareInventory.ChassisFrom(enclosure, batteryPresent: false).ShouldBe(ChassisKind.Desktop);
    }

    [Theory]
    [InlineData(null)]  // Windows didn't answer
    [InlineData(1)]     // Other
    [InlineData(2)]     // Unknown
    [InlineData(0)]     // not a type SMBIOS defines
    [InlineData(37)]
    public void Only_an_unknown_enclosure_falls_back_to_the_battery(int? enclosure)
    {
        // Some laptops report "Other" or "Unknown"; there the battery is all there is to go on.
        HardwareInventory.ChassisFrom(enclosure, batteryPresent: true).ShouldBe(ChassisKind.Laptop);
        HardwareInventory.ChassisFrom(enclosure, batteryPresent: false).ShouldBe(ChassisKind.Desktop);
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
