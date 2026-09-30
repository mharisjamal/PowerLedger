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
    private readonly GlassMaterial _glass;
    private readonly Backdrop _backdrop;

    /// <param name="looks">The switcher the App opened this window through (plan O 0.4), as Midnight's takes it; a look
    /// chosen in the window goes through <see cref="SettingsViewModel.Look"/> instead, so the choice is saved.</param>
    /// <param name="theme">Which theme is on, for the views that ask.</param>
    /// <param name="updates">For the update card, and the blocking panel while an update is required (Plan Q §3).</param>
    /// <param name="feedback">Opens the Send feedback window.</param>
    /// <param name="intro">The Aero intro video (Plan S intro), played before the banner on the first open after the move
    /// and from Settings; none leaves the banner as it was.</param>
    internal AeroWindow(ShellViewModel shell, LookSwitcher looks, ThemeManager theme, Updater updates, Action feedback, AeroIntro? intro = null)
    {
        _shell = shell;
        _intro = intro;
        _introDue = intro is { Due: true };
        Switcher = looks;
        Themes = theme;
        Updates = updates;
        Feedback = feedback;
        InitializeComponent();
        DataContext = shell;
        UpdateCard.DataContext = updates;
        VersionLine.DataContext = updates;
        // The glass itself (Aero look design §3): the A.* tokens on this window, repainted live as Settings' Glass or the
        // theme changes, never among the application's resources.
        _glass = GlassMaterial.For(this, shell.Settings, theme);
        // What is behind the glass (0.10.6, the owner's choice): what is really behind the window, the desktop and the
        // windows open on it, live, through the window's own alpha, under every style's tint. The window is only its glass
        // (0.10.1, free-form), so the same desktop shows between the panes.
        _backdrop = new Backdrop(this, Room, _glass, () => theme.Current, onScreen: true, stage: Stage);
        UpdateRequiredCover.DataContext = updates;
        UpdateCover.Attach(UpdateRequiredCover, [Side, TopBar, Banners, Pages], UpdateNowButton);
        _size = new Extent(Width, Height);
        _minimum = new Extent(MinWidth, MinHeight);
        ShowIntro();
        ShowPcs();
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
            StopIntroVideo();
            _backdrop.Dispose();
            _glass.Dispose();
        };
        if (intro is not null) intro.Finished += OnIntroVideoFinished;
        _problemTimer.Tick += (_, _) => HideProblem();
        IsVisibleChanged += (_, _) => OnShown();
        Loaded += (_, _) =>
        {
            Fit();
            ShowState();
            MovePill(animate: false);
            QueueReveal();
            // Once laid out, ahead of anything idle: the dialog opens over the stage as its panes rise.
            Dispatcher.BeginInvoke(AutoPlayIntroVideo, DispatcherPriority.Loaded);
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
    internal int IntroPanes => 1;

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
        StartShaping();
        // Held before the first frame is drawn, so the panes never flash in fully formed ahead of the reveal.
        if (!AeroMotion.Reduced) HoldReveal();
        base.OnSourceInitialized(e);
    }

    /// <summary>As MainWindow.FitToScreen: once, before the window first shows, on the monitor Windows chose, at that
    /// monitor's DPI; then the layout last left (0.10.1): spread over the work area unless the smaller one was chosen.</summary>
    private void FitToScreen()
    {
        if (WindowStartupLocation != WindowStartupLocation.CenterScreen) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (HwndSource.FromHwnd(handle)?.CompositionTarget is not { } target) return;
        Bounds ToUnits(System.Drawing.Rectangle pixels)
        {
            var topLeft = target.TransformFromDevice.Transform(new Point(pixels.Left, pixels.Top));
            var bottomRight = target.TransformFromDevice.Transform(new Point(pixels.Right, pixels.Bottom));
            return new Bounds(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
        }
        FitTo(ToUnits(System.Windows.Forms.Screen.FromHandle(handle).WorkingArea));
        OpenLayout(new Bounds(Left, Top, Width, Height), ToUnits);
    }

    /// <summary>Shown: the page it has for Classic's Now, the Dashboard's Aero figures, and a first read of what the sidebar,
    /// the bell and This month show from other pages (the household's PCs, today's alerts) (design §1).</summary>
    private void OnShown()
    {
        ShowOwnPage();
        AnnounceIntro();
        if (!IsVisible) return;
        if (_shell.Dashboard is { } dashboard) dashboard.Detailed = true;
        // Shown again, from the tray: the reveal plays every time the window opens (0.10.6), not only the first.
        if (_shown && IsLoaded) QueueReveal();
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
        if (e.PropertyName == nameof(ShellViewModel.IsSetup)) AutoPlayIntroVideo();   // the wizard done, the intro's turn
        if (e.PropertyName is nameof(ShellViewModel.Page) or nameof(ShellViewModel.IsSetup))
        {
            Dispatcher.BeginInvoke(() => MovePill(animate: true), DispatcherPriority.Loaded);
            FollowShapeFor(AeroMotion.MoveMs(PageArrives));   // the page's panes glide up into place
        }
    }

    // ---------------------------------------------------------------- layout

    /// <summary>The sidebar narrows to its icons under <see cref="NarrowBelow"/>; the pill follows once the items have
    /// their new sizes.</summary>
    private void Fit()
    {
        var narrow = ActualWidth > 0 && ActualWidth < NarrowBelow;
        SideColumn.Width = new GridLength(narrow ? SideNarrow : SideWide);
        Side.Padding = narrow ? new Thickness(9, 19, 9, 19) : new Thickness(15, 19, 15, 19);   // the demo's padding and its 1 px border
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
        Grip.Visibility = WindowState == WindowState.Normal ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- the intro

    /// <summary>True from the moment the panes are held at the reveal's start until the reveal begins: a Dashboard that
    /// loads in that time holds its panes too and waits for the window, rather than starting on its own.</summary>
    internal bool RevealHeld { get; private set; }

    /// <summary>How many times the reveal has begun, for a test.</summary>
    internal int Reveals { get; private set; }

    /// <summary>How many frames go to the screen with the panes held before the reveal begins.</summary>
    internal const int HeldFrames = 2;

    private int _heldFrames;
    private bool _waitingForFrames;

    /// <summary>
    /// The reveal, every time the window opens (0.10.6): at start, from the tray and after a look switch. The panes are
    /// held at its start (clear, 22 px down, at 95 %) before anything is painted, so nothing flashes in fully formed; it
    /// begins only once the work queued for the show is done and the window has really painted with them held, so a slow
    /// start can't swallow its first half second, as it did when it began as the window loaded. Under reduced motion
    /// nothing is held and nothing travels: the panes are simply there.
    /// </summary>
    internal void QueueReveal()
    {
        IntroPending = true;
        if (AeroMotion.Reduced)
        {
            RevealHeld = false;
            Reveal();
            return;
        }
        HoldReveal();
        if (_waitingForFrames) return;
        _waitingForFrames = true;
        _heldFrames = 0;
        // After everything the show queued (layout, the first paint, the pages' first figures): then whole frames.
        Dispatcher.BeginInvoke(() => CompositionTarget.Rendering += OnHeldFrame, DispatcherPriority.ContextIdle);
    }

    /// <summary>The window's panes, and the page's, at the reveal's start.</summary>
    private void HoldReveal()
    {
        RevealHeld = true;
        foreach (var pane in RevealPanes) Held(pane, AeroMotion.PaneRise, AeroMotion.PaneScale);
        foreach (var content in RevealContents) Held(content, AeroMotion.ContentRise, 1);
        if (Dashboard is { } dashboard) dashboard.HoldIntro();
        else if (Pages.Showing is { } page) Held(page, AeroMotion.PaneRise, AeroMotion.PaneScale);
    }

    private void OnHeldFrame(object? sender, EventArgs e)
    {
        if (++_heldFrames < HeldFrames) return;
        CompositionTarget.Rendering -= OnHeldFrame;
        _waitingForFrames = false;
        if (!IsLoaded || !IsVisible) return;   // closed or hidden again meanwhile: the next show holds and waits anew
        Reveal();
    }

    /// <summary>The reveal itself: the window's panes, then the page's.</summary>
    private void Reveal()
    {
        RevealHeld = false;
        Reveals++;
        PlayIntro();
        if (Dashboard is { IsLoaded: true } dashboard) dashboard.PlayIntro(IntroPanes);
        else if (Pages.Showing is { IsLoaded: true } page and not Aero.DashboardView) Rise(page, IntroPanes);
    }

    /// <summary>The window's own panes that rise, and what glides in inside them. The demo's search is not a rising pane:
    /// it glides in with the top bar, as its other pills do, so the page's first pane follows the sidebar at once.</summary>
    private FrameworkElement[] RevealPanes => [Side];

    private FrameworkElement[] RevealContents => [SideContent, TopBar];

    /// <summary>Holds <paramref name="element"/> at the reveal's start: clear, <paramref name="rise"/> down and at
    /// <paramref name="scale"/>, with no clock running on it.</summary>
    internal static void Held(FrameworkElement element, double rise, double scale)
    {
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = 0;
        element.RenderTransformOrigin = new Point(.5, .5);
        element.RenderTransform = new TransformGroup { Children = { new ScaleTransform(scale, scale), new TranslateTransform(0, rise) } };
    }

    /// <summary>
    /// The demo's opening (design §1): the sidebar and the search rise in on the spring, 75 ms apart, the page's panes
    /// after them (the Dashboard carries on the stagger), then the content glides in and the charts draw. Under reduced
    /// motion nothing travels: the panes are simply there.
    /// </summary>
    private void PlayIntro()
    {
        var panes = RevealPanes;
        for (var i = 0; i < panes.Length; i++) Rise(panes[i], i);
        var contents = RevealContents;
        for (var i = 0; i < contents.Length; i++) Glide(contents[i], i);
        // The frost follows the panes while they rise, then stops; so does the window's shape, until the page's last pane
        // (the Dashboard's carry on the stagger) is in place.
        AlignFrostFor(AeroMotion.MoveMs(AeroMotion.ContentDelay + AeroMotion.ContentIn + 8 * AeroMotion.Stagger));
        FollowShapeFor(AeroMotion.MoveMs(AeroMotion.PaneIn + 12 * AeroMotion.Stagger));
        Dispatcher.BeginInvoke(() => IntroPending = false, DispatcherPriority.ContextIdle);
    }

    /// <summary>The Dashboard on show, if it is.</summary>
    private Aero.DashboardView? Dashboard => Pages.Showing as Aero.DashboardView;

    /// <summary>
    /// The demo's Replay intro: the opening again, as when the window first shows (the sidebar and the search rise, the
    /// Dashboard's panes after them, the content glides in, the charts draw and the figures count up). It closes a
    /// dialog, ends a tour and brings the camera back first, and brings the Dashboard up, whose panes carry the intro on.
    /// </summary>
    internal void ReplayIntro()
    {
        if (_shell.IsSetup) return;
        CloseModal();
        Dashboard?.StopTour();
        Dashboard?.Unfocus();
        IntroPending = true;
        PlayIntro();
        if (_shell.Page != Page.Dashboard) _shell.Page = Page.Dashboard;   // the Dashboard arriving sees the intro pending
        else Dashboard?.PlayIntro(IntroPanes);
    }

    /// <summary>The demo's Play tour, on the Dashboard (brought up first): the camera on each of its panes in turn.</summary>
    internal void PlayTour()
    {
        if (_shell.IsSetup) return;
        CloseModal();
        if (_shell.Page != Page.Dashboard) _shell.Page = Page.Dashboard;
        Dispatcher.BeginInvoke(() => Dashboard?.PlayTour(), DispatcherPriority.ContextIdle);   // once the page is laid out
    }

    private void ReplayIntroClick(object sender, RoutedEventArgs e) => ReplayIntro();

    private void PlayTourClick(object sender, RoutedEventArgs e) => PlayTour();

    /// <summary>Lines the wallpaper's frost up with the panes every frame for <paramref name="ms"/>, while they move (the
    /// intro, the camera), then stops: nothing runs at rest (Plan S G5).</summary>
    internal void AlignFrostFor(double ms)
    {
        if (ms > 0) _backdrop.AlignFor(TimeSpan.FromMilliseconds(ms));
        FollowShapeFor(ms);
    }

    /// <summary>How long a page arriving glides (AeroPageHost's), with a frame to spare.</summary>
    private const double PageArrives = 500;

    /// <summary>What the backdrop chose, for a test.</summary>
    internal BackdropKind BackdropKind => _backdrop.Kind;

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
            pane.RenderTransform = Transform.Identity;
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
        if (AeroMotion.Reduced)
        {
            // Simply there, whatever held it.
            content.BeginAnimation(OpacityProperty, null);
            content.Opacity = 1;
            content.RenderTransform = Transform.Identity;
            return;
        }
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
        if (e.PropertyName == nameof(InsightsViewModel.Alerts)) ShowBell();
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
        => _shell.Insights?.Alerts ?? [];

    private void ShowBell()
    {
        var news = Bell.HasNews(_shell.Household.PendingApprovals, Alerts.Count);
        BellDot.Visibility = news ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetHelpText(BellButton, news ? "Something is waiting" : "Nothing is waiting");
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

    /// <summary>The banner shows until the look has been introduced, after the intro video when that is due (Plan S
    /// intro); its line names the look Switch back returns to.</summary>
    private void ShowIntro()
    {
        LookIntro.Visibility = _shell.Settings.LookIntroduced || _introDue || IntroPlaying ? Visibility.Collapsed : Visibility.Visible;
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
    /// here; the banner follows Settings.</summary>
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.LookIntroduced)) ShowIntro();
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
