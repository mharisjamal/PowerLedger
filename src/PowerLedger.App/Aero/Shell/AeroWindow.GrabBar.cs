using System.Windows;
using PowerLedger.App.Aero;

namespace PowerLedger.App;

/// <summary>
/// The grab bar at the top middle (visionOS's), where the mockup has it: its centre 748 of 1440, 28 right of the window's
/// middle, at the same share of any other width, in the caption strip, which since 0.10.9 holds nothing else (the window's
/// buttons are on the top bar's glass). It stays clear of the sidebar, the one piece that reaches up beside it. It hears
/// the pointer from the window's own mouse moves; over a gap between the panes the pointer is outside the window, and the
/// bar rests.
/// </summary>
internal partial class AeroWindow
{
    /// <summary>The mockup's grab bar centre as a share of the window's width: 748 of 1440.</summary>
    internal const double GrabCentre = 748.0 / 1440.0;

    /// <summary>The least room between the grab bar's hit area and the sidebar.</summary>
    internal const double GrabGap = 12;

    /// <summary>The grab bar, for a test.</summary>
    internal GrabBar GrabBar => Grab;

    private void StartGrabBar()
    {
        Grab.LayoutToggled += (_, _) => ToggleLayout();
        PreviewMouseMove += (_, e) => Grab.Track(e.GetPosition(Grab));
        MouseLeave += (_, _) => Grab.Track(null);
        Caption.SizeChanged += (_, _) => PlaceGrabBar();
        Side.SizeChanged += (_, _) => PlaceGrabBar();
    }

    /// <summary>The grab bar's left edge in the caption strip (<paramref name="captionLeft"/> in from the window's edge):
    /// its centre at <see cref="GrabCentre"/> of the window's <paramref name="windowWidth"/>, never nearer than <see
    /// cref="GrabGap"/> to the sidebar's right edge (<paramref name="sideRight"/>), nor past the strip's end.</summary>
    internal static double GrabLeft(double windowWidth, double captionLeft, double captionWidth, double grab, double sideRight)
        => Math.Clamp(windowWidth * GrabCentre - grab / 2, sideRight + GrabGap, Math.Max(sideRight + GrabGap, captionLeft + captionWidth - grab)) - captionLeft;

    private void PlaceGrabBar()
    {
        var captionLeft = Caption.Margin.Left;
        var sideRight = Side.IsVisible ? Stage.Margin.Left + SideColumn.ActualWidth : 0;   // its layout place, whatever the intro holds it at
        var left = Math.Max(0, GrabLeft(ActualWidth, captionLeft, Caption.ActualWidth, Grab.Width, sideRight));
        if (Math.Abs(Grab.Margin.Left - left) > .5) Grab.Margin = new Thickness(left, Grab.Margin.Top, 0, 0);
    }
}
