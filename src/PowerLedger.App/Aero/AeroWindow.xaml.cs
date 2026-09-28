using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PowerLedger.App.Aero;

namespace PowerLedger.App;

/// <summary>
/// The Aero shell (Aero look design §1 and §2, Plan S D1 and D2), over the same ViewModels as Classic's and Midnight's
/// windows. It keeps Task 0's contract with the look switcher (the <see cref="IShellWindow"/> members, fitting to the
/// screen it opens on, the page mapping) and draws the demo's stage: the sidebar and its pill, the top bar, the banners,
/// the page host, the intro, and the menus, dialogs and toasts over them. A close is a close here, as in the other looks:
/// the App's Closing handler hides the current window to the tray (plan O 0.4).
/// </summary>
internal partial class AeroWindow : Window, IShellWindow
{
    /// <summary>Under this width the sidebar is a column of icons (design §1: the demo's stage, kept usable at 960 px).</summary>
    internal const double NarrowBelow = 1100;

    private const double SideWide = 240;
    private const double SideNarrow = 76;

    private readonly ShellViewModel _shell;
    private readonly Extent _size;
    private readonly Extent _minimum;
    private readonly DispatcherTimer _problemTimer = new();
    private bool _switching;
    private bool _introAnnounced;
    private bool _shown;

