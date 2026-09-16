using System.Management;
using PowerLedger.Contracts;

namespace PowerLedger.Sensors;

/// <summary>An external monitor as WMI describes it (spec §4).</summary>
/// <param name="Instance">Windows' device instance in the form <c>MonitorKeys.FromInstanceName</c> gives, e.g.
/// <c>DISPLAY\DELA0B1\5&amp;2F5A1B&amp;0&amp;UID4353</c>.</param>
/// <param name="Key">What the user's choices for this monitor are kept under: maker, product code and serial number when
/// the serial number tells the monitor apart, otherwise <paramref name="Instance"/> (see
/// <c>MonitorInventory.From</c>).</param>
/// <param name="Maker">The maker's PNP id, e.g. "DEL".</param>
/// <param name="ProductCode">The maker's product code, e.g. "A0B1".</param>
/// <param name="Name">The name the monitor gives itself, e.g. "DELL U2723QE"; empty when it gives none.</param>
/// <param name="Inches">The diagonal, snapped to a common size (see <see cref="MonitorInventory.Diagonal"/>); 0 when the
/// monitor gives no size, or one under ten inches, which is an aspect ratio or nonsense.</param>
/// <param name="Width">The native resolution: the mode the monitor prefers, or the one last read for its instance when a
/// read gives none; 0 by 0 when none ever was.</param>
public sealed record MonitorFacts(
    string Instance,
    string Key,
    string Maker,
    string ProductCode,
    string Name,
    double Inches,
    int Width,
    int Height);

/// <summary>
/// The external monitors Windows reports (spec §4), from four WMI classes that each name a display by its device instance.
/// Built-in panels are left out, because the panel model already counts them, and so is any monitor Windows lists but
/// isn't showing anything on. WMI is slow, so this belongs off the sampling tick.
/// </summary>
public static class MonitorInventory
{
    /// <summary>Panel diagonals in common use, in inches, laptop and desk alike. EDID gives a screen's size in whole
    /// centimetres, which alone puts a 15.6-inch panel at 15.3.</summary>
    private static readonly double[] CommonDiagonals =
    [
        11.6, 12.5, 13.3, 13.5, 14, 15.6, 16, 17.3, 18.5, 19, 19.5, 21.5, 22, 23, 23.8, 24, 24.5,
        25, 27, 28, 29, 31.5, 32, 34, 35, 38, 40, 42, 43, 45, 48, 49,
    ];

    /// <summary>How far a diagonal may be from a common size and still be taken for it.</summary>
    private const double SnapInches = 0.5;

    /// <summary>The smallest diagonal an external monitor is believed to give. Some monitors put their aspect ratio (16 by 9)
    /// or nonsense where EDID's size belongs, and a size that small would only mislead the catalogue and the estimate.</summary>
    private const double SmallestExternalInches = 10;

    /// <summary>The fewest characters a serial number that tells a monitor apart is believed to have. Makers that give their
    /// monitors none, or give every unit the same, write "0", "1" or the like.</summary>
    private const int ShortestSerial = 4;

    /// <summary>Every serial key two attached monitors have been found sharing since the service started (see
    /// <see cref="From"/>). Sensor sets are built afresh after a resume or a read that hung, so it is kept here, where it
    /// lasts as long as the service does.</summary>
    private static readonly HashSet<string> SharedKeys = new(StringComparer.Ordinal);

    /// <summary>The native resolution last read for each instance since the service started (see <see cref="From"/>), kept
    /// here for the same reason.</summary>
    private static readonly Dictionary<string, (int Width, int Height)> Resolutions = new(StringComparer.Ordinal);

