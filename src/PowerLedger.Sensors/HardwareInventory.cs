using PowerLedger.Contracts;

namespace PowerLedger.Sensors;

/// <summary>
/// Asks Windows what this machine is made of (spec §5). Runs at service start and on resume, never per tick,
/// because WMI is slow. Every query is individually guarded: a machine that will not answer one question
/// still produces facts for the rest.
/// </summary>
public static class HardwareInventory
{
    /// <summary>Enclosure types Windows uses for portable machines.</summary>
    private static readonly HashSet<int> PortableEnclosures = [8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32];

    /// <summary>STORAGE_BUS_TYPE values for drives that are not part of the machine. FireWire, Fibre Channel, USB,
    /// iSCSI and SD cards are external or remote; virtual, file-backed and Storage Spaces disks are not drives at all.</summary>
    private static readonly HashSet<int> NotFittedBuses = [4, 6, 7, 9, 12, 14, 15, 16];

    /// <summary>The machine's own battery settles it; otherwise the enclosure type decides, and an unknown enclosure means desktop.</summary>
    /// <param name="batteryPresent">True for a battery that powers this machine alone; a UPS does not count.</param>
    public static ChassisKind ChassisFrom(int? enclosureType, bool batteryPresent)
    {
        if (batteryPresent) return ChassisKind.Laptop;
        return enclosureType is { } type && PortableEnclosures.Contains(type) ? ChassisKind.Laptop : ChassisKind.Desktop;
    }

    /// <summary>Solid-state and spinning drives fitted inside the machine. Media type 3 is a hard disk; anything else,
    /// unknown included, counts as solid state, the lower-power guess.</summary>
    public static (int Ssd, int Hdd) CountDrives(IEnumerable<(int MediaType, int BusType)> drives)
    {
        var solid = 0;
        var spinning = 0;
        foreach (var (media, bus) in drives)
        {
            if (NotFittedBuses.Contains(bus)) continue;
            if (media == 3) spinning++;
            else solid++;
        }
        return (solid, spinning);
    }

    /// <summary>One detection pass. Never throws: an unanswered question leaves its field at a sensible default.</summary>
    public static InventoryFacts Detect()
    {
        var battery = Win32.ReadBatteryState();
        var enclosure = Wmi.ReadOr(@"\\.\root\cimv2", "SELECT ChassisTypes FROM Win32_SystemEnclosure", rows =>
        {
            foreach (var row in rows)
            {
                if (row["ChassisTypes"] is ushort[] { Length: > 0 } types) return (int?)types[0];
            }
            return null;
        }, null);
        var chassis = ChassisFrom(enclosure, battery?.OwnBattery ?? false);

        var cpuName = Wmi.ReadOr(@"\\.\root\cimv2", "SELECT Name FROM Win32_Processor", rows =>
            rows.Count > 0 ? (rows[0]["Name"] as string)?.Trim() : null, null);

        var gpuName = Wmi.ReadOr(@"\\.\root\cimv2", "SELECT Name, AdapterCompatibility FROM Win32_VideoController", rows =>
        {
            string? fallback = null;
            foreach (var row in rows)
            {
                var name = (row["Name"] as string)?.Trim();
                var vendor = row["AdapterCompatibility"] as string ?? "";
                if (vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || vendor.Contains("Advanced Micro", StringComparison.OrdinalIgnoreCase))
                {
                    return name;      // prefer the discrete card over the integrated one
                }
                fallback ??= name;
            }
            return fallback;
        }, null);

        var (sticks, ddr5) = Wmi.ReadOr(@"\\.\root\cimv2", "SELECT SMBIOSMemoryType FROM Win32_PhysicalMemory", rows =>
        {
            var count = 0;
            var isDdr5 = false;
            foreach (var row in rows)
            {
                count++;
                // 34 is DDR5 in the SMBIOS memory-type table; 26 is DDR4.
                if (row["SMBIOSMemoryType"] is not null && Convert.ToInt32(row["SMBIOSMemoryType"]) >= 34) isDdr5 = true;
            }
            return (Math.Max(count, 1), isDdr5);
        }, (1, false));

        var (ssd, hdd) = Wmi.ReadOr(@"\\.\root\microsoft\windows\storage", "SELECT MediaType, BusType FROM MSFT_PhysicalDisk", rows =>
            CountDrives(rows.Select(row => (
                row["MediaType"] is null ? 0 : Convert.ToInt32(row["MediaType"]),
                row["BusType"] is null ? 0 : Convert.ToInt32(row["BusType"])))), (1, 0));

        var (monitors, diagonal) = Wmi.ReadOr(@"\\.\root\wmi", "SELECT MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams", rows =>
        {
            var count = 0;
            double inches = 0;
            foreach (var row in rows)
            {
                count++;
                if (inches > 0) continue;
                var width = Convert.ToDouble(row["MaxHorizontalImageSize"]);
                var height = Convert.ToDouble(row["MaxVerticalImageSize"]);
                if (width > 0 && height > 0) inches = Math.Round(Math.Sqrt(width * width + height * height) / 2.54, 1);
            }
            return (Math.Max(count, 1), inches);
        }, (1, 0d));

        return new InventoryFacts(
            chassis, cpuName, gpuName,
            sticks, ddr5,
            Math.Max(ssd, 0), Math.Max(hdd, 0),
            chassis == ChassisKind.Laptop ? diagonal : 0,
            monitors);
    }
}
