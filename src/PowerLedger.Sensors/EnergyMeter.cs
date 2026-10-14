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
/// Every rail's energy counter, in picowatt-hours, and the meter's own clock beside it, in milliseconds, are read
/// together through one Performance Data Helper query, one collection a tick. Watts are the energy used since the previous
/// reading over the time the meter's clock says passed, so every figure is exact over the meter's own interval rather than
/// over the service's. A meter that updates more slowly than the service reads, as Snapdragon's firmware does about once a
/// second, would otherwise give alternating noughts and doubles; a tick on which its clock did not move, or no rail's energy
/// did, holds the last figure, and the next one that moved spans the whole interval. Without a clock counter the service's
/// own is used.
/// A machine with no rails reports nothing, never zero, because a zero is indistinguishable from an idle chip, and so
/// does one whose meter has no package rail: its other rails can't stand in for the processor's figure. A Snapdragon X
/// meter is read through <see cref="QualcommRails"/>, whose table says which rails nest inside which, and whose system rail
/// measures the whole platform; one with that rail and no processor figure is still worth reading for it.
/// Single-threaded: the sampling loop owns it.
/// </summary>
public sealed class EnergyMeter : IDisposable
{
    private const string Category = "Energy Meter";
    private const string NoRails = "this machine publishes no processor power rails";
    private const string NoPackageRail = "this machine's energy meter has no processor package rail";

    /// <summary>1 pWh = 1e-12 Wh = 3.6e-9 J. Confirmed on real hardware against Windows' own milliwatt figure.</summary>
    public const double JoulesPerPicowattHour = 3.6e-9;

    private readonly List<Rail> _rails = [];
    private IRailCounters? _counters;
    private Func<long> _clock = static () => Environment.TickCount64;
    private string[] _names = [];
    private bool[] _meterClock = [];
    private RailCount?[] _now = [];
    private (long Energy, long Milliseconds)?[] _before = [];
    private double?[] _held = [];

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

            string[] rails = [.. instances.Where(instance => Classify(instance) != RailKind.Ignored)];
            Open(rails, new PdhRailCounters(Category, rails), static () => Environment.TickCount64);
            Available = true;
        }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException or Win32Exception)
        {
            Dispose();
            Available = false;
            Unavailable = error.Message;
        }
    }

    /// <summary>Test seam: these rails, read from any counters, with any clock for a meter that has none of its own.</summary>
    /// <param name="clockMilliseconds">The service's own clock, in milliseconds.</param>
    internal EnergyMeter(IReadOnlyList<string> rails, IRailCounters counters, Func<long> clockMilliseconds)
    {
        Open(rails, counters, clockMilliseconds);
        Available = true;
    }

    /// <summary>True when a package rail or a whole-platform rail exists, so the processor's own watts or the platform's
    /// can be read; the core, graphics and memory rails beside them are read too.</summary>
    public bool Available { get; }

    /// <summary>Why there is nothing to read, for the status screen; null when the meter works.</summary>
    public string? Unavailable { get; }

    /// <summary>Average watts per kind of rail over the meter's last interval. A rail whose counter or clock went backwards
    /// reports nothing and starts again from there; one Windows gave nothing for this tick reports nothing this tick.</summary>
    public EnergyMeterReading Read()
    {
        if (!Available || _counters is null) return default;

        var wall = Collect();
        // Wattop's rule: a tick on which no rail's energy moved is one the meter has not updated, whatever its clock says.
        var moved = false;
        for (var rail = 0; rail < _names.Length; rail++)
        {
            if (_now[rail] is { } count && _before[rail] is { } before && count.Energy != before.Energy) moved = true;
        }

        _rails.Clear();
        for (var rail = 0; rail < _names.Length; rail++)
        {
            if (_now[rail] is not { } count || TimeOf(rail, count, wall) is not { } at) continue;
            if (_before[rail] is not { } before)
            {
                _before[rail] = (count.Energy, at);
                continue;
            }

            var energy = count.Energy - before.Energy;
            var elapsed = at - before.Milliseconds;
            if (energy < 0 || elapsed < 0)
            {
                _before[rail] = (count.Energy, at);
                _held[rail] = null;
                continue;
            }
            if (elapsed == 0 || !moved)
            {
                // Nothing new from the meter: the last figure holds, and the interval runs on to the next update.
                if (_held[rail] is { } held) Add(rail, held, before.Energy);
                continue;
            }

            var watts = energy * JoulesPerPicowattHour / (elapsed / 1000.0);
            _before[rail] = (count.Energy, at);
            _held[rail] = watts;
            Add(rail, watts, count.Energy);
        }
        return Summarise(_rails);
    }

    private void Open(IReadOnlyList<string> rails, IRailCounters counters, Func<long> clockMilliseconds)
    {
        // Kept first, so a first reading that throws still has the counters closed by Dispose.
        _counters = counters;
        _clock = clockMilliseconds;
        _names = [.. rails];
        _now = new RailCount?[_names.Length];
        _before = new (long, long)?[_names.Length];
        _held = new double?[_names.Length];

        // Take the first reading now, so the first real tick already has an interval to measure. A rail whose meter gives
        // its clock here is timed by that clock from now on.
        var wall = Collect();
        _meterClock = [.. _now.Select(count => count?.Milliseconds is not null)];
        for (var rail = 0; rail < _names.Length; rail++)
        {
            if (_now[rail] is { } count && TimeOf(rail, count, wall) is { } at) _before[rail] = (count.Energy, at);
        }
    }

    /// <summary>Reads every rail in one collection and gives the service's own clock as it was then.</summary>
    private long Collect()
    {
        _counters!.Read(_now);
        return _clock();
    }

    /// <summary>When the rail's count was taken, by the meter's clock where it has one; null when that clock gave nothing.</summary>
    private long? TimeOf(int rail, RailCount count, long wall) => _meterClock[rail] ? count.Milliseconds : wall;

    private void Add(int rail, double watts, long energy)
        => _rails.Add(new Rail(_names[rail], (ulong)Math.Round(watts * 1000), (ulong)energy));

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

    public void Dispose()
    {
        _counters?.Dispose();
        _counters = null;
    }
}

