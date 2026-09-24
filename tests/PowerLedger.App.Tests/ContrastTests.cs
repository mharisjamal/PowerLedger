using System.Windows;
using System.Windows.Media;
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
    private static readonly string[] Marks = ["M.Accent", "M.Good", "M.Bad", "M.Warn", "M.PartCpu", "M.PartGpu", "M.PartDisplay", "M.PartRest", "M.LineStrong", "M.Focus"];
    private static readonly string[] Chips = ["M.ChipMeasured", "M.ChipCalibrated", "M.ChipEstimated"];

    /// <summary>Text set in a colour, and where it sits: white on the accent (the active pill, primary buttons, the
    /// badge), the trends' green and red on the cards and hovered rows, and the accent's text tint (the chart's "now"
    /// label and other small accent labels) there too.</summary>
    private static readonly (string Text, string Ground)[] ColouredText =
    [
        ("M.OnAccent", "M.Accent"),
        ("M.Good", "M.Panel"), ("M.Good", "M.Raised"),
        ("M.Bad", "M.Panel"), ("M.Bad", "M.Raised"),
        ("M.AccentText", "M.Panel"), ("M.AccentText", "M.Raised"),
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

    /// <summary>Review round: the area chart draws the total in the accent over the parts' bands, so the CPU's band must
    /// not read as the accent, nor as another part; and Classic's keys carry the same values for the shared views.</summary>
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void The_cpu_band_stands_apart_from_the_accent_and_the_other_parts(string theme)
    {
        var palette = Midnight(Enum.Parse<Theme>(theme));
        foreach (var other in new[] { "M.Accent", "M.PartGpu", "M.PartDisplay", "M.PartRest" })
        {
            Contrast.Difference(palette["M.PartCpu"], palette[other]).ShouldBeGreaterThanOrEqualTo(25, $"M.PartCpu and {other}, {theme}");
        }
        palette["Brush.PartCpu"].ShouldBe(palette["M.PartCpu"], theme);
        palette["Brush.Amber"].ShouldBe(palette["M.Accent"], theme);
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
        return dictionary.Keys.Cast<string>().ToDictionary(key => key, key => dictionary[key].GetType());
    });
}
