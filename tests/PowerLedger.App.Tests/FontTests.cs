using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// The faces bundled under the App's Fonts folder, a folder a family: each family answers to the pack URI its folder gives
/// it, with the static weights Styles.xaml's styles ask for, read from the App's own resources rather than from a font Windows
/// happens to have; Styles.xaml's keys reach them; and text set in one measures by that face's own advances. The tests run
/// on <see cref="RenderingTests"/>' application thread.
/// </summary>
[Trait("Category", "UI")]
public class FontTests
{
    /// <summary>The App's assembly by name, which resolves in the test host as it does in the App.</summary>
    private const string App = "pack://application:,,,/PowerLedger;component";

    private const string Fonts = App + "/Fonts/";

    private const double Size = 20;

    /// <summary>A run of one glyph, so no kerning pair moves it: its width is that glyph's advance so many times over.</summary>
    private static readonly string Zeros = new('0', 24);

    /// <summary>
    /// Folder, the family DirectWrite reads from the files, its stretch, and its weights. Archivo Narrow's files carry that
    /// name, but DirectWrite takes "Narrow" as a width, so they are the family Archivo at the semi-condensed stretch.
    /// </summary>
    public static TheoryData<string, string, string, int[]> Families => new()
    {
        { "Archivo", "Archivo", "Normal", [400, 500, 600, 700] },
        { "ArchivoNarrow", "Archivo", "SemiCondensed", [400, 500, 600, 700] },
        { "MartianMono", "Martian Mono", "Normal", [400, 500, 600] },
        { "Manrope", "Manrope", "Normal", [400, 500, 600, 700] },
        { "JetBrainsMono", "JetBrains Mono", "Normal", [400, 500, 600] },
    };

    [Theory]
    [MemberData(nameof(Families))]
    public void Each_bundled_family_offers_its_weights_from_the_apps_own_files(string folder, string family, string stretch, int[] weights)
        => RenderingTests.OnUi(() =>
        {
            var fonts = Bundled(folder, family);
            var faces = fonts.GetTypefaces()
                .Select(face => (Face: face, Glyphs: face.TryGetGlyphTypeface(out var glyphs) ? glyphs : null))
                .ToList();
            var listing = string.Join("\n", faces.Select(entry => Describe(entry.Face, entry.Glyphs)));
            // WPF lists, beside the files, the slanted faces it would make from them; those are left out.
            var real = faces.Where(entry => entry.Glyphs?.StyleSimulations == StyleSimulations.None).ToList();
            real.Select(entry => entry.Face.Style).ShouldAllBe(style => style == FontStyles.Normal, listing);
            real.Select(entry => entry.Face.Stretch.ToString()).ShouldAllBe(actual => actual == stretch, listing);
            real.Select(entry => entry.Face.Weight.ToOpenTypeWeight()).OrderBy(weight => weight).ToList()
                .ShouldBe(weights, ignoreOrder: false, customMessage: listing);
            foreach (var (face, glyphs) in real)
            {
                glyphs!.FontUri.ToString().ToLowerInvariant().ShouldStartWith((Fonts + folder + "/").ToLowerInvariant(), customMessage: listing);
                glyphs.Weight.ToOpenTypeWeight().ShouldBe(face.Weight.ToOpenTypeWeight(), listing);
                glyphs.Style.ShouldBe(FontStyles.Normal, listing);
                glyphs.CharacterToGlyphMap.Keys.ShouldContain('W');
            }
            // A static instance a weight: no file is asked to stand in for another, as a variable font's would be.
            real.Select(entry => entry.Glyphs!.FontUri).Distinct().Count().ShouldBe(weights.Length, listing);
        });