    /// <summary>The active external monitors, read from WMI, or null when WMI didn't say which are attached (see
    /// <see cref="From"/>). Never throws.</summary>
    public static IReadOnlyList<MonitorFacts>? Read()
    {
        var ids = Wmi.ReadOrNull(@"\\.\root\wmi", "SELECT InstanceName, ManufacturerName, ProductCodeID, SerialNumberID, UserFriendlyName FROM WmiMonitorID", rows =>
        {
            var found = new List<(string, ushort[], ushort[], ushort[], ushort[])>();
            foreach (var row in rows)
            {
                if (row["InstanceName"] is not string name) continue;
                // A monitor that gives no name has a null UserFriendlyName, not an empty one.
                found.Add((name, Codes(row["ManufacturerName"]), Codes(row["ProductCodeID"]), Codes(row["SerialNumberID"]), Codes(row["UserFriendlyName"])));
            }
            return found;
        });

        var connections = Wmi.ReadOrNull(@"\\.\root\wmi", "SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams", rows =>
        {
            var byInstance = new Dictionary<string, uint>();
            foreach (var row in rows)
            {
                if (row["InstanceName"] is string name && row["VideoOutputTechnology"] is uint connection) byInstance[name] = connection;
            }
            return byInstance;
        });

        var sizes = Wmi.ReadOrNull(@"\\.\root\wmi", "SELECT InstanceName, Active, MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams", rows =>
        {
            var found = new List<(string, bool, double, double)>();
            foreach (var row in rows)
            {
                if (row["InstanceName"] is not string name) continue;
                found.Add((name, row["Active"] is true, row["MaxHorizontalImageSize"] is byte width ? width : 0, row["MaxVerticalImageSize"] is byte height ? height : 0));
            }
            return found;
        });

        var nativeModes = Wmi.ReadOrNull(@"\\.\root\wmi", "SELECT InstanceName, MonitorSourceModes, PreferredMonitorSourceModeIndex FROM WmiMonitorListedSupportedSourceModes", rows =>
        {
            var byInstance = new Dictionary<string, (int Width, int Height)>();
            foreach (var row in rows)
            {
                if (row["InstanceName"] is not string name || row["MonitorSourceModes"] is not ManagementBaseObject[] modes) continue;
                try
                {
                    if (row["PreferredMonitorSourceModeIndex"] is ushort preferred && preferred < modes.Length
                        && modes[preferred]["HorizontalActivePixels"] is ushort width && modes[preferred]["VerticalActivePixels"] is ushort height)
                    {
                        byInstance[name] = (width, height);
                    }
                }
                finally
                {
                    // Each mode holds a COM object of its own, as the rows do.
                    foreach (var mode in modes) mode.Dispose();
                }
            }
            return byInstance;
        });

        return From(ids, connections, sizes, nativeModes, SharedKeys, Resolutions);
    }

    /// <summary>The same, from rows already read, with null for a class WMI didn't answer — the part tests drive.</summary>
    /// <param name="sharedKeys">The serial keys two attached monitors were found sharing before, which this adds to. It is
    /// locked while in use, because a sensor set that was abandoned may still be reading.</param>
    /// <param name="resolutions">The native resolution last read for each instance, which this reads and updates (see
    /// <see cref="Resolution"/>).</param>
    /// <returns>Null when WmiMonitorID, WmiMonitorConnectionParams or WmiMonitorBasicDisplayParams didn't answer, because
    /// without any one of them an attached monitor would be left out as if it were unplugged. The native modes give only the
    /// resolutions, and some drivers never answer for them, so without them each monitor keeps the resolution last read for
    /// its instance, or comes back at 0 by 0 when none was.</returns>
    internal static IReadOnlyList<MonitorFacts>? From(
        IReadOnlyList<(string Instance, ushort[] Maker, ushort[] Product, ushort[] Serial, ushort[] Name)>? ids,
        IReadOnlyDictionary<string, uint>? connections,
        IReadOnlyList<(string Instance, bool Active, double WidthCm, double HeightCm)>? sizes,
        IReadOnlyDictionary<string, (int Width, int Height)>? nativeModes,
        HashSet<string> sharedKeys,
        Dictionary<string, (int Width, int Height)> resolutions)
    {
        if (ids is null || connections is null || sizes is null) return null;
        var connectionOf = ByInstance(connections.Select(pair => (pair.Key, pair.Value)));
        var sizeOf = ByInstance(sizes.Select(size => (size.Instance, (size.Active, size.WidthCm, size.HeightCm))));
        var modeOf = ByInstance(nativeModes?.Select(pair => (pair.Key, pair.Value)) ?? []);

        var monitors = new List<MonitorFacts>();
        foreach (var (instanceName, maker, product, serial, name) in ids)
        {
            var instance = MonitorKeys.FromInstanceName(instanceName);
            if (!sizeOf.TryGetValue(instance, out var size) || !size.Active) continue;
            // A monitor without a known connection could be the built-in panel, so only a known external one counts.
            if (!connectionOf.TryGetValue(instance, out var connection) || HardwareInventory.BuiltInConnections.Contains(connection)) continue;

            var makerId = Text(maker);
            var productCode = Text(product);
            var serialNumber = Text(serial);
            var inches = Diagonal(size.WidthCm, size.HeightCm);
            var (width, height) = Resolution(instance, modeOf, resolutions);
            monitors.Add(new MonitorFacts(
                instance,
                // A serial number of fewer than four characters, or of one character repeated, as "0", "0000" and "1111111"
                // are, is a monitor saying it has none, or one its maker gives every unit.
                serialNumber.Length >= ShortestSerial && serialNumber.Any(character => character != serialNumber[0])
                    ? $"{makerId}{productCode}-{serialNumber}"
                    : instance,
                makerId,
                productCode,
                Text(name),
                inches < SmallestExternalInches ? 0 : inches,
                width,
                height));
        }

        // Some makers give every unit the same serial number. One that two attached monitors share tells neither apart,
        // and a shared key would merge their settings, so they are known by their instances instead. They stay so for as
        // long as the service runs, because a twin left on its own once the other is unplugged would otherwise take the
        // shared key, and the choice saved under its instance would no longer apply. Only what this run has seen is known:
        // after a restart with one twin attached, nothing says it has a twin, so it is known by the shared key until the
        // other is attached again.
        lock (sharedKeys)
        {
            sharedKeys.UnionWith(monitors.GroupBy(monitor => monitor.Key).Where(group => group.Count() > 1).Select(group => group.Key));
            return [.. monitors.Select(monitor => sharedKeys.Contains(monitor.Key) ? monitor with { Key = monitor.Instance } : monitor)];
        }
    }

