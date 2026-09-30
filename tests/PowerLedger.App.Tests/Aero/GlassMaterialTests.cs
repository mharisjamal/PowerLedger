using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan S G3: the Glass settings mapped to the glass. The demo's settings give the palette's own tokens; each style,
/// slider and switch moves the tokens it should; no setting leaves text under its contrast on the glass (3:1 with the halo
/// on the bright glass, 4.5:1 on the strict glass: 0.10.1, the owner's choice); and the material on a
/// window repaints it live when Settings raises Glass, on the window and never the application. Renders of the sample
/// for every style and switch go to %TEMP%\powerledger-renders\aero-glass-*.
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]   // AeroMotion's override is one for the process (agent D's AeroMotionScope)
public class GlassMaterialTests
{
    public static TheoryData<string> Themes => new() { "Dark", "Light" };

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_demos_settings_give_the_palettes_own_tokens(string themeName)
        => UiHarness.OnUi(() =>
        {
            var theme = Enum.Parse<Theme>(themeName);
            var palette = ThemeManager.Palette(Look.Aero, theme);
            var map = GlassMaterial.Map(GlassSettings.Default with { EdgeLight = GlassMaterial.DemoEdgeLight }, theme);
            map.Count.ShouldBeGreaterThan(100);
            // The video's glass (0.10.4): the palette's own tokens but the text steps, which are the video's (70 and 44 %
            // of the ink), the menus and dialogs, as dense as their text needs, and (0.10.6) the tint and the pill's dim,
            // which carry the dim the ink needs over whatever window is behind. Each theme keeps its own ink.
            string[] moved = ["A.C.Text2", "A.C.Text3", "A.B.Text2", "A.B.Text3", "A.C.MenuFill", "A.C.ModalTop", "A.C.ModalBottom", "A.B.MenuFill", "A.B.ModalFill",
                "A.C.GlassTintTop", "A.C.GlassTintBottom", "A.B.GlassTint", "A.C.PillDim", "A.B.PillFill"];
            foreach (var (key, value) in map)
            {
                palette.Contains(key).ShouldBeTrue($"{key} is not a palette key");
                if (moved.Contains(key)) continue;
                Describe(value).ShouldBe(Describe(palette[key]), $"{key}, {theme}");
            }
            // Over a dark desktop the dark theme's glass is the demo's tint exactly: the dim under it is black.
            if (theme == Theme.Dark)
            {
                foreach (var key in new[] { "A.C.GlassTintTop", "A.C.GlassTintBottom" })
                {
                    var (seen, own) = (Contrast.Over((Color)map[key], Colors.Black), Contrast.Over((Color)palette[key], Colors.Black));
                    Math.Abs(seen.R - own.R).ShouldBeLessThanOrEqualTo(2, $"{key} over black: {seen} against {own}");
                    Math.Abs(seen.B - own.B).ShouldBeLessThanOrEqualTo(2, $"{key} over black: {seen} against {own}");
                }
            }
            else
            {
                map["A.C.GlassTintTop"].ShouldBe(palette["A.C.GlassTintTop"], "the light glass needs nothing under it");
                map["A.C.GlassTintBottom"].ShouldBe(palette["A.C.GlassTintBottom"]);
            }
            var ink = (Color)map["A.C.Text"];
            map["A.C.Text2"].ShouldBe(Color.FromArgb(0xB2, ink.R, ink.G, ink.B));
            map["A.C.Text3"].ShouldBe(Color.FromArgb(0x70, ink.R, ink.G, ink.B));
            var strict = GlassMaterial.Map(GlassSettings.Default with { IncreaseContrast = true }, theme);
            foreach (var key in new[] { "A.C.Well", "A.C.Well2", "A.C.Well3" })
                ((Color)strict[key]).ShouldBe((Color)palette[key], $"the strict glass keeps {key}, {theme}");
        });

