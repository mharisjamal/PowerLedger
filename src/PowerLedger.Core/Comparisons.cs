namespace PowerLedger.Core;

/// <summary>Everyday equivalents for a kWh figure (spec §9).</summary>
public static class Comparisons
{
    public const double LedBulbW = 10;
    public const double PhoneChargeWh = 15;
    public const double EvKwhPerKm = 0.18;

    public static double LedBulbHours(double kwh) => kwh * 1000 / LedBulbW;
    public static double PhoneCharges(double kwh) => kwh * 1000 / PhoneChargeWh;
    public static double EvKm(double kwh) => kwh / EvKwhPerKm;
}
