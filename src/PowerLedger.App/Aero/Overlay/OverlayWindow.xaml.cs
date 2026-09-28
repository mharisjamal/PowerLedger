using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace PowerLedger.App.Aero;

/// <summary>
/// The glass watts overlay (Aero look design §5): a small pill, always on top and out of the taskbar and Alt Tab, with the
/// live watts rolling on a spring from <see cref="NowViewModel.Live"/> and, when chosen, the last 30 seconds under a thin
/// accent line; "No reading" while there is none. Right-click for its corner or Free (drag it anywhere), its opacity, the
/// sparkline and Close, each saved through <see cref="SettingsViewModel.Overlay"/>, the one channel it follows
/// (<see cref="OverlayHost"/> shows and closes it). It is placed in pixels by <see cref="OverlayPlacement"/>, again when
/// the displays, a work area or its DPI change, so it is never stranded off screen. What is behind the pill is blurred
/// when Windows' transparency effects are on and Reduce transparency is off; otherwise the pill is a deeper, opaque glass,
/// so the watts read over anything.
/// </summary>
internal sealed partial class OverlayWindow : Window, IOverlay
{
    private readonly NowViewModel _now;
    private readonly SettingsViewModel _settings;
    private OverlaySettings _shown = OverlaySettings.Default;
    private IntPtr _handle;
    private bool _blurred;
    private bool _closing;

    /// <param name="theme">Paints the pill in Aero's glass as Settings has it (accent, contrast, transparency), live, as the
    /// main window is; none leaves the palette's own, for a test.</param>
    public OverlayWindow(NowViewModel now, SettingsViewModel settings, ThemeManager? theme = null)
    {
        _now = now;
        _settings = settings;
        InitializeComponent();
        if (theme is not null)
        {
            // Once it has a window, so the material's colours go on after whatever else the window merges and win.
            GlassMaterial? material = null;
            SourceInitialized += (_, _) => material ??= GlassMaterial.For(this, settings, theme);
            Closed += (_, _) => material?.Dispose();
        }
        Spark.Seconds = 30;
        ShowLive(animate: false);
        now.PropertyChanged += OnNowChanged;
        settings.PropertyChanged += OnSettingsChanged;
        Closed += (_, _) =>
        {
            now.PropertyChanged -= OnNowChanged;
            settings.PropertyChanged -= OnSettingsChanged;
        };
        SourceInitialized += (_, _) => OnSource();
        Pill.SizeChanged += (_, _) =>
        {
            ClipShadow();
            Shape();
            Place();
        };
        MouseLeftButtonDown += OnDrag;
        ContextMenu = new ContextMenu { Style = (Style)FindResource("A.Menu") };
        ContextMenuOpening += (_, _) => FillMenu(ContextMenu);
    }

    /// <summary>False keeps it where it was put, for a test that draws it off screen; placing moves it onto a display.</summary>
    internal bool Placing { get; set; } = true;

    /// <summary>The settings it shows, as last applied.</summary>
    internal OverlaySettings Shown => _shown;

    /// <summary>The words the pill gives a screen reader: the watts now, or that there is no reading.</summary>
    internal string Reading => AutomationProperties.GetName(Glass);

    public void Apply(OverlaySettings settings)
    {
        var moved = settings.Position != _shown.Position || settings.Left != _shown.Left || settings.Top != _shown.Top;
        var before = _shown;
        _shown = settings;
        Spark.Visibility = settings.Sparkline ? Visibility.Visible : Visibility.Collapsed;
        if (Math.Abs(before.Opacity - settings.Opacity) > .001 || !IsVisible) AeroMotion.Fade(this, OpacityProperty, settings.Opacity, AeroMotion.Hover, AeroMotion.Glide);
        if (moved && IsVisible && settings.Position != OverlayPosition.Free) Hop();
        else Place();
    }

    /// <summary>Shows the pill, rising out of a slightly smaller one on a spring; under reduced motion it fades in.</summary>
    public void ShowOverlay()
    {
        Opacity = _shown.Opacity;
        Show();
        Place();
        AeroMotion.Fade(Pill, OpacityProperty, 1, AeroMotion.Toast, AeroMotion.Glide, from: 0);
        AeroMotion.Move(PillScale, ScaleTransform.ScaleXProperty, 1, AeroMotion.Toast, AeroMotion.Spring, from: AeroMotion.MenuScale);
        AeroMotion.Move(PillScale, ScaleTransform.ScaleYProperty, 1, AeroMotion.Toast, AeroMotion.Spring, from: AeroMotion.MenuScale);
    }