    /// <summary>0.10.6, the owner's choice: every style is a tint over what is really behind the window. Clear a light
    /// dim; Tinted the demo's tint over a dim; Dark black at about 55 %; Colour the user's hue; each scaled by Tint
    /// strength, and none under the dim its ink needs over a white window.</summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void Each_style_tints_as_the_design_says(string themeName)
        => UiHarness.OnUi(() =>
        {
            var theme = Enum.Parse<Theme>(themeName);
            var palette = ThemeManager.Palette(Look.Aero, theme);
            IReadOnlyDictionary<string, object> Map(GlassStyle style, double strength = 0.5, string tint = GlassSettings.DefaultTint)
                => GlassMaterial.Map(new GlassSettings { Style = style, TintStrength = strength, TintColor = tint }, theme);
            Color Top(GlassStyle style, double strength = 0.5, string tint = GlassSettings.DefaultTint) => (Color)Map(style, strength, tint)["A.C.GlassTintTop"];
            var light = (Color)ThemeManager.Palette(Look.Aero, Theme.Dark)["A.C.Text"];

            // Clear and Dark are black in either theme, with the light ink: Clear the least that reads, Dark the 55 %.
            foreach (var style in new[] { GlassStyle.Clear, GlassStyle.Dark })
            {
                var top = Top(style);
                (top.R, top.G, top.B).ShouldBe(((byte)0, (byte)0, (byte)0), $"{style} is black");
                Map(style)["A.C.Text"].ShouldBe(light, $"{style} takes the light ink, {theme}");
            }
            Top(GlassStyle.Dark).A.ShouldBe((byte)140, "Dark is black at 55 %");
            Top(GlassStyle.Clear).A.ShouldBeGreaterThanOrEqualTo((byte)Math.Round(GlassMaterial.ClearTop * 255), "Clear is at least its light dim");
            Top(GlassStyle.Clear).A.ShouldBeLessThan(Top(GlassStyle.Dark).A, "and lighter than Dark");
            Top(GlassStyle.Clear, 1).A.ShouldBe((byte)Math.Round(GlassMaterial.ClearTop * 2.2 * 255), "at full strength its own dim is more than the floor");

            // Tinted is the demo's tint: over a dark desktop exactly the palette's in the dark theme (the dim under it is black).
            var own = (Color)palette["A.C.GlassTintTop"];
            if (theme == Theme.Dark) Contrast.Over(Top(GlassStyle.Tinted), Colors.Black).ShouldBe(Contrast.Over(own, Colors.Black), "the demo's white at 13 %");
            else Top(GlassStyle.Tinted).ShouldBe(own, "the light glass needs no dim");
            Map(GlassStyle.Tinted)["A.C.Text"].ShouldBe(palette["A.C.Text"], "Tinted keeps the theme's ink");

            // Colour is the user's hue at 30 %, over the dim its ink needs.
            var hue = Color.FromArgb((byte)Math.Round(0.3 * 255), 0xFF, 0x80, 0x00);
            var colour = Top(GlassStyle.Colour, tint: "#FF8000");
            var ground = theme == Theme.Dark ? Colors.Black : Colors.White;
            var (seen, wanted) = (Contrast.Over(colour, ground), Contrast.Over(hue, ground));
            (Math.Abs(seen.R - wanted.R) <= 2 && Math.Abs(seen.G - wanted.G) <= 2 && Math.Abs(seen.B - wanted.B) <= 2)
                .ShouldBeTrue($"Colour over its own ground is the hue at 30 %: {seen} against {wanted}, {theme}");

            foreach (var style in Enum.GetValues<GlassStyle>())
            {
                Top(style, 0).A.ShouldBeLessThanOrEqualTo(Top(style, 0.5).A, $"{style}: no more at 0");
                Top(style, 1).A.ShouldBeGreaterThan(Top(style, 0.5).A, $"{style}: more at 1");
            }
        });

