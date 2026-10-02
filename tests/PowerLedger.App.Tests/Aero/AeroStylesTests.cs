using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan S 0.8: every key an Aero view may use is in Styles.Aero.xaml (or Icons.Aero.xaml, which it merges), of the type
/// the view expects, and takes the approved demo's values from the Aero palette's tokens; no colour is written into the
/// styles, and every token they name is in both Aero palettes. The dictionary is read the way a window merges it.
/// </summary>
[Trait("Category", "UI")]
public class AeroStylesTests
{
    public static TheoryData<string, Type> Keyed => new()
    {
        { "A.Text.Big", typeof(TextBlock) }, { "A.Text.BigMonth", typeof(TextBlock) }, { "A.Text.Mid", typeof(TextBlock) },
        { "A.Text.Title", typeof(TextBlock) }, { "A.Text.Body", typeof(TextBlock) }, { "A.Text.Label", typeof(TextBlock) },
        { "A.Text.Small", typeof(TextBlock) }, { "A.Text.Secondary", typeof(TextBlock) }, { "A.Text.Muted", typeof(TextBlock) },
        { "A.Text.Axis", typeof(TextBlock) }, { "A.Text.Number", typeof(TextBlock) }, { "A.PanelTitle", typeof(TextBlock) },
        { "A.Label", typeof(TextBlock) },
        { "A.Well", typeof(Border) }, { "A.Menu", typeof(ContextMenu) }, { "A.MenuItem", typeof(MenuItem) },
        { "A.Modal", typeof(GlassPanel) }, { "A.Toast", typeof(GlassPanel) }, { "A.Banner", typeof(GlassPanel) },
        { "A.NavItem", typeof(RadioButton) }, { "A.SegItem", typeof(RadioButton) }, { "A.OptItem", typeof(RadioButton) },
        { "A.GlassBtn", typeof(ButtonBase) }, { "A.RoundGlassBtn", typeof(ButtonBase) }, { "A.GlassToggle", typeof(ToggleButton) },
        { "A.OutlineBtn", typeof(ButtonBase) }, { "A.AccentBtn", typeof(ButtonBase) }, { "A.GhostBtn", typeof(ButtonBase) },
        { "A.WhiteRoundBtn", typeof(ButtonBase) }, { "A.ExpandBtn", typeof(ButtonBase) }, { "A.RowBtn", typeof(ButtonBase) },
        { "A.MenuBtn", typeof(ButtonBase) }, { "A.TextToggle", typeof(RadioButton) },
        { "A.SearchBox", typeof(TextBox) }, { "A.Field", typeof(TextBox) }, { "A.Tick", typeof(CheckBox) }, { "A.Slider", typeof(Slider) },
        { "A.Chip.Measured", typeof(ContentControl) }, { "A.Chip.Calibrated", typeof(ContentControl) }, { "A.Chip.Estimated", typeof(ContentControl) },
        { "A.Table.Header", typeof(Border) }, { "A.Table.Row", typeof(Border) },
    };

    /// <summary>Every icon in the prototype's Icons.xaml, and Insights.</summary>
    public static readonly string[] Icons =
    [
        "Bolt", "Dashboard", "History", "Replay", "Chip", "Insights", "Report", "House", "Gear", "Moon", "Search", "Bell", "Chevron", "Unit",
        "Info", "Expand", "Bars", "Pie", "Download", "Play", "Minimize", "Maximize", "Restore", "Close", "Overlay", "Check",
    ];

    [Theory]
    [MemberData(nameof(Keyed))]
    public void Each_keyed_style_targets_what_a_view_puts_it_on(string key, Type target)
        => UiHarness.OnUi(() => Style(Load(), key).TargetType.ShouldBe(target, key));

    [Fact]
    public void The_implicit_styles_and_the_focus_rings_are_there()
        => UiHarness.OnUi(() =>
        {
            var styles = Load();
            foreach (var type in new[] { typeof(ScrollBar), typeof(ToolTip), typeof(ContextMenu), typeof(MenuItem), typeof(TextBox), typeof(CheckBox), typeof(GlassPanel), typeof(GlassSwitch) })
                Style(styles, type).TargetType.ShouldBe(type, type.Name);
            Style(styles, "A.Focus.Pill").ShouldNotBeNull();
            Style(styles, "A.Focus.Soft").ShouldNotBeNull();
            Style(styles, SystemParameters.FocusVisualStyleKey).BasedOn.ShouldBeSameAs(styles["A.Focus.Soft"]);
            Style(styles, typeof(TextBox)).BasedOn.ShouldBeSameAs(styles["A.Field"]);
            Style(styles, typeof(CheckBox)).BasedOn.ShouldBeSameAs(styles["A.Tick"]);
            Style(styles, typeof(ContextMenu)).BasedOn.ShouldBeSameAs(styles["A.Menu"]);
            Style(styles, typeof(MenuItem)).BasedOn.ShouldBeSameAs(styles["A.MenuItem"]);
        });

