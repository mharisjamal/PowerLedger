using System.Windows;
using System.Windows.Media;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Holds Midnight's two palettes to what the design promises (§3): text reads on every ground at 4.5:1, the marks (the
/// accent, the trends, the parts, card borders and the focus ring) stand off the panel at 3:1, and a chip's text reads
/// on its chip at 4.5:1; and to Classic's key set, so the shared dialogs find every brush they ask for in either look.
/// The palettes load through the harness's application, which registers the pack scheme's loader.
/// </summary>
[Trait("Category", "UI")]
public class ContrastTests
{
    private static readonly string[] Texts = ["M.Ink", "M.Ink2", "M.Ink3"];
    private static readonly string[] Grounds = ["M.Ground", "M.Panel", "M.Raised"];
    private static readonly string[] Marks = ["M.Accent", "M.Good", "M.Bad", "M.Warn", "M.PartCpu", "M.PartGpu", "M.PartDisplay", "M.PartRest", "M.LineStrong", "M.Focus", "M.Tip", "M.ChartLine", "M.BarTop"];
    private static readonly string[] Parts = ["M.PartCpu", "M.PartGpu", "M.PartDisplay", "M.PartRest"];
    private static readonly string[] Chips = ["M.ChipMeasured", "M.ChipCalibrated", "M.ChipEstimated"];

