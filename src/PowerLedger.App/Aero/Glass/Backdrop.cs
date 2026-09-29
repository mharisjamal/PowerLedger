using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Win32;

namespace PowerLedger.App.Aero;

/// <summary>What shows behind Aero's glass on this PC now (Aero look design §3, §6).</summary>
internal enum BackdropKind
{
    /// <summary>The desktop itself, blurred by the DWM system backdrop (acrylic) under a light wash.</summary>
    SeeThrough,

    /// <summary>The user's wallpaper, sharp around the panes and frosted once under them.</summary>
    Wallpaper,

    /// <summary>The palette's plain ground.</summary>
    Plain,

    /// <summary>The Windows 11 bloom the approved video shows (0.10.3), mapped across the stage and frosted once under the
    /// panes, whatever the desktop is (AeroBloom).</summary>
    Bloom,
}

/// <summary>
/// The backdrop decision (Aero look design §6), pure: Aero bloom whenever it is chosen (it is always there: Windows' own
/// picture, or one drawn like it); the desktop only where Windows can blur it (the system backdrop, Windows 11 22H2 and
/// later, with Transparency effects on) and the user wants it and hasn't asked for less transparency; otherwise the
/// wallpaper when there is one; otherwise plain. Never a transparent window over nothing.
/// </summary>
internal static class BackdropRules
{
    public static BackdropKind Choose(GlassBackdrop wanted, bool reduceTransparency, bool hasSystemBackdrop, bool transparencyOn, bool hasWallpaper)
    {
        if (wanted == GlassBackdrop.Plain) return BackdropKind.Plain;
        if (wanted == GlassBackdrop.Bloom) return BackdropKind.Bloom;
        if (wanted == GlassBackdrop.Desktop && !reduceTransparency && hasSystemBackdrop && transparencyOn) return BackdropKind.SeeThrough;
        return hasWallpaper ? BackdropKind.Wallpaper : BackdropKind.Plain;
    }
}

/// <summary>Where the wallpaper is when Windows names none (a slideshow), pure.</summary>
internal static class WallpaperRules
{
    /// <summary>Explorer's BackgroundType for a solid colour.</summary>
    public const int SolidColour = 1;

    public static string? Fallback(int? backgroundType, string? transcoded) => backgroundType == SolidColour ? null : transcoded;
}

/// <summary>The few Win32 and DWM calls Aero's backdrop needs; thin, each failure harmless.</summary>
internal static class AeroNative
{
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMWCP_ROUND = 2;
    public const int DWMSBT_NONE = 1;
    public const int DWMSBT_TRANSIENTWINDOW = 3;
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
    public const int SPI_SETDESKWALLPAPER = 0x0014;
    public const int SPI_GETDESKWALLPAPER = 0x0073;

