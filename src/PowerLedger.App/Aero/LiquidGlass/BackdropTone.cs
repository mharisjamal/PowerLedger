using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App.Aero;

/// <summary>
/// The tone of what a glass piece shows (Plan AD): the mean light of its backdrop after the recipe's brightness, as an
/// sRGB colour, which GlassInk turns into the ink its text and icons take. Read from pixels already in hand (the CPU
/// path's picture, the wallpaper's, a nested piece's finished glass) on a grid of at most 32 by 32 samples, so a piece
/// of any size costs about a thousand reads and nothing is allocated. The GPU path reads the same mean on the GPU
/// (GpuGlassRenderer.Tones, Shaders/GpuGlass.hlsl's Tone).
/// </summary>
internal static class BackdropTone
{
    /// <summary>Samples a side, at most.</summary>
    public const int Grid = 32;

    /// <summary>Each 8 bit sRGB code value as linear light.</summary>
    private static readonly double[] Light = Enumerable.Range(0, 256).Select(v => ToLinear(v / 255.0)).ToArray();

    /// <summary>The mean light of <paramref name="region"/> (clipped to the picture) of an opaque BGRA picture, each texel
    /// brightened by <paramref name="brightness"/> first (clamped), as an sRGB colour; null when the region misses the
    /// picture.</summary>
    public static Color? Mean(ReadOnlySpan<byte> bgra, int width, int height, int stride, Int32Rect region, double brightness)
    {
        int x0 = Math.Max(region.X, 0), y0 = Math.Max(region.Y, 0);
        int x1 = Math.Min(region.X + region.Width, width), y1 = Math.Min(region.Y + region.Height, height);
        if (x1 <= x0 || y1 <= y0) return null;
        int nx = Math.Min(Grid, x1 - x0), ny = Math.Min(Grid, y1 - y0);
        double r = 0, g = 0, b = 0;
        for (var j = 0; j < ny; j++)
        {
            var y = y0 + (int)((j + 0.5) * (y1 - y0) / ny);
            for (var i = 0; i < nx; i++)
            {
                var at = y * stride + (x0 + (int)((i + 0.5) * (x1 - x0) / nx)) * 4;
                b += Lit(bgra[at], brightness);
                g += Lit(bgra[at + 1], brightness);
                r += Lit(bgra[at + 2], brightness);
            }
        }
        var n = nx * ny;
        return FromLinear(r / n, g / n, b / n);
    }

    /// <summary>The mean of a premultiplied BGRA picture (a nested piece's finished glass, mostly bare), as a translucent
    /// sRGB colour: its cover is the mean alpha. Averaged in sRGB, as WPF composes it.</summary>
    public static Color? MeanPremultiplied(ReadOnlySpan<byte> pbgra, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0) return null;
        int nx = Math.Min(Grid, width), ny = Math.Min(Grid, height);
        double r = 0, g = 0, b = 0, a = 0;
        for (var j = 0; j < ny; j++)
        {
            var y = (int)((j + 0.5) * height / ny);
            for (var i = 0; i < nx; i++)
            {
                var at = y * stride + (int)((i + 0.5) * width / nx) * 4;
                b += pbgra[at];
                g += pbgra[at + 1];
                r += pbgra[at + 2];
                a += pbgra[at + 3];
            }
        }
        if (a <= 0) return Color.FromArgb(0, 0, 0, 0);
        static byte Channel(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
        return Color.FromArgb(Channel(a / (nx * ny)), Channel(r / a * 255), Channel(g / a * 255), Channel(b / a * 255));
    }

    /// <summary>A linear light mean back to an opaque sRGB colour.</summary>
    public static Color FromLinear(double r, double g, double b) => Color.FromRgb(Code(r), Code(g), Code(b));

    private static double Lit(byte value, double brightness)
        => brightness == 1 ? Light[value] : Light[(int)Math.Min(255, Math.Round(value * brightness))];

    private static byte Code(double linear)
    {
        var l = Math.Clamp(linear, 0, 1);
        var s = l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
        return (byte)Math.Round(s * 255);
    }

    private static double ToLinear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
}
