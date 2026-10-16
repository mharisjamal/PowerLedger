using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App.Aero;

/// <summary>
/// The tone of what a glass piece shows (Plan AD): the light of its glass after the recipe's brightness, in cells
/// <see cref="CellPixels"/> a side across the piece, each the mean of a few samples read where the recipe bends them from,
/// packed as an 8 bit sRGB colour (four bytes a cell). GlassInk turns them into the ink its text and icons take: the
/// cells under each word for it, their mean for the piece. Fine enough that a word's own cells are the glass right behind
/// its strokes, where the glass bends a white window into blotches. Read from pixels already in hand (the CPU path's
/// picture, the wallpaper's, a nested piece's finished glass), so nothing is captured again and nothing is allocated. The
/// GPU path reads the same cells on the GPU (GpuGlassRenderer.Tones, Shaders/GpuGlass.hlsl's Tone).
/// </summary>
internal static class BackdropTone
{
    /// <summary>A cell's side, about, in device pixels: about half a line of text's height.</summary>
    public const double CellPixels = 8;

    /// <summary>Cells a side, at most (a piece 2048 device pixels across).</summary>
    public const int MaxSide = 256;

    /// <summary>Samples a cell side, 2 by 2, on the GPU and the CPU alike.</summary>
    public const int GpuSamples = 2;
    public const int CpuSamples = 2;

    /// <summary>Each 8 bit sRGB code value as linear light.</summary>
    private static readonly double[] Light = Enumerable.Range(0, 256).Select(v => ToLinear(v / 255.0)).ToArray();

    /// <summary>Linear light in 4096 steps as its 8 bit sRGB code.</summary>
    private static readonly byte[] Codes = Enumerable.Range(0, 4096).Select(i => Code(i / 4095.0)).ToArray();

    /// <summary>The cells across and down a region of <paramref name="width"/> by <paramref name="height"/> device pixels.</summary>
    public static (int Cols, int Rows) GridFor(double width, double height)
        => (Math.Clamp((int)Math.Round(width / CellPixels), 1, MaxSide), Math.Clamp((int)Math.Round(height / CellPixels), 1, MaxSide));

    /// <summary>The mean light of <paramref name="region"/> (clipped to the picture) of an opaque BGRA picture, each texel
    /// brightened by <paramref name="brightness"/> first (clamped), as an sRGB colour; null when the region misses the
    /// picture.</summary>
    public static Color? Mean(ReadOnlySpan<byte> bgra, int width, int height, int stride, Int32Rect region, double brightness)
    {
        Span<uint> cell = stackalloc uint[1];
        return Measure(bgra, width, height, stride, region, brightness, 1, 1, 32, cell) == null ? null : ColourOf(cell[0]);
    }

