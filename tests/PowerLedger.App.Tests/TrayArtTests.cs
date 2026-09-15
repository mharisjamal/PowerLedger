using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Draws the tray icon at the small-icon size of each common display scale, with readings and without one, checks what
/// its pixels can show, and writes the icons to PNGs for a person to look at: each alone at its size, and all on one
/// sheet that sets them on a light and a dark taskbar, magnified. The drawing needs GDI+ and the fonts Windows ships.
/// </summary>
[Trait("Category", "UI")]
public class TrayArtTests
{
    private static readonly Color Amber = Color.FromArgb(0xF2, 0xB2, 0x33);
    private static readonly Color Graphite = Color.FromArgb(0x1B, 0x1D, 0x1A);
    private static readonly Color LightTaskbar = Color.FromArgb(0xF3, 0xF3, 0xF3);
    private static readonly Color DarkTaskbar = Color.FromArgb(0x1F, 0x1F, 0x1F);

    /// <summary>One or two digits, drawn big.</summary>
    private static readonly string[] Wide = ["0", "7", "24", "88"];

    /// <summary>Three digits and thousands, drawn narrower.</summary>
    private static readonly string[] Narrow = ["350", "999", "1k"];

    /// <summary>Small icons at 100, 125, 150 and 200 % scale.</summary>
    private static readonly int[] Scales = [16, 20, 24, 32];

    public static TheoryData<int> Sizes => new(Scales);