    public static TheoryData<string, string, double, bool, bool, string> EveryGlass()
    {
        var data = new TheoryData<string, string, double, bool, bool, string>();
        foreach (var theme in new[] { "Dark", "Light" })
        {
            foreach (var style in Enum.GetNames<GlassStyle>())
            {
                foreach (var strength in new[] { 0.0, 0.5, 1.0 })
                {
                    foreach (var (contrast, reduce) in new[] { (false, false), (true, false), (false, true), (true, true) })
                    {
                        foreach (var tint in style == "Colour" ? new[] { GlassSettings.DefaultTint, "#FFE600", "#101010", "#FFFFFF" } : [GlassSettings.DefaultTint])
                            data.Add(theme, style, strength, contrast, reduce, tint);
                    }
                }
            }
        }
        return data;
    }

    /// <summary>0.10.6: anything may be behind the glass, a white window or a black one. Whatever the style, its strength
    /// and the switches, the main text reads at 3:1 on the glass, in a well on it, and on a pill that floats on its own;
    /// at 4.5:1 under Increase contrast.</summary>
    [Theory]
    [MemberData(nameof(EveryGlass))]
    public void The_main_text_reads_over_a_white_window_and_over_a_black_one(string themeName, string style, double strength, bool contrast, bool reduce, string tint)
        => UiHarness.OnUi(() =>
        {
            var settings = new GlassSettings
            {
                Style = Enum.Parse<GlassStyle>(style), TintStrength = strength, TintColor = tint, IncreaseContrast = contrast, ReduceTransparency = reduce,
            };
            var map = GlassMaterial.Map(settings, Enum.Parse<Theme>(themeName));
            Color C(string key) => (Color)map[key];
            var target = contrast ? GlassMaterial.StrictContrast : GlassMaterial.GlassContrast;
            var text = C("A.C.Text");
            foreach (var (name, behind) in new[] { ("a white window", Colors.White), ("a black window", Colors.Black) })
            {
                foreach (var tintKey in new[] { "A.C.GlassTintTop", "A.C.GlassTintBottom" })
                {
                    var glass = Contrast.Over(C(tintKey), behind);
                    Contrast.Ratio(Contrast.Over(text, glass), glass).ShouldBeGreaterThanOrEqualTo(target, $"on the glass ({tintKey}) over {name}: {settings}");
                    var well = Contrast.Over(C("A.C.Well"), glass);
                    Contrast.Ratio(Contrast.Over(text, well), well).ShouldBeGreaterThanOrEqualTo(target, $"in a well ({tintKey}) over {name}: {settings}");
                }
                var pill = Contrast.Over(C("A.C.BtnFill"), Contrast.Over(((SolidColorBrush)map["A.B.PillFill"]).Color, behind));
                Contrast.Ratio(Contrast.Over(text, pill), pill).ShouldBeGreaterThanOrEqualTo(target, $"on a pill over {name}: {settings}");
            }
            ((LinearGradientBrush)map["A.B.GlassTint"]).GradientStops.ShouldAllBe(stop => stop.Color.A < 255, "still glass");
        });

    [Fact]
    public void Frost_scales_the_blur_and_edge_light_the_rim()
        => UiHarness.OnUi(() =>
        {
            double Frost(double frost) => (double)GlassMaterial.Map(new GlassSettings { Frost = frost }, Theme.Dark)["A.Glass.Frost"];
            Frost(0.6).ShouldBe(26);
            Frost(0).ShouldBe(0);
            Frost(1).ShouldBe(26 / 0.6, 1e-9);
            byte Rim(double edge) => ((Color)GlassMaterial.Map(new GlassSettings { EdgeLight = edge }, Theme.Dark)["A.C.RimA"]).A;
            Rim(0).ShouldBe((byte)0);
            Rim(0.6).ShouldBe((byte)0x77);
            Rim(1).ShouldBeGreaterThan((byte)0x77);
            var brush = (LinearGradientBrush)GlassMaterial.Map(new GlassSettings { EdgeLight = 1 }, Theme.Dark)["A.B.Rim"];
            brush.GradientStops[0].Color.A.ShouldBe(Rim(1), "the brush carries the colour");
        });