    /// <summary>
    /// The mean light of each of <paramref name="cols"/> by <paramref name="rows"/> cells across <paramref name="region"/>
    /// (clipped to the picture) of an opaque BGRA picture, <paramref name="perCell"/> by <paramref name="perCell"/> samples
    /// a cell, each texel brightened by <paramref name="brightness"/> first (clamped), into <paramref name="cells"/> row by
    /// row (<see cref="Pack"/>). Returns the region the cells cover, or null when the region misses the picture.
    /// </summary>
    public static Int32Rect? Measure(ReadOnlySpan<byte> bgra, int width, int height, int stride, Int32Rect region, double brightness, int cols, int rows,
        int perCell, Span<uint> cells)
    {
        int x0 = Math.Max(region.X, 0), y0 = Math.Max(region.Y, 0);
        int x1 = Math.Min(region.X + region.Width, width), y1 = Math.Min(region.Y + region.Height, height);
        if (x1 <= x0 || y1 <= y0) return null;
        double cw = (double)(x1 - x0) / cols, ch = (double)(y1 - y0) / rows, n = perCell * perCell;
        for (var cj = 0; cj < rows; cj++)
        {
            for (var ci = 0; ci < cols; ci++)
            {
                double r = 0, g = 0, b = 0;
                for (var sj = 0; sj < perCell; sj++)
                {
                    var y = Math.Min(y1 - 1, y0 + (int)((cj + (sj + 0.5) / perCell) * ch));
                    for (var si = 0; si < perCell; si++)
                    {
                        var x = Math.Min(x1 - 1, x0 + (int)((ci + (si + 0.5) / perCell) * cw));
                        var at = y * stride + x * 4;
                        (r, g, b) = (r + Lit(bgra[at + 2], brightness), g + Lit(bgra[at + 1], brightness), b + Lit(bgra[at], brightness));
                    }
                }
                cells[cj * cols + ci] = Pack(r / n, g / n, b / n);
            }
        }
        return new Int32Rect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// As <see cref="Measure"/>, but what the glass shows rather than what lies behind it: each sample at a pixel of
    /// <paramref name="box"/> (on the screen; the picture lies at <paramref name="bounds"/>) reads the picture where the
    /// recipe's displacement takes it, the pixel plus its move mirrored into the box (<see cref="DisplacementField.Source"/>),
    /// as the glass draws it (a white window bent into blotches across a pane). Without <paramref name="field"/> the samples
    /// read straight behind. The cells cover the part of the box over the picture: returned in the box's pixels, or null
    /// when the box misses the picture.
    /// </summary>
    public static Rect? MeasureBent(ReadOnlySpan<byte> bgra, int width, int height, int stride, Rect bounds, Rect box, double brightness,
        DisplacementField? field, double scale, int cols, int rows, int perCell, Span<uint> cells)
    {
        int bw = (int)Math.Round(box.Width), bh = (int)Math.Round(box.Height);
        if (bw <= 0 || bh <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return null;
        int x0 = Math.Max(0, (int)Math.Ceiling(bounds.X - box.X)), y0 = Math.Max(0, (int)Math.Ceiling(bounds.Y - box.Y));
        int x1 = Math.Min(bw, (int)Math.Floor(bounds.Right - box.X)), y1 = Math.Min(bh, (int)Math.Floor(bounds.Bottom - box.Y));
        if (x1 <= x0 || y1 <= y0) return null;
        double sx = width / bounds.Width, sy = height / bounds.Height;
        var bend = field != null && field.Width >= bw && field.Height >= bh && scale != 0;
        double cw = (double)(x1 - x0) / cols, ch = (double)(y1 - y0) / rows, n = perCell * perCell;
        for (var cj = 0; cj < rows; cj++)
        {
            for (var ci = 0; ci < cols; ci++)
            {
                double r = 0, g = 0, b = 0;
                for (var sj = 0; sj < perCell; sj++)
                {
                    var py = Math.Min(y1 - 1, y0 + (int)((cj + (sj + 0.5) / perCell) * ch));
                    for (var si = 0; si < perCell; si++)
                    {
                        var px = Math.Min(x1 - 1, x0 + (int)((ci + (si + 0.5) / perCell) * cw));
                        var (qx, qy) = bend ? field!.Source(px, py, bw, bh, scale) : (px, py);
                        var ix = Math.Clamp((int)((box.X + qx + 0.5 - bounds.X) * sx), 0, width - 1);
                        var iy = Math.Clamp((int)((box.Y + qy + 0.5 - bounds.Y) * sy), 0, height - 1);
                        var at = iy * stride + ix * 4;
                        (r, g, b) = (r + Lit(bgra[at + 2], brightness), g + Lit(bgra[at + 1], brightness), b + Lit(bgra[at], brightness));
                    }
                }
                cells[cj * cols + ci] = Pack(r / n, g / n, b / n);
            }
        }
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>A cell: linear light as an opaque 8 bit sRGB colour in four bytes (blue lowest, then green, red, and 255).</summary>
    public static uint Pack(double r, double g, double b)
        => 0xFF000000u | (uint)Codes[Step(r)] << 16 | (uint)Codes[Step(g)] << 8 | Codes[Step(b)];

    /// <summary>An opaque colour as a cell.</summary>
    public static uint Pack(Color colour) => 0xFF000000u | (uint)colour.R << 16 | (uint)colour.G << 8 | colour.B;

    /// <summary>A cell's colour.</summary>
    public static Color ColourOf(uint cell) => Color.FromRgb((byte)(cell >> 16), (byte)(cell >> 8), (byte)cell);

    /// <summary>A cell's linear red, green and blue.</summary>
    public static (double R, double G, double B) LinearOf(uint cell) => (Light[(cell >> 16) & 255], Light[(cell >> 8) & 255], Light[cell & 255]);

    /// <summary>A cell's relative luminance.</summary>
    public static double Luminance(uint cell) => 0.2126 * Light[(cell >> 16) & 255] + 0.7152 * Light[(cell >> 8) & 255] + 0.0722 * Light[cell & 255];

    /// <summary>The relative luminance of <paramref name="top"/> (translucent) laid over a cell, as WPF composes it: in
    /// sRGB, each channel rounded.</summary>
    public static double LuminanceOver(Color top, uint cell)
    {
        double a = top.A / 255.0, k = 1 - a;
        int r = (int)Math.Round(top.R * a + ((cell >> 16) & 255) * k), g = (int)Math.Round(top.G * a + ((cell >> 8) & 255) * k), b = (int)Math.Round(top.B * a + (cell & 255) * k);
        return 0.2126 * Light[r] + 0.7152 * Light[g] + 0.0722 * Light[b];
    }

    /// <summary>The first <paramref name="count"/> cells' mean, as an sRGB colour.</summary>
    public static Color MeanOf(ReadOnlySpan<uint> cells, int count)
    {
        double r = 0, g = 0, b = 0;
        for (var c = 0; c < count; c++)
        {
            var (lr, lg, lb) = LinearOf(cells[c]);
            (r, g, b) = (r + lr, g + lg, b + lb);
        }
        return FromLinear(r / count, g / count, b / count);
    }

    /// <summary>The mean of a premultiplied BGRA picture (a nested piece's finished glass, mostly bare), as a translucent
    /// sRGB colour: its cover is the mean alpha. Averaged in sRGB, as WPF composes it.</summary>
    public static Color? MeanPremultiplied(ReadOnlySpan<byte> pbgra, int width, int height, int stride)
        => width <= 0 || height <= 0 ? null : MeanPremultiplied(pbgra, width, height, stride, new Int32Rect(0, 0, width, height), 32);

    /// <summary>The mean of <paramref name="region"/> (clipped) of a premultiplied BGRA picture, <paramref name="samples"/>
    /// by <paramref name="samples"/> samples, as a translucent sRGB colour (its cover the mean alpha); clear where the
    /// region misses the picture.</summary>
    public static Color MeanPremultiplied(ReadOnlySpan<byte> pbgra, int width, int height, int stride, Int32Rect region, int samples = 2)
    {
        int x0 = Math.Max(region.X, 0), y0 = Math.Max(region.Y, 0);
        int x1 = Math.Min(region.X + region.Width, width), y1 = Math.Min(region.Y + region.Height, height);
        if (x1 <= x0 || y1 <= y0) return Color.FromArgb(0, 0, 0, 0);
        int nx = Math.Min(samples, x1 - x0), ny = Math.Min(samples, y1 - y0);
        double r = 0, g = 0, b = 0, a = 0;
        for (var j = 0; j < ny; j++)
        {
            var y = y0 + (int)((j + 0.5) * (y1 - y0) / ny);
            for (var i = 0; i < nx; i++)
            {
                var at = y * stride + (x0 + (int)((i + 0.5) * (x1 - x0) / nx)) * 4;
                (b, g, r, a) = (b + pbgra[at], g + pbgra[at + 1], r + pbgra[at + 2], a + pbgra[at + 3]);
            }
        }
        if (a <= 0) return Color.FromArgb(0, 0, 0, 0);
        static byte Channel(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
        return Color.FromArgb(Channel(a / (nx * ny)), Channel(r / a * 255), Channel(g / a * 255), Channel(b / a * 255));
    }

    /// <summary>A linear light mean back to an opaque sRGB colour.</summary>
    public static Color FromLinear(double r, double g, double b) => Color.FromRgb(Code(r), Code(g), Code(b));

    /// <summary>The relative luminance of a cell's linear red, green and blue.</summary>
    public static double Luminance(double r, double g, double b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;

    private static int Step(double linear) => (int)Math.Round(Math.Clamp(linear, 0, 1) * 4095);

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
/// A piece's tones (Plan AD): <see cref="Cols"/> by <see cref="Rows"/> cells (<see cref="BackdropTone.Pack"/>) over
/// <see cref="Region"/>, a rectangle in the piece's own device pixels (its top left is the piece's). One a piece, filled
/// again in place as its backdrop changes; its array grows only when the piece does.
/// </summary>
internal sealed class ToneGrid
{
    private uint[] _cells = [];

    /// <summary>The cells, row by row; the first <see cref="Count"/> hold.</summary>
    public uint[] Cells => _cells;

    public int Cols { get; private set; }

    public int Rows { get; private set; }

    public int Count => Cols * Rows;

    /// <summary>What the cells cover, in the piece's device pixels.</summary>
    public Rect Region { get; private set; } = Rect.Empty;

    /// <summary>Takes new cells and their region; true when anything changed.</summary>
    public bool Set(ReadOnlySpan<uint> cells, int cols, int rows, Rect region)
    {
        var count = cols * rows;
        if (Region == region && Cols == cols && Rows == rows && cells[..count].SequenceEqual(_cells.AsSpan(0, count))) return false;
        if (_cells.Length < count) _cells = new uint[count];
        cells[..count].CopyTo(_cells);
        (Cols, Rows, Region) = (cols, rows, region);
        return true;
    }

    /// <summary>Forgets the cells.</summary>
    public void Clear() => (Cols, Rows, Region) = (0, 0, Rect.Empty);

    /// <summary>The cells <paramref name="area"/> (the piece's device pixels) covers: columns <paramref name="i0"/> to
    /// <paramref name="i1"/> and rows <paramref name="j0"/> to <paramref name="j1"/>, inclusive; false when it misses them.</summary>
    public bool Covered(Rect area, out int i0, out int i1, out int j0, out int j1)
    {
        (i0, i1, j0, j1) = (0, -1, 0, -1);
        var region = Region;
        if (Count == 0 || region.IsEmpty || area.IsEmpty) return false;
        double w = region.Width / Cols, h = region.Height / Rows;
        if (w <= 0 || h <= 0) return false;
        // A cell only touched along an edge (under a pixel of the area) is not under it.
        const double edge = 0.5;
        i0 = Math.Max(0, (int)Math.Floor((area.Left + edge - region.Left) / w));
        i1 = Math.Min(Cols - 1, (int)Math.Floor((area.Right - edge - region.Left) / w));
        j0 = Math.Max(0, (int)Math.Floor((area.Top + edge - region.Top) / h));
        j1 = Math.Min(Rows - 1, (int)Math.Floor((area.Bottom - edge - region.Top) / h));
        return i1 >= i0 && j1 >= j0;
    }

    /// <summary>The mean linear light over <paramref name="area"/> (the piece's device pixels), each cell weighted by how
    /// much of it the area covers; false when the area misses the cells.</summary>
    public bool Mean(Rect area, out double r, out double g, out double b)
    {
        (r, g, b) = (0, 0, 0);
        if (!Covered(area, out var i0, out var i1, out var j0, out var j1)) return false;
        double total = 0;
        for (var j = j0; j <= j1; j++)
        {
            for (var i = i0; i <= i1; i++)
            {
                var cell = CellAt(i, j);
                var overlap = Math.Max(0, Math.Min(area.Right, cell.Right) - Math.Max(area.Left, cell.Left)) * Math.Max(0, Math.Min(area.Bottom, cell.Bottom) - Math.Max(area.Top, cell.Top));
                if (overlap <= 0) continue;
                var (lr, lg, lb) = BackdropTone.LinearOf(_cells[j * Cols + i]);
                (r, g, b) = (r + lr * overlap, g + lg * overlap, b + lb * overlap);
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
        double w = Region.Width / Cols, h = Region.Height / Rows;
        return new Rect(Region.Left + i * w, Region.Top + j * h, w, h);
    }
}
