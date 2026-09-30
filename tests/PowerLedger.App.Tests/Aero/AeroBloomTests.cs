using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan W, the owner's direction (0.10.3): Aero bloom, the approved video's glass. Windows' bloom picture is read where
/// Windows keeps it, mapped across the stage as the video shows it, and frosted whole; without it a drawn one stands in;
/// its glass keeps text at 3:1 (the halo only if needed) and the strict glass at 4.5:1.
/// </summary>
public class AeroBloomTests
{
    [Fact]
    public void The_bloom_is_read_from_windows_own_folders_4k_first()
        => AeroBloom.Candidates(@"C:\Windows").ShouldBe(
            [@"C:\Windows\Web\4K\Wallpaper\Windows\img19_1920x1200.jpg", @"C:\Windows\Web\Wallpaper\Windows\img19.jpg"]);

    [Fact]
    public void The_stage_shows_the_part_of_the_bloom_the_video_shows()
    {
        var stage = new Rect(24, 52, 1312, 788);
        var at = AeroBloom.Place(stage, new Size(1920, 1200));
        (at.Width / at.Height).ShouldBe(1.6, 1e-9, "the picture keeps its shape");
        var left = (stage.Left - at.Left) / at.Width;
        var right = (stage.Right - at.Left) / at.Width;
        var top = (stage.Top - at.Top) / at.Height;
        var bottom = (stage.Bottom - at.Top) / at.Height;
        left.ShouldBeGreaterThanOrEqualTo(AeroBloom.Seen.Left - 1e-9);
        right.ShouldBeLessThanOrEqualTo(AeroBloom.Seen.Right + 1e-9);
        top.ShouldBeGreaterThanOrEqualTo(AeroBloom.Seen.Top - 1e-9);
        bottom.ShouldBeLessThanOrEqualTo(AeroBloom.Seen.Bottom + 1e-9);
        ((left + right) / 2).ShouldBe(AeroBloom.Seen.Left + AeroBloom.Seen.Width / 2, 1e-9, "centred on the seen part");
        (right - left).ShouldBe(AeroBloom.Seen.Width, 1e-9, "the demo's stage spans the seen part's width");
    }

    [Fact]
    public void Without_windows_bloom_a_drawn_one_in_its_colours_stands_in()
    {
        var drawn = AeroBloom.Drawn(96, 60);
        (drawn.PixelWidth, drawn.PixelHeight, drawn.IsFrozen).ShouldBe((96, 60, true));
        var pixels = new byte[96 * 60 * 4];
        new FormatConvertedBitmap(drawn, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 96 * 4, 0);
        Color At(int x, int y)
        {
            var i = (y * 96 + x) * 4;
            return Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i]);
        }
        var heart = At(44, 31);
        heart.B.ShouldBeGreaterThan((byte)200, "a bright blue heart");
        Contrast.Luminance(At(0, 0)).ShouldBeLessThan(0.01, "a deep navy round it");
        var palette = UiHarness.OnUi(() => ThemeManager.Palette(Look.Aero, Theme.Dark));
        Contrast.Luminance(heart).ShouldBeLessThanOrEqualTo(Contrast.Luminance((Color)palette["A.C.BloomBrightest"]) + 0.01, "within the bloom's grounds");
    }

    [Fact]
    public void Windows_bloom_frosted_stays_within_the_grounds_the_glass_is_worked_out_for()
    {
        var path = AeroBloom.Candidates(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).FirstOrDefault(File.Exists);
        if (path is null) return;   // a Windows without it draws its own, checked above
        var frosted = WallpaperFrost.Frost(WallpaperFrost.Load(path)!, 26 / AeroBloom.StagePixelsPerFrostPixel, 1.55, Colors.Black, Colors.White);
        var w = WallpaperFrost.FrostWidth;
        var pixels = new byte[w * WallpaperFrost.FrostHeight * 4];
        frosted.CopyPixels(pixels, w * 4, 0);
        var brightest = 0.0;
        var seen = AeroBloom.Seen;
        for (var y = (int)(seen.Top * WallpaperFrost.FrostHeight); y < (int)(seen.Bottom * WallpaperFrost.FrostHeight); y++)
        {
            for (var x = (int)(seen.Left * w); x < (int)(seen.Right * w); x++)
            {
                var i = (y * w + x) * 4;
                brightest = Math.Max(brightest, Contrast.Luminance(Color.FromRgb(pixels[i + 2], pixels[i + 1], pixels[i])));
            }
        }
        var palette = UiHarness.OnUi(() => ThemeManager.Palette(Look.Aero, Theme.Dark));
        brightest.ShouldBeLessThanOrEqualTo(Contrast.Luminance((Color)palette["A.C.BloomBrightest"]) + 0.02);
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void The_default_glass_keeps_the_mockups_text_steps_with_its_text_shadow(string themeName)
    {
        // 0.10.9, the owner's liquid glass: the mockup's text steps (its .sub at 80 %, a quieter 60 %) and its text shadow
        // (.t: 0 1px 2px, navy at 28 %). The glass is over the live desktop, not the bloom, whatever backdrop a file names.
        var theme = Enum.Parse<Theme>(themeName);
        var (map, own) = UiHarness.OnUi(() => (GlassMaterial.Map(GlassSettings.Default with { Backdrop = GlassBackdrop.Bloom }, theme), ThemeManager.Palette(Look.Aero, theme)));
        var shadow = map[GlassMaterial.TextShadowKey].ShouldBeOfType<System.Windows.Media.Effects.DropShadowEffect>();
        (shadow.Color, shadow.ShadowDepth, shadow.Direction, Math.Round(shadow.Opacity, 2)).ShouldBe((Color.FromRgb(0, 10, 40), 1d, 270d, 0.28));
        var text = (Color)map["A.C.Text"];
        text.ShouldBe((Color)own["A.C.Text"], "each theme keeps its own ink on the Tinted glass");
        map["A.C.Text2"].ShouldBe(Color.FromArgb(0xCC, text.R, text.G, text.B), "the mockup's .sub, 80 %");
        map["A.C.Text3"].ShouldBe(Color.FromArgb(0x99, text.R, text.G, text.B), "a quieter 60 %");
    }

    [Fact]
    public void Text_reads_at_4_5_to_1_under_increase_contrast_whatever_backdrop_was_saved()
    {
        var settings = GlassSettings.Default with { Backdrop = GlassBackdrop.Bloom, IncreaseContrast = true };
        var (map, palette) = UiHarness.OnUi(() => (GlassMaterial.Map(settings, Theme.Dark), ThemeManager.Palette(Look.Aero, Theme.Dark)));
        var text = (Color)map["A.C.Text"];
        var top = (Color)map["A.C.GlassTintTop"];
        var grounds = new[] { "A.C.BackdropDarkest", "A.C.BackdropBrightest" }.Select(k => Contrast.Over(top, (Color)palette[k]));
        grounds.Min(g => Contrast.Ratio(Contrast.Over(text, g), g)).ShouldBeGreaterThanOrEqualTo(4.5);
        map["A.C.Text2"].ShouldBe(text);
        map["A.C.Text3"].ShouldBe(text);
    }
}
