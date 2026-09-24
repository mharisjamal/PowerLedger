using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace PowerLedger.App;

/// <summary>
/// The Midnight shell (Midnight look design §1, plan O M1-3), over the same ViewModels as Classic's MainWindow. Closing
/// hides it to the tray, as the App does with Classic's; only <see cref="CloseForSwitch"/> closes it for good.
/// </summary>
internal partial class MidnightWindow : Window, IShellWindow
{
    private readonly ShellViewModel _shell;
    private readonly LookSwitcher _looks;
    private readonly ThemeManager _theme;
    private readonly Action _feedback;
    private readonly Extent _size;
    private readonly Extent _minimum;
    private bool _closeForGood;

    internal MidnightWindow(ShellViewModel shell, LookSwitcher looks, ThemeManager theme, Updater? updates, Action feedback)
    {
        _shell = shell;
        _looks = looks;
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
        ShowTheme();
        if (shell.Page == Page.Now) shell.Page = Page.Dashboard;   // Midnight's sidebar never selects Now
        Loaded += (_, _) => MovePill(animate: false);
        StateChanged += (_, _) => MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    public Rect Bounds
    {
        get => new(Left, Top, Width, Height);
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

    public void CloseForSwitch()
    {
        _closeForGood = true;
        Close();
    }

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

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeForGood || e.Cancel) return;
        e.Cancel = true;   // closing hides to the tray; the service keeps logging either way
        Hide();
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

    /// <summary>The sun for a dark window, the moon for a light one: each button says what it will do.</summary>
    private void ShowTheme()
    {
        var dark = _theme.Current == Theme.Dark;
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

    private void ThemeClick(object sender, RoutedEventArgs e)
    {
        _theme.Choose(_theme.Current == Theme.Dark ? ThemeChoice.Light : ThemeChoice.Dark);
        ShowTheme();
    }

    private void SwitchLookClick(object sender, RoutedEventArgs e) => _looks.Switch(Look.Classic);

    private void SettingsClick(object sender, RoutedEventArgs e) => _shell.Page = Page.Settings;

    private void HouseholdClick(object sender, RoutedEventArgs e) => _shell.Page = Page.Household;

    private void FeedbackClick(object sender, RoutedEventArgs e) => _feedback();

    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