    /// <summary>
    /// Each key's first family is a bundled folder and the family it holds. In the App, /Fonts/... is the App's own
    /// assembly, the entry assembly; the test host is another, and WPF lets no test name the App's instead, so the key's
    /// location is looked up in the App's assembly here, and the regular and bold faces it finds there are files of their own.
    /// </summary>
    [Theory]
    [InlineData("Font.Ui", "/Fonts/Archivo/#Archivo", "Normal")]
    [InlineData("Font.Narrow", "/Fonts/ArchivoNarrow/#Archivo", "SemiCondensed")]
    [InlineData("Font.Numbers", "/Fonts/MartianMono/#Martian Mono", "Normal")]
    [InlineData("Font.Ui2", "/Fonts/Manrope/#Manrope", "Normal")]
    [InlineData("Font.Numbers2", "/Fonts/JetBrainsMono/#JetBrains Mono", "Normal")]
    public void Styles_keys_name_a_bundled_folder_and_family(string key, string first, string stretch)
        => RenderingTests.OnUi(() =>
        {
            var fonts = (FontFamily)Application.Current.FindResource(key);
            fonts.Source.Split(',')[0].Trim().ShouldBe(first);
            var hash = first.IndexOf('#');
            var bundled = new FontFamily(new Uri(App + first[..hash]), "./#" + first[(hash + 1)..]);
            var width = (FontStretch)new FontStretchConverter().ConvertFromInvariantString(stretch)!;
            var regular = new Typeface(bundled, FontStyles.Normal, FontWeights.Normal, width);
            regular.TryGetGlyphTypeface(out var glyphs).ShouldBeTrue(key);
            glyphs.FontUri.ToString().ToLowerInvariant().ShouldStartWith((App + first[..hash]).ToLowerInvariant());
            glyphs.Weight.ShouldBe(FontWeights.Normal);
            glyphs.Stretch.ShouldBe(width);
            var bold = new Typeface(bundled, FontStyles.Normal, FontWeights.Bold, width);
            bold.TryGetGlyphTypeface(out var boldGlyphs).ShouldBeTrue(key);
            boldGlyphs.FontUri.ShouldNotBe(glyphs.FontUri, "bold is a file of its own, not the regular face made heavier");
            boldGlyphs.Weight.ToOpenTypeWeight().ShouldBeOneOf(600, 700);
        });

    /// <summary>
    /// A line set in the bundled face is as wide as that face's own advances say, and not as wide as the Windows face the
    /// key would fall back to would set it: the face was loaded, not stood in for.
    /// </summary>
    [Theory]
    [InlineData("Manrope", "Manrope", "Segoe UI")]
    [InlineData("Archivo", "Archivo", "Bahnschrift")]
    [InlineData("JetBrainsMono", "JetBrains Mono", "Cascadia Mono")]
    [InlineData("MartianMono", "Martian Mono", "Cascadia Mono")]
    public void Text_set_in_a_bundled_face_measures_by_its_own_advances(string folder, string family, string windows)
        => RenderingTests.OnUi(() =>
        {
            var face = Face(Bundled(folder, family));
            face.TryGetGlyphTypeface(out var glyphs).ShouldBeTrue(family);
            var expected = Zeros.Length * glyphs.AdvanceWidths[glyphs.CharacterToGlyphMap['0']] * Size;
            var bundled = Width(face);
            var fallback = Width(Face(new FontFamily(windows)));
            bundled.ShouldBe(expected, 0.05, $"{family} set {bundled:F2}, its advances say {expected:F2}: {Describe(face, glyphs)}");
            Math.Abs(fallback - expected).ShouldBeGreaterThan(1, $"{windows} sets the line at {fallback:F2}, {family} at {expected:F2}");
        });

    private static FontFamily Bundled(string folder, string family) => new(new Uri(Fonts + folder + "/"), "./#" + family);

    private static Typeface Face(FontFamily fonts) => new(fonts, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static double Width(Typeface face)
        => new FormattedText(Zeros, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, Size, Brushes.Black, 1).WidthIncludingTrailingWhitespace;

    private static string Describe(Typeface face, GlyphTypeface? glyphs)
        => $"{face.Style} {face.Weight.ToOpenTypeWeight()} {face.Stretch} -> {glyphs?.StyleSimulations.ToString() ?? "no file"} {glyphs?.FontUri}";
}
