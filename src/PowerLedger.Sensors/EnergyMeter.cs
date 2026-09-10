using System.Management;

namespace PowerLedger.Sensors;

/// <summary>What a named power rail measures.</summary>
public enum RailKind
{
    /// <summary>A roll-up or an unrelated counter instance.</summary>
    Ignored,
    /// <summary>Whole-processor package power, the figure the model wants.</summary>
    Package,
    /// <summary>The cores alone, inside the package figure.</summary>
    Cores,
    /// <summary>Integrated graphics, inside the package figure.</summary>
    IntegratedGpu,
    /// <summary>Attached memory, outside the package figure on the processors that report it.</summary>
    Memory,
}

/// <param name="Name">Counter instance name, e.g. "RAPL_Package0_PKG".</param>
/// <param name="PowerMilliwatts">The rail's current power. The unit is undocumented; a hardware test pins it.</param>
/// <param name="EnergyPicowattHours">Monotonic energy counter, used only to cross-check the power unit.</param>
public readonly record struct Rail(string Name, ulong PowerMilliwatts, ulong EnergyPicowattHours);

/// <param name="PackageW">Whole-processor watts, or null when no package rail exists.</param>
/// <param name="CoresW">Cores watts, already inside PackageW.</param>
/// <param name="IntegratedGpuW">Integrated graphics watts, already inside PackageW.</param>
/// <param name="MemoryW">Memory watts, outside PackageW.</param>
public readonly record struct EnergyMeterReading(double? PackageW, double? CoresW, double? IntegratedGpuW, double? MemoryW);

/// <summary>
/// The processor's power rails as Windows publishes them (spec §4). No kernel driver and no elevation:
/// Windows 11's inbox power-management driver populates these counters from the processor's own energy meters.
/// A machine with no rails reports nothing, never zero, because a zero is indistinguishable from an idle chip.
/// </summary>
public sealed class EnergyMeter : IDisposable
{
    private const string Query = "SELECT Name, Power, Energy FROM Win32_PerfFormattedData_PowerMeterCounter_EnergyMeter";

    private readonly ManagementObjectSearcher? _searcher;

    public EnergyMeter()
    {
        try
        {
            _searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\cimv2"), new ObjectQuery(Query));
            Available = ReadRails().Any(r => Classify(r.Name) != RailKind.Ignored);
            if (!Available) Unavailable = "this machine publishes no processor power rails";
        }
        catch (ManagementException error)
        {
            Available = false;
            Unavailable = error.Message;
        }
    }

    /// <summary>True when at least one usable rail exists.</summary>
    public bool Available { get; }

    /// <summary>Why there is nothing to read, for the status screen; null when the meter works.</summary>
    public string? Unavailable { get; }

    /// <summary>Every rail instance, unclassified and unsummed.</summary>
    public IReadOnlyList<Rail> ReadRails()
    {
        if (_searcher is null) return [];
        var rails = new List<Rail>();
        using var results = _searcher.Get();
        foreach (var row in results)
        {
            using var instance = (ManagementObject)row;
            var name = instance["Name"] as string;
            if (string.IsNullOrEmpty(name)) continue;
            rails.Add(new Rail(name, ToUInt64(instance["Power"]), ToUInt64(instance["Energy"])));
        }
        return rails;
    }

    /// <summary>One reading with the rails grouped by what they measure.</summary>
    public EnergyMeterReading Read() => Summarise(ReadRails());

    /// <summary>Groups rails by kind and sums each kind, so a two-socket machine reports one package figure.</summary>
    public static EnergyMeterReading Summarise(IReadOnlyList<Rail> rails)
    {
        double? package = null, cores = null, igpu = null, memory = null;
        foreach (var rail in rails)
        {
            var watts = rail.PowerMilliwatts / 1000.0;
            switch (Classify(rail.Name))
            {
                case RailKind.Package: package = (package ?? 0) + watts; break;
                case RailKind.Cores: cores = (cores ?? 0) + watts; break;
                case RailKind.IntegratedGpu: igpu = (igpu ?? 0) + watts; break;
                case RailKind.Memory: memory = (memory ?? 0) + watts; break;
            }
        }
        return new EnergyMeterReading(package, cores, igpu, memory);
    }

    /// <summary>Maps a counter instance name to what it measures. Intel names are RAPL_*; AMD publishes prose names.</summary>
    public static RailKind Classify(string name)
    {
        if (name.EndsWith("_PKG", StringComparison.OrdinalIgnoreCase)) return RailKind.Package;
        if (name.EndsWith("_PP0", StringComparison.OrdinalIgnoreCase)) return RailKind.Cores;
        if (name.EndsWith("_PP1", StringComparison.OrdinalIgnoreCase)) return RailKind.IntegratedGpu;
        if (name.EndsWith("_DRAM", StringComparison.OrdinalIgnoreCase)) return RailKind.Memory;
        if (name.Contains("Socket Energy", StringComparison.OrdinalIgnoreCase)) return RailKind.Package;
        if (name.Contains("Apu Energy", StringComparison.OrdinalIgnoreCase)) return RailKind.IntegratedGpu;
        if (name.Contains("VDDCR_VDD", StringComparison.OrdinalIgnoreCase)) return RailKind.Cores;
        return RailKind.Ignored;
    }

    private static ulong ToUInt64(object? value) => value is null ? 0 : Convert.ToUInt64(value);

    public void Dispose() => _searcher?.Dispose();
}
