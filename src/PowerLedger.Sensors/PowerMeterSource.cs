using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using PowerLedger.Contracts;

namespace PowerLedger.Sensors;

/// <summary>
/// Windows' own power meters: the "Power Meter" performance counters, which Windows' power metering interface fills from
/// each power meter device it has. Workstations and servers with an ACPI power meter (ACPI000D) report the platform's input
/// power there, the whole machine's draw at its plug, in milliwatts. No driver of ours and no elevation.
///
/// The counter instances are named only "Power Meter (0)", "Power Meter (1)" and on, which says nothing of what each
/// measures, and a laptop's battery is a power meter too: the owner's laptop has one instance, reading nought on mains,
/// whose device is its battery (ACPI PNP0C0A). What each measures is therefore asked of WMI (Win32_PowerMeter in
/// root\cimv2\power), whose devices are matched to the instances by their place in Windows' lists. Only a platform meter is
/// read; a battery's is left to the battery source, and meters that cannot be told apart are not read at all, since taking
/// a battery's charging power for the machine's would be worse than a model. With several platform meters the largest is
/// taken, which never counts a meter that sits inside another twice. A reading of nought is no reading.
/// Single-threaded: the sampling loop owns it.
/// </summary>
public sealed partial class PowerMeterSource : ISensorSource
{
    private const string NoMeter = "this PC has no power meter";
    private const string BatteryOnly = "this PC's only power meter is its battery's";
    private const string UnknownKind = "this PC's power meters measure something other than the whole PC";
    private const string NoWmi = "Windows would not say what its power meters measure";
    private const string NoMatch = "Windows lists its power meters in a way PowerLedger can't match up";

    private readonly IPowerMeters _meters;
    private readonly List<string> _read = [];

    /// <summary>Reads the machine's real power meters.</summary>
    public PowerMeterSource() : this(new WindowsPowerMeters())
    {
    }

    /// <summary>Test seam: any set of counters and devices.</summary>
    internal PowerMeterSource(IPowerMeters meters)
    {
        _meters = meters;
        try
        {
            Unavailable = Choose(meters.Instances(), meters.DeviceIds(), _read);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A constructor that threw would stop the whole sensor set from being built.
            _read.Clear();
            Unavailable = error.Message;
        }
        Supported = Unavailable is null;
    }

    public string Name => "power-meter";

    public bool Supported { get; }

    public string? Unavailable { get; }

    public void Contribute(SampleDraft draft)
    {
        if (!Supported || draft.SystemMeterW is not null) return;
        double? largest = null;
        foreach (var instance in _read)
        {
            try
            {
                var milliwatts = _meters.Milliwatts(instance);
                if (milliwatts > 0 && milliwatts / 1000.0 > (largest ?? 0)) largest = milliwatts / 1000.0;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // One counter that went away costs its own reading; the others may still answer.
            }
        }
        if (largest is not { } watts) return;
        draft.SystemMeterW = watts;
        draft.SystemMeter = SystemMeterKind.PowerMeter;
        draft.SystemMeterName = "ACPI power meter";
    }

    public void Dispose() => _meters.Dispose();

    /// <summary>
    /// Picks the counter instances that measure the whole machine into <paramref name="read"/>, or says why there are none.
    /// The instances are put in the order of the number in their names, and the devices WMI lists are taken in its order.
    /// Where every device is a platform meter no matching is needed; otherwise the two lists must be as long as each other.
    /// </summary>
    internal static string? Choose(IReadOnlyList<string> instances, IReadOnlyList<string>? devices, List<string> read)
    {
        var meters = instances.Where(name => !name.Equals("_Total", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Number).ThenBy(name => name, StringComparer.Ordinal).ToList();
        if (meters.Count == 0) return NoMeter;
        if (devices is null) return NoWmi;
        var platform = devices.Select(IsPlatform).ToList();
        if (!platform.Contains(true)) return devices.Count > 0 && devices.All(IsBattery) ? BatteryOnly : devices.Count == 0 ? NoWmi : UnknownKind;
        if (platform.TrueForAll(yes => yes))
        {
            read.AddRange(meters);
            return null;
        }
        if (devices.Count != meters.Count) return NoMatch;
        for (var index = 0; index < meters.Count; index++)
        {
            if (platform[index]) read.Add(meters[index]);
        }
        return null;
    }

    /// <summary>An ACPI power meter device, which measures the platform's input power (ACPI 4.0's only measurement type).</summary>
    private static bool IsPlatform(string device) => device.Contains("ACPI000D", StringComparison.OrdinalIgnoreCase);

    /// <summary>An ACPI control method battery, whose meter measures the battery's own flow.</summary>
    private static bool IsBattery(string device) => device.Contains("PNP0C0A", StringComparison.OrdinalIgnoreCase);

    private static int Number(string instance)
        => InstanceNumber().Match(instance) is { Success: true } match && int.TryParse(match.Groups[1].ValueSpan, out var number) ? number : int.MaxValue;

    [GeneratedRegex(@"\((\d+)\)\s*$")]
    private static partial Regex InstanceNumber();
}

/// <summary>Windows' power meters, one method per question, so a test can stand in for them.</summary>
internal interface IPowerMeters : IDisposable
{
    /// <summary>The "Power Meter" counter instances, _Total among them; none where the category is missing.</summary>
    IReadOnlyList<string> Instances();

    /// <summary>The device path of each power meter device, in WMI's order; null when WMI cannot answer.</summary>
    IReadOnlyList<string>? DeviceIds();

    /// <summary>What the instance's Power counter reads now, in milliwatts.</summary>
    long Milliwatts(string instance);
}

/// <summary>The real counters and WMI class. A counter is opened the first time it is read and kept.</summary>
internal sealed class WindowsPowerMeters : IPowerMeters
{
    private const string Category = "Power Meter";
    private const string PowerCounter = "Power";
    private readonly Dictionary<string, PerformanceCounter> _counters = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Instances()
    {
        try
        {
            return PerformanceCounterCategory.Exists(Category) ? new PerformanceCounterCategory(Category).GetInstanceNames() : [];
        }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException or Win32Exception)
        {
            return [];
        }
    }

    public IReadOnlyList<string>? DeviceIds()
        => Wmi.ReadOrNull<IReadOnlyList<string>>(@"root\cimv2\power", "SELECT DeviceID FROM Win32_PowerMeter",
            rows => [.. rows.Select(row => row["DeviceID"] as string ?? "")]);

    public long Milliwatts(string instance)
    {
        if (!_counters.TryGetValue(instance, out var counter))
        {
            counter = new PerformanceCounter(Category, PowerCounter, instance, readOnly: true);
            _counters[instance] = counter;
        }
        return counter.RawValue;
    }

    public void Dispose()
    {
        foreach (var counter in _counters.Values) counter.Dispose();
        _counters.Clear();
    }
}
