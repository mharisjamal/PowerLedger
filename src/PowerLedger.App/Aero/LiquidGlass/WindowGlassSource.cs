using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static PowerLedger.App.Aero.CaptureNative;

namespace PowerLedger.App.Aero;

/// <summary>
/// The picture behind one top-level window of ours (<see cref="LiquidGlassSources"/> makes one per window with glass).
/// Live, it is the window's share of its monitor's capture (<see cref="MonitorCapture"/>, shared with our other windows on
/// that monitor): its rectangle and a margin round it, so a window being dragged keeps a picture under it until the next
/// frame lands. The window is left out of capture (WDA_EXCLUDEFROMCAPTURE) so its glass never shows itself. Hidden or
/// minimised, it takes nothing. Where capture is unavailable (older Windows, policy, a rotated monitor) or screenshots are
/// allowed (<see cref="LiquidGlassSources.AllowScreenshots"/>), it is the wallpaper where Windows draws it.
/// </summary>
internal sealed class WindowGlassSource : ILiquidGlassSource, IDisposable
{
    /// <summary>How far round the window the live picture reaches, in physical pixels.</summary>
    public const int Margin = 96;

    private const int WM_WINDOWPOSCHANGED = 0x0047;
    private const int WM_SHOWWINDOW = 0x0018;
    private const int WM_SIZE = 0x0005;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_DPICHANGED = 0x02E0;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int SPI_SETDESKWALLPAPER = 0x0014;

    private readonly HwndSource _window;
    private readonly IntPtr _hwnd;
    private readonly Dispatcher _dispatcher;
    private MonitorCapture.Subscriber? _subscriber;
    private IntPtr _monitor;
    private WriteableBitmap? _bitmap;
    private RECT _placed;
    private int _posted;
    private bool _disposed;

    public WindowGlassSource(HwndSource window)
    {
        _window = window;
        _hwnd = window.Handle;
        _dispatcher = window.Dispatcher;
        _window.AddHook(Hook);
        Apply();
    }

    public LiquidGlassSourceKind Kind { get; private set; }

    public ImageSource? Image { get; private set; }

    public Rect ScreenBounds { get; private set; }

    public event Action? Changed;

    /// <summary>The live capture this window reads, or null (wallpaper, hidden, disposed), for the tests.</summary>
    internal MonitorCapture.Subscriber? Subscriber => _subscriber;

