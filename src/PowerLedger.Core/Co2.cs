namespace PowerLedger.Core;

/// <summary>Grid carbon intensity math (spec §9).</summary>
public static class Co2
{
    /// <summary>The world's grids in 2023, 481 g per kWh (Ember, as the App's grid table has it).</summary>
    public const double WorldKgPerKwh = 0.481;

    /// <summary>The factor before one is chosen: the world's, until the App seeds the one for Windows' region.</summary>
    public const double DefaultKgPerKwh = WorldKgPerKwh;

    /// <summary>0.10's default, labelled a world average though it was 17% under the world's figure.</summary>
    public const double LegacyDefaultKgPerKwh = 0.40;

    /// <summary>Kilograms of CO₂ for the energy at the given grid intensity.</summary>
    public static double Kg(double kwh, double kgPerKwh) => kwh * kgPerKwh;

    /// <summary>The factor to use once seeded (Plan AC): one still at a default, 0.10's 0.40 or the world's, takes
    /// <paramref name="suggestedKgPerKwh"/>, the grid of Windows' region; a factor the user chose is kept, as is any when
    /// the suggestion is no grid's figure.</summary>
    public static double Seeded(double current, double suggestedKgPerKwh)
        => (current == LegacyDefaultKgPerKwh || current == DefaultKgPerKwh) && double.IsFinite(suggestedKgPerKwh) && suggestedKgPerKwh >= 0
            ? suggestedKgPerKwh
            : current;
}