    /// <summary>0.10.8: Edge light defaults to 10 %, a soft edge, and every rim stop there is still a faint hairline.</summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void The_default_edge_light_is_a_faint_hairline_not_nothing(string themeName)
        => UiHarness.OnUi(() =>
        {
            GlassSettings.Default.EdgeLight.ShouldBe(0.1);
            var theme = Enum.Parse<Theme>(themeName);
            var soft = GlassMaterial.Map(GlassSettings.Default, theme);
            var demo = GlassMaterial.Map(GlassSettings.Default with { EdgeLight = GlassMaterial.DemoEdgeLight }, theme);
            foreach (var key in new[] { "A.C.RimA", "A.C.RimB", "A.C.RimC", "A.C.RimD" })
            {
                var a = ((Color)soft[key]).A;
                a.ShouldBeGreaterThanOrEqualTo((byte)Math.Floor(GlassMaterial.RimFloor * 255), $"{key} shows, {theme}");
                a.ShouldBeLessThan(((Color)demo[key]).A, $"{key} is softer than the demo's, {theme}");
            }
            ((LinearGradientBrush)soft["A.B.Rim"]).GradientStops.ShouldAllBe(stop => stop.Color.A > 0);
            ((Color)GlassMaterial.Map(GlassSettings.Default with { EdgeLight = 0 }, theme)["A.C.RimC"]).A.ShouldBe((byte)0, "off is off");
        });

    [Theory]
    [MemberData(nameof(Themes))]
    public void Reduce_transparency_makes_the_glass_denser_and_still_glass(string themeName)
        => UiHarness.OnUi(() =>
        {
            // 0.10.4: nothing is ever solid. The tint is dense, the menus and dialogs denser, and the frost still shows.
            var theme = Enum.Parse<Theme>(themeName);
            var plain = GlassMaterial.Map(new GlassSettings(), theme);
            var map = GlassMaterial.Map(new GlassSettings { ReduceTransparency = true }, theme);
            foreach (var key in new[] { "A.C.GlassTintTop", "A.C.GlassTintBottom" })
            {
                ((Color)map[key]).A.ShouldBe((byte)Math.Round(GlassMaterial.ReducedAlpha * 255), key);
                ((Color)map[key]).A.ShouldBeGreaterThan(((Color)plain[key]).A, key);
            }
            foreach (var key in new[] { "A.C.MenuFill", "A.C.ModalTop", "A.C.ModalBottom" })
            {
                ((Color)map[key]).A.ShouldBeGreaterThanOrEqualTo((byte)Math.Round(GlassMaterial.ReducedMenuAlpha * 255), key);
                ((Color)map[key]).A.ShouldBeLessThan((byte)255, key);
            }
            ((LinearGradientBrush)map["A.B.GlassTint"]).GradientStops.ShouldAllBe(stop => stop.Color.A < 255);
        });

