using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App.Tests;

/// <summary>
/// Midnight's two palettes as the design's §3 table and the plan's task 0.2 give them, both key sets: the Classic keys
/// with Midnight's values and the M.* keys. A copy for the tests to run against until Plan O task F4 lands
/// Theme/Palette.Midnight.Dark.xaml and .Light.xaml, when this goes and the tests read those.
/// </summary>
internal static class MidnightPalettes
{
    private static readonly (string Key, string Dark, string Light)[] Tokens =
    [
        ("M.Ground", "#0B0E1C", "#F3F4FA"),
        ("M.Panel", "#10142A", "#FFFFFF"),
        ("M.Raised", "#161B36", "#F7F8FD"),
        ("M.Line", "#1F2547", "#E2E5F1"),
        ("M.LineStrong", "#2B3260", "#C9CEE3"),
        ("M.Ink", "#ECEEF9", "#151A33"),
        ("M.Ink2", "#A9AFCC", "#5C6386"),
        ("M.Ink3", "#6B7196", "#8A90AE"),
        ("M.Accent", "#4C6FFF", "#3A5BFF"),
        ("M.AccentSoft", "#384C6FFF", "#243A5BFF"),
        ("M.OnAccent", "#FFFFFF", "#FFFFFF"),
        ("M.Good", "#34D399", "#1E9E6C"),
        ("M.Bad", "#F87171", "#D64545"),
        ("M.Warn", "#F5B942", "#C98A12"),
        ("M.PartCpu", "#6D8CFF", "#3A5BFF"),
        ("M.PartGpu", "#A78BFA", "#7C5CE6"),
        ("M.PartDisplay", "#38BDF8", "#0891B2"),
        ("M.PartRest", "#7C86A8", "#8A90AE"),
        ("M.ChipMeasured", "#2F3A1E", "#EEF3D6"),
        ("M.ChipMeasuredText", "#D9E27F", "#4B5A12"),
        ("M.ChipCalibrated", "#3A3418", "#FBF1CF"),
        ("M.ChipCalibratedText", "#F1D57A", "#7A5A08"),
        ("M.ChipEstimated", "#1A2F44", "#DDEFFA"),
        ("M.ChipEstimatedText", "#7CC4F5", "#0E5A85"),
        ("M.GlassFill", "#B810142A", "#B3FFFFFF"),
        ("M.GlassBorder", "#14FFFFFF", "#14151A33"),
        ("M.GlassHighlight", "#24FFFFFF", "#E6FFFFFF"),
        ("M.Shadow", "#73000000", "#29151A33"),
        ("M.Hatch", "#1AFFFFFF", "#1A151A33"),
        ("M.ChartFillTop", "#594C6FFF", "#473A5BFF"),
        ("M.ChartFillBottom", "#004C6FFF", "#003A5BFF"),
        ("M.Focus", "#8FA5FF", "#3A5BFF"),
    ];

    /// <summary>The Classic keys, each Midnight's own value under another name.</summary>
    private static readonly (string Classic, string Midnight)[] Aliases =
    [
        ("Brush.Ground", "M.Ground"), ("Brush.Panel", "M.Panel"), ("Brush.Raised", "M.Raised"), ("Brush.Line", "M.Line"),
        ("Brush.LineStrong", "M.LineStrong"), ("Brush.Ink", "M.Ink"), ("Brush.Ink2", "M.Ink2"), ("Brush.Ink3", "M.Ink3"),
        ("Brush.Amber", "M.Accent"), ("Brush.AmberSoft", "M.AccentSoft"), ("Brush.OnAmber", "M.OnAccent"),
        ("Brush.Measured", "M.ChipMeasuredText"), ("Brush.Calibrated", "M.ChipCalibratedText"), ("Brush.Estimated", "M.ChipEstimatedText"),
        ("Brush.PartCpu", "M.PartCpu"), ("Brush.PartGpu", "M.PartGpu"), ("Brush.PartDisplay", "M.PartDisplay"), ("Brush.PartRest", "M.PartRest"),
    ];

    public static ResourceDictionary Of(Theme theme)
    {
        var palette = new ResourceDictionary();
        foreach (var (key, dark, light) in Tokens)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme == Theme.Dark ? dark : light));
            brush.Freeze();
            palette[key] = brush;
        }
        foreach (var (classic, midnight) in Aliases) palette[classic] = palette[midnight];
        return palette;
    }

    /// <summary>Every colour the palette holds, opaque or not.</summary>
    public static IReadOnlySet<Color> Colours(Theme theme)
        => Tokens.Select(t => (Color)ColorConverter.ConvertFromString(theme == Theme.Dark ? t.Dark : t.Light)).ToHashSet();
}
