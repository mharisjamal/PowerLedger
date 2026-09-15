using System.Windows;
using System.Windows.Interop;

namespace PowerLedger.App;

/// <summary>The App's window (spec §9). Closing it hides it to the tray; the App decides that in Task 13.</summary>
public partial class MainWindow : Window
{
    private readonly Extent _size;
    private readonly Extent _minimum;

    public MainWindow()
    {
        InitializeComponent();
        _size = new Extent(Width, Height);
        _minimum = new Extent(MinWidth, MinHeight);
    }

    /// <summary>Sizes and places the window inside <paramref name="workArea"/>, given in the window's units (see
    /// <see cref="WindowFit"/>). The minimum goes first, so the old minimum can't hold the window at its old size.</summary>
    internal void FitTo(Bounds workArea)
    {
        if (WindowFit.Within(workArea, _size, _minimum) is not { } fit) return;
        MinWidth = fit.Minimum.Width;
        MinHeight = fit.Minimum.Height;
        Width = fit.Bounds.Width;
        Height = fit.Bounds.Height;
        Left = fit.Bounds.Left;
        Top = fit.Bounds.Top;
    }

    /// <summary>
    /// Fits a window that places itself to the screen it opens on, once, before it first shows: by SourceInitialized it has
    /// a handle and WPF has centred it on the monitor Windows chose, but it is not yet visible. The work area is that
    /// monitor's, read in pixels and turned into the window's units with the window's own DPI, from the HwndSource that
    /// WPF converts Left, Top, Width and Height with (PresentationSource.FromVisual is still null at this point).
    /// SystemParameters.WorkArea would be simpler, but it is the primary screen's at the DPI the App started with, which is
    /// wrong for a window that opens on another screen or at another scale under per-monitor DPI. A window placed by hand
    /// keeps its bounds, and a maximised window restores to the fitted bounds, which fit.
    /// </summary>
    private void FitToScreen()
    {
        if (WindowStartupLocation != WindowStartupLocation.CenterScreen) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (HwndSource.FromHwnd(handle)?.CompositionTarget is not { } target) return;
        var pixels = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var topLeft = target.TransformFromDevice.Transform(new Point(pixels.Left, pixels.Top));
        var bottomRight = target.TransformFromDevice.Transform(new Point(pixels.Right, pixels.Bottom));
        FitTo(new Bounds(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        FitToScreen();
        base.OnSourceInitialized(e);
    }

    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
