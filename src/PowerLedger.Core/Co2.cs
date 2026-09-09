namespace PowerLedger.Core;

/// <summary>Grid carbon intensity math (spec §9).</summary>
public static class Co2
{
    /// <summary>World-average grid intensity, used when no country factor is chosen.</summary>
    public const double DefaultKgPerKwh = 0.40;

    /// <summary>Kilograms of CO₂ for the energy at the given grid intensity.</summary>
    public static double Kg(double kwh, double kgPerKwh) => kwh * kgPerKwh;
}
