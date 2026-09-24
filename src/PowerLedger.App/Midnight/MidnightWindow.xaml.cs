using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PowerLedger.App;

/// <summary>
/// The Midnight shell (Midnight look design §1, plan O M1-3), over the same ViewModels as Classic's MainWindow. A close is a
/// close here, as there: the App's Closing handler hides the current window to the tray (plan O 0.4).
/// </summary>
internal partial class MidnightWindow : Window, IShellWindow
{
    private readonly ShellViewModel _shell;
    private readonly ThemeManager _theme;
    private readonly Action _feedback;
    private readonly Extent _size;
    private readonly Extent _minimum;
    private readonly DispatcherTimer _problemTimer = new();
    private bool _switching;

    /// <param name="looks">The switcher the App opened this window through (plan O 0.4). The top bar's Switch look goes
    /// through <see cref="ShellViewModel.SwitchLook"/> instead, so the choice is saved as Settings saves it.</param>
    /// <param name="theme">For the top bar's sun and moon: which theme is on.</param>
    /// <param name="updates">For the foot's update card.</param>
    /// <param name="feedback">Opens the Send feedback window, from the sidebar's Support item and the bug button.</param>
    internal MidnightWindow(ShellViewModel shell, LookSwitcher looks, ThemeManager theme, Updater updates, Action feedback)
    {
        _shell = shell;
        _theme = theme;
        _feedback = feedback;
        InitializeComponent();
        DataContext = shell;
        UpdateCard.DataContext = updates;
        _size = new Extent(Width, Height);
        _minimum = new Extent(MinWidth, MinHeight);
        PcName.Text = Environment.MachineName;
        KindGlyph.Text = shell.Settings.Service.IsDesktop ? "" : "";
        KindGlyph.ToolTip = shell.Settings.Service.IsDesktop ? "Desktop" : "Laptop";
        AutomationProperties.SetName(KindGlyph, (string)KindGlyph.ToolTip);
        ShowTheme();
        ShowState();
        // Nothing in the shell changes until the window shows: a switch whose new window fails to show puts the page back
        // as it was, which a window that had already mapped it here would have changed under it.
        shell.Settings.PropertyChanged += OnSettingsChanged;
        shell.PropertyChanged += OnShellChanged;
        Closed += (_, _) =>
        {
            shell.Settings.PropertyChanged -= OnSettingsChanged;   // Settings and the shell outlive a window a switch closes
            shell.PropertyChanged -= OnShellChanged;
            _problemTimer.Stop();
        };
        _problemTimer.Tick += (_, _) => HideProblem();
        IsVisibleChanged += (_, _) => ShowOwnPage();
        Loaded += (_, _) => MovePill(animate: false);
        // A short window draws its items closer; the pill follows once they have their new heights.
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(() => MovePill(animate: false), DispatcherPriority.Loaded);
        StateChanged += (_, _) => ShowState();
    }

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

    /// <summary>The shell's page; Classic's Now arrives as the Dashboard, the page that stands in its place here.</summary>
    public Page Page
    {
        get => _shell.Page;
        set => _shell.Page = value == Page.Now ? Page.Dashboard : value;
    }

    public Window Window => this;

    /// <summary>The pill behind the current sidebar item, for a test to check where it sits.</summary>
    internal Border Pill => NavPill;

    /// <summary>How long a failed switch's banner stays unless dismissed.</summary>
    internal TimeSpan ProblemShownFor { get; set; } = TimeSpan.FromSeconds(8);

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

    /// <summary>The maximise button's glyph and name for <paramref name="state"/>: Restore while maximised.</summary>
    internal static (string Glyph, string Name) MaximizeFace(WindowState state)
        => state == WindowState.Maximized ? ("", "Restore") : ("", "Maximize");

    private void ShowState()
    {
        var (glyph, name) = MaximizeFace(WindowState);
        MaximizeButton.Content = glyph;
        MaximizeButton.ToolTip = name;
        AutomationProperties.SetName(MaximizeButton, name);
    }

