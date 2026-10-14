using System.Text.RegularExpressions;
using PowerLedger.Contracts;

namespace PowerLedger.Core;

/// <summary>
/// Supply efficiency per 80 PLUS tier (spec §5). Unknown tiers from a newer profile fall back to Bronze.
///
/// A tier is one number only at half load, which is where a supply is at its best and where 80 PLUS certifies it. Real
/// units fall away on both sides of that, steeply at the bottom: a Gold unit carrying two percent of its rating is
/// nearer 0.68 than 0.90, so an idle desktop on a big supply draws a fifth more from the wall than the flat number
/// says. <see cref="At"/> reads the tier's curve at the load the supply is really carrying, and the callers that know
/// only the tier keep the flat number.
/// </summary>
public static partial class PsuEfficiency
{
    /// <summary>What a desktop's supply is taken to be rated for when nothing says: 650 W, about what desktops ship with
    /// (the accuracy review's wizard default, 650 to 750 W). An idle desktop's 60 W is then a tenth of it.</summary>
    public const double DefaultRatedW = 650;

    /// <summary>
    /// The shape of the curve: the load, as a fraction of the supply's rating, and how much of the tier's best the
    /// supply makes there. The three certified 80 PLUS points are the 20%, 50% and 100% rows — one shape scaled by
    /// each tier's own best reproduces every tier's certified figures to within a point — and the rows below 10% are
    /// where measurements of real units put them, well short of anything 80 PLUS asks for.
    /// </summary>
    private static readonly (double Load, double OfBest)[] Curve =
    [
        (0.000, 0.30),
        (0.005, 0.45),
        (0.020, 0.75),
        (0.050, 0.90),
        (0.100, 0.96),
        (0.200, 0.97),
        (0.500, 1.00),
        (1.000, 0.96),
    ];

    /// <summary>The tier's efficiency at half load, which is the figure 80 PLUS certifies it at.</summary>
    public static double For(PsuTier tier) => tier switch
    {
        PsuTier.White => 0.82,
        PsuTier.Bronze => 0.85,
        PsuTier.Silver => 0.87,
        PsuTier.Gold => 0.90,
        PsuTier.Platinum => 0.92,
        PsuTier.Titanium => 0.94,
        _ => 0.85,
    };

    /// <summary>
    /// The tier's efficiency at <paramref name="load"/>, a fraction of the supply's rated output. A load below nothing
    /// or above the rating is read at the end of the curve, and a load that is no number at all falls back to the
    /// tier's flat figure. The answer is always above zero, so dividing by it is always safe.
    /// </summary>
    public static double At(PsuTier tier, double load)
    {
        if (!double.IsFinite(load)) return For(tier);
        var best = For(tier);
        var at = Math.Clamp(load, Curve[0].Load, Curve[^1].Load);
        for (var point = 1; point < Curve.Length; point++)
        {
            var (upper, ofBest) = Curve[point];
            if (at > upper) continue;
            var (lower, wasOfBest) = Curve[point - 1];
            var along = upper > lower ? (at - lower) / (upper - lower) : 0;
            return best * (wasOfBest + ((ofBest - wasOfBest) * along));
        }

        return best * Curve[^1].OfBest;
    }

    /// <summary>
    /// The rated output watts a supply's model name says it is built for — RM1000i is a thousand, E650 six hundred and
    /// fifty, "Toughpower DPS G 750W" seven hundred and fifty — or null when the name does not say. A run of digits
    /// that is no rating a supply is sold at, such as a year or an 80 PLUS badge, is passed over.
    /// </summary>
    public static int? RatedWatts(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        for (var index = 0; index < name.Length; index++)
        {
            if (!char.IsAsciiDigit(name[index])) continue;
            var end = index;
            while (end < name.Length && char.IsAsciiDigit(name[end])) end++;
            if (int.TryParse(name.AsSpan(index, end - index), out var watts) && watts is >= SmallestRating and <= LargestRating)
            {
                return watts;
            }

            index = end;
        }

        return null;
    }

    /// <summary>The efficiency to read <paramref name="outputWatts"/> of DC at, for a supply named
    /// <paramref name="name"/> (none for a modelled desktop): the curve of the tier its name says it is, else
    /// <paramref name="tier"/>, at the load it carries of the rating its name says, else <see cref="DefaultRatedW"/>; the
    /// tier's flat figure for output that is no number.</summary>
    public static double For(PsuTier tier, double outputWatts, string? name)
    {
        var known = TierOf(name) ?? tier;
        return double.IsFinite(outputWatts) ? At(known, outputWatts / (RatedWatts(name) ?? DefaultRatedW)) : For(known);
    }

    /// <summary>
    /// The 80 PLUS tier a supply's model name says it is, for the series PowerLedger reads over USB: Corsair's HXi are
    /// Platinum, its RMi Gold, and NZXT's E series, built by Seasonic, Gold. Null for any other name, Thermaltake's DPS G
    /// among them, which is sold at Gold and at Titanium under the one name; the profile's tier stands for those.
    /// </summary>
    public static PsuTier? TierOf(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (CorsairHxi().IsMatch(name)) return PsuTier.Platinum;
        if (CorsairRmi().IsMatch(name) || NzxtE().IsMatch(name)) return PsuTier.Gold;
        return null;
    }

    [GeneratedRegex(@"\bHX\d{3,4}i\b", RegexOptions.IgnoreCase)]
    private static partial Regex CorsairHxi();

    [GeneratedRegex(@"\bRM\d{3,4}i\b", RegexOptions.IgnoreCase)]
    private static partial Regex CorsairRmi();

    [GeneratedRegex(@"\bNZXT E\d{3}\b", RegexOptions.IgnoreCase)]
    private static partial Regex NzxtE();

    /// <summary>No supply PowerLedger knows of is rated outside these, so a number outside them is not a rating.</summary>
    private const int SmallestRating = 200, LargestRating = 2000;
}
