using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan S G7, as <see cref="FontTests"/> for the other faces: Geist, the demo's face, is bundled under Fonts/Geist as its
/// static weights, read from the App's own resources; A.F.Ui names it first with Segoe UI Variable as the fallback; and
/// text set in it measures by Geist's own advances, not the fallback's.
/// </summary>
[Trait("Category", "UI")]
public class AeroFontTests
{
    private const string App = "pack://application:,,,/PowerLedger;component";
    private const string Folder = App + "/Fonts/Geist/";
    private static readonly string Zeros = new('H', 24);

    [Fact]
    public void Geist_offers_its_four_weights_from_the_apps_own_files()
        => UiHarness.OnUi(() =>
        {
            var faces = Geist().GetTypefaces()
                .Select(face => (Face: face, Glyphs: face.TryGetGlyphTypeface(out var glyphs) ? glyphs : null))
                .Where(entry => entry.Glyphs?.StyleSimulations == StyleSimulations.None)
                .ToList();
            faces.Select(entry => entry.Face.Weight.ToOpenTypeWeight()).OrderBy(weight => weight).ShouldBe([400, 500, 600, 700]);
            foreach (var (face, glyphs) in faces)
            {
                glyphs!.FontUri.ToString().ToLowerInvariant().ShouldStartWith(Folder.ToLowerInvariant());
                face.Style.ShouldBe(FontStyles.Normal);
                glyphs.CharacterToGlyphMap.Keys.ShouldContain('W');
            }
            faces.Select(entry => entry.Glyphs!.FontUri).Distinct().Count().ShouldBe(4, "a static file a weight, no variable font");
        });

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void The_ui_face_is_geist_first_and_segoe_after(string theme)
        => UiHarness.OnUi(() =>
        {
            var fonts = (FontFamily)ThemeManager.Palette(Look.Aero, Enum.Parse<Theme>(theme))["A.F.Ui"];
            fonts.Source.ShouldStartWith("pack://application:,,,/PowerLedger;component/Fonts/Geist/#Geist, Segoe UI Variable Display");
            var medium = new Typeface(Geist(), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
            medium.TryGetGlyphTypeface(out var glyphs).ShouldBeTrue();
            glyphs.Weight.ShouldBe(FontWeights.Medium, "the big figures' weight is a file of its own");
        });

    /// <summary>A line set in A.F.Ui is as wide as the same line set in the bundled Geist, and as wide as none of the
    /// Windows faces the key falls back to: Geist was loaded, not stood in for.</summary>
    [Fact]
    public void Text_set_in_the_ui_face_is_set_in_geist()
        => UiHarness.OnUi(() =>
        {
            var ui = new FontFamily(new Uri(App + "/"), ((FontFamily)ThemeManager.Palette(Look.Aero, Theme.Dark)["A.F.Ui"]).Source.Replace("pack://application:,,,/PowerLedger;component/", "./", StringComparison.Ordinal));
            var geist = Width(Face(Geist()));
            Width(Face(ui)).ShouldBe(geist, 0.05);
            foreach (var windows in new[] { "Segoe UI Variable Display", "Segoe UI Variable Text", "Segoe UI" })
                Math.Abs(Width(Face(new FontFamily(windows))) - geist).ShouldBeGreaterThan(1, windows);
        });

    private static FontFamily Geist() => new(new Uri(Folder), "./#Geist");

    private static Typeface Face(FontFamily family) => new(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static double Width(Typeface face)
        => new FormattedText(Zeros, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 20, Brushes.Black, 1).WidthIncludingTrailingWhitespace;
}