    /// <summary>The native resolution a read gives the monitor at <paramref name="instance"/>, which is remembered for
    /// it; or, when the read gives none, the one last read for that instance; or 0 by 0 when none ever was.</summary>
    /// <remarks>A monitor's native resolution doesn't change, but some drivers fail to answer for the modes now and then, or
    /// leave a monitor out while it wakes, and a monitor read at 0 by 0 would have its figure worked out again without it.
    /// Only a resolution read replaces the one remembered. The memory is locked while in use, because a sensor set that was
    /// abandoned may still be reading.</remarks>
    private static (int Width, int Height) Resolution(
        string instance, Dictionary<string, (int Width, int Height)> read, Dictionary<string, (int Width, int Height)> remembered)
    {
        lock (remembered)
        {
            if (read.TryGetValue(instance, out var resolution)) remembered[instance] = resolution;
            return remembered.GetValueOrDefault(instance);
        }
    }

    /// <summary>A diagonal from EDID's whole centimetres, snapped to the nearest common panel size within 0.5".</summary>
    /// <returns>Inches: the common size, or the diagonal rounded to a tenth when no common size is that close; 0 when the
    /// monitor gives no size, as a projector doesn't.</returns>
    public static double Diagonal(double widthCm, double heightCm)
    {
        if (!(widthCm > 0 && heightCm > 0)) return 0;
        var inches = Math.Sqrt(widthCm * widthCm + heightCm * heightCm) / 2.54;
        var nearest = CommonDiagonals.MinBy(common => Math.Abs(common - inches));
        return Math.Abs(nearest - inches) <= SnapInches ? nearest : Math.Round(inches, 1);
    }

    /// <summary>Rows keyed by their instance as <see cref="MonitorKeys.FromInstanceName"/> gives it, so the classes join whatever their case
    /// or suffix. The first row for an instance wins.</summary>
    private static Dictionary<string, T> ByInstance<T>(IEnumerable<(string Instance, T Value)> rows)
    {
        var byInstance = new Dictionary<string, T>();
        foreach (var (instance, value) in rows) byInstance.TryAdd(MonitorKeys.FromInstanceName(instance), value);
        return byInstance;
    }

    /// <summary>WMI's character codes, or none when it gives null.</summary>
    private static ushort[] Codes(object? value) => value as ushort[] ?? [];

    /// <summary>Text from character codes, which WMI pads with zeros.</summary>
    private static string Text(ushort[] codes) => new string(codes.TakeWhile(code => code != 0).Select(code => (char)code).ToArray()).Trim();
}
