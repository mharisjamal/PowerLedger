namespace PowerLedger.Core;

/// <summary>
/// A laptop's mains adapter (Plan AC): what it draws from the wall to deliver its DC. An adapter is at its best near half its
/// rating and falls away below it, and it draws a little with nothing to deliver. DoE Level VI, which every adapter sold in
/// the US since 2016 meets, asks 88% averaged over a quarter to all of the rating for adapters over 49 W and caps the
/// no-load draw at 0.21 W; measured units sit at 0.80 to 0.86 around a tenth of their rating, where an idle laptop
/// spends most of its time, and draw 0.1 to 0.2 W with nothing plugged in. A flat 90% read an idle laptop 5 to 10% low.
/// </summary>
public static class AdapterEfficiency
{
    /// <summary>The rating taken for an adapter: 65 W, the most common laptop adapter.</summary>
    public const double DefaultRatedW = 65;

    /// <summary>What an adapter draws with nothing to deliver, on top of what delivering costs.</summary>
    public const double NoLoadW = 0.15;

    /// <summary>The load, as a fraction of the rating, and the efficiency there, before the no-load draw.</summary>
    private static readonly (double Load, double Efficiency)[] Curve =
    [
        (0.00, 0.70),
        (0.05, 0.78),
        (0.10, 0.83),
        (0.25, 0.88),
        (0.50, 0.90),
        (0.75, 0.90),
        (1.00, 0.89),
    ];

    /// <summary>The efficiency at <paramref name="load"/>, a fraction of the rating, read at the curve's ends outside it and
    /// at half load when it is no number; always above zero.</summary>
    public static double At(double load)
    {
        if (!double.IsFinite(load)) load = 0.5;
        var at = Math.Clamp(load, Curve[0].Load, Curve[^1].Load);
        for (var point = 1; point < Curve.Length; point++)
        {
            var (upper, efficiency) = Curve[point];
            if (at > upper) continue;
            var (lower, was) = Curve[point - 1];
            return was + ((efficiency - was) * (at - lower) / (upper - lower));
        }
        return Curve[^1].Efficiency;
    }

    /// <summary>What an adapter rated <paramref name="ratedW"/> draws from the wall to deliver <paramref name="dcW"/>: the DC
    /// over its efficiency at that load, and its no-load draw.</summary>
    public static double Wall(double dcW, double ratedW = DefaultRatedW)
    {
        var rating = double.IsFinite(ratedW) && ratedW > 0 ? ratedW : DefaultRatedW;
        return dcW <= 0 ? NoLoadW : (dcW / At(dcW / rating)) + NoLoadW;
    }
}
