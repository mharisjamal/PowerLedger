namespace PowerLedger.Core;

/// <summary>Everyday equivalents for a kWh figure (spec §9). Constants are round, defensible assumptions, not measurements.</summary>
public static class Comparisons
{
    /// <summary>A 60 W-equivalent LED bulb.</summary>
    public const double LedBulbW = 10;

    /// <summary>A full charge of a ~4,000 mAh phone battery at 3.85 V, before charger losses.</summary>
    public const double PhoneChargeWh = 15;

    /// <summary>A mid-size electric car: 18 kWh per 100 km.</summary>
    public const double EvKwhPerKm = 0.18;

    public static double LedBulbHours(double kwh) => kwh * 1000 / LedBulbW;
    public static double PhoneCharges(double kwh) => kwh * 1000 / PhoneChargeWh;
    public static double EvKm(double kwh) => kwh / EvKwhPerKm;
}
