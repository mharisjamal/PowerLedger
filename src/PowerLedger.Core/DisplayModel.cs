using PowerLedger.Contracts;

namespace PowerLedger.Core;

/// <summary>Watts drawn by the internal panel and by opted-in external monitors.</summary>
public static class DisplayModel
{
    public const double PanelBaseW = 1.5;
    public const double PanelRangeW = 4.5;
    public const double MonitorSleepW = 0.5;

    public static double PanelWatts(MachineProfile profile, double? brightness, bool displayOn)
    {
        if (profile.Chassis != ChassisKind.Laptop || !displayOn) return 0;
        var b = brightness is { } value && !double.IsNaN(value) ? Math.Clamp(value, 0, 1) : 0.5;
        return (PanelBaseW + PanelRangeW * b) * SizeFactor(profile.DisplayDiagonalInches);
    }

    public static double SizeFactor(double diagonalInches) => diagonalInches switch
    {
        <= 0 => 1.0,
        <= 14.0 => 0.8,
        < 17.0 => 1.0,
        _ => 1.3,
    };

    public static double MonitorWatts(MachineProfile profile, bool displayOn)
    {
        if (!profile.IncludeMonitors || profile.ExternalMonitors <= 0) return 0;
        return profile.ExternalMonitors * (displayOn ? profile.MonitorWatts : MonitorSleepW);
    }
}
