using System.Windows;

namespace PowerLedger.App.Aero;

/// <summary>A display as Windows lays it out: its bounds and work area in pixels on the virtual screen, and its scale (1 at
/// 96 DPI, 1.5 at 144).</summary>
internal sealed record OverlayDisplay(Rect Bounds, Rect WorkArea, double Scale, bool Primary);

/// <summary>Where the overlay's pill goes: its top-left in pixels on the virtual screen, and the scale of the display it
/// is on, which sizes it there.</summary>
internal readonly record struct OverlayPlace(Point Pill, double Scale)
{
    /// <summary>The window's top-left, <paramref name="margin"/> device-independent pixels further out than the pill's on
    /// each side: the room its shadow is drawn in, which may hang past a work area's edge where the pill may not.</summary>
    public Point Window(double margin) => new(Pill.X - (margin * Scale), Pill.Y - (margin * Scale));
}

/// <summary>
/// Where the watts overlay sits (Aero look design §5), worked out in pixels, since the App is per-monitor DPI aware and a
/// place in device-independent pixels means a different pixel on each display. A corner is <see cref="Inset"/> in from the
/// corner of a work area, on the display nearest the place last remembered (the main display when there is none), so the
/// corner chosen after dragging the overlay to a second display is that display's. A Free place is where the pill was
/// dragged to, pulled back inside the work area of the display it is nearest, so a display unplugged, moved or rescaled
/// never strands it off screen or under the taskbar. The window runs this again on display, work area and DPI changes.
/// </summary>
internal static class OverlayPlacement
{
    /// <summary>How far in from a work area's edges a corner puts the pill, in device-independent pixels.</summary>
    public const double Inset = 16;

    /// <summary>The pill's place for <paramref name="settings"/> among <paramref name="displays"/>, for a pill of
    /// <paramref name="pill"/> device-independent pixels. With no display listed, as can happen for a moment while
    /// displays change, the main display's origin at 100 %.</summary>
    public static OverlayPlace Place(OverlaySettings settings, IReadOnlyList<OverlayDisplay> displays, Size pill)
    {
        if (displays.Count == 0) return new OverlayPlace(new Point(Inset, Inset), 1);
        var remembered = settings is { Left: { } left, Top: { } top } ? ToPixels(left, top, displays) : (Point?)null;
        var display = remembered is { } point ? Nearest(point, displays) : MainOf(displays);
        var area = display.WorkArea;
        var width = pill.Width * display.Scale;
        var height = pill.Height * display.Scale;
        var inset = Inset * display.Scale;
        var corner = settings.Position == OverlayPosition.Free && remembered is null ? OverlayPosition.TopRight : settings.Position;
        var place = corner switch
        {
            OverlayPosition.TopLeft => new Point(area.Left + inset, area.Top + inset),
            OverlayPosition.BottomLeft => new Point(area.Left + inset, area.Bottom - inset - height),
            OverlayPosition.BottomRight => new Point(area.Right - inset - width, area.Bottom - inset - height),
            OverlayPosition.Free => remembered!.Value,
            _ => new Point(area.Right - inset - width, area.Top + inset),
        };
        return new OverlayPlace(Clamp(place, width, height, area), display.Scale);
    }

    /// <summary>The place to keep for a pill whose top-left is at <paramref name="pill"/> pixels: device-independent pixels
    /// at the main display's scale, the one linear map that covers the whole virtual screen, so it comes back to the same
    /// pixel on whatever display it is (<see cref="OverlaySettings.Left"/>).</summary>
    public static (double Left, double Top) Remember(Point pill, IReadOnlyList<OverlayDisplay> displays)
    {
        var scale = displays.Count == 0 ? 1 : MainOf(displays).Scale;
        return (pill.X / scale, pill.Y / scale);
    }

    private static Point ToPixels(double left, double top, IReadOnlyList<OverlayDisplay> displays)
    {
        var scale = MainOf(displays).Scale;
        return new Point(left * scale, top * scale);
    }

    private static OverlayDisplay MainOf(IReadOnlyList<OverlayDisplay> displays) => displays.FirstOrDefault(d => d.Primary) ?? displays[0];

    /// <summary>The display holding <paramref name="point"/>, or else the one whose bounds come nearest it.</summary>
    private static OverlayDisplay Nearest(Point point, IReadOnlyList<OverlayDisplay> displays)
        => displays.MinBy(d => Distance(point, d.Bounds))!;

    private static double Distance(Point point, Rect bounds)
    {
        var dx = Math.Max(Math.Max(bounds.Left - point.X, 0), point.X - bounds.Right);
        var dy = Math.Max(Math.Max(bounds.Top - point.Y, 0), point.Y - bounds.Bottom);
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary><paramref name="place"/> moved just far enough for the whole pill to be inside <paramref name="area"/>; a
    /// pill bigger than the area keeps to its top-left.</summary>
    private static Point Clamp(Point place, double width, double height, Rect area)
        => new(Math.Max(area.Left, Math.Min(place.X, area.Right - width)), Math.Max(area.Top, Math.Min(place.Y, area.Bottom - height)));
}
