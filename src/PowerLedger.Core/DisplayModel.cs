using PowerLedger.Contracts;

namespace PowerLedger.Core;

/// <summary>Watts drawn by the built-in panel, a laptop's or an all-in-one's. External monitors are an
/// <see cref="IMonitorDraw"/>'s.</summary>
public static class DisplayModel
{
    public const double PanelBaseW = 1.5;
    public const double PanelRangeW = 4.5;

    /// <summary>Built-in panel watts. A laptop's panel always counts, at an unknown size too; any other machine's counts
    /// when the profile gives its size, as an all-in-one's does, and a desktop's default of 0 means none. 0 while the
    /// display is off; unknown or NaN brightness counts as 50 %.</summary>
    public static double PanelWatts(MachineProfile profile, double? brightness, bool displayOn)
    {
        if (!displayOn || !HasPanel(profile)) return 0;
        var b = brightness is { } value && !double.IsNaN(value) ? Math.Clamp(value, 0, 1) : 0.5;
        return (PanelBaseW + PanelRangeW * b) * SizeFactor(profile.DisplayDiagonalInches);
    }

    /// <summary>Panel size class: up to 14" ×0.8, above 14" and below 17" ×1.0, 17" and below 20" ×1.3, and 20" and
    /// larger ×3.0, for an all-in-one's panel, which draws about 11 W at half brightness and 18 W at full. Unknown (0 or
    /// NaN) ×1.0.</summary>
    public static double SizeFactor(double diagonalInches) => diagonalInches switch
    {
        double.NaN or <= 0 => 1.0,
        <= 14.0 => 0.8,
        < 17.0 => 1.0,
        < 20.0 => 1.3,
        _ => 3.0,
    };

    private static bool HasPanel(MachineProfile profile) => profile.Chassis == ChassisKind.Laptop || profile.DisplayDiagonalInches > 0;
}
