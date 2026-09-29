using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PowerLedger.App.Aero;

/// <summary>
/// The wallpaper backdrop (Aero look design §3, Plan S G4 and G5): the user's wallpaper, read once (SPI_GETDESKWALLPAPER),
/// shown sharp behind everything, and frosted once, off the UI thread, into a small copy that each pane shows lined up
/// with the wallpaper behind it: the HTML's <c>blur(26px) saturate(155%)</c> as three box blurs. It is frosted again only
/// when the wallpaper, the Frost slider, the theme or the glass's strictness changes. The frosted copy is held within the
/// frost bounds (A.C.FrostBrightest in dark, A.C.FrostDarkest in light), where text keeps 3:1 with the halo; on the
/// strict glass (Increase contrast, Reduce transparency) within the backdrop bounds, where it keeps 4.5:1 over any
/// wallpaper. The panes are lined up on layout, size and parallax changes, never per frame at rest; <see cref="AlignFor"/>
/// lines them up every frame for a bounded time only (the intro).
/// </summary>
internal sealed class WallpaperFrost : IDisposable
{
    /// <summary>The frosted copy's size: small, since it is blurred; one of its pixels spans about three on screen.</summary>
    public const int FrostWidth = 480;
    public const int FrostHeight = 300;

    /// <summary>The sharp wallpaper is decoded no wider than this.</summary>
    public const int SharpWidth = 1920;

    private readonly Window _window;
    private readonly Border _scene;
    private readonly bool _onScreen;
    private string? _path;
    private BitmapSource? _sharp;
    private BitmapSource? _frosted;
    private ImageBrush? _sceneBrush;
    private (int Style, bool Tile) _placement = (WallpaperPlacement.Fill, false);
    private (string? Path, double Radius, Theme Theme, bool Strict)? _made;
    private int _generation;
    private bool _listening;
    private DateTime _alignUntil;
    private bool _rendering;
    private bool _disposed;

    /// <param name="onScreen">True for Aero's free-form window (0.10.1): the wallpaper is lined up with the screen, where
    /// Windows draws it, so the frost in each pane is the wallpaper really behind it and the scene, seen only at the
    /// panes' soft edges, matches the desktop around them. False lines it up with the scene, filled (a sample window).</param>
    public WallpaperFrost(Window window, Border scene, bool onScreen = false)
    {
        _window = window;
        _scene = scene;
        _onScreen = onScreen;
    }

    /// <summary>How many CompositionTarget.Rendering handlers Aero's glass has on now, on every window: none at rest.</summary>
    internal static int RenderingHooks { get; private set; }

    /// <summary>The frosted copy in use, or null when the backdrop isn't the wallpaper (or it is still being made).</summary>
    internal BitmapSource? Frosted => _frosted;

    /// <summary>Raised on the UI thread when a new frosted copy is on the panes, for a test.</summary>
    internal event Action? Made;

