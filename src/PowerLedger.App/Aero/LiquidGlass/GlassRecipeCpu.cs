namespace PowerLedger.App.Aero;

/// <summary>
/// The recipe on the CPU, for a small picture that seldom changes: what a piece inside another piece reads
/// (<see cref="InsideGlassSource"/>). The same passes, in the same order and at the same precision as the WPF effects
/// and the GPU path: brightness, clamped, into 8 bits, over the box and a margin that reads the box mirrored (the edge
/// texel repeated, Skia's mirror tiling); the Gaussian across, then down, each into 8 bits; then the displacement, each
/// pixel taking the blurred texel its source map names (<see cref="DisplacementField.Source"/>) and going through 8 bit
/// linearRGB and back. Pixels are premultiplied BGRA. The finished glass is then a still picture: WPF draws a bitmap,
/// not four effects, and nothing runs until the picture under it changes. Pure; safe off the UI thread.
/// </summary>
internal static class GlassRecipeCpu
{
    /// <summary>The recipe over <paramref name="pixels"/> (premultiplied BGRA, <paramref name="width"/> by
    /// <paramref name="height"/> device pixels, the piece's own box): the finished glass, the same size.</summary>
    public static byte[] Draw(byte[] pixels, int width, int height, double dpiScale, double brightness, double sigma, double scale)
    {
        var (centre, pairs) = LiquidGlassEffects.BlurKernel(sigma);
        // The pairs back into the discrete Gaussian's weights, one side.
        var radius = pairs.Length == 0 ? 0 : (int)Math.Ceiling(pairs[^1].Offset);
        var weights = new double[radius + 1];
        weights[0] = centre;
        for (var p = 0; p < pairs.Length; p++)
        {
            var (offset, weight) = pairs[p];
            var first = 1 + 2 * p;
            var t = offset - first;
            weights[first] += weight * (1 - t);
            if (first + 1 <= radius) weights[first + 1] += weight * t;
        }
        int m = radius, w = width + 2 * m, h = height + 2 * m;
        // Brightness over the box and its margin, the margin reading the box mirrored.
        var bright = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            var sy = Mirror(y - m, height);
            for (var x = 0; x < w; x++)
            {
                var sx = Mirror(x - m, width);
                int from = (sy * width + sx) * 4, to = (y * w + x) * 4;
                var a = pixels[from + 3];
                for (var c = 0; c < 3; c++) bright[to + c] = Unorm(Math.Min(pixels[from + c] / 255.0 * brightness, a / 255.0));
                bright[to + 3] = a;
            }
        }
        // Across, then down: each texel the weighted sum of its neighbours, the margin's own taps past it clamped.
        var across = new byte[w * h * 4];
        Blur(bright, across, w, h, weights, horizontal: true);
        var down = new byte[w * h * 4];
        Blur(across, down, w, h, weights, horizontal: false);
        // The displacement, from the box's own source map, through 8 bit linearRGB.
        var field = DisplacementField.Shared(width, height, dpiScale);
        var result = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (sx, sy) = scale == 0 ? (x, y) : field.Source(x, y, width, height, scale);
                int from = ((sy + m) * w + sx + m) * 4, to = (y * width + x) * 4;
                var a = down[from + 3] / 255.0;
                for (var c = 0; c < 3; c++)
                {
                    var colour = down[from + c] / 255.0 / Math.Max(a, 1 / 255.0);
                    var linear = Math.Floor(ToLinear(colour) * 255 + 0.5) / 255;
                    result[to + c] = Unorm(ToSrgb(linear) * a);
                }
                result[to + 3] = down[from + 3];
            }
        }
        return result;
    }

    private static void Blur(byte[] from, byte[] to, int w, int h, double[] weights, bool horizontal)
    {
        var radius = weights.Length - 1;
        Span<double> sum = stackalloc double[4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                sum.Clear();
                for (var k = -radius; k <= radius; k++)
                {
                    int sx = horizontal ? Math.Clamp(x + k, 0, w - 1) : x, sy = horizontal ? y : Math.Clamp(y + k, 0, h - 1);
                    var weight = weights[Math.Abs(k)];
                    var at = (sy * w + sx) * 4;
                    for (var c = 0; c < 4; c++) sum[c] += from[at + c] * weight;
                }
                var o = (y * w + x) * 4;
                for (var c = 0; c < 4; c++) to[o + c] = Unorm(sum[c] / 255);
            }
        }
    }

    /// <summary>Skia's mirror tiling of a texel index into 0 to <paramref name="size"/>: the edge texel repeated.</summary>
    private static int Mirror(int i, int size)
    {
        var period = 2 * size;
        i %= period;
        if (i < 0) i += period;
        return i < size ? i : period - 1 - i;
    }

    /// <summary>A unit value into 8 bits, as the hardware's UNORM conversion rounds it.</summary>
    private static byte Unorm(double v) => (byte)Math.Floor(Math.Clamp(v, 0, 1) * 255 + 0.5);

    private static double ToLinear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static double ToSrgb(double l) => l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
}
