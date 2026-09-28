using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan S G1 and G2: the glass system drawn, in both themes, to PNGs under %TEMP%\powerledger-renders (aero-glass-*),
/// with the few facts a render can hold to: the panes show the scene through their tint rather than covering it, the rim
/// is lit above the tint, and the accent button is the palette's accent.
/// </summary>
[Trait("Category", "UI")]
public class AeroGlassRenderingTests
{
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void The_glass_sample_draws_in_each_theme(string themeName)
        => UiHarness.OnUi(() =>
        {
            var theme = Enum.Parse<Theme>(themeName);
            Directory.CreateDirectory(UiHarness.Folder);
            var window = AeroGlassSample.Window(theme);
            try
            {
                window.Show();
                window.UpdateLayout();
                UiHarness.Pump(TimeSpan.FromMilliseconds(450));
                UiHarness.Render(window, AeroGlassSample.Width, AeroGlassSample.Height, $"aero-glass-sample-{themeName.ToLowerInvariant()}.png");
                var palette = ThemeManager.Palette(Look.Aero, theme);
                var accent = UiHarness.Find<Button>(window, b => b.Style == window.FindResource("A.AccentBtn"))!;
                var at = accent.TranslatePoint(new Point(4, accent.ActualHeight / 2), window);
                MidnightHost.PixelOf(window, AeroGlassSample.Width, AeroGlassSample.Height, (int)at.X, (int)at.Y)
                    .ShouldBe((Color)palette["A.C.Accent"], "the accent button is the palette's accent");

                var side = UiHarness.Find<GlassPanel>(window)!;
                var inside = side.TranslatePoint(new Point(side.ActualWidth / 2, side.ActualHeight - 30), window);
                var tinted = MidnightHost.PixelOf(window, AeroGlassSample.Width, AeroGlassSample.Height, (int)inside.X, (int)inside.Y);
                var scene = MidnightHost.PixelOf(window, AeroGlassSample.Width, AeroGlassSample.Height, 4, (int)inside.Y);
                tinted.ShouldNotBe(scene, "the tint changes the scene");
                Contrast.Difference(tinted, scene).ShouldBeLessThan(40, "the pane is glass over the scene, not a solid card");
                var rim = side.TranslatePoint(new Point(side.ActualWidth / 2, 0.5), window);
                var rimPixel = MidnightHost.PixelOf(window, AeroGlassSample.Width, AeroGlassSample.Height, (int)rim.X, (int)rim.Y);
                var below = MidnightHost.PixelOf(window, AeroGlassSample.Width, AeroGlassSample.Height, (int)rim.X, (int)rim.Y + 12);
                if (theme == Theme.Dark) Contrast.Luminance(rimPixel).ShouldBeGreaterThan(Contrast.Luminance(below), "the rim catches the light");
                else Contrast.Luminance(rimPixel).ShouldBeLessThan(Contrast.Luminance(below), "light glass is drawn by a dark hairline");
            }
            finally
            {
                window.Close();
            }
        });
}