    /// <summary>Shows <paramref name="path"/>'s wallpaper frosted for <paramref name="glass"/> in <paramref name="theme"/>;
    /// null takes the frost off the panes (see-through or plain).</summary>
    public void Show(string? path, GlassSettings glass, Theme theme)
    {
        if (_disposed) return;
        if (path == null)
        {
            _generation++;
            _path = null;
            _frosted = null;
            _sceneBrush = null;
            _made = null;
            Listen(false);
            foreach (var pane in Panes()) pane.Frost = null;
            return;
        }
        var radius = Radius(glass, _window);
        var strict = GlassMaterial.Strict(glass);
        var wanted = (path, radius, theme, strict);
        if (_made == wanted && _frosted != null)
        {
            Listen(true);
            return;
        }
        _made = wanted;
        var generation = ++_generation;
        var reload = path != _path || _sharp == null;
        _path = path;
        var sharp = reload ? null : _sharp;
        if (_onScreen) _placement = WallpaperPlacement.Read();
        // The bright glass lets the wallpaper through up to the frost bounds; the strict glass holds it within the backdrop's.
        var brightest = Token(theme, strict ? "A.C.BackdropBrightest" : "A.C.FrostBrightest");
        var darkest = Token(theme, strict ? "A.C.BackdropDarkest" : "A.C.FrostDarkest");
        var saturate = _window.TryFindResource("A.Glass.Saturate") is double s ? s : 1.55;
        Task.Run(() =>
        {
            var picture = sharp ?? Load(path);
            var frosted = picture == null ? null : Frost(picture, radius, saturate, theme == Theme.Dark ? brightest : darkest, theme == Theme.Dark);
            return (picture, frosted);
        }).ContinueWith(done =>
        {
            if (_disposed || generation != _generation || done.Status != TaskStatus.RanToCompletion) return;
            var (picture, frosted) = done.Result;
            if (picture == null || frosted == null) return;
            _sharp = picture;
            _frosted = frosted;
            if (_onScreen)
            {
                _sceneBrush = new ImageBrush(picture) { ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill, Viewport = Place() };
                _scene.Background = _sceneBrush;
            }
            else
            {
                _sceneBrush = null;
                _scene.Background = Frozen(new ImageBrush(picture) { Stretch = Stretch.UniformToFill });
            }
            foreach (var pane in Panes()) pane.Frost = null;   // fresh brushes on the new copy
            Listen(true);
            Align();
            Made?.Invoke();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The wallpaper changed (WM_SETTINGCHANGE with SPI_SETDESKWALLPAPER): read it again next time.</summary>
    public void Forget()
    {
        _sharp = null;
        _made = null;
    }

    /// <summary>Lines every pane's frost up with the wallpaper behind it, now. A pane whose frost already lines up is left
    /// alone, so nothing repaints.</summary>
    public void Align()
    {
        if (_frosted == null || _scene.ActualWidth <= 0 || _sharp == null) return;
        var image = Place();
        if (_sceneBrush != null && _sceneBrush.Viewport != image) _sceneBrush.Viewport = image;
        foreach (var pane in Panes())
        {
            if (!pane.IsVisible || pane.ActualWidth <= 0) continue;
            if (pane.Frost is not ImageBrush brush || !ReferenceEquals(brush.ImageSource, _frosted))
            {
                brush = new ImageBrush(_frosted) { ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
                pane.Frost = brush;
            }
            Rect r;
            try
            {
                r = _scene.TransformToVisual(pane).TransformBounds(image);
            }
            catch (InvalidOperationException)
            {
                continue;   // not in the same tree as the scene (a pane in a popup)
            }
            r = new Rect(Math.Round(r.X, 2), Math.Round(r.Y, 2), Math.Round(r.Width, 2), Math.Round(r.Height, 2));
            if (brush.Viewport != r) brush.Viewport = r;
        }
    }

    /// <summary>Lines the frost up every frame for <paramref name="duration"/> (the intro's panes rising on their
    /// transforms, which no layout pass reports), then lets go of the frame clock.</summary>
    public void AlignFor(TimeSpan duration)
    {
        if (_disposed || _frosted == null) return;
        var until = DateTime.UtcNow + duration;
        if (until > _alignUntil) _alignUntil = until;
        if (_rendering) return;
        _rendering = true;
        RenderingHooks++;
        CompositionTarget.Rendering += OnFrame;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _generation++;
        Listen(false);
        StopFrames();
    }

    /// <summary>The frost blur in the small copy's pixels: A.Glass.Frost (the Frost slider through GlassMaterial) is in
    /// stage pixels, and one pixel here is about 2.6 there.</summary>
    internal static double Radius(GlassSettings glass, FrameworkElement scope)
    {
        var frost = scope.TryFindResource("A.Glass.Frost") is double f ? f : 26 * glass.Frost / GlassMaterial.DemoFrost;
        return Math.Max(0, frost / 2.6);
    }

    /// <summary>Where the wallpaper lies in the scene's units: where Windows draws it on the window's screen, or filling
    /// the scene when not lined up with the screen (or not on one yet).</summary>
    private Rect Place()
    {
        var size = new Size(_sharp!.PixelWidth, _sharp.PixelHeight);
        if (_onScreen && PresentationSource.FromVisual(_scene) != null)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
            if (hwnd != IntPtr.Zero)
            {
                var screen = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
                var all = System.Windows.Forms.SystemInformation.VirtualScreen;
                var on = WallpaperPlacement.Place(_placement.Style, _placement.Tile, new Rect(screen.X, screen.Y, screen.Width, screen.Height),
                    new Rect(all.X, all.Y, all.Width, all.Height), size);
                var topLeft = _scene.PointFromScreen(on.TopLeft);
                var bottomRight = _scene.PointFromScreen(on.BottomRight);
                return new Rect(Math.Round(topLeft.X, 2), Math.Round(topLeft.Y, 2), Math.Round(bottomRight.X - topLeft.X, 2), Math.Round(bottomRight.Y - topLeft.Y, 2));
            }
        }
        return ImageRect(new Size(_scene.ActualWidth, _scene.ActualHeight), size);
    }

    /// <summary>Where UniformToFill draws an image of <paramref name="image"/> pixels in a box of <paramref name="box"/>.</summary>
    internal static Rect ImageRect(Size box, Size image)
    {
        var scale = Math.Max(box.Width / image.Width, box.Height / image.Height);
        return new Rect((box.Width - image.Width * scale) / 2, (box.Height - image.Height * scale) / 2, image.Width * scale, image.Height * scale);
    }

    /// <summary>
    /// Frosts <paramref name="sharp"/> into a small frozen copy: three box blurs of <paramref name="radius"/> (a close
    /// Gaussian), saturated by <paramref name="saturate"/>, then held within <paramref name="bound"/>'s luminance: no
    /// brighter in the dark theme, no darker in the light one, the hue kept. Pure; safe off the UI thread.
    /// </summary>
    internal static BitmapSource Frost(BitmapSource sharp, double radius, double saturate, Color bound, bool dark)
    {
        const int w = FrostWidth, h = FrostHeight;
        var small = new TransformedBitmap(sharp, new ScaleTransform((double)w / sharp.PixelWidth, (double)h / sharp.PixelHeight));
        var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[w * h * 4];
        bgra.CopyPixels(pixels, w * 4, 0);
        var r = (int)Math.Round(radius);
        var channels = new double[3][];
        for (var c = 0; c < 3; c++)
        {
            channels[c] = new double[w * h];
            for (var i = 0; i < w * h; i++) channels[c][i] = pixels[i * 4 + c];
            if (r <= 0) continue;
            var scratch = new double[w * h];
            for (var pass = 0; pass < 3; pass++)
            {
                BoxBlur(channels[c], scratch, w, h, r, rows: true);
                BoxBlur(scratch, channels[c], w, h, r, rows: false);
            }
        }
        var limit = Contrast.Luminance(bound);
        for (var i = 0; i < w * h; i++)
        {
            double b = channels[0][i], g = channels[1][i], red = channels[2][i];
            var grey = .2126 * red + .7152 * g + .0722 * b;
            b = Math.Clamp(grey + (b - grey) * saturate, 0, 255);
            g = Math.Clamp(grey + (g - grey) * saturate, 0, 255);
            red = Math.Clamp(grey + (red - grey) * saturate, 0, 255);
            var (lr, lg, lb) = (Linear(red), Linear(g), Linear(b));
            var luminance = .2126 * lr + .7152 * lg + .0722 * lb;
            if (dark && luminance > limit)
            {
                var k = limit / luminance;
                (lr, lg, lb) = (lr * k, lg * k, lb * k);
            }
            else if (!dark && luminance < limit)
            {
                var k = (limit - luminance) / (1 - luminance);
                (lr, lg, lb) = (lr + (1 - lr) * k, lg + (1 - lg) * k, lb + (1 - lb) * k);
            }
            pixels[i * 4] = Gamma(lb);
            pixels[i * 4 + 1] = Gamma(lg);
            pixels[i * 4 + 2] = Gamma(lr);
            pixels[i * 4 + 3] = 255;
        }
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        result.Freeze();
        return result;
    }

    /// <summary>The wallpaper decoded no wider than <see cref="SharpWidth"/>, frozen; null when it can't be read.</summary>
    internal static BitmapSource? Load(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = SharpWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is System.IO.IOException or NotSupportedException or UriFormatException or ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>One running-sum box blur along rows or columns, the edges held at their last pixel.</summary>
    private static void BoxBlur(double[] from, double[] to, int w, int h, int r, bool rows)
    {
        int lines = rows ? h : w, length = rows ? w : h;
        var k = 1.0 / (2 * r + 1);
        for (var line = 0; line < lines; line++)
        {
            int At(int i) => rows ? line * w + Math.Clamp(i, 0, length - 1) : Math.Clamp(i, 0, length - 1) * w + line;
            double sum = 0;
            for (var i = -r; i <= r; i++) sum += from[At(i)];
            for (var i = 0; i < length; i++)
            {
                to[At(i)] = sum * k;
                sum += from[At(i + r + 1)] - from[At(i - r)];
            }
        }
    }

    private static double Linear(double channel)
    {
        var v = channel / 255;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static byte Gamma(double linear)
    {
        linear = Math.Clamp(linear, 0, 1);
        var v = linear <= 0.03928 / 12.92 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
    }

    private static Color Token(Theme theme, string key) => (Color)GlassMaterial.Palette(theme)[key];

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private IEnumerable<GlassPanel> Panes()
        => GlassPanel.Live.Where(pane => pane.IsLoaded && Window.GetWindow(pane) == _window).ToList();

    /// <summary>Layout passes and size changes realign the frost; a window at rest has none.</summary>
    private void Listen(bool on)
    {
        if (on == _listening) return;
        _listening = on;
        if (on)
        {
            _window.LayoutUpdated += OnLayout;
            _scene.SizeChanged += OnSize;
            if (_onScreen) _window.LocationChanged += OnLayout;   // the wallpaper stays with the screen as the window moves
        }
        else
        {
            _window.LayoutUpdated -= OnLayout;
            _scene.SizeChanged -= OnSize;
            _window.LocationChanged -= OnLayout;
        }
    }

    private void OnLayout(object? sender, EventArgs e) => Align();

    private void OnSize(object sender, SizeChangedEventArgs e) => Align();

    private void OnFrame(object? sender, EventArgs e)
    {
        Align();
        if (DateTime.UtcNow >= _alignUntil || _disposed) StopFrames();
    }

    private void StopFrames()
    {
        if (!_rendering) return;
        _rendering = false;
        RenderingHooks--;
        CompositionTarget.Rendering -= OnFrame;
    }
}

/// <summary>
/// Where Windows draws the wallpaper on a screen, pure: Fill (Windows' default, and any style it may add later) covers
/// the screen and crops the overflow evenly; Fit shows it whole; Stretch fills the screen exactly; Center (and Tile, read
/// as Center) sets it at its own size in the middle; Span covers every screen together.
/// </summary>
internal static class WallpaperPlacement
{
    public const int Center = 0;
    public const int Stretch = 2;
    public const int Fit = 6;
    public const int Fill = 10;
    public const int Span = 22;

    /// <summary>The picture's rectangle, in the same pixels as <paramref name="screen"/> and <paramref name="all"/>.</summary>
    public static Rect Place(int style, bool tile, Rect screen, Rect all, Size image)
    {
        if (image.Width <= 0 || image.Height <= 0) return screen;
        return style switch
        {
            Stretch => screen,
            Fit => Scaled(screen, image, Math.Min(screen.Width / image.Width, screen.Height / image.Height)),
            Center => Scaled(screen, image, 1),
            Span => Scaled(all, image, Math.Max(all.Width / image.Width, all.Height / image.Height)),
            _ => Scaled(screen, image, Math.Max(screen.Width / image.Width, screen.Height / image.Height)),
        };
    }

    /// <summary>Control Panel, Desktop: WallpaperStyle and TileWallpaper, as Windows keeps them; Fill when unreadable.</summary>
    public static (int Style, bool Tile) Read()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            var style = int.TryParse(key?.GetValue("WallpaperStyle") as string, out var s) ? s : Fill;
            var tile = key?.GetValue("TileWallpaper") as string == "1";
            return (style, tile);
        }
        catch (System.Security.SecurityException)
        {
            return (Fill, false);
        }
    }

    private static Rect Scaled(Rect box, Size image, double scale)
    {
        double w = image.Width * scale, h = image.Height * scale;
        return new Rect(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
    }
}
