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
/// <c>P_full × (0.45 + 0.55 b)</c>. Energy Star measures a monitor's on-mode figure at 200 cd/m². Where the list gives the
/// monitor's maximum luminance, that sits at 200 over it on the monitor's brightness scale; where it doesn't, the figure is
/// taken to sit at 75%. A monitor whose brightness is unknown is assumed to sit at 75%.
/// </summary>
public static class MonitorPower
{
    public const double FixedShare = 0.45;

    /// <summary>The brightness a monitor that doesn't give its brightness is assumed to sit at, and where the list's figure
    /// is taken to sit for a monitor whose maximum luminance is unknown.</summary>
    public const double ListedBrightness = 0.75;

    /// <summary>The luminance Energy Star sets a monitor to for its on-mode figure, in cd/m².</summary>
    public const double ListedNits = 200;

    /// <summary>The lowest brightness the list's figure is taken to sit at, for a monitor so bright that 200 cd/m² is near
    /// the bottom of its scale.</summary>
    public const double LowestAnchor = 0.05;

    /// <summary>The refresh rate Energy Star measures a monitor at, unless its manual names another default.</summary>
    public const double ListedRefreshHz = 60;

    /// <summary>What an LCD monitor's electronics add for each megapixel and each hertz above 60 (spec §3), fitted to two
    /// measurements that alone give from 0.003 to 0.010: ASUS's 27-inch 1440p PG279Q drew 1 W more at 144 Hz than at 60, and
    /// Monoprice's 3440 × 1440 Dark Matter 34 went from 20.0 to 24.3 W.</summary>
    public const double RefreshWattsPerMegapixelHz = 0.006;

    /// <summary>What a monitor draws asleep when the list gives no figure for it: the median <c>sleep_w</c> of the shipped
    /// table, 0.23 W over its 1,580 monitors, all of which give one.</summary>
    public const double DefaultSleepW = 0.23;

    /// <summary>What a monitor draws switched off when the list gives no figure for it: the median <c>off_w</c> of the
    /// shipped table, 0.16 W over the 1,572 of its 1,580 monitors that give one.</summary>
    public const double DefaultOffW = 0.16;

    /// <summary>Where the list's figure sits on a monitor's brightness scale: 200 cd/m² over its maximum luminance, clamped to
    /// 0.05–1, or <see cref="ListedBrightness"/> when the maximum luminance is unknown.</summary>
    public static double Anchor(double? maxNits)
        => maxNits is { } nits && double.IsFinite(nits) && nits > 0 ? Math.Clamp(ListedNits / nits, LowestAnchor, 1) : ListedBrightness;

    /// <summary>The draw at <paramref name="brightness"/>, 0–1, where null, NaN or infinity means unknown, of a monitor listed
    /// at <paramref name="listedOnW"/>.</summary>
    /// <param name="anchor">Where the listed figure sits on the monitor's brightness scale (see <see cref="Anchor"/>).</param>
    public static double At(double listedOnW, double? brightness, double anchor)
    {
        var b = brightness is { } value && double.IsFinite(value) ? Math.Clamp(value, 0, 1) : ListedBrightness;
        return listedOnW * Share(b) / Share(anchor);
    }

    /// <summary>What driving a monitor's panel faster than the list's 60 Hz adds (spec §3): its timing controller and column
    /// drivers work harder, and its backlight doesn't. <see cref="RefreshWattsPerMegapixelHz"/> watts for each megapixel and
    /// each hertz above 60, or 0 when the resolution or the refresh rate is unknown.</summary>
    /// <param name="width">The native width in pixels, or 0 when unknown.</param>
    /// <param name="height">The native height in pixels, or 0 when unknown.</param>
    /// <param name="refreshHz">The refresh rate Windows drives the monitor at, or null when unknown.</param>
    public static double Refresh(int width, int height, double? refreshHz)
    {
        if (width <= 0 || height <= 0 || refreshHz is not { } hz || !double.IsFinite(hz)) return 0;
        var megapixels = (double)width * height / 1_000_000;
        return RefreshWattsPerMegapixelHz * megapixels * Math.Max(0, hz - ListedRefreshHz);
    }

    private static double Share(double b) => FixedShare + (1 - FixedShare) * b;
}
