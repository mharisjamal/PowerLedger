using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App.Aero;

/// <summary>
/// The tone of what a glass piece shows (Plan AD): the mean light of its backdrop after the recipe's brightness, in 8 by 8
/// cells over the piece, which GlassInk turns into the ink its text and icons take (each cell's for text over it, their
/// mean for the piece). Read from pixels already in hand (the CPU path's picture, the wallpaper's, a nested piece's
/// finished glass) on a grid of at most 32 by 32 samples, so a piece of any size costs about a thousand reads and nothing
/// is allocated. The GPU path reads the same cells on the GPU (GpuGlassRenderer.Tones, Shaders/GpuGlass.hlsl's Tone).
/// </summary>
internal static class BackdropTone
{
    /// <summary>Samples a side, at most.</summary>
    public const int Grid = 32;

    /// <summary>Cells a side of a piece's tones, and cells in all.</summary>
    public const int Side = 8;
    public const int Cells = Side * Side;

    /// <summary>Each 8 bit sRGB code value as linear light.</summary>
    private static readonly double[] Light = Enumerable.Range(0, 256).Select(v => ToLinear(v / 255.0)).ToArray();

    /// <summary>The mean light of <paramref name="region"/> (clipped to the picture) of an opaque BGRA picture, each texel
    /// brightened by <paramref name="brightness"/> first (clamped), as an sRGB colour; null when the region misses the
    /// picture.</summary>
    public static Color? Mean(ReadOnlySpan<byte> bgra, int width, int height, int stride, Int32Rect region, double brightness)
    {
        Span<float> cells = stackalloc float[Cells * 3];
        return Measure(bgra, width, height, stride, region, brightness, cells) == null ? null : MeanOf(cells);
    }