    /// <param name="looks">The switcher the App opened this window through (plan O 0.4), as Midnight's takes it; a look
    /// chosen in the window goes through <see cref="SettingsViewModel.Look"/> instead, so the choice is saved.</param>
    /// <param name="theme">Which theme is on, for the views that ask.</param>
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
        UpdateCard.DataContext = updates;
        UpdateRequiredCover.DataContext = updates;
        UpdateCover.Attach(UpdateRequiredCover, [Side, TopBar, Banners, Pages], UpdateNowButton);
        _size = new Extent(Width, Height);
        _minimum = new Extent(MinWidth, MinHeight);
        ShowIntro();
        ShowPcs();
        ShowOverlay();
        ShowBell();
        // Nothing in the shell changes until the window shows: a switch whose new window fails to show puts the page back
        // as it was, which a window that had already mapped it here would have changed under it (as MidnightWindow).
        shell.PropertyChanged += OnShellChanged;
        shell.Settings.PropertyChanged += OnSettingsChanged;
        shell.Household.PropertyChanged += OnHouseholdChanged;
        shell.Now.PropertyChanged += OnNowChanged;
        if (shell.Insights is { } insights) insights.PropertyChanged += OnInsightsChanged;
        Closed += (_, _) =>
        {
            // The shell and its screens outlive a window a switch closes.
            shell.PropertyChanged -= OnShellChanged;
            shell.Settings.PropertyChanged -= OnSettingsChanged;
            shell.Household.PropertyChanged -= OnHouseholdChanged;
            shell.Now.PropertyChanged -= OnNowChanged;
            if (shell.Insights is { } insights) insights.PropertyChanged -= OnInsightsChanged;
            if (shell.Dashboard is { } dashboard) dashboard.Detailed = false;
            _problemTimer.Stop();
            _toastTimer?.Stop();
        };
        _problemTimer.Tick += (_, _) => HideProblem();
        IsVisibleChanged += (_, _) => OnShown();
        Loaded += (_, _) =>
        {
            Fit();
            MovePill(animate: false);
            PlayIntro();
        };
        SizeChanged += (_, _) => Fit();
        StateChanged += (_, _) => ShowState();
        PreviewKeyDown += OnKey;
    }

    /// <summary>What the constructor was given: the switcher, the theme, the updater and the feedback window.</summary>
    internal LookSwitcher Switcher { get; }

    internal ThemeManager Themes { get; }

    internal Updater Updates { get; }

    internal Action Feedback { get; }

    /// <summary>True until the window's own intro has begun and settled into its first frame: a Dashboard that loads in
    /// that time rises with it (design §1, "the panels rise in on a spring, 75 ms apart"); one opened later only draws its
    /// charts.</summary>
    internal bool IntroPending { get; private set; } = true;

    /// <summary>How many of the window's own panes rise before a page's, so a page's panes carry on the stagger.</summary>
    internal int IntroPanes => 2;

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

    /// <summary>The sidebar's pill, for a test to check where it sits.</summary>
    internal FrameworkElement Pill => NavIndicator;

    /// <summary>The page host, for a test to find the view on show.</summary>
    internal AeroPageHost PageHost => Pages;

    /// <summary>How long a failed switch's banner stays unless dismissed.</summary>
    internal TimeSpan ProblemShownFor { get; set; } = TimeSpan.FromSeconds(8);

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

    /// <summary>Shown: the page it has for Classic's Now, the Dashboard's Aero figures, and a first read of what the sidebar,
    /// the bell and This month show from other pages (the household's PCs, today's alerts) (design §1).</summary>
    private void OnShown()
    {
        ShowOwnPage();
        AnnounceIntro();
        if (!IsVisible) return;
        if (_shell.Dashboard is { } dashboard) dashboard.Detailed = true;
        if (_shown) return;
        _shown = true;
        _shell.Household.Refresh();
        _shell.Insights?.Refresh();
    }

    /// <summary>Shown, the window shows its own page for Classic's Now; while shown, it keeps doing so.</summary>
    private void ShowOwnPage()
    {
        if (IsVisible && _shell.Page == Page.Now) _shell.Page = Page.Dashboard;   // Aero's sidebar never selects Now
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Page)) ShowOwnPage();
        if (e.PropertyName is nameof(ShellViewModel.Page) or nameof(ShellViewModel.IsSetup))
            Dispatcher.BeginInvoke(() => MovePill(animate: true), DispatcherPriority.Loaded);
    }

    // ---------------------------------------------------------------- layout

    /// <summary>The sidebar narrows to its icons under <see cref="NarrowBelow"/>; the pill follows once the items have
    /// their new sizes.</summary>
    private void Fit()
    {
        var narrow = ActualWidth > 0 && ActualWidth < NarrowBelow;
        SideColumn.Width = new GridLength(narrow ? SideNarrow : SideWide);
        Side.Padding = narrow ? new Thickness(8, 18, 8, 18) : new Thickness(14, 18, 14, 18);
        Dispatcher.BeginInvoke(() => MovePill(animate: false), DispatcherPriority.Loaded);
    }

    /// <summary>Springs the glass pill to the checked item (design §1: it morphs from item to item), or puts it there at
    /// once before the window shows or under reduced motion.</summary>
    private void MovePill(bool animate)
    {
        if (!IsLoaded) return;
        var item = NavItems.Children.OfType<RadioButton>().FirstOrDefault(button => button.IsChecked == true);
        if (item is null || item.ActualHeight == 0)
        {
            NavIndicator.Opacity = 0;
            return;
        }
        var y = item.TranslatePoint(new Point(0, 0), Nav).Y;
        NavIndicator.Height = item.ActualHeight;
        AeroMotion.Move(NavY, TranslateTransform.YProperty, y, animate && NavIndicator.Opacity > 0 ? AeroMotion.NavPill : 0, AeroMotion.Spring);
        NavIndicator.Opacity = 1;
    }

    /// <summary>An item checked, by the pointer, the keyboard or the page changing: the pill follows. The item's binding
    /// has already chosen the page.</summary>
    private void NavChecked(object sender, RoutedEventArgs e) => MovePill(animate: true);

    /// <summary>The maximise button's icon and name for the state: Restore while maximised.</summary>
    private void ShowState()
    {
        var max = WindowState == WindowState.Maximized;
        MaximizeIcon.Data = (System.Windows.Media.Geometry)FindResource(max ? "A.I.Restore" : "A.I.Maximize");
        MaximizeButton.ToolTip = max ? "Restore" : "Maximize";
        AutomationProperties.SetName(MaximizeButton, max ? "Restore" : "Maximize");
    }

    // ---------------------------------------------------------------- the intro

    /// <summary>
    /// The demo's opening (design §1): the sidebar and the search rise in on the spring, 75 ms apart, the page's panes
    /// after them (the Dashboard carries on the stagger), then the content glides in and the charts draw. Under reduced
    /// motion nothing travels: the panes are simply there.
    /// </summary>
    private void PlayIntro()
    {
        FrameworkElement[] panes = [Side, SearchGlass];
        for (var i = 0; i < panes.Length; i++) Rise(panes[i], i);
        FrameworkElement[] contents = [SideContent, TopBar];
        for (var i = 0; i < contents.Length; i++) Glide(contents[i], i);
        Dispatcher.BeginInvoke(() => IntroPending = false, DispatcherPriority.ContextIdle);
    }

    /// <summary>A pane rising into place: from 22 px down and 95 %, fading in, on the spring, <paramref name="index"/>
    /// staggers late.</summary>
    internal static void Rise(FrameworkElement pane, int index)
    {
        var scale = new ScaleTransform();
        var shift = new TranslateTransform();
        pane.RenderTransformOrigin = new Point(.5, .5);
        pane.RenderTransform = new TransformGroup { Children = { scale, shift } };
        if (AeroMotion.Reduced)
        {
            AeroMotion.Fade(pane, OpacityProperty, 1, AeroMotion.ReducedFade, AeroMotion.Glide, from: 0);
            return;
        }
        var delay = index * AeroMotion.Stagger;
        AeroMotion.Move(pane, OpacityProperty, 1, AeroMotion.PaneIn, AeroMotion.Spring, delay, from: 0);
        AeroMotion.Move(shift, TranslateTransform.YProperty, 0, AeroMotion.PaneIn, AeroMotion.Spring, delay, from: AeroMotion.PaneRise);
        AeroMotion.Move(scale, ScaleTransform.ScaleXProperty, 1, AeroMotion.PaneIn, AeroMotion.Spring, delay, from: AeroMotion.PaneScale);
        AeroMotion.Move(scale, ScaleTransform.ScaleYProperty, 1, AeroMotion.PaneIn, AeroMotion.Spring, delay, from: AeroMotion.PaneScale);
    }

    /// <summary>A pane's content gliding in after the panes: 8 px up and in, on the glide.</summary>
    internal static void Glide(FrameworkElement content, int index)
    {
        if (AeroMotion.Reduced) return;
        var shift = new TranslateTransform();
        content.RenderTransform = shift;
        var delay = AeroMotion.ContentDelay + index * AeroMotion.Stagger;
        AeroMotion.Move(content, OpacityProperty, 1, AeroMotion.ContentIn, AeroMotion.Glide, delay, from: 0);
        AeroMotion.Move(shift, TranslateTransform.YProperty, 0, AeroMotion.ContentIn, AeroMotion.Glide, delay, from: AeroMotion.ContentRise);
    }

    // ---------------------------------------------------------------- the sidebar's PCs, the bell, the overlay

    private void OnHouseholdChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HouseholdViewModel.Members) or nameof(HouseholdViewModel.PendingApprovals))
        {
            ShowPcs();
            ShowBell();
        }
    }

    private void OnNowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NowViewModel.Live)) ShowPcs();
    }

    private void OnInsightsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InsightsViewModel.Report)) ShowBell();
    }

    /// <summary>"Your PCs" and the household button's line, redrawn only when what they say has changed.</summary>
    private void ShowPcs()
    {
        var rows = YourPcs.Rows(_shell.Household.Members, _shell.Now.Live.Watts, System.Globalization.CultureInfo.CurrentCulture);
        if (Pcs.ItemsSource is not IReadOnlyList<PcRow> shown || !shown.SequenceEqual(rows)) Pcs.ItemsSource = rows;
        HouseholdLine.Text = YourPcs.Summary(rows.Count);
    }

    /// <summary>Today's alerts, three at the most, and the bell's dot while anything waits.</summary>
    internal IReadOnlyList<UsageAnomaly> Alerts
        => Bell.Alerts(_shell.Insights?.Report?.Anomalies ?? [], DateTimeOffset.Now, TimeZoneInfo.Local);

    private void ShowBell()
    {
        var news = Bell.HasNews(_shell.Household.PendingApprovals, Alerts.Count);
        BellDot.Visibility = news ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetHelpText(BellButton, news ? "Something is waiting" : "Nothing is waiting");
    }

    private void ShowOverlay() => OverlayButton.IsChecked = _shell.Settings.Overlay.Enabled;

    /// <summary>The overlay on or off, saved through Settings, where the overlay listens (Plan S 0.4).</summary>
    private void OverlayClick(object sender, RoutedEventArgs e)
    {
        var overlay = _shell.Settings.Overlay;
        _shell.Settings.Overlay = overlay with { Enabled = !overlay.Enabled };
        ShowOverlay();
    }

    private void PcClick(object sender, RoutedEventArgs e) => _shell.Page = Page.Household;

    // ---------------------------------------------------------------- search

    /// <summary>The top bar's search narrows the Dashboard's history table (design §1), and brings the Dashboard up to
    /// show it.</summary>
    private void SearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_shell.Dashboard is not { } dashboard) return;
        dashboard.HistoryQuery = Search.Text;
        if (Search.Text.Length > 0 && !_shell.IsSetup && _shell.Page != Page.Dashboard) _shell.Page = Page.Dashboard;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control && !_shell.IsSetup)
        {
            e.Handled = true;
            Search.Focus();
            Search.SelectAll();
        }
        else if (e.Key == Key.Escape && _modal is not null)
        {
            e.Handled = true;
            CloseModal();
        }
        else if (e.Key == Key.Escape && Search.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            Search.Clear();
            MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        }
    }

    // ---------------------------------------------------------------- the new look, and switching looks

    /// <summary>The banner shows until the look has been introduced; its line names the look Switch back returns to.</summary>
    private void ShowIntro()
    {
        LookIntro.Visibility = _shell.Settings.LookIntroduced ? Visibility.Collapsed : Visibility.Visible;
        var back = AeroLooks.SwitchBackTo(_shell.Settings.LookBeforeAero);
        LookIntroLine.Text = $"Liquid glass, with Insights and a watts overlay. Prefer {back}? Switch back any time here, or in Settings.";
        SwitchBackButton.ToolTip = $"Back to the {back} look";
    }

    /// <summary>A screen reader hears the banner once, when the window it is in first shows with it.</summary>
    private void AnnounceIntro()
    {
        if (_introAnnounced || !IsVisible || LookIntro.Visibility != Visibility.Visible) return;
        _introAnnounced = true;
        UIElementAutomationPeer.CreatePeerForElement(LookIntro)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    /// <summary>A look chosen here that didn't open: Settings has put why on its message line, out of sight, so it shows
    /// here; the banner and the overlay button follow Settings.</summary>
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.LookIntroduced)) ShowIntro();
        if (e.PropertyName == nameof(SettingsViewModel.Overlay)) ShowOverlay();
        if (e.PropertyName != nameof(SettingsViewModel.Look) || !_switching) return;
        _switching = false;
        if (_shell.Settings.AppMessage is { } problem) ShowProblem(problem);
    }

    /// <summary>Chooses <paramref name="look"/> through Settings, so it is saved and replaces this window, or says why not.</summary>
    internal void SwitchTo(Look look)
    {
        _switching = true;
        _shell.Settings.Look = look;
        _switching = false;   // a look already chosen raises nothing to clear it
    }

    /// <summary>Switch back: the look the move to Aero left, Midnight when none, the banner retired first whatever the
    /// switch does (design §1).</summary>
    private void SwitchBackClick(object sender, RoutedEventArgs e)
    {
        var back = AeroLooks.SwitchBackTo(_shell.Settings.LookBeforeAero);
        _shell.Settings.IntroduceLook();
        SwitchTo(back);
    }

    private void GotItClick(object sender, RoutedEventArgs e) => _shell.Settings.IntroduceLook();

    private void ShowProblem(string problem)
    {
        SwitchProblemText.Text = problem;
        SwitchProblem.Visibility = Visibility.Visible;
        AeroMotion.Fade(SwitchProblem, OpacityProperty, 1, AeroMotion.Toast, AeroMotion.Glide, from: 0);
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

    // ---------------------------------------------------------------- the caption

    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
