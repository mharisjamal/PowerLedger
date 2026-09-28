using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;

namespace PowerLedger.App;

/// <summary>
/// The Aero shell (Aero look design §2), over the same ViewModels as Classic's and Midnight's windows. Task 0 gives it the
/// look switcher's contract only: the <see cref="IShellWindow"/> members, fitting to the screen it opens on, and the page
/// mapping; agent D draws the shell. A close is a close here, as in the other looks: the App's Closing handler hides the
/// current window to the tray (plan O 0.4).
/// </summary>
internal partial class AeroWindow : Window, IShellWindow
{
    private readonly ShellViewModel _shell;
    private readonly Extent _size;
    private readonly Extent _minimum;

    /// <param name="looks">The switcher the App opened this window through (plan O 0.4), as Midnight's takes it; a look
    /// chosen in the window goes through <see cref="SettingsViewModel.Look"/> instead, so the choice is saved.</param>
    /// <param name="theme">For the top bar's theme toggle: which theme is on.</param>
    /// <param name="updates">For the update card, and the blocking panel while an update is required (Plan Q §3).</param>
    /// <param name="feedback">Opens the Send feedback window.</param>
    internal AeroWindow(ShellViewModel shell, LookSwitcher looks, ThemeManager theme, Updater updates, Action feedback)
    {
        _shell = shell;
        Switcher = looks;
        Themes = theme;
        Updates = updates;
        Feedback = feedback;
        InitializeComponent();
        DataContext = shell;
        _size = new Extent(Width, Height);
        _minimum = new Extent(MinWidth, MinHeight);
        // Nothing in the shell changes until the window shows: a switch whose new window fails to show puts the page back
        // as it was, which a window that had already mapped it here would have changed under it (as MidnightWindow).
        shell.PropertyChanged += OnShellChanged;
        Closed += (_, _) => shell.PropertyChanged -= OnShellChanged;   // the shell outlives a window a switch closes
        IsVisibleChanged += (_, _) => ShowOwnPage();
    }

    /// <summary>What the constructor was given, for agent D's shell; unused by Task 0's placeholder.</summary>
    internal LookSwitcher Switcher { get; }

    internal ThemeManager Themes { get; }

    internal Updater Updates { get; }

    internal Action Feedback { get; }

    /// <summary>The bounds a switch carries over: the restored ones once shown, so a maximised window hands on the size it
    /// comes back to. Setting them places the window by hand, so it no longer fits itself to the screen it opens on.</summary>
    public Rect Bounds
    {
        get => RestoreBounds.IsEmpty ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        set
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = value.Left;
            Top = value.Top;
            Width = value.Width;
            Height = value.Height;
        }
    }

    public WindowState State { get => WindowState; set => WindowState = value; }

    /// <summary>The shell's page; Classic's Now arrives as the Dashboard, the page that stands in its place here. Aero has
    /// every other page, Parts and Insights among them.</summary>
    public Page Page
    {
        get => _shell.Page;
        set => _shell.Page = OwnPage(value);
    }

    public Window Window => this;

    /// <summary>The page Aero shows for <paramref name="page"/>: the Dashboard for Classic's Now, any other as it is.</summary>
    internal static Page OwnPage(Page page) => page == Page.Now ? Page.Dashboard : page;

    /// <summary>Closes for good: the App's Closing handler hides a window to the tray only while it is the current one.</summary>
    public void CloseForSwitch() => Close();

    /// <summary>Sizes and places the window inside <paramref name="workArea"/>, given in the window's units (see <see cref="WindowFit"/>).</summary>
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

    protected override void OnSourceInitialized(EventArgs e)
    {
        FitToScreen();
        base.OnSourceInitialized(e);
    }

    /// <summary>As MainWindow.FitToScreen: once, before the window first shows, on the monitor Windows chose, at that monitor's DPI.</summary>
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

    /// <summary>Shown, the window shows its own page for Classic's Now; while shown, it keeps doing so.</summary>
    private void ShowOwnPage()
    {
        if (IsVisible && _shell.Page == Page.Now) _shell.Page = Page.Dashboard;   // Aero's sidebar never selects Now
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Page)) ShowOwnPage();
    }
}
