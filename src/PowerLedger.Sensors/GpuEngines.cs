using System.Diagnostics;
using PowerLedger.Core;

namespace PowerLedger.Sensors;

/// <summary>Every GPU engine's running time as Windows counted it at one moment.</summary>
/// <param name="At">When Windows took the counts, in 100 ns units.</param>
/// <param name="RunningTime">Engine time used so far, in 100 ns units, by counter instance. An instance is one process on
/// one engine of one adapter, e.g. "pid_1234_luid_0x00000000_0x0000D1A4_phys_0_eng_0_engtype_3D".</param>
public sealed record EngineSnapshot(long At, IReadOnlyDictionary<string, long> RunningTime);

/// <summary>
/// Windows' "GPU Engine" performance counters: how long each process has kept each engine of each graphics adapter busy.
/// Windows keeps them for every vendor's card, so they give any card's load with no vendor library, and on the
/// development laptop reading them left the switched-off GeForce switched off. A read walks every process on every
/// engine, which costs one to two milliseconds of processor time there, so the caller reads them seconds apart.
/// </summary>
public sealed class GpuEngines
{
    private const string Category = "GPU Engine";

    /// <summary>A 100 ns timer: its raw value is the engine time used so far, its sample time the moment it was read.</summary>
    private const string Counter = "Utilization Percentage";

    private readonly PerformanceCounterCategory _category = new(Category);

    /// <summary>True when Windows publishes the counters, as it has since Windows 10 version 1709.</summary>
    public static bool Published => PerformanceCounterCategory.Exists(Category);

    /// <summary>One snapshot of every instance. Throws what the performance-counter API throws when Windows will not answer.</summary>
    public EngineSnapshot Read()
    {
        var times = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long at = 0;
        if (_category.ReadCategory()[Counter] is { } instances)
        {
            foreach (InstanceData instance in instances.Values)
            {
                times[instance.InstanceName] = instance.RawValue;
                at = instance.Sample.TimeStamp100nSec;
            }
        }
        return new EngineSnapshot(at, times);
    }

    /// <summary>
    /// The adapter's load between two snapshots, 0..1, much as Task Manager reports a GPU: each engine's processes are
    /// added up and capped at the whole engine, and the busiest engine is the load. Only the 3D and compute engines count
    /// (<see cref="IsLoadEngine"/>): the load is turned into watts by the card's rating, which its shaders draw, and video
    /// decode, encode and copy run on small fixed-function engines that draw a few watts at full tilt, so a 4K film would
    /// otherwise count as a game. A process that started or ended in between is left out, and a count that went backwards,
    /// as when Windows reuses a process id, counts as idle.
    /// </summary>
    /// <param name="luid">The adapter as the counters name it; see <see cref="GpuAdapter.CounterLuid"/>.</param>
    /// <param name="everyEngine">Every engine counts, video and copy included: whether the card works at all, for the idle
    /// test, rather than what its shaders draw.</param>
    /// <returns>0 when no process uses the adapter; null when no time passed between the snapshots.</returns>
    public static double? Busiest(EngineSnapshot before, EngineSnapshot after, string luid, bool everyEngine = false)
    {
        var elapsed = after.At - before.At;
        var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, time) in after.RunningTime)
        {
            if (EngineOf(instance, luid) is not { } engine || !(everyEngine || IsLoadEngine(instance))
                || !before.RunningTime.TryGetValue(instance, out var earlier)) continue;
            if (elapsed <= 0) return null;
            engines[engine] = engines.GetValueOrDefault(engine) + Math.Max(0, time - earlier) / (double)elapsed;
        }
        return engines.Count == 0 ? 0 : Math.Min(1, engines.Values.Max());
    }

    /// <summary>
    /// True for an engine that draws the card's power when busy: 3D ("3D", "High Priority 3D", NVIDIA's "Graphics_1") and
    /// compute ("Compute_0", "High Priority Compute", "Cuda"). Video decode, encode and processing, copy, security, overlay
    /// and timer engines are not. An instance that names no engine type counts, since nothing says what it is.
    /// </summary>
    internal static bool IsLoadEngine(string instance)
    {
        var at = instance.LastIndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return true;
        var type = instance.AsSpan(at + "_engtype_".Length);
        return type.IsEmpty
            || type.Contains("3D", StringComparison.OrdinalIgnoreCase)
            || type.Contains("Compute", StringComparison.OrdinalIgnoreCase)
            || type.StartsWith("Cuda", StringComparison.OrdinalIgnoreCase)
            || type.StartsWith("Graphics", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The engine an instance runs on, e.g. "phys_0_eng_3", or null when it belongs to another adapter.</summary>
    internal static string? EngineOf(string instance, string luid)
    {
        var at = instance.IndexOf(luid + "_", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var engine = instance[(at + luid.Length + 1)..];
        var type = engine.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
        return type < 0 ? engine : engine[..type];
    }

    /// <summary>The process an instance counts, from its "pid_1234_" prefix; null when it names none.</summary>
    internal static uint? ProcessOf(string instance)
    {
        if (!instance.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) return null;
        var digits = instance.AsSpan(4);
        var end = digits.IndexOf('_');
        return uint.TryParse(end < 0 ? digits : digits[..end], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid)
            ? pid
            : null;
    }

    /// <summary>The adapter an instance runs on, as the counters name it ("luid_0x00000000_0x0000D1A4"); null when it names
    /// none.</summary>
    internal static string? AdapterOf(string instance)
    {
        var at = instance.IndexOf("luid_0x", StringComparison.OrdinalIgnoreCase);
        const int Length = 26;                                      // luid_0x + 8 hex digits + _0x + 8 hex digits
        return at < 0 || at + Length > instance.Length ? null : instance.Substring(at, Length);
    }

    /// <summary>
    /// Each process's time between two snapshots on the engines of <paramref name="cards"/> and of
    /// <paramref name="processorGraphics"/>, adapters as the counters name them (<see cref="GpuAdapter.CounterLuid"/>), for
    /// the Apps page (Plan AE). An instance new since the first snapshot counts all its time, which began with it; a count
    /// that went backwards, as when Windows gives a process's id to another, counts as none.
    /// </summary>
    internal static GraphicsWork Work(EngineSnapshot before, EngineSnapshot after, IReadOnlyCollection<string> cards, IReadOnlyCollection<string> processorGraphics)
    {
        var work = new Dictionary<uint, GraphicsTime>();
        foreach (var (instance, time) in after.RunningTime)
        {
            if (ProcessOf(instance) is not { } pid) continue;
            var onCard = cards.Any(luid => instance.Contains(luid, StringComparison.OrdinalIgnoreCase));
            if (!onCard && !processorGraphics.Any(luid => instance.Contains(luid, StringComparison.OrdinalIgnoreCase))) continue;
            var spent = Math.Max(0, before.RunningTime.TryGetValue(instance, out var earlier) ? time - earlier : time);
            if (spent == 0) continue;
            var load = IsLoadEngine(instance) ? spent : 0;
            var sum = work.GetValueOrDefault(pid);
            work[pid] = onCard
                ? sum with { CardLoad = sum.CardLoad + load, CardAny = sum.CardAny + spent }
                : sum with { ProcessorLoad = sum.ProcessorLoad + load, ProcessorAny = sum.ProcessorAny + spent };
        }
        return new GraphicsWork(work);
    }
}
