using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PowerLedger.App;

/// <summary>
/// Draws the tray icon: the brand's amber tile filling the icon, with the watts on it in graphite, or with the logo until
/// there is a reading. The tile stands out on a light taskbar and a dark one alike. Digits are drawn one at a time at
/// whole pixels, hinted and a clear gap apart, as large as fit inside the tile's edge; every reading of a kind (one or two
/// digits, or three digits and thousands) gets the same size, so the readout keeps its size as it changes.
/// </summary>
internal static class TrayArt
{
    private static readonly Color Amber = Color.FromArgb(0xF2, 0xB2, 0x33);
    private static readonly Color Graphite = Color.FromArgb(0x1B, 0x1D, 0x1A);
    private static readonly Color Cream = Color.FromArgb(0xFF, 0xF6, 0xDC);

    /// <summary>The app icon is drawn on a grid this many units square.</summary>
    private const double Grid = 48;

    /// <summary>One or two digits: Bahnschrift SemiBold at its semi-condensed width, else plain Bahnschrift, else Segoe UI.</summary>
    private static readonly Lazy<(FontFamily Family, FontStyle Style)> Wide = new(() => Installed(
        ("Bahnschrift SemiBold SemiConden", FontStyle.Regular), ("Bahnschrift", FontStyle.Bold), ("Segoe UI", FontStyle.Bold)));

    /// <summary>Three digits and thousands: Bahnschrift SemiBold at its condensed width, else as for one or two digits.</summary>
    private static readonly Lazy<(FontFamily Family, FontStyle Style)> Narrow = new(() => Installed(
        ("Bahnschrift SemiBold Condensed", FontStyle.Regular), ("Bahnschrift SemiBold SemiConden", FontStyle.Regular),
        ("Bahnschrift", FontStyle.Bold), ("Segoe UI", FontStyle.Bold)));

