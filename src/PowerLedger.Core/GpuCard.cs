using PowerLedger.Contracts;

namespace PowerLedger.Core;

/// <summary>
/// One discrete graphics card in one tick. A machine can have several, a workstation with two cards or a card beside
/// another maker's, and every one counts: the power model adds each card's watts, those its own library measured as they
/// are and the others worked out from the card's load and rating.
/// </summary>
/// <param name="Name">The card's name as its library or Windows gives it; empty when neither did.</param>
/// <param name="Watts">What the card's own library measured, 0 for a card Windows has switched off; null when nothing
/// measured it, so the model works its watts out from <paramref name="Load"/>.</param>
/// <param name="Load">0..1; null when unknown, which the model counts as idle.</param>
/// <param name="Scope">What <paramref name="Watts"/> covers.</param>
/// <param name="RatedW">The card's own rated power for working its watts out from its load; null leaves it to the
/// machine's figure.</param>
public sealed record GpuCard(string Name, double? Watts, double? Load, GpuPowerScope Scope = GpuPowerScope.Board, double? RatedW = null)
{
    /// <summary>The single figures a tick keeps for all its cards together, as the frame, the stored reading and the
    /// validator's older checks see them: a card is present when there is any; the watts are the sum only when every card
    /// measured its own, since otherwise part of the figure is the model's; the load is the busiest card's; and the scope is
    /// the least a measured card covers.</summary>
    public static GpuTotals Totals(IReadOnlyList<GpuCard> cards)
    {
        if (cards.Count == 0) return new GpuTotals(false, null, null, GpuPowerScope.Board);

        double? watts = 0;
        double? load = null;
        var scope = GpuPowerScope.Board;
        foreach (var card in cards)
        {
            watts = card.Watts is { } w && double.IsFinite(w) && watts is { } sum ? sum + w : null;
            if (card.Load is { } l && double.IsFinite(l)) load = Math.Max(load ?? l, l);
            if (card.Watts is not null && Covers(card.Scope) < Covers(scope)) scope = card.Scope;
        }
        return new GpuTotals(true, watts, load, watts is null ? GpuPowerScope.Board : scope);
    }

    /// <summary>How much of the card a scope covers: the chip alone, the package, or the whole board.</summary>
    private static int Covers(GpuPowerScope scope) => scope switch
    {
        GpuPowerScope.ChipOnly => 0,
        GpuPowerScope.Package => 1,
        _ => 2,
    };
}

/// <summary>What a tick's cards come to together; see <see cref="GpuCard.Totals"/>.</summary>
public readonly record struct GpuTotals(bool Present, double? Watts, double? Load, GpuPowerScope Scope);