    /// <summary>Fades the pill out, then closes for good.</summary>
    public void CloseOverlay()
    {
        if (_closing) return;
        _closing = true;
        if (!IsVisible)
        {
            Close();
            return;
        }
        AeroMotion.Fade(Pill, OpacityProperty, 0, AeroMotion.ReducedFade, AeroMotion.Glide, done: Close);
    }

    /// <summary>Where the pill is now, as a place to remember.</summary>
    internal (double Left, double Top) Here()
    {
        if (_handle == IntPtr.Zero) return (_shown.Left ?? 0, _shown.Top ?? 0);
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var window = OverlayNative.TopLeft(_handle);
        var room = Room.Margin.Left * scale;
        return OverlayPlacement.Remember(new Point(window.X + room, window.Y + room), OverlayNative.Displays());
    }

    private void OnSource()
    {
        _handle = new WindowInteropHelper(this).Handle;
        OverlayNative.MakeToolWindow(_handle);
        HwndSource.FromHwnd(_handle)?.AddHook(OnMessage);
        ApplyGlass();
    }

    /// <summary>A display added, removed or rescaled, a work area changed (the taskbar moved), or Windows' transparency
    /// switched: placed and glazed again once WPF has taken the change in.</summary>
    private IntPtr OnMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case OverlayNative.WM_DISPLAYCHANGE:
            case OverlayNative.WM_DPICHANGED:
                Dispatcher.BeginInvoke(() =>
                {
                    Shape();
                    Place();
                });
                break;
            case OverlayNative.WM_SETTINGCHANGE:
                Dispatcher.BeginInvoke(() =>
                {
                    ApplyGlass();
                    Place();
                });
                break;
        }
        return IntPtr.Zero;
    }

    private void OnNowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NowViewModel.Live)) ShowLive(animate: true);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.Glass)) ApplyGlass();
    }

    private void ShowLive(bool animate)
    {
        var live = _now.Live;
        var reading = double.IsFinite(live.Watts);
        Number.Visibility = Unit.Visibility = reading ? Visibility.Visible : Visibility.Collapsed;
        NoReading.Visibility = reading ? Visibility.Collapsed : Visibility.Visible;
        if (reading) Number.Set((int)Math.Round(Math.Max(0, live.Watts)), animate && !AeroMotion.Reduced);
        Spark.Samples = live.Spark;
        AutomationProperties.SetName(Glass, reading ? $"{Math.Round(Math.Max(0, live.Watts)):0} watts now" : "No reading");
    }

    /// <summary>The blur of what is behind the pill where Windows allows it; otherwise, or with Reduce transparency, a
    /// deeper glass of the palette's ink, so the watts stay readable over anything.</summary>
    private void ApplyGlass()
    {
        var ink = TryFindResource("A.C.Ink") is Color c ? c : Colors.Black;
        _blurred = false;
        if (_handle != IntPtr.Zero)
        {
            var wanted = OverlayNative.TransparencyOn && !_settings.Glass.ReduceTransparency;
            _blurred = wanted && OverlayNative.Blur(_handle, true, (uint)(0x10 << 24 | ink.B << 16 | ink.G << 8 | ink.R));
            if (!_blurred) OverlayNative.Blur(_handle, false, 0);
        }
        Glass.Frost = new LinearGradientBrush(
            Color.FromArgb(_blurred ? (byte)0x55 : (byte)0xE8, ink.R, ink.G, ink.B),
            Color.FromArgb(_blurred ? (byte)0x66 : (byte)0xEE, ink.R, ink.G, ink.B), 90);
        Shape();
    }

    /// <summary>With the blur on, Windows blurs the whole window; the region trims it to the pill.</summary>
    private void Shape()
    {
        if (_handle == IntPtr.Zero || Pill.ActualWidth <= 0) return;
        if (!_blurred)
        {
            OverlayNative.Shape(_handle, null, 0);
            return;
        }
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var room = Room.Margin.Left;
        OverlayNative.Shape(_handle, new Rect(room * scale, room * scale, Pill.ActualWidth * scale, Pill.ActualHeight * scale), Pill.ActualHeight / 2 * scale);
    }

    /// <summary>The shadow only outside the pill.</summary>
    private void ClipShadow()
    {
        if (Pill.ActualWidth <= 0) return;
        var room = -Shadow.Margin.Left;
        var outside = new RectangleGeometry(new Rect(0, 0, Pill.ActualWidth + (2 * room), Pill.ActualHeight + (2 * room)));
        var pill = new RectangleGeometry(new Rect(room, room, Pill.ActualWidth, Pill.ActualHeight), Pill.ActualHeight / 2, Pill.ActualHeight / 2);
        var clip = new CombinedGeometry(GeometryCombineMode.Exclude, outside, pill);
        clip.Freeze();
        Shadow.Clip = clip;
    }

    private void Place()
    {
        if (!Placing || _handle == IntPtr.Zero || Pill.ActualWidth <= 0) return;
        var place = OverlayPlacement.Place(_shown, OverlayNative.Displays(), new Size(Pill.ActualWidth, Pill.ActualHeight));
        var target = place.Window(Room.Margin.Left);
        var now = OverlayNative.TopLeft(_handle);
        if (Math.Abs(now.X - target.X) < 1 && Math.Abs(now.Y - target.Y) < 1) return;
        OverlayNative.MoveTo(_handle, target);
    }

    /// <summary>To another corner: the pill fades and shrinks a little, moves, and springs back up there, so it reads as
    /// the same pill arriving rather than a window jumping. Under reduced motion only the fade is left.</summary>
    private void Hop()
    {
        AeroMotion.Fade(Pill, OpacityProperty, 0, AeroMotion.ReducedFade, AeroMotion.Glide, done: () =>
        {
            Place();
            AeroMotion.Fade(Pill, OpacityProperty, 1, AeroMotion.Toast, AeroMotion.Glide);
            AeroMotion.Move(PillScale, ScaleTransform.ScaleXProperty, 1, AeroMotion.Toast, AeroMotion.Spring, from: AeroMotion.MenuScale);
            AeroMotion.Move(PillScale, ScaleTransform.ScaleYProperty, 1, AeroMotion.Toast, AeroMotion.Spring, from: AeroMotion.MenuScale);
        });
    }

    /// <summary>A drag moves it and makes it Free, where it was left; a click that doesn't move it changes nothing.</summary>
    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (_handle == IntPtr.Zero) return;
        var before = OverlayNative.TopLeft(_handle);
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;   // the button was already up
        }
        var after = OverlayNative.TopLeft(_handle);
        if (Math.Abs(after.X - before.X) < 2 && Math.Abs(after.Y - before.Y) < 2) return;
        var (left, top) = Here();
        Save(_settings.Overlay with { Position = OverlayPosition.Free, Left = left, Top = top });
    }

    private void Save(OverlaySettings settings) => _settings.Overlay = settings;

    /// <summary>The menu's lines for the settings shown and the place the pill is at now, filled as it opens, so a corner
    /// is taken on the display the pill is on and Free keeps it where it is.</summary>
    private void FillMenu(ContextMenu menu)
    {
        var head = (Style)FindResource("Overlay.MenuHead");
        var item = (Style)FindResource("A.MenuItem");
        menu.Items.Clear();
        foreach (var choice in OverlayMenu.Items(_shown, Here()))
        {
            switch (choice.Kind)
            {
                case OverlayChoiceKind.Separator:
                    menu.Items.Add(new Separator());
                    break;
                case OverlayChoiceKind.Heading:
                    menu.Items.Add(new MenuItem { Header = choice.Header, Style = head });
                    break;
                default:
                    var line = new MenuItem { Header = choice.Header, Style = item, IsCheckable = choice.Apply is not null && choice.Header != "Close overlay", IsChecked = choice.Checked };
                    var apply = choice.Apply!;
                    line.Click += (_, _) => Save(apply(_settings.Overlay));
                    menu.Items.Add(line);
                    break;
            }
        }
    }

    /// <summary>For a test: the menu as it would open now.</summary>
    internal ContextMenu OpenMenu()
    {
        FillMenu(ContextMenu);
        return ContextMenu;
    }
}
