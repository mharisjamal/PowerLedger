using System.Windows;
using PowerLedger.App.Aero;

namespace PowerLedger.App;

/// <summary>
/// The grab bar at the top middle (visionOS's): centred over the stage in the caption strip, which since 0.10.9 holds
/// nothing else (the window's buttons are on the top bar's glass). It hears the pointer from the window's own mouse moves;
/// over a gap between the panes the pointer is outside the window, and the bar rests.
/// </summary>
internal partial class AeroWindow
{
    /// <summary>The grab bar, for a test.</summary>
    internal GrabBar GrabBar => Grab;

    private void StartGrabBar()
    {
        Grab.LayoutToggled += (_, _) => ToggleLayout();
        PreviewMouseMove += (_, e) => Grab.Track(e.GetPosition(Grab));
        MouseLeave += (_, _) => Grab.Track(null);
        Caption.SizeChanged += (_, _) => PlaceGrabBar();
    }

    private void PlaceGrabBar()
    {
        var width = Caption.ActualWidth;
        var left = Math.Max(0, (width - Grab.Width) / 2);
        if (Math.Abs(Grab.Margin.Left - left) > .5) Grab.Margin = new Thickness(left, Grab.Margin.Top, 0, 0);
    }
}
