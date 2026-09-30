using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerLedger.App.Aero;

namespace PowerLedger.App.Tests;

/// <summary>A backdrop source a test controls: a picture laid on the screen where it says.</summary>
internal sealed class FakeGlassSource(ImageSource? image, Rect bounds, LiquidGlassSourceKind kind = LiquidGlassSourceKind.Live) : ILiquidGlassSource
{
    public LiquidGlassSourceKind Kind { get; set; } = kind;

    public ImageSource? Image { get; set; } = image;

    public Rect ScreenBounds { get; set; } = bounds;

    public event Action? Changed;

    public void Raise() => Changed?.Invoke();

    /// <summary>A frozen 96 dpi BGRA picture of <paramref name="width"/> by <paramref name="height"/> from
    /// <paramref name="pixel"/> (red, green, blue at x, y).</summary>
    public static BitmapSource Picture(int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = pixel(x, y);
                var at = (y * width + x) * 4;
                (pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]) = (b, g, r, 255);
            }
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