    [Theory]
    [MemberData(nameof(Themes))]
    public void Increase_contrast_makes_the_rim_solid_the_text_full_and_the_wash_darker(string themeName)
        => UiHarness.OnUi(() =>
        {
            var theme = Enum.Parse<Theme>(themeName);
            var plain = GlassMaterial.Map(GlassSettings.Default, theme);
            var map = GlassMaterial.Map(new GlassSettings { IncreaseContrast = true }, theme);
            var rims = new[] { "A.C.RimA", "A.C.RimB", "A.C.RimC", "A.C.RimD" }.Select(key => (Color)map[key]).Distinct().ToList();
            rims.Count.ShouldBe(1, "one colour all round");
            rims[0].A.ShouldBeGreaterThanOrEqualTo((byte)204);
            map["A.C.Text2"].ShouldBe(map["A.C.Text"]);
            map["A.C.Text3"].ShouldBe(map["A.C.Text"]);
            ((Color)map["A.C.SeeThroughWash"]).A.ShouldBeGreaterThan(((Color)plain["A.C.SeeThroughWash"]).A);
            var glass = Contrast.Over((Color)map["A.C.GlassTintBottom"], (Color)map["A.C.BackdropBrightest"]);
            Contrast.Ratio(Contrast.Over(rims[0], glass), glass).ShouldBeGreaterThanOrEqualTo(3, "a solid rim reads all round");
        });

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_accent_and_its_ink_reach_the_shared_keys(string themeName)
        => UiHarness.OnUi(() =>
        {
            var theme = Enum.Parse<Theme>(themeName);
            var palette = ThemeManager.Palette(Look.Aero, theme);
            foreach (var accent in Enum.GetValues<GlassAccent>())
            {
                var map = GlassMaterial.Map(new GlassSettings { Accent = accent }, theme);
                var colour = (Color)palette["A.C.Accent." + accent];
                map["A.C.Accent"].ShouldBe(colour, accent.ToString());
                map["A.C.AccentInk"].ShouldBe(palette["A.C.AccentInk." + accent]);
                ((SolidColorBrush)map["A.B.Accent"]).Color.ShouldBe(colour);
                foreach (var key in new[] { "M.Accent", "M.Focus", "Brush.Amber", "M.PartCpu", "Brush.PartCpu", "M.ChartLine", "M.BarTop" })
                    ((SolidColorBrush)map[key]).Color.ShouldBe(colour, $"{key}, {accent}");
                ((SolidColorBrush)map["M.OnAccent"]).Color.ShouldBe((Color)palette["A.C.AccentInk." + accent]);
            }
        });

    /// <summary>Menus and dialogs are dense enough for their text at 4.5:1 over the look's backdrop bounds, whatever the
    /// glass (the panes' own text is <see cref="The_main_text_reads_over_a_white_window_and_over_a_black_one"/>'s).</summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void No_style_or_switch_leaves_a_menus_or_a_dialogs_text_under_its_contrast(string themeName)
        => UiHarness.OnUi(() =>
        {
            var theme = Enum.Parse<Theme>(themeName);
            foreach (var style in Enum.GetValues<GlassStyle>())
            {
                foreach (var strength in new[] { 0.0, 0.5, 1.0 })
                {
                    foreach (var (reduce, increase) in new[] { (false, false), (false, true), (true, true) })
                    {
                        var settings = new GlassSettings { Style = style, TintStrength = strength, ReduceTransparency = reduce, IncreaseContrast = increase };
                        var map = GlassMaterial.Map(settings, theme);
                        foreach (var backdrop in new[] { "A.C.BackdropDarkest", "A.C.BackdropBrightest" })
                        {
                            foreach (var fill in new[] { "A.C.MenuFill", "A.C.ModalTop", "A.C.ModalBottom" })
                            {
                                var card = Contrast.Over((Color)map[fill], (Color)map[backdrop]);
                                foreach (var text in new[] { "A.C.Text", "A.C.Text2" })
                                {
                                    Contrast.Ratio(Contrast.Over((Color)map[text], card), card)
                                        .ShouldBeGreaterThanOrEqualTo(4.5, $"{text} on {fill} over {backdrop}: {settings}, {theme}");
                                }
                            }
                        }
                    }
                }
            }
        });

    [Fact]
    public void Reduce_motion_sets_aeros_motion_and_null_follows_windows()
        => UiHarness.OnUi(() =>
        {
            var settings = new GlassSettings { ReduceMotion = true };
            var host = new Border();
            using var material = new GlassMaterial(host, () => settings, () => Theme.Dark);
            try
            {
                AeroMotion.Override.ShouldBe(true);
                AeroMotion.Reduced.ShouldBeTrue();
                settings = new GlassSettings { ReduceMotion = null };
                material.Refresh();
                AeroMotion.Override.ShouldBeNull();
                AeroMotion.Reduced.ShouldBe(!SystemParameters.ClientAreaAnimation);
            }
            finally
            {
                AeroMotion.SetOverride(null);
            }
        });