    /// <summary>Picks live or wallpaper from the switches and the capture's health, and sets the window's affinity.</summary>
    public void Apply()
    {
        if (_disposed) return;
        var exclude = LiquidGlassSources.ExcludeFromCapture && !LiquidGlassSources.AllowScreenshots;
        SetWindowDisplayAffinity(_hwnd, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
        var live = !LiquidGlassSources.AllowScreenshots && LiquidGlassSources.CaptureAllowed;
        if (live && _subscriber == null) StartLive();
        if (live && _subscriber is { Session.Failed: true }) live = false;
        if (!live)
        {
            StopLive();
            ShowWallpaper();
        }
        else
        {
            Follow();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.RemoveHook(Hook);
        StopLive();
        if (!_window.IsDisposed) SetWindowDisplayAffinity(_hwnd, WDA_NONE);
    }

    private void StartLive()
    {
        _monitor = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);
        var session = MonitorCapture.For(_monitor);
        if (session.Failed) return;
        _subscriber = session.Subscribe(OnDelivered);
        session.FailedChanged += OnCaptureFailed;
        Follow();
    }

    private void StopLive()
    {
        if (_subscriber == null) return;
        _subscriber.Session.FailedChanged -= OnCaptureFailed;
        _subscriber.Dispose();
        _subscriber = null;
        _bitmap = null;
    }

    private void OnCaptureFailed() => _dispatcher.BeginInvoke(Apply);

    /// <summary>Keeps the live region on the window and its margin, on the window's monitor, and reading only while the
    /// window shows.</summary>
    private void Follow()
    {
        if (_subscriber == null) return;
        var monitor = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != _monitor)
        {
            StopLive();   // moved to another monitor: that monitor's session
            StartLive();
            return;
        }
        GetWindowRect(_hwnd, out var r);
        var info = new MONITORINFO { Size = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        int left = Math.Max(r.Left - Margin, info.Monitor.Left), top = Math.Max(r.Top - Margin, info.Monitor.Top);
        int right = Math.Min(r.Right + Margin, info.Monitor.Right), bottom = Math.Min(r.Bottom + Margin, info.Monitor.Bottom);
        _subscriber.Region = right > left && bottom > top ? new Int32Rect(left, top, right - left, bottom - top) : Int32Rect.Empty;
        _subscriber.Active = IsWindowVisible(_hwnd) && !IsIconic(_hwnd);
    }

    /// <summary>On the capture thread: new pixels. One post to the UI thread at a time; it takes whatever is newest.</summary>
    private void OnDelivered(MonitorCapture.Subscriber subscriber)
    {
        if (Interlocked.Exchange(ref _posted, 1) == 1) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            Interlocked.Exchange(ref _posted, 0);
            if (_disposed || subscriber != _subscriber) return;
            subscriber.Take(Show);
        });
    }

    /// <summary>On the UI thread, under the subscriber's lock: the changed part into the bitmap.</summary>
    private void Show(Int32Rect region, byte[] pixels, Int32Rect dirty)
    {
        var stride = region.Width * 4;
        if (_bitmap == null || _bitmap.PixelWidth != region.Width || _bitmap.PixelHeight != region.Height)
        {
            _bitmap = new WriteableBitmap(region.Width, region.Height, 96, 96, PixelFormats.Bgra32, null);
            dirty = new Int32Rect(0, 0, region.Width, region.Height);
        }
        _bitmap.WritePixels(dirty, pixels, stride, dirty.X * 4 + dirty.Y * stride);
        var bounds = new Rect(region.X, region.Y, region.Width, region.Height);
        var changed = Kind != LiquidGlassSourceKind.Live || !ReferenceEquals(Image, _bitmap) || ScreenBounds != bounds;
        Kind = LiquidGlassSourceKind.Live;
        Image = _bitmap;
        ScreenBounds = bounds;
        if (changed) Changed?.Invoke();
    }

    private void ShowWallpaper()
    {
        var picture = WallpaperPicture.Current();
        if (picture == null)
        {
            Set(LiquidGlassSourceKind.None, null, Rect.Empty);
            return;
        }
        var monitor = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { Size = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        var screen = new Rect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top);
        var all = System.Windows.Forms.SystemInformation.VirtualScreen;
        var (style, tile) = WallpaperPlacement.Read();
        var bounds = WallpaperPlacement.Place(style, tile, screen, new Rect(all.X, all.Y, all.Width, all.Height), new Size(picture.PixelWidth, picture.PixelHeight));
        Set(LiquidGlassSourceKind.Wallpaper, picture, bounds);
    }

    private void Set(LiquidGlassSourceKind kind, ImageSource? image, Rect bounds)
    {
        if (Kind == kind && ReferenceEquals(Image, image) && ScreenBounds == bounds) return;
        Kind = kind;
        Image = image;
        ScreenBounds = bounds;
        Changed?.Invoke();
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_WINDOWPOSCHANGED or WM_SHOWWINDOW or WM_SIZE or WM_DPICHANGED:
                if (_subscriber != null) Follow();
                else if (Kind == LiquidGlassSourceKind.Wallpaper && msg == WM_WINDOWPOSCHANGED) ShowWallpaper();   // another monitor
                // Moved (a drag, a corner): the pieces place themselves on the picture at once, where the margin still
                // holds what is under them, rather than a frame later when the new region's picture lands.
                GetWindowRect(_hwnd, out var r);
                if (r.Left != _placed.Left || r.Top != _placed.Top || r.Right != _placed.Right || r.Bottom != _placed.Bottom)
                {
                    _placed = r;
                    if (Image != null) Changed?.Invoke();
                }
                break;
            case WM_DISPLAYCHANGE:
                _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    StopLive();
                    Apply();
                });
                break;
            case WM_SETTINGCHANGE when wParam == SPI_SETDESKWALLPAPER:
                WallpaperPicture.Forget();
                if (Kind == LiquidGlassSourceKind.Wallpaper) _dispatcher.BeginInvoke(DispatcherPriority.Background, ShowWallpaper);
                break;
        }
        return IntPtr.Zero;
    }
}

/// <summary>The wallpaper at full size for the liquid glass's fallback, read once and shared, frozen.</summary>
internal static class WallpaperPicture
{
    private static (string? Path, BitmapSource? Picture)? _loaded;

    public static BitmapSource? Current()
    {
        var path = AeroNative.WallpaperPath();
        if (_loaded is { } loaded && loaded.Path == path) return loaded.Picture;
        BitmapSource? picture = null;
        if (path != null)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.EndInit();
                // Units of the picture are its pixels, so the glass's viewbox maths reads the same for it and the capture.
                picture = image.DpiX == 96 && image.DpiY == 96 ? image : BitmapSource.Create(image.PixelWidth, image.PixelHeight, 96, 96,
                    image.Format, image.Palette, Pixels(image), (image.PixelWidth * image.Format.BitsPerPixel + 7) / 8);
                picture.Freeze();
            }
            catch (Exception error) when (error is System.IO.IOException or NotSupportedException or UriFormatException or ArgumentException
                or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                picture = null;
            }
        }
        _loaded = (path, picture);
        return picture;
    }

    public static void Forget() => _loaded = null;

    private static byte[] Pixels(BitmapSource image)
    {
        var stride = (image.PixelWidth * image.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        return pixels;
    }
}
