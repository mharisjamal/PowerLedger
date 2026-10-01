using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static PowerLedger.App.Aero.CaptureNative;

namespace PowerLedger.App.Aero;

/// <summary>
/// The picture behind one top-level window of ours (<see cref="LiquidGlassSources"/> makes one per window with glass),
/// from its monitor's capture (<see cref="MonitorCapture"/>, shared with our other windows on that monitor). The window is
/// left out of capture (WDA_EXCLUDEFROMCAPTURE) so its glass never shows itself. Hidden or minimised, it takes nothing.
/// <list type="bullet">
/// <item>Live on the GPU (<see cref="Gpu"/>, the usual way): the pieces are drawn on the GPU straight from the
/// duplicated desktop into one picture the window shows through a D3DImage (GpuGlassWindow). No pixel reaches the CPU.</item>
/// <item>Live on the CPU (<see cref="Image"/> a WriteableBitmap), the fallback where the GPU path can't run (a remote
/// session, WPF drawing in software, Direct3D 11 under feature level 11_0, a device lost three times in a minute): the
/// window's rectangle and a margin round it read back, which the pieces' WPF effects draw from.</item>
/// <item>The wallpaper where Windows draws it, through the pieces' WPF effects, where capture is unavailable (older
/// Windows, policy, a rotated monitor, an HDR desktop off the GPU path) or screenshots are allowed
/// (<see cref="LiquidGlassSources.AllowScreenshots"/>).</item>
/// </list>
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
    private GpuGlassWindow? _gpu;
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

    /// <summary>The CPU path's share of the capture, or null (the GPU path, the wallpaper, disposed), for the tests.</summary>
    internal MonitorCapture.Subscriber? Subscriber => _subscriber;

    /// <summary>The GPU path, or null (the CPU path, the wallpaper, disposed). The pieces draw through it when it's here.</summary>
    public GpuGlassWindow? Gpu => _gpu;

    /// <summary>The monitor's capture this window reads, on either path.</summary>
    internal MonitorCapture? Session => _gpu?.Session ?? _subscriber?.Session;

    /// <summary>Whether the GPU path can run for this window: the switch allows it (tests turn it off to try the CPU path),
    /// WPF draws it in hardware (not a remote session, not software), and its GPU path hasn't given out.</summary>
    private bool GpuPossible
    {
        get
        {
            if (!LiquidGlassSources.GpuAllowed || _gpuGaveOut) return false;
            if (SystemParameters.IsRemoteSession || (RenderCapability.Tier >> 16) < 2) return false;
            if (RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly) return false;
            return _window.CompositionTarget?.RenderMode != RenderMode.SoftwareOnly;
        }
    }

    private bool _gpuGaveOut;

    /// <summary>Picks the GPU path, the CPU path or the wallpaper from the switches and the capture's health, and sets the
    /// window's affinity.</summary>
    public void Apply()
    {
        if (_disposed) return;
        var exclude = LiquidGlassSources.ExcludeFromCapture && !LiquidGlassSources.AllowScreenshots;
        SetWindowDisplayAffinity(_hwnd, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
        var live = !LiquidGlassSources.AllowScreenshots && LiquidGlassSources.CaptureAllowed;
        var gpu = live && GpuPossible;
        if (!gpu) StopGpu();
        if (gpu && _gpu == null)
        {
            StopLive();
            StartGpu();
        }
        if (!gpu && live && _subscriber == null) StartLive();
        if (Session is { Failed: true } || (_subscriber?.Session.Hdr ?? false)) live = false;
        if (!live)
        {
            StopGpu();
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
        StopGpu();
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

    private void StartGpu()
    {
        _monitor = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);
        var session = MonitorCapture.For(_monitor);
        if (session.Failed) return;
        _gpu = new GpuGlassWindow(_dispatcher, session);
        session.FailedChanged += OnCaptureFailed;
        _gpu.Changed += OnGpuChanged;
        _bitmap = null;
        Set(LiquidGlassSourceKind.Live, null, Rect.Empty);
        Follow();
        Changed?.Invoke();
    }

    private void StopGpu()
    {
        if (_gpu == null) return;
        _gpu.Session.FailedChanged -= OnCaptureFailed;
        _gpu.Changed -= OnGpuChanged;
        _gpu.Dispose();
        _gpu = null;
        Changed?.Invoke();
    }

    /// <summary>The GPU path gave out: the CPU path from now on, for this window.</summary>
    private void OnGpuChanged()
    {
        if (_gpu is not { Failed: true }) return;
        _gpuGaveOut = true;
        StopGpu();
        Apply();
    }

    private void OnCaptureFailed() => _dispatcher.BeginInvoke(Apply);

    /// <summary>Keeps the live region on the window and its margin, on the window's monitor, and reading only while the
    /// window shows.</summary>
    private void Follow()
    {
        var shown = IsWindowVisible(_hwnd) && !IsIconic(_hwnd);
        var monitor = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);
        if (_gpu != null)
        {
            if (monitor != _monitor)
            {
                StopGpu();   // moved to another monitor: that monitor's session, and the pieces place themselves again
                StartGpu();
                return;
            }
            _gpu.Active = shown;
            return;
        }
        if (_subscriber == null) return;
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
        _subscriber.Active = shown;
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
                if (_subscriber != null || _gpu != null) Follow();
                else if (Kind == LiquidGlassSourceKind.Wallpaper && msg == WM_WINDOWPOSCHANGED) ShowWallpaper();   // another monitor
                // Moved (a drag, a corner): the pieces place themselves on the picture at once, where the margin still
                // holds what is under them, rather than a frame later when the new region's picture lands.
                GetWindowRect(_hwnd, out var r);
                if (r.Left != _placed.Left || r.Top != _placed.Top || r.Right != _placed.Right || r.Bottom != _placed.Bottom)
                {
                    _placed = r;
                    if (Image != null || _gpu != null) Changed?.Invoke();
                }
                break;
            case WM_DISPLAYCHANGE:
                _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    StopGpu();
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
