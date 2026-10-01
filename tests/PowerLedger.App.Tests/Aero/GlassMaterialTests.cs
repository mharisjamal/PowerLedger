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
    public void The_default_settings_give_the_palettes_own_tokens(string themeName)
        => UiHarness.OnUi(() =>
        {
            var theme = Enum.Parse<Theme>(themeName);
            var palette = ThemeManager.Palette(Look.Aero, theme);
            var map = GlassMaterial.Map(GlassSettings.Default, theme);
            map.Count.ShouldBeGreaterThan(100);
            // The recipe's glass (0.10.9): the palette's own tokens but the text steps (the mockup's, 80 and 60 % of the
            // ink), the menus and dialogs, as dense as their text needs, and the text shadow the palette leaves to it.
            string[] moved = ["A.C.Text2", "A.C.Text3", "A.B.Text2", "A.B.Text3", "A.C.MenuFill", "A.C.ModalTop", "A.C.ModalBottom", "A.B.MenuFill", "A.B.ModalFill",
                GlassMaterial.TextShadowKey];
            foreach (var (key, value) in map)
            {
                palette.Contains(key).ShouldBeTrue($"{key} is not a palette key");
                if (moved.Contains(key)) continue;
                Describe(value).ShouldBe(Describe(palette[key]), $"{key}, {theme}");
            }
            ((LinearGradientBrush)map["A.B.GlassTint"]).GradientStops.ShouldAllBe(stop => stop.Color.A == 0, "no tint, as the recipe");
            map[GlassMaterial.GlowKey].ShouldBe(LiquidGlassRecipe.HighlightOpacity, "the owner's 10 % is the recipe's 70 %");
            map[GlassMaterial.LiveKey].ShouldBe(true);
            var ink = (Color)map["A.C.Text"];
            map["A.C.Text2"].ShouldBe(Color.FromArgb(0xCC, ink.R, ink.G, ink.B));
            map["A.C.Text3"].ShouldBe(Color.FromArgb(0x99, ink.R, ink.G, ink.B));
            map["A.C.NavText"].ShouldBe(Color.FromArgb(0xE0, ink.R, ink.G, ink.B), "the mockup's pages, 88 %");
            map["A.C.SegText"].ShouldBe(Color.FromArgb(0xD9, ink.R, ink.G, ink.B), "the mockup's segmented words, 85 %");
            var strict = GlassMaterial.Map(GlassSettings.Default with { IncreaseContrast = true }, theme);
            foreach (var key in new[] { "A.C.Well", "A.C.Well2", "A.C.Well3" })
                ((Color)strict[key]).ShouldBe((Color)palette[key], $"the strict glass keeps {key}, {theme}");
        });

    /// <summary>0.10.9, the owner's recipe: no tint by default. Clear and Tinted lay none at the default strength and at most
    /// the mockup's own at full (white at 2 and 10 %); Dark lays the mockup's smoked navy at 55 %, Colour the hue at 36 %,
    /// each scaled by Tint strength around its default.</summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void Each_style_tints_as_the_mockup_says(string themeName)
        => UiHarness.OnUi(() =>
        {
            var theme = Enum.Parse<Theme>(themeName);
            var palette = ThemeManager.Palette(Look.Aero, theme);
            IReadOnlyDictionary<string, object> Map(GlassStyle style, double strength = 0.5, string tint = GlassSettings.DefaultTint)
                => GlassMaterial.Map(new GlassSettings { Style = style, TintStrength = strength, TintColor = tint }, theme);
            Color Top(GlassStyle style, double strength = 0.5, string tint = GlassSettings.DefaultTint) => (Color)Map(style, strength, tint)["A.C.GlassTintTop"];
            var light = (Color)ThemeManager.Palette(Look.Aero, Theme.Dark)["A.C.Text"];

            foreach (var style in new[] { GlassStyle.Clear, GlassStyle.Tinted })
            {
                Top(style).A.ShouldBe((byte)0, $"{style} is untinted at the default strength");
                Top(style, 0).A.ShouldBe((byte)0, $"{style} at 0");
                Map(style)["A.C.Text"].ShouldBe(palette["A.C.Text"], $"{style} keeps the theme's ink");
            }
            Top(GlassStyle.Clear, 1).ShouldBe(Color.FromArgb(5, 255, 255, 255), "Clear at full strength: the mockup's white at 2 %");
            Top(GlassStyle.Tinted, 1).ShouldBe(Color.FromArgb(26, 255, 255, 255), "Tinted at full strength: the mockup's white at 10 %");

            Top(GlassStyle.Dark).ShouldBe(Color.FromArgb(140, 12, 16, 28), "Dark is the mockup's smoked navy at 55 %");
            Map(GlassStyle.Dark)["A.C.Text"].ShouldBe(light, $"Dark takes the light ink, {theme}");

            Top(GlassStyle.Colour, tint: "#FF8000").ShouldBe(Color.FromArgb((byte)Math.Round(0.36 * 255), 0xFF, 0x80, 0x00), "Colour is the hue at the mockup's 36 %");
            foreach (var style in new[] { GlassStyle.Dark, GlassStyle.Colour })
            {
                Top(style, 0).A.ShouldBeLessThan(Top(style, 0.5).A, $"{style}: less at 0");
                Top(style, 1).A.ShouldBeGreaterThan(Top(style, 0.5).A, $"{style}: more at 1");
            }
        });

    public static TheoryData<string, string, double, bool, bool, string> EveryStrictGlass()
    {
        var data = new TheoryData<string, string, double, bool, bool, string>();
        foreach (var theme in new[] { "Dark", "Light" })
        {
            foreach (var style in Enum.GetNames<GlassStyle>())
            {
                foreach (var strength in new[] { 0.0, 0.5, 1.0 })
                {
                    foreach (var (contrast, reduce) in new[] { (true, false), (false, true), (true, true) })
                    {
                        foreach (var tint in style == "Colour" ? new[] { GlassSettings.DefaultTint, "#FFE600", "#101010", "#FFFFFF" } : [GlassSettings.DefaultTint])
                            data.Add(theme, style, strength, contrast, reduce, tint);
                    }
                }
            }
        }
        return data;
    }

    /// <summary>The recipe's glass has no tint, so over a white window light text is the window's own to spoil: that is the
    /// owner's choice (0.10.9). The strict glass (Increase contrast, Reduce transparency) holds every text step at 4.5:1 on
    /// the glass, in a well on it and under a control's hover wash, over a white window and a black one.</summary>
    [Theory]
    [MemberData(nameof(EveryStrictGlass))]
    public void On_the_strict_glass_the_text_reads_over_a_white_window_and_over_a_black_one(string themeName, string style, double strength, bool contrast, bool reduce, string tint)
        => UiHarness.OnUi(() =>
        {
            var settings = new GlassSettings
            {
                Style = Enum.Parse<GlassStyle>(style), TintStrength = strength, TintColor = tint, IncreaseContrast = contrast, ReduceTransparency = reduce,
            };
            var map = GlassMaterial.Map(settings, Enum.Parse<Theme>(themeName));
            Color C(string key) => (Color)map[key];
            var text = C("A.C.Text");
            C("A.C.Text2").ShouldBe(text, "every step whole");
            foreach (var (name, behind) in new[] { ("a white window", Colors.White), ("a black window", Colors.Black) })
            {
                foreach (var tintKey in new[] { "A.C.GlassTintTop", "A.C.GlassTintBottom" })
                {
                    var glass = Contrast.Over(C(tintKey), behind);
                    Contrast.Ratio(Contrast.Over(text, glass), glass).ShouldBeGreaterThanOrEqualTo(GlassMaterial.StrictContrast, $"on the glass ({tintKey}) over {name}: {settings}");
                    var well = Contrast.Over(C("A.C.Well"), glass);
                    Contrast.Ratio(Contrast.Over(text, well), well).ShouldBeGreaterThanOrEqualTo(GlassMaterial.StrictContrast, $"in a well ({tintKey}) over {name}: {settings}");
                }
            }
            ((LinearGradientBrush)map["A.B.GlassTint"]).GradientStops.ShouldAllBe(stop => stop.Color.A < 255, "still glass");
        });

    /// <summary>Edge light (0.10.9) is the glowing edge's opacity: the owner's 10 % is the recipe's 70 %, 0 is none, 100 %
    /// fully white, straight lines between.</summary>
    [Fact]
    public void Edge_light_is_the_glows_opacity_with_the_owners_ten_percent_at_the_recipes_seventy()
        => UiHarness.OnUi(() =>
        {
            GlassSettings.Default.EdgeLight.ShouldBe(0.1);
            double Glow(double edge) => (double)GlassMaterial.Map(new GlassSettings { EdgeLight = edge }, Theme.Dark)[GlassMaterial.GlowKey];
            Glow(0.1).ShouldBe(0.7, 1e-9);
            Glow(0).ShouldBe(0);
            Glow(0.05).ShouldBe(0.35, 1e-9);
            Glow(1).ShouldBe(1, 1e-9);
            Glow(0.55).ShouldBe(0.85, 1e-9);
            double Frost(double frost) => (double)GlassMaterial.Map(new GlassSettings { Frost = frost }, Theme.Dark)["A.Glass.Frost"];
            Frost(0.6).ShouldBe(26);
        });

    [Theory]
    [MemberData(nameof(Themes))]
    public void Reduce_transparency_turns_the_live_backdrop_off_and_makes_the_glass_denser_and_still_glass(string themeName)
        => UiHarness.OnUi(() =>
        {
            // Nothing is ever solid. The tint is dense, the menus and dialogs denser, and what is behind still shows a little.
            var theme = Enum.Parse<Theme>(themeName);
            var plain = GlassMaterial.Map(new GlassSettings(), theme);
            var map = GlassMaterial.Map(new GlassSettings { ReduceTransparency = true }, theme);
            map[GlassMaterial.LiveKey].ShouldBe(false, "the engine's live backdrop is off");
            foreach (var key in new[] { "A.C.GlassTintTop", "A.C.GlassTintBottom" })
            {
                ((Color)map[key]).A.ShouldBeGreaterThanOrEqualTo((byte)Math.Round(GlassMaterial.ReducedAlpha * 255), key);
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
    public void Increase_contrast_lays_the_tint_the_text_needs_and_the_text_full(string themeName)
        => UiHarness.OnUi(() =>
        {
            var theme = Enum.Parse<Theme>(themeName);
            var plain = GlassMaterial.Map(GlassSettings.Default, theme);
            var map = GlassMaterial.Map(new GlassSettings { IncreaseContrast = true }, theme);
            map["A.C.Text2"].ShouldBe(map["A.C.Text"]);
            map["A.C.Text3"].ShouldBe(map["A.C.Text"]);
            map["A.C.NavText"].ShouldBe(map["A.C.Text"]);
            ((Color)map["A.C.GlassTintTop"]).A.ShouldBeGreaterThan((byte)0, "a tint where the recipe has none");
            ((Color)map["A.C.SeeThroughWash"]).A.ShouldBeGreaterThan(((Color)plain["A.C.SeeThroughWash"]).A);
            map[GlassMaterial.LiveKey].ShouldBe(true, "the live backdrop stays: only the tint and the text change");
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

    [Fact]
    public void Show_in_screenshots_reaches_the_liquid_glass_at_once()
        => UiHarness.OnUi(() =>
        {
            var settings = new GlassSettings { ShowInScreenshots = true };
            using var material = new GlassMaterial(new Border(), () => settings, () => Theme.Dark);
            try
            {
                LiquidGlassSources.AllowScreenshots.ShouldBeTrue();
                settings = new GlassSettings();
                material.Refresh();
                LiquidGlassSources.AllowScreenshots.ShouldBeFalse();
            }
            finally
            {
                LiquidGlassSources.AllowScreenshots = false;
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
            ((SolidColorBrush)accent.Background).Color.ShouldBe(Color.FromRgb(0xD9, 0xF2, 0x5A));

            settings.Glass = new GlassSettings { Accent = GlassAccent.Rose, IncreaseContrast = true };
            UiHarness.Pump(TimeSpan.FromMilliseconds(50));

            ((SolidColorBrush)accent.Background).Color.ShouldBe(Color.FromRgb(0xFF, 0x8F, 0xB1), "the rose accent, at once");
            heard.Last().Accent.ShouldBe(GlassAccent.Rose);
            Application.Current.Resources.MergedDictionaries.Count.ShouldBe(before, "nothing is put on the application");
            Application.Current.Resources.Contains("A.B.Accent").ShouldBeFalse();
            material.Dispose();
            host.Resources.MergedDictionaries.Count.ShouldBe(2, "disposed, the material takes its tokens off again");
            ((SolidColorBrush)accent.Background).Color.ShouldBe(Color.FromRgb(0xD9, 0xF2, 0x5A));
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