    [Fact]
    public void Every_icon_is_a_geometry()
        => UiHarness.OnUi(() =>
        {
            var styles = Load();
            foreach (var icon in Icons) styles["A.I." + icon].ShouldBeAssignableTo<System.Windows.Media.Geometry>("A.I." + icon);
        });

    /// <summary>The demo's sizes, as a view gets them: the styles on elements under the Aero palette.</summary>
    [Theory]
    [InlineData("A.Text.Big", 40)]
    [InlineData("A.Text.BigMonth", 34)]
    [InlineData("A.Text.Mid", 22)]
    [InlineData("A.Text.Title", 16)]
    [InlineData("A.PanelTitle", 16)]
    [InlineData("A.Text.Body", 14)]
    [InlineData("A.Text.Label", 13)]
    [InlineData("A.Label", 13)]
    [InlineData("A.Text.Small", 12.5)]
    [InlineData("A.Text.Axis", 11)]
    public void Each_text_style_sets_the_demos_size(string key, double size)
        => UiHarness.OnUi(() =>
        {
            var (host, text) = Dressed(new TextBlock { Text = "12 345" }, key, Theme.Dark);
            text.FontSize.ShouldBe(size, key);
            text.FontFamily.Source.ShouldStartWith("pack://application:,,,/PowerLedger;component/Fonts/Geist/#Geist", customMessage: key);
            GC.KeepAlive(host);
        });

