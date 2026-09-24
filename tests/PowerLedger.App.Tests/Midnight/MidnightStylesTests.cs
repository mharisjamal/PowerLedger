using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan O 0.2 and 0.3: every key a Midnight view may use is in Styles.Midnight.xaml, of the type the view expects, so
/// a page written against the contract finds what it asks for. The dictionary is read the way a window merges it.
/// </summary>
public class MidnightStylesTests
{
    private const string Source = "pack://application:,,,/PowerLedger;component/Midnight/Styles.Midnight.xaml";

    [Fact]
    public void The_fonts_are_font_families()
        => Check(styles =>
        {
            foreach (var key in new[] { "M.Font.Ui", "M.Font.Numbers", "M.Font.Glyphs" }) styles[key].ShouldBeOfType<FontFamily>(key);
            ((FontFamily)styles["M.Font.Ui"]).Source.ShouldContain("Manrope");
            ((FontFamily)styles["M.Font.Numbers"]).Source.ShouldContain("Manrope", customMessage: "figures in the text face, with tabular numerals, not a monospace one");
            ((FontFamily)styles["M.Font.Glyphs"]).Source.ShouldContain("Segoe Fluent Icons");
        });

    [Fact]
    public void The_text_styles_target_text_blocks()
        => Check(styles =>
        {
            foreach (var key in new[] { "M.Text.Display", "M.Text.Title", "M.Text.Heading", "M.Text.Body", "M.Text.Secondary", "M.Text.Muted", "M.Text.Eyebrow", "M.Text.Column", "M.Text.Number" })
                Style(styles, key).TargetType.ShouldBe(typeof(TextBlock), key);
            Setter(styles, "M.Text.Display", TextBlock.FontSizeProperty).ShouldBe(36.0);
            Setter(styles, "M.Text.Title", TextBlock.FontSizeProperty).ShouldBe(20.0);
            Setter(styles, "M.Text.Heading", TextBlock.FontSizeProperty).ShouldBe(15.0);
            Setter(styles, "M.Text.Body", TextBlock.FontSizeProperty).ShouldBe(13.0);
            Setter(styles, "M.Text.Eyebrow", TextBlock.FontSizeProperty).ShouldBe(10.5);
            Setter(styles, "M.Text.Display", TextBlock.FontFamilyProperty).ShouldBeSameAs(styles["M.Font.Numbers"]);
            Setter(styles, "M.Text.Number", TextBlock.FontFamilyProperty).ShouldBeSameAs(styles["M.Font.Numbers"]);
            Setter(styles, "M.Text.Body", TextBlock.FontFamilyProperty).ShouldBeSameAs(styles["M.Font.Ui"]);
            Setter(styles, "M.Text.Display", TextBlock.FontWeightProperty).ShouldBe(FontWeights.Normal, "the big figures are light, as the reference's");
            Setter(styles, "M.Text.Number", System.Windows.Documents.Typography.NumeralAlignmentProperty).ShouldBe(FontNumeralAlignment.Tabular);
        });

    [Fact]
    public void The_surfaces_navigation_controls_and_tables_target_their_elements()
        => Check(styles =>
        {
            var expected = new (string Key, Type Target)[]
            {
                ("M.Card", typeof(Border)), ("M.Card.Flat", typeof(Border)), ("M.Divider", typeof(Border)),
                ("M.NavItem", typeof(RadioButton)), ("M.NavGroup", typeof(TextBlock)), ("M.Badge", typeof(ContentControl)),
                ("M.Button.Primary", typeof(Button)), ("M.Button.Outline", typeof(Button)), ("M.Button.Quiet", typeof(Button)), ("M.IconButton", typeof(Button)), ("M.IconButton.Quiet", typeof(Button)),
                ("M.Pill", typeof(RadioButton)), ("M.Switch", typeof(ToggleButton)), ("M.Field", typeof(TextBox)), ("M.Tick", typeof(CheckBox)),
                ("M.Chip.Measured", typeof(ContentControl)), ("M.Chip.Calibrated", typeof(ContentControl)), ("M.Chip.Estimated", typeof(ContentControl)),
                ("M.StatusPill.Good", typeof(ContentControl)), ("M.StatusPill.Warn", typeof(ContentControl)), ("M.StatusPill.Bad", typeof(ContentControl)),
                ("M.Table.Header", typeof(Border)), ("M.Table.Row", typeof(Border)), ("M.ShareTrack", typeof(Border)),
            };
            foreach (var (key, target) in expected) Style(styles, key).TargetType.ShouldBe(target, key);
            Setter(styles, "M.NavItem", FrameworkElement.HeightProperty).ShouldBe(40.0);
            Setter(styles, "M.IconButton", FrameworkElement.WidthProperty).ShouldBe(32.0);
            Setter(styles, "M.IconButton", FrameworkElement.HeightProperty).ShouldBe(32.0);
            Setter(styles, "M.IconButton", Control.BorderThicknessProperty).ShouldBe(new Thickness(1), "a small framed square");
            Setter(styles, "M.IconButton.Quiet", Control.BorderThicknessProperty).ShouldBe(new Thickness(0));
            Setter(styles, "M.Pill", FrameworkElement.HeightProperty).ShouldBe(26.0);
            Setter(styles, "M.Table.Row", FrameworkElement.MinHeightProperty).ShouldBe(48.0);
            Setter(styles, "M.Card", Border.PaddingProperty).ShouldBe(new Thickness(20));
        });