    /// <summary>The one live channel (Plan S 0.4): Settings raises Glass, and the window's glass changes, on the window only.</summary>
    [Fact]
    public void Raising_glass_repaints_the_window_live_and_never_the_application()
        => UiHarness.OnUi(() =>
        {
            var settings = new Settable();
            var (host, text) = AeroStylesTests.Dressed(new TextBlock(), "A.Text.Body", Theme.Dark);
            var accent = new Border();
            accent.SetResourceReference(Border.BackgroundProperty, "A.B.Accent");
            ((Border)host).Child = null;
            var stack = new StackPanel();
            stack.Children.Add(text);
            stack.Children.Add(accent);
            host.Child = stack;
            var before = Application.Current.Resources.MergedDictionaries.Count;
            using var material = new GlassMaterial(host, () => settings.Glass, () => Theme.Dark, settings);
            var heard = new List<GlassSettings>();
            material.Changed += heard.Add;
            ((SolidColorBrush)accent.Background).Color.ShouldBe(Color.FromRgb(0xD3, 0xF0, 0x3F));

            settings.Glass = new GlassSettings { Accent = GlassAccent.Rose, IncreaseContrast = true };
            UiHarness.Pump(TimeSpan.FromMilliseconds(50));

            ((SolidColorBrush)accent.Background).Color.ShouldBe(Color.FromRgb(0xFF, 0x8F, 0xB1), "the rose accent, at once");
            heard.Last().Accent.ShouldBe(GlassAccent.Rose);
            Application.Current.Resources.MergedDictionaries.Count.ShouldBe(before, "nothing is put on the application");
            Application.Current.Resources.Contains("A.B.Accent").ShouldBeFalse();
            material.Dispose();
            host.Resources.MergedDictionaries.Count.ShouldBe(2, "disposed, the material takes its tokens off again");
            ((SolidColorBrush)accent.Background).Color.ShouldBe(Color.FromRgb(0xD3, 0xF0, 0x3F));
        });

    /// <summary>The sample drawn for each style and switch, in both themes, for the eye.</summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void The_sample_draws_in_every_style_and_switch(string themeName)
        => UiHarness.OnUi(() =>
        {
            var theme = Enum.Parse<Theme>(themeName);
            Directory.CreateDirectory(UiHarness.Folder);
            var cases = Enum.GetValues<GlassStyle>().Select(style => (style.ToString().ToLowerInvariant(), new GlassSettings { Style = style }))
                .Append(("reduce-transparency", new GlassSettings { ReduceTransparency = true }))
                .Append(("increase-contrast", new GlassSettings { IncreaseContrast = true }))
                .Append(("accent-ice", new GlassSettings { Accent = GlassAccent.Ice }))
                .Append(("strong-colour", new GlassSettings { Style = GlassStyle.Colour, TintColor = "#2F7552", TintStrength = 1, EdgeLight = 1 }));
            foreach (var (name, settings) in cases)
            {
                var window = AeroGlassSample.Window(theme);
                using var material = new GlassMaterial(window, () => settings, () => theme);
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    UiHarness.Pump(TimeSpan.FromMilliseconds(450));
                    UiHarness.Render(window, AeroGlassSample.Width, AeroGlassSample.Height, $"aero-glass-{name}-{themeName.ToLowerInvariant()}.png");
                }
                finally
                {
                    window.Close();
                }
            }
        });

    private static string Describe(object value) => value switch
    {
        Color colour => colour.ToString(),
        double number => number.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        SolidColorBrush brush => "solid " + brush.Color,
        LinearGradientBrush brush => $"linear {brush.StartPoint} {brush.EndPoint} {brush.MappingMode} "
            + string.Join(" ", brush.GradientStops.Select(stop => $"{stop.Color}@{stop.Offset}")),
        _ => value.ToString() ?? "",
    };

    /// <summary>A stand-in for SettingsViewModel's Glass: set raises it.</summary>
    private sealed class Settable : INotifyPropertyChanged
    {
        private GlassSettings _glass = GlassSettings.Default;

        public event PropertyChangedEventHandler? PropertyChanged;

        public GlassSettings Glass
        {
            get => _glass;
            set
            {
                _glass = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Glass)));
            }
        }
    }
}
