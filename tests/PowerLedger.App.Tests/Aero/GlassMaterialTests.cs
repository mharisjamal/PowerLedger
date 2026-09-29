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
            var map = GlassMaterial.Map(GlassSettings.Default, theme);
            map.Count.ShouldBeGreaterThan(100);
            // The video's glass (0.10.4): the palette's own tokens but the text steps, which are the video's (70 and 44 %
            // of the ink), and the menus and dialogs, as dense as their text needs. Aero bloom's grounds are the video's, so
            // the light theme reads in the video's light ink there: its ink family moves too.
            string[] moved = ["A.C.Text2", "A.C.Text3", "A.B.Text2", "A.B.Text3", "A.C.MenuFill", "A.C.ModalTop", "A.C.ModalBottom", "A.B.MenuFill", "A.B.ModalFill"];
            var dark = ThemeManager.Palette(Look.Aero, Theme.Dark);
            foreach (var (key, value) in map)
            {
                palette.Contains(key).ShouldBeTrue($"{key} is not a palette key");
                if (moved.Contains(key)) continue;
                if (theme == Theme.Light && dark.Contains(key) && Describe(value) == Describe(dark[key])) continue;
                Describe(value).ShouldBe(Describe(palette[key]), $"{key}, {theme}");
            }
            var ink = (Color)map["A.C.Text"];
            map["A.C.Text2"].ShouldBe(Color.FromArgb(0xB2, ink.R, ink.G, ink.B));
            map["A.C.Text3"].ShouldBe(Color.FromArgb(0x70, ink.R, ink.G, ink.B));
            var strict = GlassMaterial.Map(GlassSettings.Default with { IncreaseContrast = true }, theme);
            foreach (var key in new[] { "A.C.Well", "A.C.Well2", "A.C.Well3" })
                ((Color)strict[key]).ShouldBe((Color)palette[key], $"the strict glass keeps {key}, {theme}");
        });

    [Theory]
    [MemberData(nameof(Themes))]
    public void Each_style_tints_as_the_design_says(string themeName)
        => UiHarness.OnUi(() =>
        {
            var theme = Enum.Parse<Theme>(themeName);
            Color Top(GlassStyle style, double strength = 0.5, string tint = GlassSettings.DefaultTint)
                => (Color)GlassMaterial.Map(new GlassSettings { Style = style, TintStrength = strength, TintColor = tint }, theme)["A.C.GlassTintTop"];
            var tinted = Top(GlassStyle.Tinted);
            tinted.ShouldBe((Color)ThemeManager.Palette(Look.Aero, theme)["A.C.GlassTintTop"], "Tinted is the demo's");
            Top(GlassStyle.Clear).ShouldBe(Color.FromArgb((byte)Math.Round(GlassMaterial.ClearTop * 255), 0, 0, 0), "Clear dims the live desktop a little");
            if (theme == Theme.Dark) Top(GlassStyle.Dark).ShouldBe(Color.FromArgb(140, 0, 0, 0), "Dark is black at 55 %");
            else Top(GlassStyle.Dark).ShouldSatisfyAllConditions(
                dark => (dark.R, dark.G, dark.B).ShouldBe(((byte)0, (byte)0, (byte)0)),
                dark => dark.A.ShouldBeGreaterThanOrEqualTo((byte)140, "at least the 55 %, so the white ink it takes reads"),
                dark => ((Color)GlassMaterial.Map(new GlassSettings { Style = GlassStyle.Dark, IncreaseContrast = true }, theme)["A.C.GlassTintTop"]).A
                    .ShouldBeGreaterThan((byte)140, "denser on the strict glass over a light scene"));
            var colour = Top(GlassStyle.Colour, tint: "#FF8000");
            (colour.R, colour.G, colour.B).ShouldBe(((byte)0xFF, (byte)0x80, (byte)0x00), "Colour is the user's hue");
            colour.A.ShouldBe((byte)Math.Round(0.3 * 255));
            foreach (var style in Enum.GetValues<GlassStyle>())
            {
                Top(style, 0).A.ShouldBeLessThan(Top(style, 0.5).A, $"{style}: less at 0");
                Top(style, 1).A.ShouldBeGreaterThan(Top(style, 0.5).A, $"{style}: more at 1");
            }
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

    /// <summary>Aero look design §7, as the owner chose for 0.10.1: on the strict glass (Increase contrast, Reduce
    /// transparency) text over the darkest and brightest backdrop reads at 4.5:1; on the bright glass, over the frost's
    /// darkest and brightest with the halo, at 3:1; menus and dialogs at 4.5:1 either way; each style, any strength.</summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void No_style_or_switch_leaves_text_under_its_contrast(string themeName)
        => UiHarness.OnUi(() =>
        {
            var theme = Enum.Parse<Theme>(themeName);
            foreach (var style in Enum.GetValues<GlassStyle>())
            {
                foreach (var strength in new[] { 0.0, 0.5, 1.0 })
                {
                    foreach (var tint in new[] { GlassSettings.DefaultTint, "#FFE600", "#101010", "#FFFFFF" })
                    {
                        // Increase contrast alone holds text to a ratio (0.10.4): the default glass takes the video's steps.
                        foreach (var (reduce, increase, behind) in new[] { (false, true, GlassBackdrop.Wallpaper), (true, true, GlassBackdrop.Bloom), (false, true, GlassBackdrop.Bloom) })
                        {
                            var settings = new GlassSettings { Style = style, TintStrength = strength, TintColor = tint, ReduceTransparency = reduce, IncreaseContrast = increase, Backdrop = behind };
                            var map = GlassMaterial.Map(settings, theme);
                            var strict = reduce || increase;
                            var share = strict || map[GlassMaterial.HaloOnKey] is false ? 0 : (double)ThemeManager.Palette(Look.Aero, theme)["A.Glass.HaloShare"];
                            var halo = (Color)map["A.C.Halo"];
                            var target = strict ? GlassMaterial.StrictContrast : GlassMaterial.GlassContrast;
                            var frost = strict ? new[] { "A.C.BackdropDarkest", "A.C.BackdropBrightest" }
                                : behind == GlassBackdrop.Bloom ? ["A.C.BloomDarkest", "A.C.BloomBrightest"] : ["A.C.FrostDarkest", "A.C.FrostBrightest"];
                            foreach (var (backdrop, glassBackdrop) in new[] { "A.C.BackdropDarkest", "A.C.BackdropBrightest" }.Zip(frost))
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
                                foreach (var tintKey in new[] { "A.C.GlassTintTop", "A.C.GlassTintBottom" })
                                {
                                    var glass = Contrast.Over(Color.FromArgb((byte)Math.Round(255 * share), halo.R, halo.G, halo.B),
                                        Contrast.Over((Color)map[tintKey], (Color)map[glassBackdrop]));
                                    foreach (var text in new[] { "A.C.Text", "A.C.Text2", "A.C.Text3" })
                                    {
                                        Contrast.Ratio(Contrast.Over((Color)map[text], glass), glass)
                                            .ShouldBeGreaterThanOrEqualTo(target, $"{text} on {tintKey} over {glassBackdrop}: {settings}, {theme}");
                                    }
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