    /// <summary>Shown, the window shows its own page for Classic's Now; while shown, it keeps doing so.</summary>
    private void ShowOwnPage()
    {
        if (IsVisible && _shell.Page == Page.Now) _shell.Page = Page.Dashboard;   // Midnight's sidebar never selects Now
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Page)) ShowOwnPage();
    }

    /// <summary>The sun for a dark window, the moon for a light one: each button says what it will do, to a screen reader too.</summary>
    private void ShowTheme()
    {
        var dark = _theme.Current == Theme.Dark;
        AutomationProperties.SetName(ThemeButton, dark ? "Light theme" : "Dark theme");
        ThemeButton.Content = dark ? "" : "";
        ThemeButton.ToolTip = dark ? "Light theme" : "Dark theme";
    }

    /// <summary>Slides the pill to the checked item (plan O 0.5), or puts it there at once before the window shows or under reduced motion.</summary>
    private void MovePill(bool animate)
    {
        if (!IsLoaded) return;
        var item = UiTree.Descendants<RadioButton>(NavItems).FirstOrDefault(button => button.IsChecked == true);
        if (item is null || item.ActualHeight == 0)
        {
            NavPill.Opacity = 0;
            return;
        }
        var at = item.TransformToAncestor(Nav).Transform(new Point(0, 0));   // the canvas fills the same cell, so Nav's coordinates are its
        NavPill.Width = item.ActualWidth;
        NavPill.Height = item.ActualHeight;
        Canvas.SetLeft(NavPill, at.X);
        var travel = animate && NavPill.Opacity > 0 ? Motion.Of(Motion.Base) : new Duration(TimeSpan.Zero);
        NavPill.BeginAnimation(Canvas.TopProperty, travel.TimeSpan == TimeSpan.Zero
            ? null
            : new DoubleAnimation(at.Y, travel) { EasingFunction = (IEasingFunction)FindResource("M.Ease.Pill") });
        if (travel.TimeSpan == TimeSpan.Zero) Canvas.SetTop(NavPill, at.Y);
        NavPill.Opacity = 1;
    }

    private void NavChecked(object sender, RoutedEventArgs e) => MovePill(animate: true);

    /// <summary>The other theme, chosen in Settings as its Theme choice is (design §1), so it is saved; Settings applies it.</summary>
    private void ThemeClick(object sender, RoutedEventArgs e)
        => _shell.Settings.Theme = _theme.Current == Theme.Dark ? ThemeChoice.Light : ThemeChoice.Dark;

    /// <summary>A theme chosen here or in Settings: the toggle turns to offer the other. A look chosen by the top bar's
    /// Switch look that didn't open: Settings has already put why on its message line, which is out of sight here.</summary>
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.Theme)) ShowTheme();
        if (e.PropertyName != nameof(SettingsViewModel.Look) || !_switching) return;
        _switching = false;
        if (_shell.Settings.AppMessage is { } problem) ShowProblem(problem);
    }

    /// <summary>The Click comes just before the button's command, which chooses the look through Settings.</summary>
    private void SwitchLookClick(object sender, RoutedEventArgs e) => _switching = true;

    private void ShowProblem(string problem)
    {
        SwitchProblemText.Text = problem;
        SwitchProblem.Visibility = Visibility.Visible;
        SwitchProblem.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.Fade(Motion.Fast)) { EasingFunction = (IEasingFunction)FindResource("M.Ease.In") });
        UIElementAutomationPeer.CreatePeerForElement(SwitchProblem)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        _problemTimer.Stop();
        _problemTimer.Interval = ProblemShownFor;
        _problemTimer.Start();
    }

    private void HideProblem()
    {
        _problemTimer.Stop();
        SwitchProblem.BeginAnimation(OpacityProperty, null);
        SwitchProblem.Visibility = Visibility.Collapsed;
    }

    private void DismissProblem(object sender, RoutedEventArgs e) => HideProblem();

    private void SettingsClick(object sender, RoutedEventArgs e) => _shell.Page = Page.Settings;

    private void HouseholdClick(object sender, RoutedEventArgs e) => _shell.Page = Page.Household;

    private void FeedbackClick(object sender, RoutedEventArgs e) => _feedback();

    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
