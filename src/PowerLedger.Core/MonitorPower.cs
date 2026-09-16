namespace PowerLedger.Core;

/// <summary>What the external monitors draw right now; the service's board implements it (Plan J).</summary>
public interface IMonitorDraw
{
    /// <summary>Watts for every monitor that counts, with the display on or asleep.</summary>
    double Watts(bool displayOn);
}

/// <summary>No external monitors: the model's default, and what tests use unless they say otherwise.</summary>
public sealed class NoMonitors : IMonitorDraw
{
    private NoMonitors()
    {
    }

    public static NoMonitors Instance { get; } = new();

    public double Watts(bool displayOn) => 0;
}

/// <summary>
/// A monitor's draw at a brightness (spec §5). Power is close to linear in screen luminance, and a monitor's fixed
/// electronics are 28–51% of its full-brightness draw (measured, TechPowerUp 2021–2026), so the draw at brightness b is
/// <c>P_full × (0.45 + 0.55 b)</c>. Energy Star's on-mode figure is taken as the draw at 75%, and a monitor whose
/// brightness is unknown is assumed to sit there too.
/// </summary>
public static class MonitorPower
{
    public const double FixedShare = 0.45;
    public const double ListedBrightness = 0.75;
    public const double DefaultSleepW = 0.2;

    /// <summary>The draw of a monitor listed at <paramref name="listedOnW"/> at <paramref name="brightness"/>, 0–1, where
    /// null, NaN or infinity means unknown.</summary>
    public static double At(double listedOnW, double? brightness)
    {
        var b = brightness is { } value && double.IsFinite(value) ? Math.Clamp(value, 0, 1) : ListedBrightness;
        return listedOnW * Share(b) / Share(ListedBrightness);
    }

    private static double Share(double b) => FixedShare + (1 - FixedShare) * b;
}
