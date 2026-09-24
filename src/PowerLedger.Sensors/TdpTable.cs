using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerLedger.Sensors;

/// <summary>A graphics card's rated watts, and whether they are only a rough figure from its memory.</summary>
/// <param name="Watts">The rating the load is scaled by.</param>
/// <param name="Rough">True when the table does not know the card, so the watts are <see cref="TdpTable.GuessFromMemory"/>'s.</param>
public readonly record struct GpuRating(double Watts, bool Rough);

/// <summary>
/// Thermal design power by model name, for the parts that report no watts of their own (spec §5).
/// Windows reports decorated names, so a key matches when it appears anywhere in the name; the longest
/// matching key wins, so "i7-1165G7" beats "i7-1165". A variant therefore needs a key of its own or it is
/// rated as the model its name begins with: "RX 7900 XTX" sits beside "RX 7900 XT", and "RX 6800M" beside "RX 6800".
/// A key that ends in a digit never matches a longer number, so "Quadro 600" is not a Quadro 6000 and "RX 570" not
/// an RX 5700, and runs of spaces in the name count as one, as the first Core i7s pad theirs ("i7 CPU         920").
/// A series name, as older AMD drivers give ("Radeon HD 7900 Series"), is rated as the series' top model.
/// </summary>
public sealed partial class TdpTable(IReadOnlyDictionary<string, double> cpu, IReadOnlyDictionary<string, double> gpu)
{
    private const ulong GiB = 1UL << 30;

    private static readonly Lazy<TdpTable> Loaded = new(Load, isThreadSafe: true);

    /// <summary>The table shipped with the app, read once.</summary>
    public static TdpTable Bundled => Loaded.Value;

    public int CpuCount => cpu.Count;

    public int GpuCount => gpu.Count;

    /// <summary>Watts for a processor name, or null when the model is unknown.</summary>
    public double? Cpu(string? name) => Match(cpu, name);

    /// <summary>Watts for a graphics card name, or null when the model is unknown.</summary>
    public double? Gpu(string? name) => Match(gpu, name);

    /// <summary>A graphics card's rating: the table's figure, or, for a card the table does not know, a rough one from its
    /// memory, so that no card counts as drawing nothing.</summary>
    /// <param name="dedicatedBytes">The card's own memory; 0 when unknown.</param>
    public GpuRating GpuOrGuess(string? name, ulong dedicatedBytes)
        => Gpu(name) is { } watts ? new GpuRating(watts, Rough: false) : new GpuRating(GuessFromMemory(dedicatedBytes), Rough: true);

    /// <summary>
    /// A rough board power for a card the table does not know, from its memory alone: up to 2 GB 75 W, the most a card
    /// without a power connector may draw; up to 4 GB 120 W; up to 8 GB 180 W; more 250 W. Cards with more memory are,
    /// broadly, bigger cards, and that is all this claims. It is a stopgap, not a rating, and the App says so.
    /// </summary>
    public static double GuessFromMemory(ulong dedicatedBytes) => dedicatedBytes switch
    {
        <= 2 * GiB => 75,
        <= 4 * GiB => 120,
        <= 8 * GiB => 180,
        _ => 250,
    };

    private static double? Match(IReadOnlyDictionary<string, double> table, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = Spaces().Replace(name, " ");
        double? best = null;
        var bestLength = 0;
        foreach (var (key, watts) in table)
        {
            if (key.Length <= bestLength || !Contains(name, key)) continue;
            best = watts;
            bestLength = key.Length;
        }
        return best;
    }

    /// <summary>True when the key appears in the name and, if it ends in a digit, is not followed by another.</summary>
    private static bool Contains(string name, string key)
    {
        var endsInDigit = key.Length > 0 && char.IsAsciiDigit(key[^1]);
        for (var at = name.IndexOf(key, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = name.IndexOf(key, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var next = at + key.Length;
            if (!endsInDigit || next >= name.Length || !char.IsAsciiDigit(name[next])) return true;
        }
        return false;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

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
