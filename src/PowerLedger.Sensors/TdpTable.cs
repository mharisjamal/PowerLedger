using System.Reflection;
using System.Text.Json;

namespace PowerLedger.Sensors;

/// <summary>
/// Thermal design power by model name, for the parts that report no watts of their own (spec §5).
/// Windows reports decorated names, so a key matches when it appears anywhere in the name; the longest
/// matching key wins, so "i7-1165G7" beats "i7-1165".
/// </summary>
public sealed class TdpTable(IReadOnlyDictionary<string, double> cpu, IReadOnlyDictionary<string, double> gpu)
{
    private static readonly Lazy<TdpTable> Loaded = new(Load, isThreadSafe: true);

    /// <summary>The table shipped with the app, read once.</summary>
    public static TdpTable Bundled => Loaded.Value;

    public int CpuCount => cpu.Count;

    public int GpuCount => gpu.Count;

    /// <summary>Watts for a processor name, or null when the model is unknown.</summary>
    public double? Cpu(string? name) => Match(cpu, name);

    /// <summary>Watts for a graphics card name, or null when the model is unknown.</summary>
    public double? Gpu(string? name) => Match(gpu, name);

    private static double? Match(IReadOnlyDictionary<string, double> table, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        double? best = null;
        var bestLength = 0;
        foreach (var (key, watts) in table)
        {
            if (key.Length <= bestLength || !name.Contains(key, StringComparison.OrdinalIgnoreCase)) continue;
            best = watts;
            bestLength = key.Length;
        }
        return best;
    }

    private static TdpTable Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PowerLedger.Sensors.tdp-table.json")
            ?? throw new InvalidOperationException("tdp-table.json is missing from the assembly");
        var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(stream)
            ?? throw new InvalidOperationException("tdp-table.json is not a table");
        return new TdpTable(
            parsed.GetValueOrDefault("cpu") ?? new Dictionary<string, double>(),
            parsed.GetValueOrDefault("gpu") ?? new Dictionary<string, double>());
    }
}
