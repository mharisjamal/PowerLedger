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

    /// <summary>The enclosure types SMBIOS defines that say what the machine is: 1 is "Other", 2 is "Unknown", and
    /// nothing past 36, "Stick PC", is defined.</summary>
    private const int FirstTellingEnclosure = 3;
    private const int LastTellingEnclosure = 36;

    /// <summary>STORAGE_BUS_TYPE values for drives that are not part of the machine. FireWire, Fibre Channel, USB,
    /// iSCSI and SD cards are external or remote; virtual, file-backed and Storage Spaces disks are not drives at all.</summary>
    private static readonly HashSet<int> NotFittedBuses = [4, 6, 7, 9, 12, 14, 15, 16];

    /// <summary>D3DKMDT_VIDEO_OUTPUT_TECHNOLOGY values for a panel built into the machine: LVDS, embedded
    /// DisplayPort, embedded UDI and the generic "internal".</summary>
    private static readonly HashSet<uint> BuiltInConnections = [6, 11, 13, 0x80000000];

    /// <summary>A known enclosure type settles it: a portable one is a laptop, and any other is a desktop even when a
    /// battery shows, because the battery on a desktop is a UPS whose drain covers everything plugged into it. Only an
    /// unknown enclosure (none, "Other", "Unknown", or a type SMBIOS doesn't define) falls back to the battery.</summary>
    /// <param name="batteryPresent">True when Windows shows a battery it doesn't mark short-term. It marks some UPS units
    /// so, but not all.</param>
    public static ChassisKind ChassisFrom(int? enclosureType, bool batteryPresent)
    {
        if (enclosureType is { } type && type is >= FirstTellingEnclosure and <= LastTellingEnclosure)
        {
            return PortableEnclosures.Contains(type) ? ChassisKind.Laptop : ChassisKind.Desktop;
        }
        return batteryPresent ? ChassisKind.Laptop : ChassisKind.Desktop;
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

    /// <summary>The built-in panel's diagonal in inches, or 0 when no built-in panel shows. The panel is found by how
    /// it is connected, never by where Windows lists it, so a docked laptop reports its own panel, not the desk monitor.</summary>
    /// <param name="connections">Connection type by monitor instance name.</param>
    /// <param name="sizes">Maximum image size in centimetres by monitor instance name.</param>
    public static double BuiltInDiagonal(
        IReadOnlyDictionary<string, uint> connections, IEnumerable<(string Instance, double WidthCm, double HeightCm)> sizes)
    {
        foreach (var (instance, width, height) in sizes)
        {
            if (width <= 0 || height <= 0) continue;
            if (!connections.TryGetValue(instance, out var connection) || !BuiltInConnections.Contains(connection)) continue;
            return Math.Round(Math.Sqrt(width * width + height * height) / 2.54, 1);
        }
        return 0;
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
            DiscreteGpu.PreferredName(rows.Select(row => ((row["Name"] as string)?.Trim(), row["AdapterCompatibility"] as string))), null);

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

        var connections = Wmi.ReadOr(@"\\.\root\wmi", "SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams", rows =>
        {
            var byInstance = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (row["InstanceName"] is string name && row["VideoOutputTechnology"] is uint connection) byInstance[name] = connection;
            }
            return byInstance;
        }, []);

        var (monitors, diagonal) = Wmi.ReadOr(@"\\.\root\wmi", "SELECT InstanceName, Active, MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams", rows =>
        (
            Math.Max(rows.Count(row => row["Active"] is true), 1),
            BuiltInDiagonal(connections, rows.Select(row => (
                row["InstanceName"] as string ?? "",
                Convert.ToDouble(row["MaxHorizontalImageSize"]),
                Convert.ToDouble(row["MaxVerticalImageSize"]))))
        ), (1, 0d));

        return new InventoryFacts(
            chassis, cpuName, gpuName,
            sticks, ddr5,
            Math.Max(ssd, 0), Math.Max(hdd, 0),
            diagonal,
            monitors);
    }
}
