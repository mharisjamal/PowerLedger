using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PowerLedger.App.Aero;

/// <summary>
/// Aero bloom (0.10.3, the owner's choice: the approved video's glass): Windows 11's own bloom picture, read where Windows
/// keeps it and never shipped (it is Microsoft's), frosted under the panes and mapped across the stage so each pane shows
/// its own part of it, as the video does; the desktop between the panes stays the real one. Where Windows has no such
/// picture, one drawn in its colours stands in.
/// </summary>
internal static class AeroBloom
{
    /// <summary>What the frost caches the bloom under, in place of a wallpaper's path.</summary>
    public const string Key = "aero-bloom";

    /// <summary>The part of the picture the video's stage covers (the demo's stage on the bloom, filling a 1440 by 1080
    /// frame), as fractions of its width and height: each pane shows the part of this that lies behind it.</summary>
    public static readonly Rect Seen = new(432 / 1920.0, 283 / 1200.0, 1056 / 1920.0, 691 / 1200.0);

    /// <summary>How many stage pixels one pixel of the frosted copy spans at the demo's stage (1312 wide): the bloom is
    /// frosted at the demo's blur in stage pixels, as the video's glass is.</summary>
    public const double StagePixelsPerFrostPixel = 5;

    /// <summary>Where Windows keeps the bloom: the 4K folder's, then the older folder's.</summary>
    public static IEnumerable<string> Candidates(string windows)
    {
        yield return Path.Combine(windows, "Web", "4K", "Wallpaper", "Windows", "img19_1920x1200.jpg");
        yield return Path.Combine(windows, "Web", "Wallpaper", "Windows", "img19.jpg");
    }

    private static readonly object Gate = new();
    private static ((double, double, Color, Color) Key, (BitmapSource? Picture, BitmapSource? Frosted) Made)? _cache;

    /// <summary>The bloom already frosted for <paramref name="key"/> (radius, saturation and bounds), if it has been.</summary>
    public static (BitmapSource? Picture, BitmapSource? Frosted)? Cached((double, double, Color, Color) key)
    {
        lock (Gate) return _cache is { } c && c.Key == key && c.Made.Frosted != null ? c.Made : null;
    }

    /// <summary>Keeps the frosted bloom for the App's next window (the one before it, frozen, is shared by all).</summary>
    public static (BitmapSource? Picture, BitmapSource? Frosted) Remember((double, double, Color, Color) key, (BitmapSource? Picture, BitmapSource? Frosted) made)
    {
        lock (Gate) _cache = (key, made);
        return made;
    }

    /// <summary>The bloom, sharp: Windows' own, or the drawn one. Safe off the UI thread.</summary>
    public static BitmapSource Load()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var path in Candidates(windows))
        {
            if (File.Exists(path) && WallpaperFrost.Load(path) is { } picture) return picture;
        }
        return Drawn();
    }

    /// <summary>The frost blur for the bloom in the frosted copy's pixels: A.Glass.Frost is in stage pixels.</summary>
    public static double Radius(GlassSettings glass, FrameworkElement scope)
    {
        var frost = scope.TryFindResource("A.Glass.Frost") is double f ? f : 26 * glass.Frost / GlassMaterial.DemoFrost;
        return Math.Max(0, frost / StagePixelsPerFrostPixel);
    }

    /// <summary>Where the picture lies, in the stage's units, for a stage at <paramref name="stage"/>: the part the video
    /// shows (<see cref="Seen"/>) covering it, centred on it, the picture's shape kept.</summary>
    public static Rect Place(Rect stage, Size image)
    {
        if (image.Width <= 0 || image.Height <= 0 || stage.Width <= 0 || stage.Height <= 0) return stage;
        var width = Math.Max(stage.Width / Seen.Width, stage.Height / Seen.Height * image.Width / image.Height);
        var height = width * image.Height / image.Width;
        var x = stage.X + stage.Width / 2 - (Seen.X + Seen.Width / 2) * width;
        var y = stage.Y + stage.Height / 2 - (Seen.Y + Seen.Height / 2) * height;
        return new Rect(x, y, width, height);
    }

    /// <summary>A bloom drawn in the picture's colours, for a Windows without it: deep navy, a bright blue heart a little
    /// left of centre, a cyan light above it and violet low on the right, each a soft radial glow. Pure; frozen.</summary>
    public static BitmapSource Drawn(int width = 960, int height = 600)
    {
        (double X, double Y, double R, Color C, double A)[] glows =
        [
            (0.46, 0.52, 0.42, Color.FromRgb(0x10, 0x5A, 0xFF), 0.95),
            (0.36, 0.64, 0.30, Color.FromRgb(0x1E, 0x3C, 0xE6), 0.8),
            (0.58, 0.38, 0.22, Color.FromRgb(0x00, 0xA4, 0xFF), 0.7),
            (0.72, 0.70, 0.26, Color.FromRgb(0x5A, 0x3C, 0xE6), 0.5),
        ];
        var ground = Color.FromRgb(0x02, 0x08, 0x1C);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                double r = ground.R, g = ground.G, b = ground.B;
                var u = (double)x / width;
                var v = (double)y / height;
                foreach (var glow in glows)
                {
                    var dx = (u - glow.X) * width / height;
                    var dy = v - glow.Y;
                    var t = Math.Clamp(1 - Math.Sqrt(dx * dx + dy * dy) / glow.R, 0, 1);
                    var a = glow.A * t * t * (3 - 2 * t);
                    r += (glow.C.R - r) * a;
                    g += (glow.C.G - g) * a;
                    b += (glow.C.B - b) * a;
                }
                var i = (y * width + x) * 4;
                pixels[i] = (byte)Math.Round(b);
                pixels[i + 1] = (byte)Math.Round(g);
                pixels[i + 2] = (byte)Math.Round(r);
                pixels[i + 3] = 255;
            }
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
