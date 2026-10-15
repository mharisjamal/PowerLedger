using System.Windows;
using System.Windows.Media;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan AD: Aero's ink follows what each glass piece shows (the owner, 2026-10-15: white text over a white window can't be
/// read). The pure parts: the luminance of the backdrop to the ink, with hysteresis; the inks' contrast on the grounds
/// each one is used on; the tone a picture gives a piece.
/// </summary>
[Trait("Category", "UI")]
public class GlassInkTests
{
    [Fact]
    public void A_dark_backdrop_keeps_white_ink_and_a_bright_one_turns_it_dark()
    {
        GlassInk.Dark(false, 0).ShouldBeFalse();
        GlassInk.Dark(false, 0.2).ShouldBeFalse("the mockup's blues and violets keep the white ink");
        GlassInk.Dark(false, GlassInk.ToDark - 0.001).ShouldBeFalse();
        GlassInk.Dark(false, GlassInk.ToDark).ShouldBeTrue();
        GlassInk.Dark(false, 1).ShouldBeTrue("a white window behind");
        GlassInk.Dark(true, 1).ShouldBeTrue();
        GlassInk.Dark(true, GlassInk.ToLight + 0.001).ShouldBeTrue();
        GlassInk.Dark(true, GlassInk.ToLight).ShouldBeFalse();
        GlassInk.Dark(true, 0).ShouldBeFalse();
    }

    /// <summary>A backdrop wavering about the middle (a video, a gradient under a moving pane) never flips the ink to and fro.</summary>
    [Fact]
    public void A_backdrop_wavering_in_the_band_does_not_flicker()
    {
        var middle = (GlassInk.ToDark + GlassInk.ToLight) / 2;
        var swing = (GlassInk.ToDark - GlassInk.ToLight) / 2 * 0.9;
        foreach (var start in new[] { false, true })
        {
            var dark = start;
            for (var i = 0; i < 200; i++)
            {
                dark = GlassInk.Dark(dark, middle + swing * Math.Sin(i * 0.7));
                dark.ShouldBe(start, $"step {i}");
            }
        }
        (GlassInk.ToDark - GlassInk.ToLight).ShouldBeGreaterThanOrEqualTo(0.05, "a band wide enough to hold");
    }

    public static TheoryData<string> DarkSteps => new() { "A.C.Text", "A.C.Text2", "A.C.NavText", "A.C.SegText", "A.C.LabText", "A.C.CapText", "A.C.HomeSub" };

    /// <summary>Dark ink is used from ToLight up: every text step at WCAG AA's 4.5:1 there and above; the quietest (Text3,
    /// Muted) at 3:1, as the white ink's quiet steps are held.</summary>
    [Fact]
    public void The_dark_ink_reads_on_every_ground_it_is_used_on()
        => UiHarness.OnUi(() =>
    {
        foreach (var strict in new[] { false, true })
        {
            var family = GlassInk.Family(dark: true, strict);
            family.Keys.ShouldBe(GlassInk.Keys, ignoreOrder: true);
            for (var l = GlassInk.ToLight; l <= 1.0001; l += 0.02)
            {
                var ground = GlassInk.GroundAt(Math.Min(l, 1));
                foreach (var key in GlassInk.Keys)
                {
                    var least = key is "A.C.Text3" or "A.C.Muted" ? 3.0 : 4.5;
                    Contrast.Ratio(Contrast.Over(family[key], ground), ground).ShouldBeGreaterThanOrEqualTo(least, $"{key} at luminance {l:0.00}, strict {strict}");
                }
            }
        }
        Contrast.Luminance(GlassInk.Family(dark: true, strict: false)["A.C.Text"]).ShouldBeLessThan(0.02, "near black");
    });