    /// <summary>The icon, <paramref name="size"/> pixels square as small icons are: <paramref name="text"/> on the tile, or the logo when it is null.</summary>
    public static Bitmap Draw(string? text, int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 16);   // no smaller small icon exists
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;        // whole coordinates fall on the edges between pixels
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        using (var tile = Tile(size))
        using (var amber = new SolidBrush(Amber))
        {
            graphics.FillPath(amber, tile);
        }
        if (text is null) Logo(graphics, size);
        else Digits(graphics, text, size);
        return bitmap;
    }

    /// <summary>
    /// <see cref="Draw"/>'s picture as an icon, made the way Windows makes one from an icon file: a file of one PNG image,
    /// loaded from memory. <see cref="Bitmap.GetHicon"/> would be shorter, but it hands Windows premultiplied colours,
    /// which Windows darkens again, so the tile's antialiased corners would turn grey on a light taskbar.
    /// </summary>
    public static Icon DrawIcon(string? text, int size)
    {
        using var png = new MemoryStream();
        using (var picture = Draw(text, size))
        {
            picture.Save(png, ImageFormat.Png);
        }
        using var file = new MemoryStream();
        using (var writer = new BinaryWriter(file, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write((short)0);          // reserved
            writer.Write((short)1);          // an icon, not a cursor
            writer.Write((short)1);          // of one image,
            writer.Write((byte)size);        // this wide
            writer.Write((byte)size);        // and this high,
            writer.Write((byte)0);           // with no palette;
            writer.Write((byte)0);           // reserved
            writer.Write((short)1);          // one plane
            writer.Write((short)32);         // of 32 bits a pixel,
            writer.Write((int)png.Length);   // this long,
            writer.Write(6 + 16);            // after this header and entry
            writer.Write(png.GetBuffer(), 0, (int)png.Length);
        }
        file.Position = 0;
        return new Icon(file, size, size);
    }

    /// <summary>The corner radius of the tile, the app icon's 10.5 units in 48.</summary>
    private static double Radius(int size) => size * 10.5 / Grid;

    private static GraphicsPath Tile(int size)
    {
        var diameter = (float)(2 * Radius(size));
        var far = size - diameter;
        var path = new GraphicsPath();
        path.AddArc(0, 0, diameter, diameter, 180, 90);
        path.AddArc(far, 0, diameter, diameter, 270, 90);
        path.AddArc(far, far, diameter, diameter, 0, 90);
        path.AddArc(0, far, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// The logo, from the app icon's geometry on its 48-unit grid: a letter P as one even-odd path, a dial hole in its bowl
    /// holding a cream needle, and two ledger rows beside the stem. Its straight edges are moved to whole pixels, and the
    /// P's stroke and the rows each keep one width, so it stays crisp at every size; a half pixel rounds to even, which
    /// leaves the P a two-pixel stroke and an open dial at 20 pixels. The needle is placed within the dial as in the design.
    /// </summary>
    private static void Logo(Graphics graphics, int size)
    {
        var unit = size / Grid;
        int Snap(double units) => (int)Math.Round(units * unit);
        var stroke = Math.Max(1, Snap(6));
        var row = Math.Max(1, Snap(3));
        // The gap between the rows, under the bowl and beside the stem rounds down, so at 24 pixels the bowl keeps its height.
        var gap = Math.Max(1, (int)(3 * unit));
        var left = Snap(12);                                // the stem's left edge
        var stem = left + stroke;                           // its right edge, where the bowl and the dial begin
        var top = Snap(9);
        var foot = Snap(45);                                // the stem's foot, and the second row's
        var right = Snap(39);                               // the bowl's far side, and the first row's end
        var bowl = foot - 2 * row - 2 * gap;                // the bowl's bottom edge
        var outer = (bowl - top) / 2f;                      // the bowl's radius
        var inner = outer - stroke;                         // the dial's
        var centre = right - outer;                         // the arcs' centre, across

        using var letter = new GraphicsPath(FillMode.Alternate);
        letter.AddLine(left, top, centre, top);
        letter.AddArc(centre - outer, top, 2 * outer, 2 * outer, 270, 180);
        letter.AddLine(centre, bowl, stem, bowl);
        letter.AddLine(stem, bowl, stem, foot);
        letter.AddLine(stem, foot, left, foot);
        letter.CloseFigure();
        letter.StartFigure();
        letter.AddLine(stem, top + stroke, centre, top + stroke);
        letter.AddArc(centre - inner, top + stroke, 2 * inner, 2 * inner, 270, 180);
        letter.AddLine(centre, bowl - stroke, stem, bowl - stroke);
        letter.CloseFigure();
        using var graphite = new SolidBrush(Graphite);
        graphics.FillPath(graphite, letter);
        graphics.FillRectangle(graphite, stem + gap, foot - 2 * row - gap, right - stem - gap, row);
        graphics.FillRectangle(graphite, stem + gap, foot - row, right - stroke - stem - gap, row);

        // The design's dial runs from 18 to 33 across and from 15 to 27 down; the needle turns about (22.5, 25.5) and
        // points to (30, 18), three units wide with round ends, over a pivot 2.25 units in radius.
        PointF InDial(double x, double y) => new(
            (float)(stem + (x - 18) / 15 * (right - stroke - stem)),
            (float)(top + stroke + (y - 15) / 12 * (bowl - top - 2 * stroke)));
        var pivot = InDial(22.5, 25.5);
        using var needle = new Pen(Cream, (float)(3 * unit)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawLine(needle, pivot, InDial(30, 18));
        var dot = (float)(2.25 * unit);
        using var cream = new SolidBrush(Cream);
        graphics.FillEllipse(cream, pivot.X - dot, pivot.Y - dot, 2 * dot, 2 * dot);
    }

    /// <summary>
    /// The watts in graphite, centred on the tile. One or two digits are drawn in the wide font, three digits and
    /// thousands in the narrow one, each at the largest size at which two or three of the widest digit fit (or as many as
    /// the text has, should a reading ever run longer).
    /// </summary>
    private static void Digits(Graphics graphics, string text, int size)
    {
        var wide = text.Length <= 2 && text.All(char.IsAsciiDigit);
        var (family, style) = (wide ? Wide : Narrow).Value;
        var (em, top, height) = Fit(family, style, wide ? 2 : Math.Max(3, text.Length), size);
        using var font = new Font(family, em, style, GraphicsUnit.Pixel);
        var gap = Gap(em);
        var glyphs = text.Select(c => Glyph.Draw(c, font)).ToList();
        try
        {
            var x = (size - glyphs.Sum(glyph => glyph.Width) - gap * (glyphs.Count - 1)) / 2;
            var y = (size - height) / 2 - top;
            foreach (var glyph in glyphs)
            {
                glyph.Paint(graphics, x, y);
                x += glyph.Width + gap;
            }
        }
        finally
        {
            foreach (var glyph in glyphs) glyph.Dispose();
        }
    }

    /// <summary>The space between digits: Bahnschrift's own, a little under a tenth of an em, and never under a pixel.</summary>
    private static int Gap(int em) => Math.Max(1, (int)Math.Round(em * 0.09));

    /// <summary>
    /// The largest whole-pixel font size at which <paramref name="slots"/> of the widest digit, a gap apart, fit inside the
    /// tile's edge with a pixel of amber around them for every 16 pixels of icon, with where the digits' ink begins below
    /// the drawing point and how tall it is.
    /// </summary>
    private static (int Em, int Top, int Height) Fit(FontFamily family, FontStyle style, int slots, int size)
    {
        var margin = Math.Max(1, (int)Math.Round(size / 16.0));
        for (var em = size; ; em--)
        {
            using var font = new Font(family, em, style, GraphicsUnit.Pixel);
            int widest = 0, top = int.MaxValue, bottom = int.MinValue;
            foreach (var digit in "0123456789")
            {
                using var glyph = Glyph.Draw(digit, font);
                widest = Math.Max(widest, glyph.Width);
                top = Math.Min(top, glyph.Top);
                bottom = Math.Max(bottom, glyph.Bottom);
            }
            if (em <= 6 || Fits(slots * widest + (slots - 1) * Gap(em), bottom - top, size, margin)) return (em, top, bottom - top);
        }
    }

    /// <summary>
    /// Whether a box this wide and high, centred as the digits are, keeps <paramref name="margin"/> pixels of amber
    /// between it and the tile's edge: along the sides, and at the rounded corners, measured to the centre of the box's
    /// corner pixel.
    /// </summary>
    private static bool Fits(int width, int height, int size, int margin)
    {
        if (width > size - 2 * margin || height > size - 2 * margin) return false;
        var radius = Radius(size);
        var across = radius - ((size - width) / 2 + 0.5);
        var down = radius - ((size - height) / 2 + 0.5);
        var reach = radius - margin - 0.5;
        return across <= 0 || down <= 0 || across * across + down * down <= reach * reach;
    }

    /// <summary>The first of <paramref name="choices"/> installed here, or the generic sans-serif in bold.</summary>
    private static (FontFamily Family, FontStyle Style) Installed(params (string Name, FontStyle Style)[] choices)
    {
        foreach (var (name, style) in choices)
        {
            try
            {
                var family = new FontFamily(name);
                if (family.IsStyleAvailable(style)) return (family, style);
                family.Dispose();
            }
            catch (ArgumentException)
            {
                // Not installed on this PC; try the next.
            }
        }
        return (new FontFamily(GenericFontFamilies.SansSerif), FontStyle.Bold);
    }

    /// <summary>
    /// One character drawn alone in graphite, from a whole-pixel point so its hinting holds wherever it is painted, and
    /// where its ink lies from that point. A column at either side holding less than a pixel of ink in all is an
    /// antialiased tip, the end of the 4's crossbar or of the k's arm: it is left out, keeping a clear gap between digits.
    /// </summary>
    private sealed class Glyph : IDisposable
    {
        private readonly Bitmap _image;
        private readonly int _origin;

        private Glyph(Bitmap image, int origin, int left, int right, int top, int bottom)
        {
            _image = image;
            _origin = origin;
            Left = left;
            Right = right;
            Top = top;
            Bottom = bottom;
        }

        /// <summary>The ink's columns, from <see cref="Left"/> to before <see cref="Right"/>, relative to the drawing point.</summary>
        public int Left { get; }

        public int Right { get; }

        /// <summary>The ink's rows, from <see cref="Top"/> to before <see cref="Bottom"/>, relative to the drawing point.</summary>
        public int Top { get; }

        public int Bottom { get; }

        public int Width => Right - Left;

        public static Glyph Draw(char character, Font font)
        {
            var origin = (int)Math.Ceiling(font.Size);
            var side = 3 * origin;
            var image = new Bitmap(side, side, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                graphics.TextContrast = 0;   // keeps the antialiased pixels dark, which small digits on amber need
                using var brush = new SolidBrush(Graphite);
                using var format = StringFormat.GenericTypographic;
                graphics.DrawString(character.ToString(), font, brush, origin, origin, format);
            }
            var ink = new int[side];   // the alpha in each column, added up
            int top = side, bottom = 0;
            var data = image.LockBits(new Rectangle(0, 0, side, side), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[4 * side];
                for (var y = 0; y < side; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    for (var x = 0; x < side; x++)
                    {
                        var alpha = row[4 * x + 3];
                        if (alpha == 0) continue;
                        ink[x] += alpha;
                        top = Math.Min(top, y);
                        bottom = Math.Max(bottom, y + 1);
                    }
                }
            }
            finally
            {
                image.UnlockBits(data);
            }
            var left = Array.FindIndex(ink, sum => sum > 0);
            var right = Array.FindLastIndex(ink, sum => sum > 0) + 1;
            if (left < 0) return new Glyph(image, origin, 0, 0, 0, 0);
            while (right - left > 1 && ink[left] < byte.MaxValue) left++;
            while (right - left > 1 && ink[right - 1] < byte.MaxValue) right--;
            return new Glyph(image, origin, left - origin, right - origin, top - origin, bottom - origin);
        }

        /// <summary>Paints the ink with its left edge at <paramref name="x"/> and its drawing point's row at <paramref name="y"/>, pixel for pixel.</summary>
        public void Paint(Graphics graphics, int x, int y)
        {
            if (Width == 0) return;
            var height = Bottom - Top;
            graphics.DrawImage(_image, new Rectangle(x, y + Top, Width, height),
                new Rectangle(_origin + Left, _origin + Top, Width, height), GraphicsUnit.Pixel);
        }

        public void Dispose() => _image.Dispose();
    }
}