    /// <summary>Text set in a colour, and where it sits: white on the accent (the active pill, primary buttons, the
    /// badge), the trends' green and red on the cards and hovered rows, the accent's text tint (the chart's "now"
    /// label and other small accent labels) there too, and the tooltip's words on its bubble.</summary>
    private static readonly (string Text, string Ground)[] ColouredText =
    [
        ("M.OnAccent", "M.Accent"),
        ("M.Good", "M.Panel"), ("M.Good", "M.Raised"),
        ("M.Bad", "M.Panel"), ("M.Bad", "M.Raised"),
        ("M.AccentText", "M.Panel"), ("M.AccentText", "M.Raised"),
        ("M.TipText", "M.Tip"), ("M.TipMuted", "M.Tip"),
    ];

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Text_reads_on_every_ground_at_four_and_a_half_to_one(string theme)
    {
        var palette = Midnight(Enum.Parse<Theme>(theme));
        foreach (var text in Texts)
        {
            foreach (var ground in Grounds)
            {
                Contrast.Ratio(palette[text], palette[ground]).ShouldBeGreaterThanOrEqualTo(4.5, $"{text} on {ground}, {theme}");
            }
        }
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Marks_stand_off_the_panel_at_three_to_one(string theme)
    {
        var palette = Midnight(Enum.Parse<Theme>(theme));
        foreach (var mark in Marks)
        {
            Contrast.Ratio(palette[mark], palette["M.Panel"]).ShouldBeGreaterThanOrEqualTo(3, $"{mark} on M.Panel, {theme}");
        }
    }

    /// <summary>Review round: coloured text is text, so it reads at 4.5:1 too, not the 3:1 its colour has as a mark.</summary>
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Text_in_a_colour_reads_where_it_sits_at_four_and_a_half_to_one(string theme)
    {
        var palette = Midnight(Enum.Parse<Theme>(theme));
        foreach (var (text, ground) in ColouredText)
        {
            Contrast.Ratio(palette[text], palette[ground]).ShouldBeGreaterThanOrEqualTo(4.5, $"{text} on {ground}, {theme}");
        }
    }

    /// <summary>0.8.1, the reference's one colour family: the parts are indigo, violet, sky and slate, and every two of
    /// them, stacked in History's chart side by side, read as different colours (CIE76 20 apart); and Classic's keys carry
    /// the same values for the shared views.</summary>
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Every_two_parts_read_as_different_colours(string theme)
    {
        var palette = Midnight(Enum.Parse<Theme>(theme));
        foreach (var (one, other) in Parts.SelectMany((one, i) => Parts.Skip(i + 1).Select(other => (one, other))))
        {
            Contrast.Difference(palette[one], palette[other]).ShouldBeGreaterThanOrEqualTo(20, $"{one} and {other}, {theme}");
        }
        foreach (var part in Parts) palette["Brush." + part[2..]].ShouldBe(palette[part], theme);
        palette["Brush.Amber"].ShouldBe(palette["M.Accent"], theme);
    }

    /// <summary>The owner's rule for the data colours: none is teal, cyan or green, which the trends' good news keeps. A
    /// colour's hue, 0 to 360, sits outside 70 to 190.</summary>
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void No_data_colour_is_teal_cyan_or_green(string theme)
    {
        var palette = Midnight(Enum.Parse<Theme>(theme));
        foreach (var key in Parts.Append("M.ChartLine").Append("M.BarTop").Append("M.BarBottom"))
        {
            var hue = Hue(palette[key]);
            (hue is >= 70 and <= 190).ShouldBeFalse($"{key} at {hue:0} degrees, {theme}");
        }
    }

    /// <summary>0.8.1: the sidebar's current page is its words in ink over the accent's wash, at its strongest behind them.</summary>
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void The_current_pages_words_read_on_the_sidebars_wash(string theme)
    {
        var palette = Midnight(Enum.Parse<Theme>(theme));
        var accent = palette["M.Accent"];
        var wash = Contrast.Over(Color.FromArgb((byte)Math.Round(255 * NavGlow.Strength), accent.R, accent.G, accent.B), palette["M.Panel"]);
        Contrast.Ratio(palette["M.Ink"], wash).ShouldBeGreaterThanOrEqualTo(4.5, theme);
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void A_chips_text_reads_on_its_chip_at_four_and_a_half_to_one(string theme)
    {
        var palette = Midnight(Enum.Parse<Theme>(theme));
        foreach (var chip in Chips)
        {
            Contrast.Ratio(palette[chip + "Text"], palette[chip]).ShouldBeGreaterThanOrEqualTo(4.5, $"{chip}, {theme}");
        }
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Midnight_defines_every_key_classic_defines_and_all_as_solid_brushes(string theme)
    {
        var classic = Keys(Look.Classic, Enum.Parse<Theme>(theme));
        var midnight = Keys(Look.Midnight, Enum.Parse<Theme>(theme));
        classic.Keys.ShouldBeSubsetOf(midnight.Keys);
        midnight.Values.ShouldAllBe(type => type == typeof(SolidColorBrush));
        midnight.Keys.Count(key => key.StartsWith("M.", StringComparison.Ordinal)).ShouldBe(midnight.Count - classic.Count);
    }

    [Fact]
    public void Both_midnight_palettes_define_the_same_keys()
        => Keys(Look.Midnight, Theme.Dark).Keys.OrderBy(k => k).ShouldBe(Keys(Look.Midnight, Theme.Light).Keys.OrderBy(k => k));

    /// <summary>Aero look design §3: Aero's palettes define every key Classic's and Midnight's do, so the shared dialogs,
    /// the wizard and any Midnight-keyed view take Aero's colours, each as the same type of value as there; every other
    /// key is Aero's own, under A.</summary>
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Aero_defines_every_key_classic_and_midnight_define_and_the_rest_under_a(string theme)
    {
        var midnight = Keys(Look.Midnight, Enum.Parse<Theme>(theme));
        var aero = Keys(Look.Aero, Enum.Parse<Theme>(theme));
        midnight.Keys.ShouldBeSubsetOf(aero.Keys);
        foreach (var (key, type) in midnight) aero[key].ShouldBe(type, key);
        aero.Keys.Except(midnight.Keys).ShouldAllBe(key => key.StartsWith("A.", StringComparison.Ordinal));
        aero.Keys.Count(key => key.StartsWith("A.", StringComparison.Ordinal)).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Both_aero_palettes_define_the_same_keys_with_the_same_types()
        => Keys(Look.Aero, Theme.Dark).OrderBy(k => k.Key).ShouldBe(Keys(Look.Aero, Theme.Light).OrderBy(k => k.Key));

    /// <summary>Plan S G2: Aero's text reads at 4.5:1 on the glass wherever a pane can sit, over the darkest and the
    /// brightest backdrop the look allows (WallpaperFrost and the see-through wash keep the scene between them), at the
    /// tint's top and its foot, and in the wells, menus and dialogs laid on it.</summary>
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Aeros_text_reads_on_the_glass_over_every_backdrop_at_four_and_a_half_to_one(string theme)
    {
        var aero = Aero(Enum.Parse<Theme>(theme));
        foreach (var (where, ground, tiers) in AeroGrounds(aero))
        {
            foreach (var text in new[] { "A.C.Text", "A.C.Text2", "A.C.Text3" }.Take(tiers))
            {
                Contrast.Ratio(Contrast.Over(aero[text], ground), ground).ShouldBeGreaterThanOrEqualTo(4.5, $"{text} on {where}, {theme}");
            }
        }
    }

    /// <summary>The marks a user must see stand off the glass at 3:1: the accent (bars, the chart's line, the chosen
    /// page's disc) and the focus ring, which is the accent; each accent the Glass settings offer; and the rim at its
    /// brightest, which draws the pane's edge.</summary>
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Aeros_accents_rim_and_focus_ring_stand_off_the_glass_at_three_to_one(string theme)
    {
        var aero = Aero(Enum.Parse<Theme>(theme));
        foreach (var (where, glass) in AeroGlass(aero))
        {
            Contrast.Ratio(aero["A.C.Accent"], glass).ShouldBeGreaterThanOrEqualTo(3, $"the accent on {where}, {theme}");
            foreach (var accent in AeroAccents) Contrast.Ratio(aero["A.C.Accent." + accent], glass).ShouldBeGreaterThanOrEqualTo(3, $"{accent} on {where}, {theme}");
            Contrast.Ratio(Contrast.Over(aero["A.C.RimA"], glass), glass).ShouldBeGreaterThanOrEqualTo(3, $"the rim on {where}, {theme}");
        }
    }

    /// <summary>Words set on a filled mark: each accent's ink on it (a button, the chosen page's icon), the ink on the
    /// pill (the chosen segment, the tooltip) and the glyph on the white round button.</summary>
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Aeros_inks_read_on_what_they_are_set_on(string theme)
    {
        var aero = Aero(Enum.Parse<Theme>(theme));
        Contrast.Ratio(aero["A.C.AccentInk"], aero["A.C.Accent"]).ShouldBeGreaterThanOrEqualTo(4.5, theme);
        foreach (var accent in AeroAccents)
            Contrast.Ratio(aero["A.C.AccentInk." + accent], aero["A.C.Accent." + accent]).ShouldBeGreaterThanOrEqualTo(4.5, $"{accent}, {theme}");
        aero["A.C.Accent"].ShouldBe(aero["A.C.Accent.Lime"], "lime is the default accent");
        Contrast.Ratio(aero["A.C.Ink"], aero["A.C.Pill"]).ShouldBeGreaterThanOrEqualTo(4.5, theme);
        Contrast.Ratio(aero["A.C.WhiteRoundInk"], Contrast.Over(aero["A.C.WhiteRound"], aero["A.C.BackdropDarkest"])).ShouldBeGreaterThanOrEqualTo(3, theme);
    }

    /// <summary>Aero's Classic and Midnight keys, which the shared dialogs and the wizard draw with, hold to Midnight's
    /// rules: text on every ground at 4.5:1, marks on the panel at 3:1, coloured text where it sits and a chip's text on
    /// its chip at 4.5:1; and Classic's part keys carry the Midnight ones.</summary>
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Aeros_shared_keys_keep_midnights_rules(string theme)
    {
        var palette = Solid(Look.Aero, Enum.Parse<Theme>(theme));
        foreach (var text in Texts)
        {
            foreach (var ground in Grounds) Contrast.Ratio(palette[text], palette[ground]).ShouldBeGreaterThanOrEqualTo(4.5, $"{text} on {ground}, {theme}");
        }
        foreach (var mark in Marks) Contrast.Ratio(palette[mark], palette["M.Panel"]).ShouldBeGreaterThanOrEqualTo(3, $"{mark} on M.Panel, {theme}");
        foreach (var (text, ground) in ColouredText) Contrast.Ratio(palette[text], palette[ground]).ShouldBeGreaterThanOrEqualTo(4.5, $"{text} on {ground}, {theme}");
        foreach (var chip in Chips) Contrast.Ratio(palette[chip + "Text"], palette[chip]).ShouldBeGreaterThanOrEqualTo(4.5, $"{chip}, {theme}");
        foreach (var part in Parts) palette["Brush." + part[2..]].ShouldBe(palette[part], theme);
        palette["Brush.Amber"].ShouldBe(palette["M.Accent"], theme);
        palette["M.Accent"].ShouldBe(palette["M.Focus"], theme);
    }

    private static readonly string[] AeroAccents = ["Lime", "Ice", "Indigo", "Amber", "Rose"];

    /// <summary>The bright glass (0.10.1, the owner's choice, as the demo): with GlassMaterial's default settings in each
    /// style, every text tier reads at 3:1 on the glass, its chart well and its wells, over the frost at its darkest and
    /// its brightest (the busiest a wallpaper may get once frosted), with the halo behind the text counted as its colour
    /// at A.Glass.HaloShare (HaloTests measure what it really lays down).</summary>
    [Theory]
    [InlineData("Dark", "Tinted")]
    [InlineData("Dark", "Clear")]
    [InlineData("Dark", "Dark")]
    [InlineData("Dark", "Colour")]
    [InlineData("Light", "Tinted")]
    [InlineData("Light", "Clear")]
    [InlineData("Light", "Colour")]
    public void Aeros_text_reads_on_the_bright_glass_at_three_to_one_with_its_halo(string theme, string style)
    {
        var settings = GlassSettings.Default with { Style = Enum.Parse<GlassStyle>(style) };
        var map = Mapped(settings, Enum.Parse<Theme>(theme));
        var share = UiHarness.OnUi(() => (double)ThemeManager.Palette(Look.Aero, Enum.Parse<Theme>(theme))["A.Glass.HaloShare"]);
        foreach (var (where, ground, tiers) in GlassGrounds(map, ["A.C.FrostDarkest", "A.C.FrostBrightest"], share))
        {
            foreach (var text in new[] { "A.C.Text", "A.C.Text2", "A.C.Text3" }.Take(tiers))
                Contrast.Ratio(Contrast.Over(map[text], ground), ground).ShouldBeGreaterThanOrEqualTo(GlassMaterial.GlassContrast, $"{text} on {where}, {style}, {theme}");
        }
    }

    /// <summary>Increase contrast and Reduce transparency keep the strict glass exactly: the frost held within the
    /// backdrop bounds, no halo, and every tier at 4.5:1 on the glass, its wells, menus and dialogs.</summary>
    [Theory]
    [InlineData("Dark", true, false)]
    [InlineData("Dark", false, true)]
    [InlineData("Light", true, false)]
    [InlineData("Light", false, true)]
    public void Aeros_strict_glass_keeps_four_and_a_half_to_one(string theme, bool increase, bool reduce)
    {
        var settings = GlassSettings.Default with { IncreaseContrast = increase, ReduceTransparency = reduce };
        GlassMaterial.Strict(settings).ShouldBeTrue();
        UiHarness.OnUi(() => GlassMaterial.Halo(settings, Enum.Parse<Theme>(theme), Colors.Black)).ShouldBeNull("no halo on the strict glass");
        var map = Mapped(settings, Enum.Parse<Theme>(theme));
        foreach (var (where, ground, tiers) in GlassGrounds(map, ["A.C.BackdropDarkest", "A.C.BackdropBrightest"], 0))
        {
            foreach (var text in new[] { "A.C.Text", "A.C.Text2", "A.C.Text3" }.Take(tiers))
                Contrast.Ratio(Contrast.Over(map[text], ground), ground).ShouldBeGreaterThanOrEqualTo(GlassMaterial.StrictContrast, $"{text} on {where}, {theme}");
        }
        foreach (var backdrop in new[] { "A.C.BackdropDarkest", "A.C.BackdropBrightest" })
        {
            foreach (var fill in new[] { "A.C.MenuFill", "A.C.ModalTop", "A.C.ModalBottom" })
            {
                var ground = Contrast.Over(map[fill], map[backdrop]);
                foreach (var text in new[] { "A.C.Text", "A.C.Text2" })
                    Contrast.Ratio(Contrast.Over(map[text], ground), ground).ShouldBeGreaterThanOrEqualTo(4.5, $"{text} on {fill} over {backdrop}, {theme}");
            }
        }
    }

    private static Dictionary<string, Color> Mapped(GlassSettings settings, Theme theme) => UiHarness.OnUi(()
        => GlassMaterial.Map(settings, theme).Where(p => p.Value is Color).ToDictionary(p => p.Key, p => (Color)p.Value));

    /// <summary>Where text sits on GlassMaterial's glass over each of <paramref name="backdrops"/>: the tint's top and foot,
    /// the halo at <paramref name="share"/> over that, then the chart well (three tiers), the quiet wells (two) and the
    /// brighter wells (the ink), which lie over the halo as the content they are.</summary>
    private static IEnumerable<(string Where, Color Ground, int Tiers)> GlassGrounds(Dictionary<string, Color> map, string[] backdrops, double share)
    {
        var halo = map["A.C.Halo"];
        foreach (var backdrop in backdrops)
        {
            foreach (var tint in new[] { "A.C.GlassTintTop", "A.C.GlassTintBottom" })
            {
                var glass = Contrast.Over(Color.FromArgb((byte)Math.Round(255 * share), halo.R, halo.G, halo.B), Contrast.Over(map[tint], map[backdrop]));
                var where = $"{tint} over {backdrop}";
                yield return (where, glass, 3);
                yield return ($"A.C.ChartWell in {where}", Contrast.Over(map["A.C.ChartWell"], glass), 3);
                foreach (var well in new[] { "A.C.Well", "A.C.WellHover", "A.C.MenuHover" })
                    yield return ($"{well} in {where}", Contrast.Over(map[well], glass), 2);
                foreach (var well in new[] { "A.C.Well2", "A.C.Well3" })
                    yield return ($"{well} in {where}", Contrast.Over(map[well], glass), 1);
            }
        }
    }

    /// <summary>The glass as it reads over the darkest and the brightest backdrop, at the tint's top and its foot.</summary>
    private static IEnumerable<(string Where, Color Glass)> AeroGlass(Dictionary<string, Color> aero)
    {
        foreach (var backdrop in new[] { "A.C.BackdropDarkest", "A.C.BackdropBrightest" })
        {
            foreach (var tint in new[] { "A.C.GlassTintTop", "A.C.GlassTintBottom" })
                yield return ($"{tint} over {backdrop}", Contrast.Over(aero[tint], aero[backdrop]));
        }
    }

    /// <summary>Everywhere text sits in Aero, and how many of its three inks may sit there (the palettes' Ink comment):
    /// all three on the glass and the chart well (axes); the ink and Text2 on the quiet wells, a hovered row, a menu and
    /// a dialog; only the ink on the brighter wells (Well2 and Well3: the key cap, the change against yesterday).</summary>
    private static IEnumerable<(string Where, Color Ground, int Tiers)> AeroGrounds(Dictionary<string, Color> aero)
    {
        foreach (var (where, glass) in AeroGlass(aero))
        {
            yield return (where, glass, 3);
            yield return ($"A.C.ChartWell in {where}", Contrast.Over(aero["A.C.ChartWell"], glass), 3);
            foreach (var well in new[] { "A.C.Well", "A.C.WellHover", "A.C.MenuHover" })
                yield return ($"{well} in {where}", Contrast.Over(aero[well], glass), 2);
            foreach (var well in new[] { "A.C.Well2", "A.C.Well3" })
                yield return ($"{well} in {where}", Contrast.Over(aero[well], glass), 1);
        }
        foreach (var backdrop in new[] { "A.C.BackdropDarkest", "A.C.BackdropBrightest" })
        {
            foreach (var fill in new[] { "A.C.MenuFill", "A.C.ModalTop", "A.C.ModalBottom" })
                yield return ($"{fill} over {backdrop}", Contrast.Over(aero[fill], aero[backdrop]), 2);
        }
    }

    /// <summary>Aero's colours by key, translucent ones as they are, for a test to lay each over what it sits on.</summary>
    private static Dictionary<string, Color> Aero(Theme theme) => UiHarness.OnUi(() =>
    {
        var dictionary = new ResourceDictionary { Source = LookRules.PaletteFor(Look.Aero, theme) };
        var colours = new Dictionary<string, Color>();
        foreach (var key in dictionary.Keys.Cast<string>())
        {
            if (dictionary[key] is Color colour) colours[key] = colour;
            else if (dictionary[key] is SolidColorBrush brush) colours[key] = brush.Color;
        }
        return colours;
    });

    /// <summary>A palette's solid brushes by key, a translucent one laid over the panel, as <see cref="Midnight"/>.</summary>
    private static Dictionary<string, Color> Solid(Look look, Theme theme) => UiHarness.OnUi(() =>
    {
        var dictionary = new ResourceDictionary { Source = LookRules.PaletteFor(look, theme) };
        var panel = ((SolidColorBrush)dictionary["M.Panel"]).Color;
        return dictionary.Keys.Cast<string>().Where(key => !key.StartsWith("A.", StringComparison.Ordinal))
            .ToDictionary(key => key, key => Contrast.Over(((SolidColorBrush)dictionary[key]).Color, panel));
    });

    [Fact]
    public void The_maths_is_wcags()
    {
        Contrast.Ratio(Colors.White, Colors.Black).ShouldBe(21, 0.001);
        Contrast.Ratio(Colors.Black, Colors.White).ShouldBe(21, 0.001);
        Contrast.Ratio(Color.FromRgb(0x77, 0x77, 0x77), Colors.White).ShouldBe(4.48, 0.01);   // the classic threshold grey
        Contrast.Luminance(Color.FromRgb(0x80, 0x80, 0x80)).ShouldBe(0.2159, 0.001);
        Contrast.Over(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF), Colors.Black).ShouldBe(Color.FromRgb(0x80, 0x80, 0x80));
    }

    [Fact]
    public void The_colour_difference_is_cie76_in_lab()
    {
        Contrast.Difference(Colors.Black, Colors.White).ShouldBe(100, 0.05);
        Contrast.Difference(Color.FromRgb(0xFF, 0, 0), Color.FromRgb(0, 0xFF, 0)).ShouldBe(170.6, 0.1);
        Contrast.Difference(Color.FromRgb(0x80, 0x80, 0x80), Colors.White).ShouldBe(46.4, 0.1);
        Contrast.Difference(Colors.Teal, Colors.Teal).ShouldBe(0);
    }

    /// <summary>A colour's hue in degrees, as HSV has it.</summary>
    private static double Hue(Color colour)
    {
        var (r, g, b) = (colour.R / 255.0, colour.G / 255.0, colour.B / 255.0);
        var (max, min) = (Math.Max(r, Math.Max(g, b)), Math.Min(r, Math.Min(g, b)));
        var span = max - min;
        if (span == 0) return 0;
        var hue = max == r ? (g - b) / span % 6 : max == g ? (b - r) / span + 2 : (r - g) / span + 4;
        return (hue * 60 + 360) % 360;
    }

    /// <summary>The colours of a Midnight palette, by key; a translucent one laid over the panel, as the screen shows it.
    /// Read on the application's thread, which owns the brushes.</summary>
    private static Dictionary<string, Color> Midnight(Theme theme) => UiHarness.OnUi(() =>
    {
        var dictionary = new ResourceDictionary { Source = LookRules.PaletteFor(Look.Midnight, theme) };
        var panel = ((SolidColorBrush)dictionary["M.Panel"]).Color;
        return dictionary.Keys.Cast<string>().ToDictionary(key => key, key => Contrast.Over(((SolidColorBrush)dictionary[key]).Color, panel));
    });

    /// <summary>What a palette defines: each key and the type of its value.</summary>
    private static Dictionary<string, Type> Keys(Look look, Theme theme) => UiHarness.OnUi(() =>
    {
        var dictionary = new ResourceDictionary { Source = LookRules.PaletteFor(look, theme) };
        return dictionary.Keys.Cast<string>().ToDictionary(key => key, key => dictionary[key]?.GetType() ?? typeof(void));   // null: A.Glass.Halo, none
    });
}