    /// <summary>White ink is kept below ToDark and settles below ToLight: there it holds the look's approved 3:1 floor (the
    /// default glass's text over its brightest frost), and 4.5:1 on any ground darker than 0.18.</summary>
    [Fact]
    public void The_white_ink_reads_where_it_settles()
        => UiHarness.OnUi(() =>
    {
        var family = GlassInk.Family(dark: false, strict: false);
        family["A.C.Text"].ShouldBe(Colors.White);
        for (var l = 0.0; l <= GlassInk.ToLight; l += 0.01)
        {
            var ground = GlassInk.GroundAt(l);
            Contrast.Ratio(family["A.C.Text"], ground).ShouldBeGreaterThanOrEqualTo(l <= 0.18 ? 4.5 : 3.0, $"luminance {l:0.00}");
        }
    });

    /// <summary>The white family is the look's own (GlassMaterial's steps), so a piece that turns back shows exactly what it
    /// inherits.</summary>
    [Fact]
    public void The_white_family_is_the_materials_own()
        => UiHarness.OnUi(() =>
        {
            foreach (var strict in new[] { false, true })
            {
                var map = GlassMaterial.Map(GlassSettings.Default with { IncreaseContrast = strict }, Theme.Dark);
                var family = GlassInk.Family(dark: false, strict);
                foreach (var key in GlassInk.Keys) family[key].ShouldBe((Color)map[key], $"{key}, strict {strict}");
            }
        });

    /// <summary>The accent's chart line on a bright ground: drawn darker in its own hue until it stands off at 3:1 (WCAG's
    /// for graphics) on every ground the dark ink is used on.</summary>
    [Theory]
    [InlineData("#E8FF78")]
    [InlineData("#8FDDFF")]
    [InlineData("#AFB8FF")]
    [InlineData("#FFD57A")]
    [InlineData("#FF9DC1")]
    public void The_accent_line_reads_on_a_bright_ground(string hex)
    {
        var accent = (Color)ColorConverter.ConvertFromString(hex);
        var line = GlassInk.OnBright(accent);
        for (var l = GlassInk.ToLight; l <= 1.0001; l += 0.02)
        {
            var ground = GlassInk.GroundAt(Math.Min(l, 1));
            Contrast.Ratio(line, ground).ShouldBeGreaterThanOrEqualTo(3, $"{hex} at {l:0.00}");
        }
        // Its own hue: the channels keep their order.
        (line.R >= line.G).ShouldBe(accent.R >= accent.G);
        (line.G >= line.B).ShouldBe(accent.G >= accent.B);
    }

