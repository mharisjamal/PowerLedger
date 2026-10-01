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
        // Brightness over the box and its margin, the margin reading the box mirrored: per channel a function of the
        // texel's value and its alpha alone, so it is one table look up (Bright), the same bytes as the sum it tables.
        var brightTable = Bright(brightness);
        var bright = new byte[w * h * 4];
        var columns = new int[w];
        for (var x = 0; x < w; x++) columns[x] = Mirror(x - m, width);
        for (var y = 0; y < h; y++)
        {
            var row = Mirror(y - m, height) * width;
            for (var x = 0; x < w; x++)
            {
                int from = (row + columns[x]) * 4, to = (y * w + x) * 4;
                var a = pixels[from + 3];
                var at = a << 8;
                bright[to] = brightTable[at | pixels[from]];
                bright[to + 1] = brightTable[at | pixels[from + 1]];
                bright[to + 2] = brightTable[at | pixels[from + 2]];
                bright[to + 3] = a;
            }
        }
        // Across, then down: each texel the weighted sum of its neighbours, the margin's own taps past it clamped.
        var across = new byte[w * h * 4];
        Blur(bright, across, w, h, weights, horizontal: true);
        var down = new byte[w * h * 4];
        Blur(across, down, w, h, weights, horizontal: false);
        // The displacement, from the box's own source map, through 8 bit linearRGB: again a function of a texel's value
        // and alpha alone (Linear).
        var field = DisplacementField.Shared(width, height, dpiScale);
        var trip = Linear.Value;
        var result = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (sx, sy) = scale == 0 ? (x, y) : field.Source(x, y, width, height, scale);
                int from = ((sy + m) * w + sx + m) * 4, to = (y * width + x) * 4;
                var alpha = down[from + 3];
                var at = alpha << 8;
                result[to] = trip[at | down[from]];
                result[to + 1] = trip[at | down[from + 1]];
                result[to + 2] = trip[at | down[from + 2]];
                result[to + 3] = alpha;
            }
        }
        return result;
    }

    /// <summary>brightness(b) of a premultiplied channel, by its alpha (high byte) and value (low byte): the value
    /// times b, held under the alpha, into 8 bits.</summary>
    private static byte[] Bright(double brightness)
    {
        lock (BrightTables)
        {
            if (BrightTables.TryGetValue(brightness, out var table)) return table;
            table = new byte[256 * 256];
            for (var a = 0; a < 256; a++)
            {
                for (var v = 0; v < 256; v++) table[(a << 8) | v] = Unorm(Math.Min(v / 255.0 * brightness, a / 255.0));
            }
            BrightTables[brightness] = table;
            return table;
        }
    }

    private static readonly Dictionary<double, byte[]> BrightTables = [];

    /// <summary>A premultiplied channel through 8 bit linearRGB and back, by its alpha (high byte) and value (low byte).</summary>
    private static readonly Lazy<byte[]> Linear = new(() =>
    {
        var table = new byte[256 * 256];
        for (var alpha = 0; alpha < 256; alpha++)
        {
            var a = alpha / 255.0;
            for (var v = 0; v < 256; v++)
            {
                var colour = v / 255.0 / Math.Max(a, 1 / 255.0);
                var linear = Math.Floor(ToLinear(colour) * 255 + 0.5) / 255;
                table[(alpha << 8) | v] = Unorm(ToSrgb(linear) * a);
            }
        }
        return table;
    });

    private static void Blur(byte[] from, byte[] to, int w, int h, double[] weights, bool horizontal)
    {
        var radius = weights.Length - 1;
        // Each tap's weight, from -radius to radius, in the order the sums take them.
        var taps = new double[2 * radius + 1];
        for (var k = -radius; k <= radius; k++) taps[k + radius] = weights[Math.Abs(k)];
        var (length, lines) = horizontal ? (w, h) : (h, w);
        var step = horizontal ? 4 : w * 4;
        var lineStep = horizontal ? w * 4 : 4;
        for (var line = 0; line < lines; line++)
        {
            var origin = line * lineStep;
            for (var i = 0; i < length; i++)
            {
                double b = 0, g = 0, r = 0, a = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    var j = i + k;
                    if (j < 0) j = 0;
                    else if (j >= length) j = length - 1;
                    var weight = taps[k + radius];
                    var at = origin + j * step;
                    b += from[at] * weight;
                    g += from[at + 1] * weight;
                    r += from[at + 2] * weight;
                    a += from[at + 3] * weight;
                }
                var o = origin + i * step;
                to[o] = Unorm(b / 255);
                to[o + 1] = Unorm(g / 255);
                to[o + 2] = Unorm(r / 255);
                to[o + 3] = Unorm(a / 255);
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
