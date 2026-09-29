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
    /// <summary>A neural processor, inside the package figure (Snapdragon X's <c>npu</c>).</summary>
    Npu,
    /// <summary>The whole platform, display included, on the machines that meter it (Snapdragon X's <c>system</c>).</summary>
    Platform,
}

/// <param name="Name">Counter instance name, e.g. "RAPL_Package0_PKG".</param>
/// <param name="PowerMilliwatts">The rail's average power over the last tick.</param>
/// <param name="EnergyPicowattHours">The rail's monotonic energy counter at the end of that tick.</param>
public readonly record struct Rail(string Name, ulong PowerMilliwatts, ulong EnergyPicowattHours);

/// <param name="PackageW">Whole-processor watts, or null when no package rail exists.</param>
/// <param name="CoresW">Cores watts, already inside PackageW.</param>
/// <param name="IntegratedGpuW">Integrated graphics watts, already inside PackageW.</param>
/// <param name="MemoryW">Memory watts, outside PackageW.</param>
/// <param name="PlatformW">The whole platform's watts, every other rail and the display inside it, or null where no rail
/// meters the platform (every Intel and AMD machine so far).</param>
public readonly record struct EnergyMeterReading(double? PackageW, double? CoresW, double? IntegratedGpuW, double? MemoryW, double? PlatformW = null);

/// <summary>
/// The processor's power rails as Windows publishes them (spec §4). No kernel driver and no elevation: Windows 11's
/// inbox power-management driver fills the "Energy Meter" performance counters from the processor's own meters.
/// Each rail's energy counter, in picowatt-hours, is read directly through the performance-counter API in well
/// under a millisecond, and watts are the energy used since the previous read over the time between reads, so every
/// figure is exact over its tick rather than over some window of Windows' choosing.
/// A machine with no rails reports nothing, never zero, because a zero is indistinguishable from an idle chip, and so
/// does one whose meter has no package rail: its other rails can't stand in for the processor's figure. A Snapdragon X
/// meter is read through <see cref="QualcommRails"/>, whose table says which rails nest inside which, and whose system rail
/// measures the whole platform; one with that rail and no processor figure is still worth reading for it.
/// Single-threaded: the sampling loop owns it.
/// </summary>
public sealed class EnergyMeter : IDisposable
{
    private const string Category = "Energy Meter";
    private const string EnergyCounter = "Energy";
    private const string NoRails = "this machine publishes no processor power rails";
    private const string NoPackageRail = "this machine's energy meter has no processor package rail";

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
                Unavailable = NoRails;
                return;
            }

            var instances = new PerformanceCounterCategory(Category).GetInstanceNames();
            Unavailable = WhyUnavailable(instances);
            if (Unavailable is not null) return;

            foreach (var instance in instances)
            {
                if (Classify(instance) == RailKind.Ignored) continue;
                _rails.Add((instance, new PerformanceCounter(Category, EnergyCounter, instance, readOnly: true)));
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

    /// <summary>True when a package rail or a whole-platform rail exists, so the processor's own watts or the platform's
    /// can be read; the core, graphics and memory rails beside them are read too.</summary>
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
    public static EnergyMeterReading Summarise(IReadOnlyList<Rail> rails) => Summarise(rails, QualcommRails.Table);

    /// <summary>
    /// Groups rails by kind and sums each kind, so a two-socket machine reports one package figure. Intel's and AMD's rails
    /// are known by name, and their package figure is the package rails alone. Snapdragon rails are looked up in
    /// <paramref name="table"/>: the package figure is every processor rail (soc, clusters, graphics, NPU) that no other
    /// processor rail present holds, so the soc rail alone where it exists, and its parts once each where it does not; and
    /// the platform figure is the outermost platform rail. Without the soc rail or a CPU cluster there is no processor
    /// figure, since graphics or NPU watts alone are not the processor's.
    /// </summary>
    public static EnergyMeterReading Summarise(IReadOnlyList<Rail> rails, IReadOnlyList<PlatformRail> table)
    {
        double? package = null, cores = null, igpu = null, memory = null, platform = null;
        var tabled = new List<(PlatformRail Row, double Watts)>();
        foreach (var rail in rails)
        {
            var watts = rail.PowerMilliwatts / 1000.0;
            var vendor = VendorKind(rail.Name);
            if (vendor != RailKind.Ignored) Add(vendor, watts);
            else if (QualcommRails.Find(rail.Name, table) is { } row) tabled.Add((row, watts));
        }
        if (tabled.Count == 0) return new EnergyMeterReading(package, cores, igpu, memory);

        var present = tabled.Select(entry => entry.Row.Key).ToHashSet(StringComparer.Ordinal);
        var anchored = tabled.Exists(entry => entry.Row.Kind is RailKind.Package or RailKind.Cores);
        double? processor = null;
        foreach (var (row, watts) in tabled)
        {
            // The parts are reported as they are; the package figure is made below, from the outermost processor rails.
            if (row.Kind is RailKind.Cores or RailKind.IntegratedGpu or RailKind.Memory) Add(row.Kind, watts);
            if (anchored && IsProcessor(row.Kind) && !QualcommRails.InsideAnother(row, present, table, IsProcessor))
            {
                processor = (processor ?? 0) + watts;
            }
            if (row.Kind == RailKind.Platform && !QualcommRails.InsideAnother(row, present, table, static kind => kind == RailKind.Platform))
            {
                platform = (platform ?? 0) + watts;
            }
        }
        if (processor is { } fromTable) package = (package ?? 0) + fromTable;
        return new EnergyMeterReading(package, cores, igpu, memory, platform);

        void Add(RailKind kind, double watts)
        {
            switch (kind)
            {
                case RailKind.Package: package = (package ?? 0) + watts; break;
                case RailKind.Cores: cores = (cores ?? 0) + watts; break;
                case RailKind.IntegratedGpu: igpu = (igpu ?? 0) + watts; break;
                case RailKind.Memory: memory = (memory ?? 0) + watts; break;
            }
        }

        static bool IsProcessor(RailKind kind) => kind is RailKind.Package or RailKind.Cores or RailKind.IntegratedGpu or RailKind.Npu;
    }

    /// <summary>Why a meter with these counter instances can't give the processor's own watts or the whole platform's, or
    /// null when it can. The processor's figure comes from a package rail (on Snapdragon, the soc rail or the CPU clusters):
    /// core, graphics and memory rails without one would leave the processor modelled while the meter's presence claimed
    /// it measured.</summary>
    public static string? WhyUnavailable(IEnumerable<string> instances)
    {
        var known = instances.Where(name => Classify(name) != RailKind.Ignored).ToList();
        if (known.Count == 0) return NoRails;
        var reading = Summarise([.. known.Select(name => new Rail(name, 0, 0))]);
        return reading.PackageW is not null || reading.PlatformW is not null ? null : NoPackageRail;
    }

    /// <summary>Maps a counter instance name to what it measures. Intel names are RAPL_*; AMD publishes prose names;
    /// Snapdragon's short lowercase names are looked up in <see cref="QualcommRails.Table"/>.</summary>
    public static RailKind Classify(string name)
    {
        var vendor = VendorKind(name);
        return vendor != RailKind.Ignored ? vendor : QualcommRails.Find(name, QualcommRails.Table)?.Kind ?? RailKind.Ignored;
    }

    private static RailKind VendorKind(string name)
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
