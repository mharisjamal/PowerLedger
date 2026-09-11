using PowerLedger.Contracts;
using PowerLedger.Sensors;

namespace PowerLedger.Service.Tests;

internal static class Facts
{
    public static InventoryFacts Laptop(int ramSticks = 2) => new(
        ChassisKind.Laptop, "11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", "NVIDIA GeForce MX330",
        ramSticks, RamIsDdr5: false, SsdCount: 1, HddCount: 0, DisplayDiagonalInches: 15.3, MonitorCount: 1);

    public static InventoryFacts Desktop() => new(
        ChassisKind.Desktop, "Unknown Chip 9000", null, 4, RamIsDdr5: true, SsdCount: 2, HddCount: 1, DisplayDiagonalInches: 0, MonitorCount: 2);
}