    [Fact]
    public void The_ground_is_the_tint_over_the_backdrop()
    {
        var white = Colors.White;
        GlassInk.Ground(white, null).ShouldBe(white);
        GlassInk.Ground(white, Brushes.Transparent).ShouldBe(white);
        // The Dark style's smoked navy over a white window: still a dark ground, the white ink stays.
        var smoked = new SolidColorBrush(GlassMaterial.DarkTint);
        Contrast.Luminance(GlassInk.Ground(white, smoked)).ShouldBeLessThan(GlassInk.ToLight);
        // A gradient's stops are averaged.
        var gradient = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(255, 0, 0, 0), 90);
        var half = GlassInk.Ground(white, gradient);
        half.R.ShouldBeInRange((byte)126, (byte)129);
    }

    [Fact]
    public void A_pictures_tone_is_its_mean_light()
    {
        static byte[] Solid(int w, int h, byte v)
        {
            var pixels = new byte[w * h * 4];
            for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (v, v, v, 255);
            return pixels;
        }
        BackdropTone.Mean(Solid(40, 30, 255), 40, 30, 160, new Int32Rect(0, 0, 40, 30), 1).ShouldBe(Colors.White);
        BackdropTone.Mean(Solid(40, 30, 0), 40, 30, 160, new Int32Rect(0, 0, 40, 30), 1.1).ShouldBe(Colors.Black);
        // brightness(1.1) first, as the glass draws it.
        BackdropTone.Mean(Solid(40, 30, 100), 40, 30, 160, new Int32Rect(5, 5, 10, 10), 1.1)!.Value.R.ShouldBe((byte)110);
        // Half black, half white: the mean light, half of white's (sRGB 188), not the mean code value (128).
        var half = new byte[64 * 64 * 4];
        for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
            {
                var v = (byte)(x < 32 ? 0 : 255);
                var at = (y * 64 + x) * 4;
                (half[at], half[at + 1], half[at + 2], half[at + 3]) = (v, v, v, 255);
            }
        var mean = BackdropTone.Mean(half, 64, 64, 256, new Int32Rect(0, 0, 64, 64), 1)!.Value;
        Contrast.Luminance(mean).ShouldBe(0.5, 0.02);
        // Only the region, clipped to the picture; none at all outside it.
        BackdropTone.Mean(half, 64, 64, 256, new Int32Rect(40, 0, 100, 64), 1).ShouldBe(Colors.White);
        BackdropTone.Mean(half, 64, 64, 256, new Int32Rect(100, 0, 10, 10), 1).ShouldBeNull();
    }

    /// <summary>A piece's tone comes in 8 by 8 cells, so text over one side of a pane can take that side's ink.</summary>
    [Fact]
    public void A_pictures_tones_come_in_eight_by_eight_cells()
    {
        var pixels = new byte[64 * 32 * 4];
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 64; x++)
            {
                var v = (byte)(x < 32 ? 0 : 255);
                var at = (y * 64 + x) * 4;
                (pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]) = (v, v, v, 255);
            }
        var cells = new float[BackdropTone.Cells * 3];
        BackdropTone.Measure(pixels, 64, 32, 256, new Int32Rect(-10, 0, 100, 32), 1, cells).ShouldBe(new Int32Rect(0, 0, 64, 32), "clipped to the picture");
        for (var j = 0; j < BackdropTone.Side; j++)
            for (var i = 0; i < BackdropTone.Side; i++)
                cells[(j * BackdropTone.Side + i) * 3 + 1].ShouldBe(i < 4 ? 0f : 1f, 0.001f, $"cell {i}, {j}");
        BackdropTone.MeanOf(cells).ShouldBe(BackdropTone.FromLinear(0.5, 0.5, 0.5));
        var grid = new ToneGrid();
        grid.Set(cells, new Rect(0, 0, 64, 32)).ShouldBeTrue();
        grid.Set(cells, new Rect(0, 0, 64, 32)).ShouldBeFalse("nothing new");
        grid.Mean(new Rect(0, 0, 32, 32), out _, out var g, out _).ShouldBeTrue();
        g.ShouldBe(0, 0.001);
        grid.Mean(new Rect(40, 4, 10, 10), out _, out g, out _).ShouldBeTrue();
        g.ShouldBe(1, 0.001);
        grid.Mean(new Rect(16, 0, 32, 32), out _, out g, out _).ShouldBeTrue();
        g.ShouldBe(0.5, 0.001, "weighted by how much of each cell the area covers");
        grid.Mean(new Rect(100, 0, 10, 10), out _, out _, out _).ShouldBeFalse();
        // A picture smaller than the cells: every cell still has its tone.
        var tiny = new byte[4 * 4 * 4];
        Array.Fill(tiny, (byte)255);
        BackdropTone.Measure(tiny, 4, 4, 16, new Int32Rect(0, 0, 4, 4), 1, cells).ShouldNotBeNull();
        cells.ShouldAllBe(v => Math.Abs(v - 1) < 0.001);
    }

    [Fact]
    public void A_translucent_pictures_tone_keeps_its_cover()
    {
        // Premultiplied white at half cover over half the picture, the rest bare.
        var pixels = new byte[32 * 32 * 4];
        for (var y = 0; y < 32; y++)
            for (var x = 16; x < 32; x++)
            {
                var at = (y * 32 + x) * 4;
                (pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]) = (128, 128, 128, 128);
            }
        var tone = BackdropTone.MeanPremultiplied(pixels, 32, 32, 128)!.Value;
        tone.A.ShouldBeInRange((byte)62, (byte)66);
        tone.R.ShouldBeInRange((byte)253, (byte)255);
        BackdropTone.MeanPremultiplied(new byte[16 * 4], 4, 4, 16)!.Value.A.ShouldBe((byte)0);
    }
}
