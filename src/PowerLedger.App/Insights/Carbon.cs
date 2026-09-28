using PowerLedger.Core;

namespace PowerLedger.App;

/// <summary>
/// The Carbon insight's maths (Aero look design §4): energy times the grid's factor. The factor is the one carbon
/// setting the App has, Settings' CO₂ per kWh (<see cref="UiPreferences.Co2KgPerKwh"/>), which Now and the
/// Report already use, so every screen agrees; <see cref="GridFactors"/> only suggests a figure for Windows' region. What
/// the page says the figure is follows from it: the default is called that, a figure matching the region's table entry
/// is credited to its source, the world's likewise, and anything else is the user's own.
/// </summary>
internal static class Carbon
{
    public static CarbonEstimate Estimate(double monthKwh, double sinceStartKwh, double kgPerKwh, string? region)
        => new(Co2.Kg(Math.Max(0, monthKwh), kgPerKwh), Co2.Kg(Math.Max(0, sinceStartKwh), kgPerKwh), kgPerKwh * 1000, SourceOf(kgPerKwh * 1000, region));

    /// <summary>Where a factor of <paramref name="grams"/> comes from, in the page's words.</summary>
    public static string SourceOf(double grams, string? region)
    {
        if (Math.Abs(grams - Co2.DefaultKgPerKwh * 1000) < 0.5) return "The default in Settings, a world average";
        if (GridFactors.For(region) is { } local && Math.Abs(grams - local.Grams) <= GridFactors.Match)
            return Capitalised($"{local.Country}'s grid in {local.Year}, from {local.Source}");
        if (Math.Abs(grams - GridFactors.World.Grams) <= GridFactors.Match)
            return $"The world's grids in {GridFactors.World.Year}, from {GridFactors.World.Source}";
        return "Your own figure, set in Settings";
    }

    /// <summary>"the United Kingdom's grid" as a line's start.</summary>
    private static string Capitalised(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
