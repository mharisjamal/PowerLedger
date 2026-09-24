using System.Diagnostics;

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
    /// The adapter's load between two snapshots, 0..1, the way Task Manager reports a GPU: each engine's processes are
    /// added up and capped at the whole engine, and the busiest engine is the load. A process that started or ended in
    /// between is left out, and a count that went backwards, as when Windows reuses a process id, counts as idle.
    /// </summary>
    /// <param name="luid">The adapter as the counters name it; see <see cref="GpuAdapter.CounterLuid"/>.</param>
    /// <returns>0 when no process uses the adapter; null when no time passed between the snapshots.</returns>
    public static double? Busiest(EngineSnapshot before, EngineSnapshot after, string luid)
    {
        var elapsed = after.At - before.At;
        var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, time) in after.RunningTime)
        {
            if (EngineOf(instance, luid) is not { } engine || !before.RunningTime.TryGetValue(instance, out var earlier)) continue;
            if (elapsed <= 0) return null;
            engines[engine] = engines.GetValueOrDefault(engine) + Math.Max(0, time - earlier) / (double)elapsed;
        }
        return engines.Count == 0 ? 0 : Math.Min(1, engines.Values.Max());
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
}
