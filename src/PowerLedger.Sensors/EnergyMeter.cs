using System.ComponentModel;
using System.Diagnostics;

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
/// <param name="PowerMilliwatts">The rail's average power over the last tick.</param>
/// <param name="EnergyPicowattHours">The rail's monotonic energy counter at the end of that tick.</param>
public readonly record struct Rail(string Name, ulong PowerMilliwatts, ulong EnergyPicowattHours);

/// <param name="PackageW">Whole-processor watts, or null when no package rail exists.</param>
/// <param name="CoresW">Cores watts, already inside PackageW.</param>
/// <param name="IntegratedGpuW">Integrated graphics watts, already inside PackageW.</param>
/// <param name="MemoryW">Memory watts, outside PackageW.</param>
public readonly record struct EnergyMeterReading(double? PackageW, double? CoresW, double? IntegratedGpuW, double? MemoryW);

/// <summary>
/// The processor's power rails as Windows publishes them (spec §4). No kernel driver and no elevation: Windows 11's
/// inbox power-management driver fills the "Energy Meter" performance counters from the processor's own meters.
/// Each rail's energy counter, in picowatt-hours, is read directly through the performance-counter API in well
/// under a millisecond, and watts are the energy used since the previous read over the time between reads, so every
/// figure is exact over its tick rather than over some window of Windows' choosing.
/// A machine with no rails reports nothing, never zero, because a zero is indistinguishable from an idle chip.
/// Single-threaded: the sampling loop owns it.
/// </summary>
public sealed class EnergyMeter : IDisposable
{
    private const string Category = "Energy Meter";
    private const string EnergyCounter = "Energy";

    /// <summary>1 pWh = 1e-12 Wh = 3.6e-9 J. Confirmed on real hardware against Windows' own milliwatt figure.</summary>
    public const double JoulesPerPicowattHour = 3.6e-9;

    private readonly List<(string Name, PerformanceCounter Energy)> _rails = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Dictionary<string, long> _previousEnergy = [];
    private TimeSpan _previousAt;

    public EnergyMeter()
    {
        try
        {
            if (!PerformanceCounterCategory.Exists(Category))
            {
                Unavailable = "this machine publishes no processor power rails";
                return;
            }

            foreach (var instance in new PerformanceCounterCategory(Category).GetInstanceNames())
            {
                if (Classify(instance) == RailKind.Ignored) continue;
                _rails.Add((instance, new PerformanceCounter(Category, EnergyCounter, instance, readOnly: true)));
            }

            if (_rails.Count == 0)
            {
                Unavailable = "this machine publishes no processor power rails";
                return;
            }

            // Take the first reading now, so the first real tick already has an interval to measure.
            _previousEnergy = Snapshot();
            _previousAt = _clock.Elapsed;
            Available = true;
        }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException or Win32Exception)
        {
            Dispose();
            Available = false;
            Unavailable = error.Message;
        }
    }

    /// <summary>True when at least one usable rail exists.</summary>
    public bool Available { get; }

    /// <summary>Why there is nothing to read, for the status screen; null when the meter works.</summary>
    public string? Unavailable { get; }

    /// <summary>Average watts per kind of rail since the previous call. A rail whose counter went backwards reports nothing.</summary>
    public EnergyMeterReading Read()
    {
        if (!Available) return default;

        var now = _clock.Elapsed;
        var seconds = (now - _previousAt).TotalSeconds;
        var energy = Snapshot();
        var previous = _previousEnergy;
        _previousEnergy = energy;
        _previousAt = now;
        if (seconds <= 0) return default;

        var rails = new List<Rail>(energy.Count);
        foreach (var (name, value) in energy)
        {
            if (!previous.TryGetValue(name, out var before) || value < before) continue;
            var watts = (value - before) * JoulesPerPicowattHour / seconds;
            rails.Add(new Rail(name, (ulong)Math.Round(watts * 1000), (ulong)value));
        }
        return Summarise(rails);
    }

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

    private Dictionary<string, long> Snapshot()
    {
        var snapshot = new Dictionary<string, long>(_rails.Count);
        foreach (var (name, counter) in _rails) snapshot[name] = counter.RawValue;
        return snapshot;
    }

    public void Dispose()
    {
        foreach (var (_, counter) in _rails) counter.Dispose();
        _rails.Clear();
    }
}
