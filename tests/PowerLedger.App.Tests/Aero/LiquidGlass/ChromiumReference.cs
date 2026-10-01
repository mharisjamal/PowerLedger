using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PowerLedger.App.Tests;

/// <summary>
/// Headless Edge's shots of the liquid glass recipe (scripts/liquid-glass/reference.ps1 and reference.html), cropped to
/// the glass's device pixel box. Each case is a box at a display scale; dispx and dispy name, per device pixel, the pixel
/// of the backdrop Chromium moved there; full is the whole recipe over the page's test picture.
/// </summary>
internal static class ChromiumReference
{
    /// <summary>The cases: the box in CSS pixels (as the page's hash gives it) and the display scale.</summary>
    public static readonly (string Name, int Left, int Top, int Width, int Height, double Scale)[] Cases =
    [
        ("a", 200, 150, 320, 200, 1),
        ("b", 61, 233, 173, 97, 1),
        ("c", 80, 60, 560, 380, 1),
        ("d", 200, 150, 320, 200, 1.5),
        ("e", 41, 37, 250, 150, 1.25),
    ];

    /// <summary>A case's box in device pixels, as Chromium snaps it.</summary>
    public static (int X, int Y, int Width, int Height) DeviceBox(string name)
    {
        var c = Cases.Single(c => c.Name == name);
        // Half a pixel snaps up, as Chromium snaps (Math.Round alone would round it to even).
        static int Snap(double v) => (int)Math.Floor(v + 0.5);
        int x = Snap(c.Left * c.Scale), y = Snap(c.Top * c.Scale);
        return (x, y, Snap((c.Left + c.Width) * c.Scale) - x, Snap((c.Top + c.Height) * c.Scale) - y);
    }

    /// <summary>The shot's pixels as BGRA rows.</summary>
    public static (byte[] Pixels, int Width, int Height) Load(string name, string mode) => LoadFile($"{name}-{mode}");

    /// <summary>A frozen BGRA picture of shot pixels, at <paramref name="dpi"/> (96: a pixel a unit).</summary>
    public static BitmapSource Bitmap(byte[] bgra, int width, int height, double dpi = 96)
    {
        var bitmap = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Bgra32, null, bgra, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>A reference file's pixels as BGRA rows (dash-1-bg: the Dashboard mockup's background under the Energy pane
    /// at display scale 1; dash-1-full: Edge's pane over it, from scripts/liquid-glass/dashboard.html).</summary>
    public static (byte[] Pixels, int Width, int Height) LoadFile(string file) => Sta.Run(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Aero", "LiquidGlass", "Reference", $"{file}.png");
        var frame = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        return (pixels, bgra.PixelWidth, bgra.PixelHeight);
    });

    /// <summary>Where Chromium took each device pixel of the box from, relative to the box: the dispx and dispy shots
    /// decoded (<c>(red - 136) + 64 * (green - 136)</c> is the page's device x or y).</summary>
    public static (int[] X, int[] Y, int Width, int Height) Sources(string name)
    {
        var box = DeviceBox(name);
        var (xs, w, h) = Load(name, "dispx");
        var (ys, _, _) = Load(name, "dispy");
        var sx = new int[w * h];
        var sy = new int[w * h];
        for (var i = 0; i < w * h; i++)
        {
            sx[i] = xs[i * 4 + 2] - 136 + 64 * (xs[i * 4 + 1] - 136) - box.X;
            sy[i] = ys[i * 4 + 2] - 136 + 64 * (ys[i * 4 + 1] - 136) - box.Y;
        }
        return (sx, sy, w, h);
    }

    /// <summary>The page's test picture at device pixel (x, y) and display scale s, as its script paints it: red, green,
    /// blue.</summary>
    public static (byte R, byte G, byte B) Picture(int x, int y, double s)
    {
        double u = x / s, v = y / s;
        var checker = (((int)Math.Floor(u / 16) + (int)Math.Floor(v / 16)) & 1) == 1;
        return ((byte)Math.Floor(128 + 110 * Math.Sin(u / 37) * (checker ? 1 : 0.6) + 0.5), (byte)Math.Floor(128 + 110 * Math.Cos(v / 23) + 0.5),
            (byte)(Math.Floor(u) % 12 < 6 ? 230 : 40));
    }
}
