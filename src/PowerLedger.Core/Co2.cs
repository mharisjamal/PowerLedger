namespace PowerLedger.Core;

public static class Co2
{
    /// <summary>World-average grid intensity, used when no country factor is chosen.</summary>
    public const double DefaultKgPerKwh = 0.40;

    public static double Kg(double kwh, double kgPerKwh) => kwh * kgPerKwh;
}
