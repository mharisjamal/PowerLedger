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
            family.Keys.ShouldBe([.. GlassInk.Keys, .. GlassInk.Graphics], ignoreOrder: true);
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

    /// <summary>
    /// Every step of both inks at both switch points, and on every ground its ink is used on: the dark ink from ToLight up
    /// (text at WCAG AA's 4.5:1, the quietest steps at 3:1); the white ink laid, held, up to ToDark at the look's 3:1 floor,
    /// every step (full white reads 3:1 just past ToDark, so each step is all but whole), and its words at 4.5:1 on any
    /// ground darker than 0.18.
    /// </summary>
    [Fact]
    public void Every_step_of_both_inks_reads_at_both_switch_points()
        => UiHarness.OnUi(() =>
    {
        foreach (var strict in new[] { false, true })
        {
            var dark = GlassInk.Family(dark: true, strict);
            var white = GlassInk.Family(dark: false, strict);
            white["A.C.Text"].ShouldBe(Colors.White);
            foreach (var l in new[] { GlassInk.ToLight, GlassInk.ToDark })
            {
                var ground = GlassInk.GroundAt(l);
                foreach (var key in GlassInk.Keys)
                {
                    Contrast.Ratio(Contrast.Over(dark[key], ground), ground).ShouldBeGreaterThanOrEqualTo(key is "A.C.Text3" or "A.C.Muted" ? 3 : 4.5, $"dark {key} at {l:0.000}, strict {strict}");
                    Contrast.Ratio(Contrast.Over(white[key], ground), ground).ShouldBeGreaterThanOrEqualTo(3, $"white {key} at {l:0.000}, strict {strict}");
                }
            }
            for (var l = 0.0; l <= GlassInk.ToDark; l += 0.01)
            {
                var ground = GlassInk.GroundAt(l);
                foreach (var key in GlassInk.Keys)
                {
                    Contrast.Ratio(Contrast.Over(white[key], ground), ground)
                        .ShouldBeGreaterThanOrEqualTo(l <= 0.18 && key is not ("A.C.Text3" or "A.C.Muted") ? 4.5 : 3, $"white {key} at {l:0.00}, strict {strict}");
                }
            }
        }
    });

    /// <summary>
    /// Where an ink is not laid (a piece on the look's own), its steps are the look's own (GlassMaterial's, 80 % and 60 %
    /// of the ink and the rest): white's each read at 3:1 on every ground up to HeldFrom, the point the quietest stops, and
    /// the light look's dark ones on every ground down to DarkHeldFrom. Past it (the ground still too far from the other
    /// ink to take it) the ink is laid held, and lets go again back past the margin. The white ink's lines stay the look's
    /// own.
    /// </summary>
    [Fact]
    public void The_looks_own_steps_hold_until_the_ink_is_held()
        => UiHarness.OnUi(() =>
        {
            GlassInk.HeldFrom.ShouldBeInRange(0.12, GlassInk.ToLight);
            GlassInk.HeldBack.ShouldBeLessThan(GlassInk.HeldFrom);
            GlassInk.DarkHeldFrom.ShouldBeInRange(GlassInk.ToDark, 0.4);
            GlassInk.DarkHeldBack.ShouldBeGreaterThan(GlassInk.DarkHeldFrom);
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                var map = GlassMaterial.Map(GlassSettings.Default, theme);
                var (from, to) = theme == Theme.Dark ? (0.0, GlassInk.HeldFrom) : (GlassInk.DarkHeldFrom, 1.0);
                for (var l = from; l <= to + 1e-9; l += 0.005)
                {
                    var ground = GlassInk.GroundAt(Math.Min(l, 1));
                    foreach (var (key, _) in GlassMaterial.TextSteps)
                        Contrast.Ratio(Contrast.Over((Color)map[key], ground), ground).ShouldBeGreaterThanOrEqualTo(3, $"the {theme} look's {key} at {l:0.000}");
                }
                var past = GlassInk.GroundAt(theme == Theme.Dark ? GlassInk.HeldFrom + 0.01 : GlassInk.DarkHeldFrom - 0.01);
                Contrast.Ratio(Contrast.Over((Color)map["A.C.Text3"], past), past).ShouldBeLessThan(3, $"the {theme} look's quietest step stops just past it");
            }
            // Held from HeldFrom, let go under HeldBack (and the mirror for the dark ink); in between it stays as it was.
            GlassInk.Held(false, false, GlassInk.HeldFrom).ShouldBeTrue();
            GlassInk.Held(false, false, GlassInk.HeldFrom - 0.01).ShouldBeFalse();
            GlassInk.Held(false, true, GlassInk.HeldFrom - 0.01).ShouldBeTrue();
            GlassInk.Held(false, true, GlassInk.HeldBack).ShouldBeFalse();
            GlassInk.Held(true, false, GlassInk.DarkHeldFrom).ShouldBeTrue();
            GlassInk.Held(true, false, GlassInk.DarkHeldFrom + 0.01).ShouldBeFalse();
            GlassInk.Held(true, true, GlassInk.DarkHeldFrom + 0.01).ShouldBeTrue();
            GlassInk.Held(true, true, GlassInk.DarkHeldBack).ShouldBeFalse();
            var family = GlassInk.Family(dark: false, strict: false);
            var own = GlassMaterial.Map(GlassSettings.Default, Theme.Dark);
            foreach (var key in GlassInk.Graphics.Where(k => own.ContainsKey(k))) family[key].ShouldBe((Color)own[key], key);
            family["A.C.GroupSep"].ShouldBe(((SolidColorBrush)GlassMaterial.Palette(Theme.Dark)["A.B.GroupSep"]).Color);
        });

    /// <summary>The dark ink's lines are the light palette's: bars' tracks, rules, guides and the hairlines between a
    /// glass group's buttons, black where the white ink's are white.</summary>
    [Fact]
    public void The_dark_inks_lines_are_the_light_palettes()
        => UiHarness.OnUi(() =>
        {
            var light = GlassMaterial.Palette(Theme.Light);
            var family = GlassInk.Family(dark: true, strict: false);
            foreach (var key in GlassInk.Graphics)
            {
                var expected = light.Contains(key) ? (Color)light[key] : ((SolidColorBrush)light["A.B." + key[4..]]).Color;
                family[key].ShouldBe(expected, key);
                Contrast.Luminance(Color.FromRgb(family[key].R, family[key].G, family[key].B)).ShouldBeLessThan(0.05, $"{key} is dark");
            }
            GlassInk.DarkInkLuminance.ShouldBe(Contrast.Luminance((Color)light["A.C.Text"]), 1e-9, "the dark ink is the light palette's");
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

    /// <summary>A piece's tone comes in cells about 8 pixels a side (an opaque sRGB colour in a word each), so the words
    /// over one side of a pane, or over one blotch of a bent window, take that side's ink.</summary>
    [Fact]
    public void A_pictures_tones_come_in_small_cells()
    {
        BackdropTone.GridFor(1440, 990).ShouldBe((180, 124), "a 1152 by 792 pane at 125 %");
        BackdropTone.GridFor(4000, 3).ShouldBe((BackdropTone.MaxSide, 1), "at most 256 a side, at least one");
        BackdropTone.Pack(Colors.White).ShouldBe(0xFFFFFFFFu);
        BackdropTone.ColourOf(BackdropTone.Pack(1, 0, 0.2140)).ShouldBe(Color.FromRgb(255, 0, 127));
        BackdropTone.Luminance(BackdropTone.Pack(Colors.White)).ShouldBe(1, 1e-9);
        var pixels = new byte[64 * 32 * 4];
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 64; x++)
            {
                var v = (byte)(x < 32 ? 0 : 255);
                var at = (y * 64 + x) * 4;
                (pixels[at], pixels[at + 1], pixels[at + 2], pixels[at + 3]) = (v, v, v, 255);
            }
        var cells = new uint[8 * 4];
        BackdropTone.Measure(pixels, 64, 32, 256, new Int32Rect(-10, 0, 100, 32), 1, 8, 4, 2, cells).ShouldBe(new Int32Rect(0, 0, 64, 32), "clipped to the picture");
        for (var j = 0; j < 4; j++)
            for (var i = 0; i < 8; i++)
                BackdropTone.Luminance(cells[j * 8 + i]).ShouldBe(i < 4 ? 0 : 1, 0.001, $"cell {i}, {j}");
        Contrast.Luminance(BackdropTone.MeanOf(cells, 32)).ShouldBe(0.5, 0.01);
        var grid = new ToneGrid();
        grid.Set(cells, 8, 4, new Rect(0, 0, 64, 32)).ShouldBeTrue();
        grid.Set(cells, 8, 4, new Rect(0, 0, 64, 32)).ShouldBeFalse("nothing new");
        (grid.Cols, grid.Rows, grid.Count).ShouldBe((8, 4, 32));
        grid.Mean(new Rect(0, 0, 32, 32), out _, out var g, out _).ShouldBeTrue();
        g.ShouldBe(0, 0.001);
        grid.Mean(new Rect(40, 4, 10, 10), out _, out g, out _).ShouldBeTrue();
        g.ShouldBe(1, 0.001);
        grid.Mean(new Rect(16, 0, 32, 32), out _, out g, out _).ShouldBeTrue();
        g.ShouldBe(0.5, 0.001, "weighted by how much of each cell the area covers");
        grid.Mean(new Rect(100, 0, 10, 10), out _, out _, out _).ShouldBeFalse();
        // The cells a word's bounds cover: not one only touched along an edge.
        grid.Covered(new Rect(8, 8, 16, 8), out var i0, out var i1, out var j0, out var j1).ShouldBeTrue();
        (i0, i1, j0, j1).ShouldBe((1, 2, 1, 1));
        // A picture smaller than the cells: every cell still has its tone.
        var tiny = new byte[4 * 4 * 4];
        Array.Fill(tiny, (byte)255);
        BackdropTone.Measure(tiny, 4, 4, 16, new Int32Rect(0, 0, 4, 4), 1, 8, 4, 2, cells).ShouldNotBeNull();
        cells.ShouldAllBe(c => c == 0xFFFFFFFFu);
    }

    /// <summary>
    /// A word over grounds that differ (the coordinator's "Share" over white glass beside a navy blotch) takes the ink whose
    /// least contrast over every cell its bounds cover is the greater, held by a margin; over one ground, the same as the
    /// band's thresholds.
    /// </summary>
    [Fact]
    public void A_word_over_grounds_that_differ_takes_the_ink_whose_worst_reads_better()
    {
        // Over one ground: the band, exactly.
        for (var l = 0.0; l <= 1.0001; l += 0.01)
        {
            GlassInk.Dark(false, l, l, 1, 1).ShouldBe(l >= GlassInk.ToDark, $"from white at {l:0.00}");
            GlassInk.Dark(true, l, l, 1, 1).ShouldBe(l > GlassInk.ToLight, $"from dark at {l:0.00}");
        }
        // Grey mist on bright glass: the dark ink's worst (2.5) beats white's (1.9), whatever the ink was.
        GlassInk.Dark(false, 0.10, 0.50, 0.3, 0.9).ShouldBeTrue("dark: 2.5 against 1.9");
        GlassInk.Dark(true, 0.10, 0.50, 0.3, 0.9).ShouldBeTrue();
        // Dark glass with a little brighter mist: white's worst (2.3) the better.
        GlassInk.Dark(false, 0.01, 0.40, 0.9, 0.2).ShouldBeFalse("white: 2.3 against 1.0");
        GlassInk.Dark(true, 0.01, 0.40, 0.9, 0.2).ShouldBeFalse("and from dark too, past the margin");
        // Within the margin: the ink there holds.
        var (least, most) = (0.074, 0.45);
        var (white, black) = (1.05 / (most + 0.05), (least + 0.05) / (GlassInk.DarkInkLuminance + 0.05));
        (black / white).ShouldBeInRange(1 / GlassInk.Margin, GlassInk.Margin, "a pair within the margin");
        Math.Max(white, black).ShouldBeGreaterThanOrEqualTo(GlassInk.Hold);
        GlassInk.Dark(false, least, most, 0.5, 0.5).ShouldBeFalse("white holds");
        GlassInk.Dark(true, least, most, 0.5, 0.5).ShouldBeTrue("dark holds");
        // White glass with a navy blotch under part of the word ("Share" beside the blotch): neither ink holds 2:1 over its
        // worst, so the word takes the ink that reads over more of it.
        GlassInk.Dark(false, 0.03, 0.90, 0.25, 0.75).ShouldBeTrue("mostly over white glass: dark");
        GlassInk.Dark(true, 0.03, 0.90, 0.80, 0.20).ShouldBeFalse("mostly over the navy: white");
        GlassInk.Dark(false, 0.03, 0.90, 0.45, 0.52).ShouldBeFalse("about even: white holds");
        GlassInk.Dark(true, 0.03, 0.90, 0.52, 0.45).ShouldBeTrue("about even: dark holds");
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
