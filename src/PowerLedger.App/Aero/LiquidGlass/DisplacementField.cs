using System.Collections.Concurrent;

namespace PowerLedger.App.Aero;

/// <summary>
/// The recipe's url filter as Chromium runs it under <c>backdrop-filter</c>, per device pixel of a piece, measured against
/// headless Edge (scripts/liquid-glass, LiquidGlassChromiumTests):
/// <list type="bullet">
/// <item>The turbulence is evaluated in the piece's own space: device pixel (i, j) of the piece's box, at display scale s,
/// reads the noise at user point ((i + 1) / s, (j + 1) / s). The one pixel shift is Skia's (it sets the noise's matrix
/// "due to WebKit's 1 based coordinates"); the GPU path floors the pixel centre first.</item>
/// <item>The gradients are Chromium's 16 bit packed ones (<see cref="Turbulence"/>, chromiumGradients).</item>
/// <item>The turbulence lands in an 8 bit premultiplied texture: red, green and alpha each clamped to 0 to 1, red and green
/// times alpha, each rounded to 1/255. feDisplacementMap unpremultiplies them in float. Both are in linearRGB, the
/// filter's colour space, so no conversion happens between them (sRGB curves on either, tried, match far worse).</item>
/// <item>The move, in device pixels, is <c>scale * s * (channel - 0.5)</c>; the sample is the texel under
/// <c>pixel centre + move</c>, and a sample outside the box mirrors back into it (the backdrop is only the box, read with
/// Skia's mirror tiling), so nothing outside a piece ever shows in it.</item>
/// </list>
/// A field is the premultiplied bytes for a box, anchored at the box's top left: a smaller box reads a larger field's
/// corner, so <see cref="Shared"/> keeps one per display scale and grows it. Pure; safe off the UI thread.
/// </summary>
internal sealed class DisplacementField
{
    private static readonly Lazy<Turbulence> Noise = new(() => new Turbulence(LiquidGlassRecipe.Seed, chromiumGradients: true));
    private static readonly ConcurrentDictionary<double, DisplacementField> Fields = new();

    private readonly byte[] _red;
    private readonly byte[] _green;
    private readonly byte[] _alpha;

    private DisplacementField(int width, int height, double dpiScale, byte[] red, byte[] green, byte[] alpha)
    {
        Width = width;
        Height = height;
        DpiScale = dpiScale;
        _red = red;
        _green = green;
        _alpha = alpha;
    }

    public int Width { get; }

    public int Height { get; }

    public double DpiScale { get; }

    /// <summary>The recipe's turbulence over <paramref name="width"/> by <paramref name="height"/> device pixels at
    /// display scale <paramref name="dpiScale"/>, as Chromium's premultiplied 8 bit intermediate holds it.</summary>
    public static DisplacementField Compute(int width, int height, double dpiScale)
    {
        var red = new byte[width * height];
        var green = new byte[width * height];
        var alpha = new byte[width * height];
        var noise = Noise.Value;
        const double f = LiquidGlassRecipe.BaseFrequency;
        Parallel.For(0, height, j =>
        {
            var y = (j + 1) / dpiScale;
            for (var i = 0; i < width; i++)
            {
                var x = (i + 1) / dpiScale;
                var a = noise.Value(3, x, y, f, f, LiquidGlassRecipe.Octaves, fractalSum: false);
                var at = j * width + i;
                red[at] = Byte(noise.Value(0, x, y, f, f, LiquidGlassRecipe.Octaves, fractalSum: false) * a);
                green[at] = Byte(noise.Value(1, x, y, f, f, LiquidGlassRecipe.Octaves, fractalSum: false) * a);
                alpha[at] = Byte(a);
            }
        });
        return new DisplacementField(width, height, dpiScale, red, green, alpha);
    }