    [Theory]
    [InlineData("A.Text.Body", "A.C.Text")]
    [InlineData("A.Text.Secondary", "A.C.Text2")]
    [InlineData("A.Label", "A.C.Text2")]
    [InlineData("A.Text.Muted", "A.C.Text3")]
    [InlineData("A.Text.Axis", "A.C.Text3")]
    public void Each_text_style_takes_its_ink_from_the_palette(string key, string colour)
        => UiHarness.OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                var (_, text) = Dressed(new TextBlock(), key, theme);
                ((SolidColorBrush)text.Foreground).Color.ShouldBe((Color)ThemeManager.Palette(Look.Aero, theme)[colour], $"{key}, {theme}");
            }
        });

    /// <summary>0.10.9's audit: the mockup sets its figures in Geist's own proportional digits ("+12%" 3 px and "$5.73"
    /// 2 px narrower than tabular ones), so Aero's text styles leave the font's default.</summary>
    [Fact]
    public void Numbers_are_set_in_geists_proportional_figures_as_the_mockups()
        => UiHarness.OnUi(() =>
        {
            foreach (var key in new[] { "A.Text.Number", "A.Text.Big", "A.Text.Body" })
            {
                var (_, text) = Dressed(new TextBlock(), key, Theme.Dark);
                System.Windows.Documents.Typography.GetNumeralAlignment(text).ShouldBe(FontNumeralAlignment.Normal, key);
            }
        });

    [Fact]
    public void The_buttons_and_pills_have_the_demos_sizes()
        => UiHarness.OnUi(() =>
        {
            Size(new Button(), "A.RoundGlassBtn").ShouldBe(new Size(42, 42));
            Size(new Button(), "A.WhiteRoundBtn").ShouldBe(new Size(40, 40));
            Size(new Button(), "A.ExpandBtn").ShouldBe(new Size(30, 30));
            Size(new RadioButton(), "A.NavItem").Height.ShouldBe(46);   // the mockup's .nav
            Size(new RadioButton(), "A.SegItem").Height.ShouldBe(34);   // the mockup's Day bubble
            Size(new RadioButton(), "A.OptItem").Height.ShouldBe(32);
            Size(new Button(), "A.AccentBtn").Height.ShouldBe(36);   // the mockup's Open report
            var (_, accent) = Dressed(new Button(), "A.AccentBtn", Theme.Dark);
            ((SolidColorBrush)accent.Background).Color.ShouldBe(Color.FromArgb(0xDB, 0xD9, 0xF2, 0x5A), "the mockup's lime glass: the lime at 86 % over the recipe's glass");
            ((SolidColorBrush)accent.Foreground).Color.ShouldBe(Color.FromRgb(0x1B, 0x20, 0x04), "the lime's ink");
            Press.ScaleOf(accent).ShouldNotBeNull("a button compresses when pressed");
            var (_, row) = Dressed(new Button(), "A.RowBtn", Theme.Dark);
            Press.ScaleOf(row).ShouldBeNull("a row lights rather than compresses");
        });

    [Fact]
    public void The_panes_take_the_demos_radii()
        => UiHarness.OnUi(() =>
        {
            Dressed(new GlassPanel(), null, Theme.Dark).Element.CornerRadius.ShouldBe(new CornerRadius(28));   // the recipe's
            Dressed(new GlassPanel(), "A.Modal", Theme.Dark).Element.CornerRadius.ShouldBe(new CornerRadius(28));
            Dressed(new GlassPanel(), "A.Toast", Theme.Dark).Element.CornerRadius.ShouldBe(new CornerRadius(22));
            Dressed(new GlassPanel(), "A.Banner", Theme.Dark).Element.CornerRadius.ShouldBe(new CornerRadius(22));
            Dressed(new Border(), "A.Well", Theme.Dark).Element.CornerRadius.ShouldBe(new CornerRadius(22));   // the mockup's .inset
            var pane = Dressed(new GlassPanel(), null, Theme.Dark).Element;
            pane.HorizontalContentAlignment.ShouldBe(HorizontalAlignment.Stretch, "a pane's content fills it");
            pane.VerticalContentAlignment.ShouldBe(VerticalAlignment.Stretch);
            Dressed(new GlassPanel(), "A.Toast", Theme.Dark).Element.VerticalContentAlignment.ShouldBe(VerticalAlignment.Center, "a toast's words sit in its middle");
        });

    /// <summary>Tokens, never raw values (Plan S ground rules): no colour is written into the styles, every token they
    /// name by DynamicResource is an Aero one both palettes define, and every StaticResource is a key of the styles' own.</summary>
    [Fact]
    public void Every_token_the_styles_name_is_in_both_aero_palettes_and_no_colour_is_written_in()
    {
        var xaml = File.ReadAllText(Path.Combine(Root(), "src", "PowerLedger.App", "Aero", "Styles.Aero.xaml"));
        Regex.IsMatch(xaml, "(Color|Brush|Fill|Stroke|Background|Foreground)=\"#").ShouldBeFalse("a colour is written into the styles instead of a token");
        Regex.IsMatch(xaml, "(Fill|Stroke|Background|Foreground|BorderBrush)=\"(White|Black)\"").ShouldBeFalse("a named colour instead of a token");
        var tokens = Regex.Matches(xaml, @"\{DynamicResource ([^}]+)\}").Select(m => m.Groups[1].Value).Distinct().ToList();
        tokens.ShouldNotBeEmpty();
        UiHarness.OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                var palette = ThemeManager.Palette(Look.Aero, theme);
                foreach (var token in tokens)
                {
                    token.ShouldStartWith("A.", customMessage: token);
                    palette.Contains(token).ShouldBeTrue($"{token} is not in Aero's {theme} palette");
                }
            }
            var styles = Load();
            foreach (var key in Regex.Matches(xaml, @"\{StaticResource ([^}]+)\}").Select(m => m.Groups[1].Value).Distinct())
            {
                if (key.StartsWith("{x:Type", StringComparison.Ordinal)) continue;
                styles.Contains(key).ShouldBeTrue($"{key} is named but not defined");
            }
        });
    }

    /// <summary>The dictionary as a window merges it.</summary>
    internal static ResourceDictionary Load() => new() { Source = new Uri(AeroHost.Styles, UriKind.Absolute) };

    /// <summary><paramref name="element"/> under a host holding Aero's palette for <paramref name="theme"/> and its styles,
    /// as a view in an Aero window is, with the keyed style (or the implicit one) applied.</summary>
    internal static (Border Host, T Element) Dressed<T>(T element, string? key, Theme theme)
        where T : FrameworkElement
    {
        var host = new Border();
        host.Resources.MergedDictionaries.Add(ThemeManager.Palette(Look.Aero, theme));
        host.Resources.MergedDictionaries.Add(new SharedDictionary { Source = new Uri(AeroHost.Styles, UriKind.Absolute) });
        host.Child = element;
        if (key is not null) element.Style = (Style)host.FindResource(key);
        element.ApplyTemplate();
        element.Measure(new Size(1000, 1000));
        return (host, element);
    }

    private static Size Size<T>(T element, string key)
        where T : FrameworkElement
    {
        var (_, dressed) = Dressed(element, key, Theme.Dark);
        return new Size(dressed.Width, dressed.Height);
    }

    private static Style Style(ResourceDictionary styles, object key)
    {
        styles.Contains(key).ShouldBeTrue($"{key} is missing");
        return styles[key].ShouldBeOfType<Style>(key.ToString());
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PowerLedger.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("The repository root was not found above the test binaries.");
    }
}