/// <summary>One rail's counts as one collection found them.</summary>
/// <param name="Energy">The rail's energy counter, in picowatt-hours.</param>
/// <param name="Milliseconds">The meter's own clock when it took that count, in milliseconds since boot; null where the meter
/// publishes no clock.</param>
internal readonly record struct RailCount(long Energy, long? Milliseconds);

/// <summary>A meter's rails, read together, so a test can stand in for Windows.</summary>
internal interface IRailCounters : IDisposable
{
    /// <summary>Takes one reading of every rail at once, in the order they were opened, into <paramref name="counts"/>: null
    /// for a rail Windows gave nothing for. Throws when Windows will not answer at all.</summary>
    void Read(RailCount?[] counts);
}

/// <summary>The real counters: each rail's Energy and Time in one PDH query.</summary>
internal sealed class PdhRailCounters : IRailCounters
{
    private readonly PdhQuery _query = new();
    private readonly int[] _energy;
    private readonly int?[] _time;

    public PdhRailCounters(string category, IReadOnlyList<string> rails)
    {
        try
        {
            _energy = new int[rails.Count];
            _time = new int?[rails.Count];
            for (var rail = 0; rail < rails.Count; rail++)
            {
                _energy[rail] = _query.TryAdd($@"\{category}({rails[rail]})\Energy")
                    ?? throw new InvalidOperationException($"Windows has no energy counter for the rail {rails[rail]}");
                _time[rail] = _query.TryAdd($@"\{category}({rails[rail]})\Time");
            }
        }
        catch
        {
            _query.Dispose();
            throw;
        }
    }

    public void Read(RailCount?[] counts)
    {
        _query.Collect();
        for (var rail = 0; rail < _energy.Length; rail++)
        {
            counts[rail] = _query.Raw(_energy[rail]) is { } energy
                ? new RailCount(energy, _time[rail] is { } time ? _query.Raw(time) : null)
                : null;
        }
    }

    public void Dispose() => _query.Dispose();
}