    /// <summary>A field at least <paramref name="width"/> by <paramref name="height"/> at <paramref name="dpiScale"/>, kept
    /// and grown (to the larger of each side) for every piece at that scale.</summary>
    public static DisplacementField Shared(int width, int height, double dpiScale)
    {
        dpiScale = Math.Round(dpiScale, 4);
        if (Fields.TryGetValue(dpiScale, out var field) && field.Width >= width && field.Height >= height) return field;
        lock (Fields)
        {
            if (Fields.TryGetValue(dpiScale, out field) && field.Width >= width && field.Height >= height) return field;
            field = Compute(Math.Max(width, field?.Width ?? 0), Math.Max(height, field?.Height ?? 0), dpiScale);
            Fields[dpiScale] = field;
            return field;
        }
    }

    /// <summary>The move at device pixel (<paramref name="x"/>, <paramref name="y"/>), in device pixels, for a
    /// displacement scale of <paramref name="scale"/> units.</summary>
    public (double X, double Y) Move(int x, int y, double scale)
    {
        var at = y * Width + x;
        var alpha = _alpha[at];
        // feDisplacementMap unpremultiplies; a clear texel (alpha 0) reads as colour 0.
        double r = alpha == 0 ? 0 : (double)_red[at] / alpha, g = alpha == 0 ? 0 : (double)_green[at] / alpha;
        var k = scale * DpiScale;
        return (k * (r - 0.5), k * (g - 0.5));
    }

    /// <summary>The device pixel of a <paramref name="boxWidth"/> by <paramref name="boxHeight"/> box whose colour lands on
    /// (<paramref name="x"/>, <paramref name="y"/>): the texel under the pixel's centre plus its move, mirrored into the box.</summary>
    public (int X, int Y) Source(int x, int y, int boxWidth, int boxHeight, double scale)
    {
        var (mx, my) = Move(x, y, scale);
        return (Mirror(x + 0.5 + mx, boxWidth), Mirror(y + 0.5 + my, boxHeight));
    }

    /// <summary>Skia's mirror tiling of a coordinate into 0 to <paramref name="size"/>, then the texel it falls in.</summary>
    internal static int Mirror(double p, int size)
    {
        var period = 2.0 * size;
        p %= period;
        if (p < 0) p += period;
        if (p >= size) p = period - p;
        return Math.Clamp((int)Math.Floor(p), 0, size - 1);
    }

    /// <summary>
    /// The source map the displacement shader reads for a box of <paramref name="boxWidth"/> by <paramref name="boxHeight"/>
    /// device pixels drawn with <paramref name="margin"/> device pixels round it: Bgra32, (box + 2 margin) square, opaque,
    /// each texel naming how far away the box pixel its colour comes from lies (Source minus itself), plus 2048, in 12 bits
    /// a side: red the x's high 8, green the y's high 8, blue the x's low 4 then the y's low 4. The margin's texels stay
    /// put (they are clipped away).
    /// </summary>
    public byte[] SourceMap(int boxWidth, int boxHeight, int margin, double scale)
    {
        if (boxWidth > Width || boxHeight > Height) throw new ArgumentException("The field is smaller than the box.");
        if (boxWidth > 2048 || boxHeight > 2048) throw new ArgumentException("A source map moves at most 2047 pixels a side.");
        int w = boxWidth + 2 * margin, h = boxHeight + 2 * margin;
        var map = new byte[w * h * 4];
        Parallel.For(0, h, row =>
        {
            for (var column = 0; column < w; column++)
            {
                int x = column - margin, y = row - margin;
                var (sx, sy) = x >= 0 && y >= 0 && x < boxWidth && y < boxHeight ? Source(x, y, boxWidth, boxHeight, scale) : (x, y);
                int mx = sx - x + 2048, my = sy - y + 2048;
                var at = (row * w + column) * 4;
                map[at] = (byte)(((mx & 15) << 4) | (my & 15));   // blue
                map[at + 1] = (byte)(my >> 4);                      // green
                map[at + 2] = (byte)(mx >> 4);                      // red
                map[at + 3] = 255;
            }
        });
        return map;
    }

    private static byte Byte(double unit) => (byte)Math.Floor(Math.Clamp(unit, 0, 1) * 255 + 0.5);
}
