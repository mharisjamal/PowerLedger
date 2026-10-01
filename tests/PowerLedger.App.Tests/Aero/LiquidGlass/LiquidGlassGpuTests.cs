using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerLedger.App.Aero;
using Shouldly;
using Xunit.Abstractions;

namespace PowerLedger.App.Tests;

/// <summary>
/// The GPU path on the real screen: a plain window (the "desk", captured like any other) shows the reference page's
/// test picture, pixel for pixel, and a window of ours holds a piece of glass over exactly the box Chromium drew its
/// glass in. The piece's pixels, read back from the GPU, are compared with Chromium's; and the path's frame keeping is
/// checked: nothing drawn at rest, and nothing while hidden.
/// </summary>
public class LiquidGlassGpuTests(ITestOutputHelper output)
{
    private sealed class Scene(Window desk, Window window, LiquidGlassBackdrop glass) : IDisposable
    {
        public Window Desk => desk;

        public Window Window => window;

        public LiquidGlassBackdrop Glass => glass;

        public WindowGlassSource Source => LiquidGlassSources.Live.OfType<WindowGlassSource>().Single();

        public void Dispose()
        {
            window.Close();
            desk.Close();
        }
    }

    /// <summary>The desk showing <paramref name="picture"/> at device pixel (<paramref name="x"/>, <paramref name="y"/>),
    /// and a piece of glass over it, <paramref name="width"/> by <paramref name="height"/> device pixels, square cornered.</summary>
    private static Scene Show(BitmapSource picture, int x, int y, int width, int height)
    {
        LiquidGlassSources.Override = null;
        LiquidGlassSources.ExcludeFromCapture = true;
        LiquidGlassSources.AllowScreenshots = false;
        LiquidGlassSources.GpuAllowed = true;
        var image = new Image { Source = picture, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        var desk = ScreenCapture.Show(new Border { Background = Brushes.Black, Child = image }, 0, 0, 10, 10);
        var dpi = VisualTreeHelper.GetDpi(desk).DpiScaleX;
        (desk.Left, desk.Top, desk.Width, desk.Height) = (x / dpi, y / dpi, picture.PixelWidth / dpi, picture.PixelHeight / dpi);
        (image.Width, image.Height) = (picture.PixelWidth / dpi, picture.PixelHeight / dpi);
        var glass = new LiquidGlassBackdrop { CornerRadius = new CornerRadius(0), Width = width / dpi, Height = height / dpi, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var window = ScreenCapture.Show(new Grid { Background = Brushes.Magenta, Children = { glass } }, x / dpi, y / dpi, width / dpi, height / dpi);
        return new Scene(desk, window, glass);
    }

    private static GpuGlassWindow Gpu(Scene scene) => scene.Source.Gpu ?? throw new InvalidOperationException("The window isn't on the GPU path.");

    [Fact]
    public void On_the_gpu_the_recipe_draws_as_chromium_does()
    {
        var result = UiHarness.OnUi(() => ScreenCapture.Aware(() =>
        {
            var probe = new Window();
            var scale = 1.0;
            ScreenCapture.Aware(() =>
            {
                probe = ScreenCapture.Show(new Grid(), 0, 0, 10, 10);
                scale = VisualTreeHelper.GetDpi(probe).DpiScaleX;
                probe.Close();
                return 0;
            });
            var match = ChromiumReference.Cases.FirstOrDefault(c => Math.Abs(c.Scale - scale) < 0.01 && System.IO.File.Exists(
                System.IO.Path.Combine(AppContext.BaseDirectory, "Aero", "LiquidGlass", "Reference", $"{c.Name}-full.png")));
            if (match.Name == null) return ($"no Chromium case at display scale {scale}", 1.0, 1.0);
            var box = ChromiumReference.DeviceBox(match.Name);
            var picture = FakeGlassSource.Picture(box.Width, box.Height, (px, py) => ChromiumReference.Picture(box.X + px, box.Y + py, scale));
            using var scene = Show(picture, 300, 200, box.Width, box.Height);
            UiHarness.PumpUntil(() => scene.Glass.OnGpu && Gpu(scene).Frames > 0, TimeSpan.FromSeconds(20), "a frame on the GPU path");
            UiHarness.Pump(TimeSpan.FromMilliseconds(300));
            var read = Gpu(scene).ReadAsync(scene.Glass);
            UiHarness.PumpUntil(() => read.IsCompleted, TimeSpan.FromSeconds(10), "the read back");
            var drawn = read.Result;
            var (chromium, w, h) = ChromiumReference.Load(match.Name, "full");
            drawn.Length.ShouldBe(w * h * 4);
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
            return ($"{match.Name}: {w}x{h} at {scale} on the GPU: channels equal {Share(0):P2}, within 1 {Share(1):P2}, within 2 {Share(2):P2}, within 8 {Share(8):P2}, mean {errors.Average():F3}, max {errors.Max()}",
                Share(1), Share(8));
        }));
        output.WriteLine(result.Item1);
        result.Item2.ShouldBeGreaterThan(0.98);
        result.Item3.ShouldBeGreaterThan(0.995);
    }

    [Fact]
    public void On_the_gpu_nothing_is_drawn_at_rest_or_while_hidden_and_a_change_behind_is()
        => UiHarness.OnUi(() => ScreenCapture.Aware(() =>
        {
            var picture = FakeGlassSource.Picture(400, 300, (x, y) => ((byte)x, (byte)y, 90));
            using var scene = Show(picture, 300, 200, 300, 200);
            UiHarness.PumpUntil(() => scene.Glass.OnGpu && Gpu(scene).Frames > 0, TimeSpan.FromSeconds(20), "a frame on the GPU path");
            UiHarness.Pump(TimeSpan.FromMilliseconds(500));
            var gpu = Gpu(scene);
            var before = gpu.Frames;
            UiHarness.Pump(TimeSpan.FromSeconds(2));
            var atRest = gpu.Frames - before;
            // Something behind changes: drawn.
            ((Border)scene.Desk.Content).Child = new Border { Background = Brushes.OrangeRed };
            UiHarness.PumpUntil(() => gpu.Frames > before + atRest, TimeSpan.FromSeconds(5), "the change behind to be drawn");
            // Our own window repainting over the glass: compared away, not drawn.
            var settled = gpu.Frames;
            var grid = (Grid)scene.Window.Content;
            var flicker = new Border { Background = Brushes.Lime, Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            grid.Children.Add(flicker);
            for (var i = 0; i < 10; i++)
            {
                flicker.Background = i % 2 == 0 ? Brushes.Lime : Brushes.Blue;
                UiHarness.Pump(TimeSpan.FromMilliseconds(100));
            }
            var ownRepaints = gpu.Frames - settled;
            // Hidden: the duplication is let go, and nothing is drawn.
            var session = gpu.Session;
            scene.Window.Hide();
            UiHarness.PumpUntil(() => !session.Running, TimeSpan.FromSeconds(5), "the capture to stop");
            var frames = session.FramesAcquired;
            ((Border)scene.Desk.Content).Child = new Border { Background = Brushes.Navy };
            UiHarness.Pump(TimeSpan.FromSeconds(1));
            var hidden = session.FramesAcquired - frames;
            output.WriteLine($"frames at rest over 2 s: {atRest}; while our window repainted 10 times: {ownRepaints}; taken while hidden over 1 s: {hidden}");
            atRest.ShouldBe(0);
            ownRepaints.ShouldBe(0);
            hidden.ShouldBe(0);
            return 0;
        }));
}
