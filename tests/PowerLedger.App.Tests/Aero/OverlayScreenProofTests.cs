using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// 0.10.9: the watts overlay on the real screen, as Windows composes it, over a white window, a black one and the
/// wallpaper (a window showing the user's wallpaper picture): nothing is painted outside its capsule. Drawn only with
/// PL_PROOF_DIR set; the engine is handed the picture behind the overlay (a fake source) and our windows are left
/// capturable for the BitBlt, as the engine's own screen tests do.
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]
public class OverlayScreenProofTests
{
    [Fact]
    public void The_overlay_paints_nothing_outside_its_capsule_on_screen()
    {
        if (Environment.GetEnvironmentVariable("PL_PROOF_DIR") is not { Length: > 0 } dir || !Directory.Exists(dir)) return;
        var outDir = Path.Combine(dir, "overlay");
        Directory.CreateDirectory(outDir);
        var wallpaper = AeroNative.WallpaperPath();
        foreach (var ground in new[] { "white", "black", "wallpaper" })
        {
            if (ground == "wallpaper" && wallpaper is null) continue;
            UiHarness.OnUi(() => ScreenCapture.Aware(() =>
            {
                Brush fill = ground switch
                {
                    "white" => Brushes.White,
                    "black" => Brushes.Black,
                    _ => new ImageBrush(LiquidGlassProofTests.Load(wallpaper!)) { Stretch = Stretch.UniformToFill },
                };
                var behind = ScreenCapture.Show(new Border { Background = fill }, 200, 200, 520, 240);
                var now = MidnightFixtures.NowScreen(out _);
                OverlayWindow? overlay = null;
                try
                {
                    UiHarness.Pump(TimeSpan.FromMilliseconds(300));
                    var source = (HwndSource)PresentationSource.FromVisual(behind)!;
                    var scale = source.CompositionTarget.TransformToDevice.M11;
                    var box = new Rect(behind.Left * scale, behind.Top * scale, behind.ActualWidth * scale, behind.ActualHeight * scale);
                    var picture = new RenderTargetBitmap((int)Math.Round(box.Width), (int)Math.Round(box.Height), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    picture.Render((Visual)behind.Content);
                    picture.Freeze();
                    LiquidGlassSources.Override = _ => new FakeGlassSource(picture, box);
                    LiquidGlassSources.ExcludeFromCapture = false;

                    var settings = MidnightFixtures.SettingsScreen(new FakeUiSettings());
                    overlay = AeroHost.Dressed(new OverlayWindow(now, settings) { Placing = false, Left = 300, Top = 280 }, Theme.Dark);
                    overlay.Apply(OverlaySettings.Default with { Enabled = true });
                    overlay.ShowOverlay();
                    UiHarness.Pump(TimeSpan.FromMilliseconds(1500));
                    overlay.UpdateLayout();

                    var margin = 24;
                    int x = (int)Math.Round(overlay.Left * scale) - margin, y = (int)Math.Round(overlay.Top * scale) - margin;
                    int w = (int)Math.Round(overlay.ActualWidth * scale) + 2 * margin, h = (int)Math.Round(overlay.ActualHeight * scale) + 2 * margin;
                    var pixels = ScreenCapture.Grab(x, y, w, h);
                    var shot = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, pixels, w * 4);
                    LiquidGlassProofTests.Write(shot, Path.Combine(outDir, $"overlay-{ground}-1x.png"));
                    LiquidGlassProofTests.Write(Nearest(shot, 4), Path.Combine(outDir, $"overlay-{ground}-4x.png"));

                    // Outside the capsule the screen is the window behind, pixel for pixel: the ring 3 px out from the pill.
                    var pill = (FrameworkElement)overlay.FindName("Pill");
                    var at = pill.TranslatePoint(new Point(0, 0), overlay);
                    int px = margin + (int)Math.Round(at.X * scale), py = margin + (int)Math.Round(at.Y * scale);
                    int pw = (int)Math.Round(pill.ActualWidth * scale), ph = (int)Math.Round(pill.ActualHeight * scale);
                    if (ground != "wallpaper")
                    {
                        var expected = ground == "white" ? (byte)255 : (byte)0;
                        foreach (var (cx, cy) in new[] { (px - 3, py + ph / 2), (px + pw + 2, py + ph / 2), (px + pw / 2, py - 3), (px + pw / 2, py + ph + 2), (px - 3, py - 3), (px + pw + 2, py + ph + 2) })
                            pixels[(cy * w + cx) * 4 + 1].ShouldBe(expected, $"{ground}: the screen just outside the capsule at {cx}, {cy} is the window behind it");
                    }
                }
                finally
                {
                    overlay?.Close();
                    behind.Close();
                    LiquidGlassSources.ExcludeFromCapture = true;
                    LiquidGlassSources.Override = null;
                    UiHarness.NoCapture();
                }
                return 0;
            }));
        }
    }

    /// <summary><paramref name="source"/> scaled up <paramref name="times"/> times, each pixel a square.</summary>
    private static BitmapSource Nearest(BitmapSource source, int times)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = converted.PixelWidth, h = converted.PixelHeight;
        var from = new byte[w * h * 4];
        converted.CopyPixels(from, w * 4, 0);
        var to = new byte[w * times * h * times * 4];
        for (var y = 0; y < h * times; y++)
        {
            for (var x = 0; x < w * times; x++)
            {
                Array.Copy(from, ((y / times) * w + x / times) * 4, to, (y * w * times + x) * 4, 4);
                to[(y * w * times + x) * 4 + 3] = 255;
            }
        }
        return BitmapSource.Create(w * times, h * times, 96, 96, PixelFormats.Bgra32, null, to, w * times * 4);
    }
}
