namespace PowerLedger.Core;

/// <summary>What the external monitors draw right now; the service's board implements it (Plan J).</summary>
public interface IMonitorDraw
{
    /// <summary>Watts for every monitor that counts, with the display on or asleep, by where they draw from.</summary>
    MonitorWatts Watts(bool displayOn);
}

/// <summary>What the monitors that count draw, split by where they draw from.</summary>
/// <param name="OwnPlug">Watts for the monitors with a plug of their own, which draw outside the PC.</param>
/// <param name="FromPc">Watts for the monitors that run off the PC, as a portable monitor on a laptop's USB-C port does,
/// which the PC's own supply or battery delivers.</param>
public readonly record struct MonitorWatts(double OwnPlug, double FromPc)
{
    /// <summary>Watts for every monitor that counts, wherever it draws from: what a reading's <c>Components.Monitors</c>
    /// holds.</summary>
    public double Total => OwnPlug + FromPc;
}

/// <summary>No external monitors: the model's default, and what tests use unless they say otherwise.</summary>
public sealed class NoMonitors : IMonitorDraw
{
    private NoMonitors()
    {
    }

    public static NoMonitors Instance { get; } = new();

    public MonitorWatts Watts(bool displayOn) => new(OwnPlug: 0, FromPc: 0);
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

    /// <summary>What a monitor draws switched off when the list gives no figure for it: the median <c>off_w</c> of the
    /// shipped table, 0.16 W over the 1,572 of its 1,580 monitors that give one.</summary>
    public const double DefaultOffW = 0.16;

    /// <summary>The draw of a monitor listed at <paramref name="listedOnW"/> at <paramref name="brightness"/>, 0–1, where
    /// null, NaN or infinity means unknown.</summary>
    public static double At(double listedOnW, double? brightness)
    {
        var b = brightness is { } value && double.IsFinite(value) ? Math.Clamp(value, 0, 1) : ListedBrightness;
        return listedOnW * Share(b) / Share(ListedBrightness);
    }

    private static double Share(double b) => FixedShare + (1 - FixedShare) * b;
}
