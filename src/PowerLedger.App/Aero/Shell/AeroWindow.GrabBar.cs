using System.Windows;
using PowerLedger.App.Aero;

namespace PowerLedger.App;

/// <summary>
/// The grab bar at the top middle (visionOS's): centred over the stage in the caption strip, and moved left of the
/// caption's buttons when a narrow window would put it over them, so it never covers a control. It hears the pointer from
/// the window's own mouse moves; over a gap between the panes the pointer is outside the window, and the bar rests.
/// </summary>
internal partial class AeroWindow
{
    /// <summary>The least room between the grab bar's hit area and the caption's buttons.</summary>
    internal const double GrabGap = 12;

    /// <summary>The grab bar, for a test.</summary>
    internal GrabBar GrabBar => Grab;

    private void StartGrabBar()
    {
        Grab.LayoutToggled += (_, _) => ToggleLayout();
        PreviewMouseMove += (_, e) => Grab.Track(e.GetPosition(Grab));
        MouseLeave += (_, _) => Grab.Track(null);
        Caption.SizeChanged += (_, _) => PlaceGrabBar();
        CaptionButtons.SizeChanged += (_, _) => PlaceGrabBar();
    }

    /// <summary>Where the grab bar's left edge goes in a caption <paramref name="width"/> wide: centred, unless that
    /// would bring it within <paramref name="gap"/> of the buttons starting at <paramref name="buttonsLeft"/>.</summary>
    internal static double GrabLeft(double width, double grab, double buttonsLeft, double gap)
        => Math.Max(0, Math.Min((width - grab) / 2, buttonsLeft - gap - grab));

    private void PlaceGrabBar()
    {
        var width = Caption.ActualWidth;
        var left = GrabLeft(width, Grab.Width, width - CaptionButtons.ActualWidth, GrabGap);
        if (Math.Abs(Grab.Margin.Left - left) > .5) Grab.Margin = new Thickness(left, 0, 0, 0);
    }
}
