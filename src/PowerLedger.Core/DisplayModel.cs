using PowerLedger.Contracts;

namespace PowerLedger.Core;

/// <summary>Watts drawn by the internal panel and by opted-in external monitors.</summary>
public static class DisplayModel
{
    public const double PanelBaseW = 1.5;
    public const double PanelRangeW = 4.5;
    public const double MonitorSleepW = 0.5;

    /// <summary>Internal panel watts: 0 for desktops or while the display is off; unknown or NaN brightness counts as 50 %.</summary>
    public static double PanelWatts(MachineProfile profile, double? brightness, bool displayOn)
    {
        if (profile.Chassis != ChassisKind.Laptop || !displayOn) return 0;
        var b = brightness is { } value && !double.IsNaN(value) ? Math.Clamp(value, 0, 1) : 0.5;
        return (PanelBaseW + PanelRangeW * b) * SizeFactor(profile.DisplayDiagonalInches);
    }

    /// <summary>Panel size class: up to 14" ×0.8, above 14" and below 17" ×1.0, 17" and larger ×1.3. Unknown (0 or NaN) ×1.0.</summary>
    public static double SizeFactor(double diagonalInches) => diagonalInches switch
    {
        double.NaN or <= 0 => 1.0,
        <= 14.0 => 0.8,
        < 17.0 => 1.0,
        _ => 1.3,
    };

    /// <summary>Total watts for all opted-in external monitors (profile.MonitorWatts is per monitor); 0 when not opted in.</summary>
    public static double MonitorWatts(MachineProfile profile, bool displayOn)
    {
        if (!profile.IncludeMonitors || profile.ExternalMonitors <= 0) return 0;
        return profile.ExternalMonitors * (displayOn ? profile.MonitorWatts : MonitorSleepW);
    }
}