    [Theory]
    [MemberData(nameof(Sizes))]
    public void The_watts_sit_centred_inside_the_edge_of_the_amber_tile(int size)
    {
        foreach (var text in Wide.Concat(Narrow))
        {
            using var icon = TrayArt.Draw(text, size);
            TileFillsTheIcon(icon, text);

            var ink = InkBox(icon);
            ink.IsEmpty.ShouldBeFalse(text);
            ink.Left.ShouldBeGreaterThan(0, $"{text} at {size} px touches the tile's left edge");
            ink.Top.ShouldBeGreaterThan(0, $"{text} at {size} px touches the tile's top edge");
            ink.Right.ShouldBeLessThan(size, $"{text} at {size} px touches the tile's right edge");
            ink.Bottom.ShouldBeLessThan(size, $"{text} at {size} px touches the tile's bottom edge");
            (ink.Left + ink.Right - size).ShouldBeInRange(-1, 1, $"{text} at {size} px is off centre across, at {ink}");
            (ink.Top + ink.Bottom - size).ShouldBeInRange(-1, 1, $"{text} at {size} px is off centre down, at {ink}");

            // The strokes are hinted to whole pixels: somewhere the graphite covers a pixel fully, at the heart of the tile.
            var heart = new Rectangle(size / 4, size / 4, size / 2, size / 2);
            Count(icon, heart, pixel => pixel.ToArgb() == Graphite.ToArgb()).ShouldBeGreaterThan(0, $"{text} at {size} px has no solid stroke");
            Count(icon, heart, pixel => pixel.ToArgb() == Amber.ToArgb()).ShouldBeGreaterThan(0, $"{text} at {size} px hides the amber");
        }
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void A_reading_keeps_its_size_as_it_changes_and_one_or_two_digits_are_bigger(int size)
    {
        int Height(string text)
        {
            using var icon = TrayArt.Draw(text, size);
            return InkBox(icon).Height;
        }

        var wide = Wide.Select(Height).Distinct().ToList();
        var narrow = Narrow.Select(Height).Distinct().ToList();
        wide.Count.ShouldBe(1, $"At {size} px one and two digits are drawn at {string.Join(", ", wide)} pixels high.");
        narrow.Count.ShouldBe(1, $"At {size} px three digits and thousands are drawn at {string.Join(", ", narrow)} pixels high.");
        wide[0].ShouldBeGreaterThan(narrow[0]);
        wide[0].ShouldBeGreaterThanOrEqualTo(size / 2, "one or two digits are as large as fit");
        narrow[0].ShouldBeGreaterThanOrEqualTo(size * 2 / 5, "three digits are as large as fit");
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Without_a_reading_the_icon_is_the_logo_with_its_edges_on_whole_pixels(int size)
    {
        using var icon = TrayArt.Draw(null, size);
        TileFillsTheIcon(icon, "the logo");
        var all = new Rectangle(0, 0, size, size);
        Count(icon, all, pixel => pixel.ToArgb() == Graphite.ToArgb()).ShouldBeGreaterThan(size, "the P and the rows are graphite");
        Count(icon, all, pixel => pixel.A == 255 && pixel.G >= Amber.G + 24).ShouldBeGreaterThan(0, "the needle is cream");
        var ink = InkBox(icon);
        ink.Left.ShouldBeGreaterThan(0);
        ink.Top.ShouldBeGreaterThan(0);
        ink.Right.ShouldBeLessThan(size);
        ink.Bottom.ShouldBeLessThan(size);

        // Across the middle, the first thing after the amber is the P's stem, solid from its first pixel; down that
        // column the stem starts and ends on a pixel's edge too.
        var middle = size / 2;
        var stem = Enumerable.Range(0, size).First(x => icon.GetPixel(x, middle).ToArgb() != Amber.ToArgb());
        icon.GetPixel(stem, middle).ToArgb().ShouldBe(Graphite.ToArgb(), $"the stem's left edge is blurred at {size} px");
        var column = Enumerable.Range(0, size).Select(y => icon.GetPixel(stem, y)).ToList();
        var top = column.FindIndex(pixel => pixel.ToArgb() != Amber.ToArgb());
        var foot = column.FindLastIndex(pixel => pixel.ToArgb() != Amber.ToArgb());
        column[top].ToArgb().ShouldBe(Graphite.ToArgb(), $"the P's top is blurred at {size} px");
        column[foot].ToArgb().ShouldBe(Graphite.ToArgb(), $"the stem's foot is blurred at {size} px");
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Windows_gets_the_icons_own_colours_so_its_corners_blend_into_a_light_taskbar(int size)
    {
        // Windows' copy of the pixels, read back through the icon's handle. It treats them as unpremultiplied: had it
        // been given premultiplied ones, the rounded corners' faint pixels would come back darkened and be drawn grey.
        using var icon = TrayArt.DrawIcon("88", size);
        icon.Size.ShouldBe(new Size(size, size));
        using var windows = Icon.FromHandle(icon.Handle);
        using var pixels = windows.ToBitmap();
        TileFillsTheIcon(pixels, $"Windows' copy at {size} px");
        Count(pixels, new Rectangle(0, 0, size, size), pixel => pixel.A is > 0 and < 255).ShouldBeGreaterThan(0, "the corners are antialiased");
    }

    [Fact]
    public void The_icons_are_drawn_to_pngs_for_review()
    {
        Directory.CreateDirectory(RenderingTests.Folder);
        string?[] texts = [null, .. Wide, .. Narrow];
        const int Cell = 136;   // each icon magnified to about 128 pixels, with a border
        using var sheet = new Bitmap(texts.Length * Cell, Scales.Length * 2 * Cell, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(sheet))
        {
            graphics.Clear(Color.Gray);
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            using var light = new SolidBrush(LightTaskbar);
            using var dark = new SolidBrush(DarkTaskbar);
            for (var row = 0; row < Scales.Length; row++)
            {
                var size = Scales[row];
                var zoom = 128 / size;
                for (var column = 0; column < texts.Length; column++)
                {
                    using var icon = TrayArt.Draw(texts[column], size);
                    icon.Save(Path.Combine(RenderingTests.Folder, $"tray-{texts[column] ?? "none"}-{size}.png"), ImageFormat.Png);
                    foreach (var (ground, band) in new[] { (light, 0), (dark, 1) })
                    {
                        var cell = new Rectangle(column * Cell, (row * 2 + band) * Cell, Cell - 2, Cell - 2);
                        graphics.FillRectangle(ground, cell);
                        var drawn = size * zoom;
                        graphics.DrawImage(icon, new Rectangle(cell.X + (cell.Width - drawn) / 2, cell.Y + (cell.Height - drawn) / 2, drawn, drawn));
                    }
                }
            }
        }
        sheet.Save(Path.Combine(RenderingTests.Folder, "tray-sheet.png"), ImageFormat.Png);

        new FileInfo(Path.Combine(RenderingTests.Folder, "tray-sheet.png")).Length.ShouldBeGreaterThan(10_000);
        foreach (var size in Scales)
        {
            foreach (var text in texts)
            {
                File.Exists(Path.Combine(RenderingTests.Folder, $"tray-{text ?? "none"}-{size}.png")).ShouldBeTrue();
            }
        }
    }

    /// <summary>
    /// The tile fills the icon: the corners are clear, the middle of each edge is amber, and every pixel the tile's
    /// rounded corners leave partly clear is amber, so no ink reaches them. GDI+ keeps such pixels premultiplied, so a
    /// faint one's colour comes back rounded; what counts is how far it moves the taskbar under it, at most two levels.
    /// </summary>
    private static void TileFillsTheIcon(Bitmap icon, string what)
    {
        var last = icon.Width - 1;
        foreach (var (x, y) in new[] { (0, 0), (last, 0), (0, last), (last, last) })
        {
            icon.GetPixel(x, y).A.ShouldBe((byte)0, $"{what}: the corner at ({x}, {y}) is not clear");
        }
        foreach (var (x, y) in new[] { (last / 2, 0), (0, last / 2), (last, last / 2), (last / 2, last) })
        {
            icon.GetPixel(x, y).ToArgb().ShouldBe(Amber.ToArgb(), $"{what}: the edge at ({x}, {y}) is not the tile's amber");
        }
        for (var y = 0; y <= last; y++)
        {
            for (var x = 0; x <= last; x++)
            {
                var pixel = icon.GetPixel(x, y);
                if (pixel.A is 0 or 255) continue;
                var amber = new[] { (pixel.R, Amber.R), (pixel.G, Amber.G), (pixel.B, Amber.B) }
                    .All(channel => Math.Abs(channel.Item1 - channel.Item2) * pixel.A <= 2 * 255);
                amber.ShouldBeTrue($"{what}: the partly clear pixel at ({x}, {y}) is {pixel}, not amber");
            }
        }
    }

    /// <summary>The box around every opaque pixel that is not the tile's amber: the digits, or the logo's P, needle and rows.</summary>
    private static Rectangle InkBox(Bitmap icon)
    {
        int left = icon.Width, top = icon.Height, right = 0, bottom = 0;
        for (var y = 0; y < icon.Height; y++)
        {
            for (var x = 0; x < icon.Width; x++)
            {
                var pixel = icon.GetPixel(x, y);
                if (pixel.A != 255 || pixel.ToArgb() == Amber.ToArgb()) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + 1);
                bottom = Math.Max(bottom, y + 1);
            }
        }
        return right == 0 ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static int Count(Bitmap icon, Rectangle area, Func<Color, bool> match)
    {
        var count = 0;
        for (var y = area.Top; y < area.Bottom; y++)
        {
            for (var x = area.Left; x < area.Right; x++)
            {
                if (match(icon.GetPixel(x, y))) count++;
            }
        }
        return count;
    }
}