    /// <summary>
    /// The mean light of each of 8 by 8 cells across <paramref name="region"/> (clipped to the picture) of an opaque BGRA
    /// picture, each texel brightened by <paramref name="brightness"/> first (clamped): <paramref name="cells"/> takes
    /// three floats a cell, row by row, its linear red, green and blue. Returns the region the cells cover, or null when
    /// the region misses the picture.
    /// </summary>
    public static Int32Rect? Measure(ReadOnlySpan<byte> bgra, int width, int height, int stride, Int32Rect region, double brightness, Span<float> cells)
    {
        int x0 = Math.Max(region.X, 0), y0 = Math.Max(region.Y, 0);
        int x1 = Math.Min(region.X + region.Width, width), y1 = Math.Min(region.Y + region.Height, height);
        if (x1 <= x0 || y1 <= y0) return null;
        int nx = Math.Min(Grid, x1 - x0), ny = Math.Min(Grid, y1 - y0);
        Span<int> counts = stackalloc int[Cells];
        cells[..(Cells * 3)].Clear();
        double r = 0, g = 0, b = 0;
        for (var j = 0; j < ny; j++)
        {
            var y = y0 + (int)((j + 0.5) * (y1 - y0) / ny);
            var row = j * Side / ny * Side;
            for (var i = 0; i < nx; i++)
            {
                var at = y * stride + (x0 + (int)((i + 0.5) * (x1 - x0) / nx)) * 4;
                double sb = Lit(bgra[at], brightness), sg = Lit(bgra[at + 1], brightness), sr = Lit(bgra[at + 2], brightness);
                var cell = row + i * Side / nx;
                cells[cell * 3] += (float)sr;
                cells[cell * 3 + 1] += (float)sg;
                cells[cell * 3 + 2] += (float)sb;
                counts[cell]++;
                (r, g, b) = (r + sr, g + sg, b + sb);
            }
        }
        Finish(cells, counts, r, g, b, nx * ny);
        return new Int32Rect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// As <see cref="Measure"/>, but what the glass shows rather than what lies behind it: each sample at a pixel of
    /// <paramref name="box"/> (on the screen; the picture lies at <paramref name="bounds"/>) reads the picture where the
    /// recipe's displacement takes it, the pixel plus its move mirrored into the box (<see cref="DisplacementField.Source"/>),
    /// as the glass draws it (a white window bent into blotches across a pane). Without <paramref name="field"/> the samples
    /// read straight behind. The samples cover the part of the box over the picture: returned in the box's pixels, or null
    /// when the box misses the picture.
    /// </summary>
    public static Rect? MeasureBent(ReadOnlySpan<byte> bgra, int width, int height, int stride, Rect bounds, Rect box, double brightness,
        DisplacementField? field, double scale, Span<float> cells)
    {
        int bw = (int)Math.Round(box.Width), bh = (int)Math.Round(box.Height);
        if (bw <= 0 || bh <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return null;
        int x0 = Math.Max(0, (int)Math.Ceiling(bounds.X - box.X)), y0 = Math.Max(0, (int)Math.Ceiling(bounds.Y - box.Y));
        int x1 = Math.Min(bw, (int)Math.Floor(bounds.Right - box.X)), y1 = Math.Min(bh, (int)Math.Floor(bounds.Bottom - box.Y));
        if (x1 <= x0 || y1 <= y0) return null;
        double sx = width / bounds.Width, sy = height / bounds.Height;
        var bend = field != null && field.Width >= bw && field.Height >= bh && scale != 0;
        int nx = Math.Min(Grid, x1 - x0), ny = Math.Min(Grid, y1 - y0);
        Span<int> counts = stackalloc int[Cells];
        cells[..(Cells * 3)].Clear();
        double r = 0, g = 0, b = 0;
        for (var j = 0; j < ny; j++)
        {
            var py = y0 + (int)((j + 0.5) * (y1 - y0) / ny);
            var row = j * Side / ny * Side;
            for (var i = 0; i < nx; i++)
            {
                var px = x0 + (int)((i + 0.5) * (x1 - x0) / nx);
                var (qx, qy) = bend ? field!.Source(px, py, bw, bh, scale) : (px, py);
                var ix = Math.Clamp((int)((box.X + qx + 0.5 - bounds.X) * sx), 0, width - 1);
                var iy = Math.Clamp((int)((box.Y + qy + 0.5 - bounds.Y) * sy), 0, height - 1);
                var at = iy * stride + ix * 4;
                double sb = Lit(bgra[at], brightness), sg = Lit(bgra[at + 1], brightness), sr = Lit(bgra[at + 2], brightness);
                var cell = row + i * Side / nx;
                cells[cell * 3] += (float)sr;
                cells[cell * 3 + 1] += (float)sg;
                cells[cell * 3 + 2] += (float)sb;
                counts[cell]++;
                (r, g, b) = (r + sr, g + sg, b + sb);
            }
        }
        Finish(cells, counts, r, g, b, nx * ny);
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>Each cell's sums to its mean; a cell no sample fell in (a picture smaller than the cells) takes the mean of
    /// them all.</summary>
    private static void Finish(Span<float> cells, ReadOnlySpan<int> counts, double r, double g, double b, int n)
    {
        for (var c = 0; c < Cells; c++)
        {
            if (counts[c] == 0) (cells[c * 3], cells[c * 3 + 1], cells[c * 3 + 2]) = ((float)(r / n), (float)(g / n), (float)(b / n));
            else (cells[c * 3], cells[c * 3 + 1], cells[c * 3 + 2]) = (cells[c * 3] / counts[c], cells[c * 3 + 1] / counts[c], cells[c * 3 + 2] / counts[c]);
        }
    }

    /// <summary>The cells' mean, as an sRGB colour.</summary>
    public static Color MeanOf(ReadOnlySpan<float> cells)
    {
        double r = 0, g = 0, b = 0;
        for (var c = 0; c < Cells; c++) (r, g, b) = (r + cells[c * 3], g + cells[c * 3 + 1], b + cells[c * 3 + 2]);
        return FromLinear(r / Cells, g / Cells, b / Cells);
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

    /// <summary>An sRGB channel (0 to 1) as linear light.</summary>
    public static double ToLinear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    /// <summary>An opaque sRGB colour's linear red, green and blue.</summary>
    public static (double R, double G, double B) Linear(Color colour) => (Light[colour.R], Light[colour.G], Light[colour.B]);
}

/// <summary>
/// A piece's tones (Plan AD): 8 by 8 cells of mean linear light over <see cref="Region"/>, a rectangle in the piece's own
/// device pixels (its top left is the piece's). One a piece, filled again in place as its backdrop changes.
/// </summary>
internal sealed class ToneGrid
{
    /// <summary>Three floats a cell, row by row: linear red, green and blue.</summary>
    public readonly float[] Cells = new float[BackdropTone.Cells * 3];

    /// <summary>What the cells cover, in the piece's device pixels.</summary>
    public Rect Region { get; private set; } = Rect.Empty;

    /// <summary>Takes new cells and their region; true when anything changed.</summary>
    public bool Set(ReadOnlySpan<float> cells, Rect region)
    {
        if (Region == region && cells[..Cells.Length].SequenceEqual(Cells)) return false;
        cells[..Cells.Length].CopyTo(Cells);
        Region = region;
        return true;
    }

    /// <summary>Forgets the cells.</summary>
    public void Clear() => Region = Rect.Empty;

    /// <summary>The mean linear light over <paramref name="area"/> (the piece's device pixels), each cell weighted by how
    /// much of it the area covers; false when the area misses the cells.</summary>
    public bool Mean(Rect area, out double r, out double g, out double b)
    {
        (r, g, b) = (0, 0, 0);
        var region = Region;
        if (region.IsEmpty || area.IsEmpty) return false;
        double w = region.Width / BackdropTone.Side, h = region.Height / BackdropTone.Side, total = 0;
        if (w <= 0 || h <= 0) return false;
        int i0 = Math.Max(0, (int)Math.Floor((area.Left - region.Left) / w)), i1 = Math.Min(BackdropTone.Side - 1, (int)Math.Floor((area.Right - region.Left) / w));
        int j0 = Math.Max(0, (int)Math.Floor((area.Top - region.Top) / h)), j1 = Math.Min(BackdropTone.Side - 1, (int)Math.Floor((area.Bottom - region.Top) / h));
        for (var j = j0; j <= j1; j++)
        {
            for (var i = i0; i <= i1; i++)
            {
                double left = region.Left + i * w, top = region.Top + j * h;
                var overlap = Math.Max(0, Math.Min(area.Right, left + w) - Math.Max(area.Left, left)) * Math.Max(0, Math.Min(area.Bottom, top + h) - Math.Max(area.Top, top));
                if (overlap <= 0) continue;
                var c = (j * BackdropTone.Side + i) * 3;
                (r, g, b) = (r + Cells[c] * overlap, g + Cells[c + 1] * overlap, b + Cells[c + 2] * overlap);
                total += overlap;
            }
        }
        if (total <= 0) return false;
        (r, g, b) = (r / total, g / total, b / total);
        return true;
    }

    /// <summary>The cell <paramref name="i"/>, <paramref name="j"/>'s rectangle in the piece's device pixels.</summary>
    public Rect CellAt(int i, int j)
    {
        double w = Region.Width / BackdropTone.Side, h = Region.Height / BackdropTone.Side;
        return new Rect(Region.Left + i * w, Region.Top + j * h, w, h);
    }
}