    /// <summary>Windows 11 22H2 or later: the build that takes DWMWA_SYSTEMBACKDROP_TYPE.</summary>
    public static bool HasSystemBackdrop => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>Settings, Personalization, Colors, Transparency effects. Off means DWM paints every backdrop solid.</summary>
    public static bool TransparencyOn
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("EnableTransparency") is not int value || value != 0;
            }
            catch (System.Security.SecurityException)
            {
                return false;
            }
        }
    }

    public static int SetDword(IntPtr hwnd, int attribute, int value) => DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));

    public static void ExtendFrame(IntPtr hwnd, int all)
    {
        var margins = new Margins { Left = all, Right = all, Top = all, Bottom = all };
        _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    /// <summary>The wallpaper's file, or null for none. Windows names it through SPI_GETDESKWALLPAPER; a slideshow names
    /// none, and then the picture on screen is the copy Windows keeps (Themes\TranscodedWallpaper), unless the background
    /// is a solid colour, whose leftover copy would be stale.</summary>
    public static string? WallpaperPath()
    {
        var buffer = new char[520];
        if (SystemParametersInfo(SPI_GETDESKWALLPAPER, buffer.Length, buffer, 0))
        {
            var end = Array.IndexOf(buffer, '\0');
            var path = new string(buffer, 0, end >= 0 ? end : buffer.Length);
            if (path.Length > 0 && System.IO.File.Exists(path)) return path;
        }
        return WallpaperRules.Fallback(BackgroundType(), TranscodedWallpaper());
    }

    private static int? BackgroundType()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers");
            return key?.GetValue("BackgroundType") as int?;
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string? TranscodedWallpaper()
    {
        var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
        return System.IO.File.Exists(path) ? path : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(int action, int param, char[] buffer, int winIni);
}

/// <summary>
/// Aero's backdrop on a window (Aero look design §3, Plan S G4): chooses see-through, wallpaper or plain
/// (<see cref="BackdropRules"/>), sets the window up for it, and keeps it right as Windows' transparency, theme or
/// wallpaper change (WM_SETTINGCHANGE, WM_DWMCOLORIZATIONCOLORCHANGED) and as the Glass settings do. The scene element
/// is the window's layer behind everything: it takes the wash, the sharp wallpaper or the plain ground, and the parallax
/// shift. Nothing here runs per frame: the frost is lined up with the panes on layout, size and parallax changes only.
/// </summary>
internal sealed class Backdrop : IDisposable
{
    private readonly Window _window;
    private readonly Border _scene;
    private readonly GlassMaterial _material;
    private readonly Func<Theme> _theme;
    private readonly WallpaperFrost _frost;
    private readonly Parallax _parallax;
    private readonly bool _onScreen;
    private HwndSource? _source;
    private bool _disposed;

    /// <param name="window">The Aero window, before or after its handle exists.</param>
    /// <param name="scene">The layer behind everything, filling the window.</param>
    /// <param name="tilt">What tilts with the pointer (the panes' container), or null for none.</param>
    /// <param name="onScreen">Aero's free-form window (0.10.1): the wallpaper lined up with the screen behind the window's
    /// shape, and no parallax. Its surfaces float over the real desktop, which never drifts, so a scene drifting behind
    /// the glass would part the frost from what really lies behind it; and a tilted pane can't be a window region's
    /// rounded rectangle.</param>
    /// <param name="stage">What Aero bloom is mapped across (the window's stage); null maps it across the scene.</param>
    public Backdrop(Window window, Border scene, GlassMaterial material, Func<Theme> theme, FrameworkElement? tilt = null, bool onScreen = false,
        FrameworkElement? stage = null)
    {
        _window = window;
        _scene = scene;
        _material = material;
        _theme = theme;
        _onScreen = onScreen;
        _frost = new WallpaperFrost(window, scene, onScreen, stage);
        _parallax = new Parallax(window, scene, tilt, _frost.Align);
        _material.Changed += OnGlassChanged;
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Hook();
        else window.SourceInitialized += OnSourceInitialized;
        Apply();
    }

    public BackdropKind Kind { get; private set; } = BackdropKind.Plain;

    /// <summary>Raised when the kind changes, for the window's own chrome.</summary>
    public event Action<BackdropKind>? KindChanged;

    public Parallax Parallax => _parallax;

    public WallpaperFrost Frost => _frost;

    /// <summary>Lines the frost up every frame for a while (the intro, a camera move), then stops.</summary>
    public void AlignFor(TimeSpan duration) => _frost.AlignFor(duration);

    /// <summary>Chooses again from the settings and Windows now, and sets the window up for it.</summary>
    public void Apply()
    {
        if (_disposed) return;
        var glass = _material.Current;
        var path = glass.Backdrop is GlassBackdrop.Plain or GlassBackdrop.Bloom ? null : AeroNative.WallpaperPath();
        var kind = BackdropRules.Choose(glass.Backdrop, glass.ReduceTransparency, AeroNative.HasSystemBackdrop, AeroNative.TransparencyOn, path != null);
        var hwnd = new WindowInteropHelper(_window).Handle;
        var dark = _theme() == Theme.Dark;
        if (hwnd != IntPtr.Zero)
        {
            AeroNative.SetDword(hwnd, AeroNative.DWMWA_USE_IMMERSIVE_DARK_MODE, dark ? 1 : 0);
            AeroNative.SetDword(hwnd, AeroNative.DWMWA_WINDOW_CORNER_PREFERENCE, AeroNative.DWMWCP_ROUND);
        }
        var chrome = WindowChrome.GetWindowChrome(_window);
        if (kind == BackdropKind.SeeThrough && hwnd != IntPtr.Zero)
        {
            if (chrome != null) chrome.GlassFrameThickness = new Thickness(-1);
            _window.Background = Brushes.Transparent;
            if (_source?.CompositionTarget != null) _source.CompositionTarget.BackgroundColor = Colors.Transparent;
            AeroNative.ExtendFrame(hwnd, -1);
            AeroNative.SetDword(hwnd, AeroNative.DWMWA_SYSTEMBACKDROP_TYPE, AeroNative.DWMSBT_TRANSIENTWINDOW);
            _scene.SetResourceReference(Border.BackgroundProperty, "A.B.SeeThroughWash");
            _frost.Show(null, glass, _theme());
        }
        else
        {
            if (hwnd != IntPtr.Zero && AeroNative.HasSystemBackdrop) AeroNative.SetDword(hwnd, AeroNative.DWMWA_SYSTEMBACKDROP_TYPE, AeroNative.DWMSBT_NONE);
            if (chrome != null) chrome.GlassFrameThickness = new Thickness(0);
            if (hwnd != IntPtr.Zero) AeroNative.ExtendFrame(hwnd, 0);
            _window.SetResourceReference(Window.BackgroundProperty, "A.B.Plain");
            if (_source?.CompositionTarget != null && _window.TryFindResource("A.C.Plain") is Color plain) _source.CompositionTarget.BackgroundColor = plain;
            _scene.SetResourceReference(Border.BackgroundProperty, "A.B.Plain");
            if (kind == BackdropKind.Bloom) _frost.ShowBloom(glass, _theme());
            else _frost.Show(kind == BackdropKind.Wallpaper ? path : null, glass, _theme());
        }
        _parallax.Enabled = kind == BackdropKind.Wallpaper && glass.Parallax && !_onScreen;
        if (kind == Kind) return;
        Kind = kind;
        KindChanged?.Invoke(kind);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _material.Changed -= OnGlassChanged;
        _window.SourceInitialized -= OnSourceInitialized;
        _source?.RemoveHook(Hook);
        _parallax.Dispose();
        _frost.Dispose();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        Hook();
        Apply();
    }

    private void Hook()
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
        _source?.AddHook(Hook);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == AeroNative.WM_SETTINGCHANGE && wParam == AeroNative.SPI_SETDESKWALLPAPER) _frost.Forget();
        if (msg is AeroNative.WM_SETTINGCHANGE or AeroNative.WM_DWMCOLORIZATIONCOLORCHANGED)
        {
            AeroMotion.WindowsChanged();
            _window.Dispatcher.InvokeAsync(Apply, System.Windows.Threading.DispatcherPriority.Background);
        }
        return IntPtr.Zero;
    }

    private void OnGlassChanged(GlassSettings glass) => Apply();
}
