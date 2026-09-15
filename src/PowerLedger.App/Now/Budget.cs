using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>Spec §6's four energy bands.</summary>
internal enum Part
{
    Cpu,
    Gpu,
    Display,
    Rest,
}

/// <param name="Watts">The band's watts, never negative.</param>
/// <param name="Share">Its share of the four bands together.</param>
internal sealed record BudgetShare(Part Part, double Watts, double Share);

internal static class Budget
{
    /// <summary>The four bands for one reading. A negative rest, which measured mode shows when the parts over-report,
    /// counts as zero here as on every chart (spec §9).</summary>
    public static IReadOnlyList<BudgetShare> Of(ReadingFrame frame)
    {
        (Part Part, double Watts)[] bands =
        [
            (Part.Cpu, frame.Components.Cpu), (Part.Gpu, frame.Components.Gpu),
            (Part.Display, frame.DisplayBandW), (Part.Rest, frame.RestBandW),
        ];
        var clean = bands.Select(b => (b.Part, Watts: double.IsFinite(b.Watts) ? Math.Max(0, b.Watts) : 0)).ToList();
        var total = clean.Sum(b => b.Watts);
        return [.. clean.Select(b => new BudgetShare(b.Part, b.Watts, total > 0 ? b.Watts / total : 0))];
    }
}