    [Fact]
    public void The_implicit_styles_are_scoped_by_type_and_the_focus_ring_replaces_the_default()
        => Check(styles =>
        {
            foreach (var type in new[] { typeof(ScrollBar), typeof(ToolTip), typeof(ContextMenu), typeof(MenuItem), typeof(TextBox), typeof(CheckBox), typeof(Button) })
                Style(styles, type).TargetType.ShouldBe(type, type.Name);
            styles[SystemParameters.FocusVisualStyleKey].ShouldBeOfType<Style>().BasedOn.ShouldBeSameAs(styles["M.Focus.Ring"]);
            styles["M.Focus.Ring"].ShouldBeOfType<Style>();
            Style(styles, typeof(Button)).BasedOn.ShouldBeSameAs(styles["M.Button.Quiet"]);
        });

    [Fact]
    public void The_numbers_are_doubles_the_durations_are_durations_and_the_easings_ease()
        => Check(styles =>
        {
            var numbers = new (string Key, double Value)[]
            {
                ("M.Radius.Card", 10), ("M.Radius.Pill", 999), ("M.Radius.Control", 6), ("M.Elevation.Tooltip", 14),
            };
            foreach (var (key, value) in numbers) styles[key].ShouldBeOfType<double>(key).ShouldBe(value, key);
            styles["M.Motion.Fast"].ShouldBeOfType<Duration>().ShouldBe(Motion.Fast);
            styles["M.Motion.Base"].ShouldBeOfType<Duration>().ShouldBe(Motion.Base);
            styles["M.Motion.Slow"].ShouldBeOfType<Duration>().ShouldBe(Motion.Slow);
            Motion.Fast.TimeSpan.TotalMilliseconds.ShouldBe(150);
            Motion.Base.TimeSpan.TotalMilliseconds.ShouldBe(220);
            Motion.Slow.TimeSpan.TotalMilliseconds.ShouldBe(320);
            styles["M.Ease.In"].ShouldBeOfType<CubicEase>().EasingMode.ShouldBe(EasingMode.EaseOut);
            styles["M.Ease.Out"].ShouldBeOfType<CubicEase>().EasingMode.ShouldBe(EasingMode.EaseIn);
            var pill = styles["M.Ease.Pill"].ShouldBeOfType<BackEase>();
            pill.EasingMode.ShouldBe(EasingMode.EaseOut);
            pill.Amplitude.ShouldBe(0.2);
        });

    [Fact]
    public void Every_brush_the_styles_name_is_a_palette_key_and_no_colour_is_written_in()
    {
        var xaml = File.ReadAllText(Path.Combine(Root(), "src", "PowerLedger.App", "Midnight", "Styles.Midnight.xaml"));
        Regex.IsMatch(xaml, "(Color|Brush|Fill|Stroke|Background|Foreground)=\"#").ShouldBeFalse("a colour is written into the styles instead of a token");
        foreach (Match match in Regex.Matches(xaml, @"\{DynamicResource ([^}]+)\}"))
            match.Groups[1].Value.ShouldStartWith("M.", customMessage: match.Value);
    }

    /// <summary>The dictionary as a window merges it. No Application runs here, so the pack scheme and the application
    /// part are registered by hand, which their static constructors do.</summary>
    internal static ResourceDictionary Load()
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = Application.ResourceAssembly;
        return new ResourceDictionary { Source = new Uri(Source, UriKind.Absolute) };
    }

    private static void Check(Action<ResourceDictionary> assert) => Sta.Run(() =>
    {
        assert(Load());
        return true;
    });

    private static Style Style(ResourceDictionary styles, object key)
    {
        styles.Contains(key).ShouldBeTrue($"{key} is missing");
        return styles[key].ShouldBeOfType<Style>(key.ToString());
    }

    /// <summary>A style's value for <paramref name="property"/>, from the style or the one it is based on, as it applies.</summary>
    private static object? Setter(ResourceDictionary styles, string key, DependencyProperty property)
    {
        for (var style = Style(styles, key); style is not null; style = style.BasedOn)
        {
            if (style.Setters.OfType<Setter>().FirstOrDefault(s => s.Property == property) is { } setter) return setter.Value;
        }
        throw new ShouldAssertException($"{key} sets no {property.Name}.");
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PowerLedger.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("The repository root was not found above the test binaries.");
    }
}
