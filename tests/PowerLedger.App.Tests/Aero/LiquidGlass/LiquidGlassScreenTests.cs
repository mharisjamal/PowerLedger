using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PowerLedger.App.Aero;
using Shouldly;
using Xunit.Abstractions;

namespace PowerLedger.App.Tests;

/// <summary>The liquid glass as the app draws it, on the real screen through WPF's hardware renderer, against Chromium.</summary>
public class LiquidGlassScreenTests(ITestOutputHelper output)
{
    [Fact]
    public void On_screen_the_recipe_draws_as_chromium_does()
    {
        var result = UiHarness.OnUi(() => ScreenCapture.Aware(() =>
        {
            var glass = new LiquidGlassBackdrop();
            var fake = new FakeGlassSource(null, Rect.Empty);
            LiquidGlassSources.Override = _ => fake;
            var root = new Grid { Background = Brushes.Black };
            root.Children.Add(glass);
            var window = ScreenCapture.Show(root, 200, 200, 100, 100);
            try
            {
                var scale = VisualTreeHelper.GetDpi(glass).DpiScaleX;
                var match = ChromiumReference.Cases.FirstOrDefault(c => Math.Abs(c.Scale - scale) < 0.01);
                if (match.Name == null) return ($"no Chromium case at display scale {scale}", 0.0, 0.0);
                var box = ChromiumReference.DeviceBox(match.Name);
                // Chromium snaps each edge to the device grid; the box's device size is what matters here.
                glass.Width = box.Width / scale;
                glass.Height = box.Height / scale;
                glass.HorizontalAlignment = HorizontalAlignment.Left;
                glass.VerticalAlignment = VerticalAlignment.Top;
                window.Width = match.Width + 40;
                window.Height = match.Height + 40;
                try
                {
                    UiHarness.PumpUntil(() => glass.ScreenBox() is { Width: > 0 } b && (int)b.Width == box.Width, TimeSpan.FromSeconds(10), "the glass's size");
                }
                catch (TimeoutException)
                {
                    throw new TimeoutException($"scale {scale}, case {match.Name}, box {box}, glass {glass.ActualWidth}x{glass.ActualHeight}, screen {glass.ScreenBox()}, window dpi {VisualTreeHelper.GetDpi(window).DpiScaleX}");
                }
                var screen = glass.ScreenBox()!.Value;
                fake.Image = FakeGlassSource.Picture(box.Width, box.Height, (x, y) => ChromiumReference.Picture(box.X + x, box.Y + y, scale));
                fake.ScreenBounds = screen;
                fake.Raise();
                UiHarness.PumpUntil(() => glass.IsReady, TimeSpan.FromSeconds(20), "the source map");
                UiHarness.Pump(TimeSpan.FromMilliseconds(400));
                var drawn = ScreenCapture.Grab((int)screen.X, (int)screen.Y, box.Width, box.Height);
                var (chromium, w, h) = ChromiumReference.Load(match.Name, "full");
                var corner = (int)Math.Ceiling(LiquidGlassRecipe.CornerRadius * scale);
                var errors = new List<int>();
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        if ((x < corner || x >= w - corner) && (y < corner || y >= h - corner)) continue;
                        for (var c = 0; c < 3; c++) errors.Add(Math.Abs(drawn[(y * w + x) * 4 + c] - chromium[(y * w + x) * 4 + c]));
                    }
                }
                double Share(int within) => errors.Count(e => e <= within) / (double)errors.Count;
                return ($"{match.Name}: {w}x{h} at {scale} on screen: channels equal {Share(0):P2}, within 1 {Share(1):P2}, within 2 {Share(2):P2}, within 8 {Share(8):P2}, mean {errors.Average():F3}, max {errors.Max()}",
                    Share(1), Share(8));
            }
            finally
            {
                window.Close();
                LiquidGlassSources.Override = null;
            }
        }));
        output.WriteLine(result.Item1);
        if (result.Item2 == 0 && result.Item3 == 0) return;
        result.Item2.ShouldBeGreaterThan(0.95);
        result.Item3.ShouldBeGreaterThan(0.995);
    }
}
