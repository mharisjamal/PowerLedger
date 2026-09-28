namespace PowerLedger.App.Aero;

/// <summary>What a line of the overlay's menu is.</summary>
internal enum OverlayChoiceKind
{
    Heading,
    Separator,
    Choice,
}

/// <summary>A line of the overlay's menu: its words, whether it is ticked, and the settings it makes of the current ones
/// (null for a heading or a separator).</summary>
internal sealed record OverlayChoice(OverlayChoiceKind Kind, string Header, bool Checked, Func<OverlaySettings, OverlaySettings>? Apply);

/// <summary>
/// The overlay's right-click menu (Aero look design §5), as the prototype's: the four corners or Free, the opacity, the
/// sparkline and Close. Each choice is a new <see cref="OverlaySettings"/>, which the window saves through
/// <see cref="SettingsViewModel.Overlay"/>, the one channel the overlay follows; the menu is built again from what was
/// kept. A corner or Free remembers where the pill is now, so a corner is taken on the display it is on
/// (<see cref="OverlayPlacement"/>) and Free stays put until dragged.
/// </summary>
internal static class OverlayMenu
{
    /// <summary>The opacities offered, from opaque down to <see cref="OverlaySettings.MinOpacity"/>.</summary>
    public static IReadOnlyList<(string Label, double Value)> Opacities { get; } = [("100%", 1), ("85%", .85), ("70%", .7), ("55%", OverlaySettings.MinOpacity)];

    /// <param name="here">Where the pill is now, as <see cref="OverlayPlacement.Remember"/> keeps a place.</param>
    public static IReadOnlyList<OverlayChoice> Items(OverlaySettings settings, (double Left, double Top) here)
    {
        List<OverlayChoice> items = [Heading("Position")];
        foreach (var (position, label) in Positions)
        {
            items.Add(Choice(label, settings.Position == position, s => s with { Position = position, Left = here.Left, Top = here.Top }));
        }
        items.Add(Separator());
        items.Add(Heading("Opacity"));
        foreach (var (label, value) in Opacities)
        {
            items.Add(Choice(label, Math.Abs(settings.Opacity - value) < .01, s => s with { Opacity = value }));
        }
        items.Add(Separator());
        items.Add(Choice("Show the last 30 seconds", settings.Sparkline, s => s with { Sparkline = !s.Sparkline }));
        items.Add(Choice("Close overlay", false, s => s with { Enabled = false }));
        return items;
    }

    private static readonly (OverlayPosition Position, string Label)[] Positions =
    [
        (OverlayPosition.TopLeft, "Top left"), (OverlayPosition.TopRight, "Top right"), (OverlayPosition.BottomLeft, "Bottom left"),
        (OverlayPosition.BottomRight, "Bottom right"), (OverlayPosition.Free, "Free (drag it anywhere)"),
    ];

    private static OverlayChoice Heading(string header) => new(OverlayChoiceKind.Heading, header, false, null);

    private static OverlayChoice Separator() => new(OverlayChoiceKind.Separator, "", false, null);

    private static OverlayChoice Choice(string header, bool ticked, Func<OverlaySettings, OverlaySettings> apply)
        => new(OverlayChoiceKind.Choice, header, ticked, apply);
}
