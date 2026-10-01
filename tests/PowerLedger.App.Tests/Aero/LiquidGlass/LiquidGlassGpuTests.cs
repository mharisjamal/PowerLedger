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

        public WindowGlassSource Source => LiquidGlassSources.Live.OfType<WindowGlassSource>().First(s => s.Gpu?.Composes(glass) ?? false || s.Gpu?.TargetOf(glass) != null || s.Subscriber != null || s.Gpu != null);

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
            scene.Glass.Composed.ShouldBeTrue("a plain window's piece is composed beneath its content (DirectComposition)");
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

    /// <summary>
    /// The mockup's Energy pane over the mockup's own background (a white window in it, which Edge bends into white
    /// blotches inside the pane), with the mockup's glass bars inside it: on the GPU the pane draws as Edge does, and the
    /// bars, a piece inside another, read nothing of the screen (Chromium's backdrop root: Edge draws the pane with its
    /// bars pixel for pixel as the pane alone).
    /// </summary>
    [Fact]
    public void On_the_gpu_a_pane_over_the_mockups_background_draws_as_edge_does_and_its_bars_draw_nothing()
    {
        var result = UiHarness.OnUi(() => ScreenCapture.Aware(() =>
        {
            var probe = ScreenCapture.Show(new Grid(), 0, 0, 10, 10);
            var scale = VisualTreeHelper.GetDpi(probe).DpiScaleX;
            probe.Close();
            var file = $"dash-{scale:0.##}";
            if (!System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "Aero", "LiquidGlass", "Reference", $"{file}-full.png")))
                return ($"no Edge shot at display scale {scale}", 1.0, 1.0);
            var (background, w, h) = ChromiumReference.LoadFile($"{file}-bg");
            using var scene = Show(ChromiumReference.Bitmap(background, w, h), 300, 200, w, h);
            scene.Glass.CornerRadius = new CornerRadius(LiquidGlassRecipe.CornerRadius);
            var bars = new List<LiquidGlassBackdrop>();
            var content = new Canvas();
            int[] heights = [96, 136, 115, 186, 149, 215, 170, 126, 200, 232, 180, 255, 224, 300];
            for (var i = 0; i < heights.Length; i++)
            {
                var bar = new LiquidGlassBackdrop { CornerRadius = new CornerRadius(14), Width = 40, Height = heights[i] };
                var element = new Grid { Children = { bar } };
                Canvas.SetLeft(element, 42 + i * 49);
                Canvas.SetTop(element, 494 - 60 - heights[i]);
                content.Children.Add(element);
                bars.Add(bar);
            }
            // Inside the pane's element (the Grid the pane's backdrop is the bottom layer of), over the backdrop.
            ((Grid)scene.Window.Content).Children.Add(new Border { Child = content });
            UiHarness.PumpUntil(() => scene.Glass.Composed && Gpu(scene).Frames > 0, TimeSpan.FromSeconds(20), "a frame on the GPU path");
            UiHarness.Pump(TimeSpan.FromMilliseconds(300));
            foreach (var bar in bars)
            {
                bar.IsNested.ShouldBeTrue();
                bar.OnGpu.ShouldBeFalse();
                Gpu(scene).OrderOf(bar).ShouldBeNull();
                bar.Kind.ShouldBe(LiquidGlassSourceKind.Inside);
            }
            // At rest a piece inside another draws nothing again; a change of the content beneath it does.
            var rested = InsideGlassSource.Drawn;
            UiHarness.Pump(TimeSpan.FromSeconds(1));
            InsideGlassSource.Drawn.ShouldBe(rested);
            var under = new Border { Width = 40, Height = 40, Background = Brushes.OrangeRed };
            Canvas.SetLeft(under, 42);
            Canvas.SetTop(under, 494 - 60 - 40);
            content.Children.Insert(0, under);
            UiHarness.PumpUntil(() => InsideGlassSource.Drawn > rested, TimeSpan.FromSeconds(5), "the bar over the new content drawn again");
            UiHarness.Pump(TimeSpan.FromMilliseconds(500));
            var again = InsideGlassSource.Drawn;
            under.Background = Brushes.SteelBlue;   // no layout: a new colour alone
            UiHarness.Pump(TimeSpan.FromMilliseconds(500));
            output.WriteLine($"a colour changed without a layout pass: drawn again {InsideGlassSource.Drawn - again} times");
            (InsideGlassSource.Drawn - again).ShouldBeLessThanOrEqualTo(2);   // the two bars its margin reaches, once each
            var read = Gpu(scene).ReadAsync(scene.Glass);
            UiHarness.PumpUntil(() => read.IsCompleted, TimeSpan.FromSeconds(10), "the read back");
            var drawn = read.Result;
            var (edge, _, _) = ChromiumReference.LoadFile($"{file}-full");
            drawn.Length.ShouldBe(w * h * 4);
            System.IO.Directory.CreateDirectory(UiHarness.Folder);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(ChromiumReference.Bitmap(drawn, w, h)));
            using (var png = System.IO.File.Create(System.IO.Path.Combine(UiHarness.Folder, $"liquid-glass-gpu-{file}.png"))) encoder.Save(png);
            var corner = (int)Math.Ceiling(LiquidGlassRecipe.CornerRadius * scale);
            var errors = new List<int>();
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    if ((x < corner || x >= w - corner) && (y < corner || y >= h - corner)) continue;
                    for (var c = 0; c < 3; c++) errors.Add(Math.Abs(drawn[(y * w + x) * 4 + c] - edge[(y * w + x) * 4 + c]));
                }
            }
            double Share(int within) => errors.Count(e => e <= within) / (double)errors.Count;
            return ($"Energy pane {w}x{h} at {scale} on the GPU: channels equal {Share(0):P2}, within 1 {Share(1):P2}, within 2 {Share(2):P2}, within 8 {Share(8):P2}, mean {errors.Average():F3}, max {errors.Max()}",
                Share(1), Share(8));
        }));
        output.WriteLine(result.Item1);
        result.Item2.ShouldBeGreaterThan(0.98);
        result.Item3.ShouldBeGreaterThan(0.995);
    }

    [Fact]
    public void Every_piece_is_composed_beneath_its_window_in_tree_order_a_layered_window_too()
        => UiHarness.OnUi(() => ScreenCapture.Aware(() =>
        {
            var picture = FakeGlassSource.Picture(400, 300, (x, y) => ((byte)x, (byte)y, 90));
            using var scene = Show(picture, 300, 200, 300, 200);
            // A dialog's glass over the page's: composed too, drawn after it.
            var over = new LiquidGlassBackdrop { CornerRadius = new CornerRadius(0), Width = 60, Height = 40, Margin = new Thickness(20), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            ((Grid)scene.Window.Content).Children.Add(over);
            // The watts overlay's kind of window: layered.
            var pillGlass = new LiquidGlassBackdrop { CornerRadius = new CornerRadius(22) };
            var pill = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowActivated = false,
                ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.Manual, Left = 200, Top = 400, Width = 160, Height = 44,
                Content = pillGlass,
            };
            pill.Show();
            try
            {
                UiHarness.PumpUntil(() => scene.Glass.Composed && over.Composed && pillGlass.Composed, TimeSpan.FromSeconds(20), "every piece composed");
                var gpu = Gpu(scene);
                gpu.OrderOf(over)!.Value.ShouldBeGreaterThan(gpu.OrderOf(scene.Glass)!.Value);
                // Composed, a piece draws nothing in WPF: no D3DImage is held.
                over.ShowsGpu.ShouldBeFalse();
                pillGlass.ShowsGpu.ShouldBeFalse();
                var pillSource = LiquidGlassSources.Live.OfType<WindowGlassSource>().Single(s => s.Gpu != null && s.Gpu != gpu);
                UiHarness.PumpUntil(() => pillSource.Gpu!.Frames > 0, TimeSpan.FromSeconds(10), "the pill's first frame");
                // Each window's glass lies in its own window directly beneath it, left out of capture as it is.
                foreach (var (owner, glassWindow) in new[] { (scene.Window, gpu), (pill, pillSource.Gpu!) })
                {
                    var hwnd = new WindowInteropHelper(owner).Handle;
                    glassWindow.CompanionHwnd.ShouldNotBe(IntPtr.Zero);
                    GetWindow(glassWindow.CompanionHwnd, 3).ShouldBe(hwnd);   // GW_HWNDPREV: the window just above
                    GetWindowDisplayAffinity(glassWindow.CompanionHwnd, out var affinity).ShouldBeTrue();
                    affinity.ShouldBe(0x11u);   // WDA_EXCLUDEFROMCAPTURE
                }
                // Screenshots allowed: no live glass, so no glass window left behind.
                var companion = gpu.CompanionHwnd;
                LiquidGlassSources.AllowScreenshots = true;
                UiHarness.Pump(TimeSpan.FromMilliseconds(200));
                IsWindow(companion).ShouldBeFalse();
                scene.Glass.Kind.ShouldBe(LiquidGlassSourceKind.Wallpaper);
            }
            finally
            {
                LiquidGlassSources.AllowScreenshots = false;
                pill.Close();
            }
            return 0;
        }));

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

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

    /// <summary>The watts overlay's pill changes width with its reading and goes back to widths it had: a piece keeps the
    /// source maps of its last few sizes, so going back makes none.</summary>
    [Fact]
    public void A_piece_back_at_a_size_it_had_makes_no_new_map()
        => UiHarness.OnUi(() => ScreenCapture.Aware(() =>
        {
            var picture = FakeGlassSource.Picture(400, 300, (x, y) => ((byte)x, (byte)y, 90));
            using var scene = Show(picture, 300, 200, 300, 200);
            UiHarness.PumpUntil(() => scene.Glass.OnGpu && Gpu(scene).Frames > 0, TimeSpan.FromSeconds(20), "a frame on the GPU path");
            var wide = scene.Glass.Width;
            var made = GpuGlassWindow.MapsMade;
            scene.Glass.Width = wide - 20;
            UiHarness.PumpUntil(() => GpuGlassWindow.MapsMade == made + 1 && Gpu(scene).State.Contains("with maps 1"), TimeSpan.FromSeconds(10), "the narrower map");
            UiHarness.Pump(TimeSpan.FromMilliseconds(300));
            scene.Glass.Width = wide;
            UiHarness.Pump(TimeSpan.FromMilliseconds(300));
            scene.Glass.Width = wide - 20;
            UiHarness.Pump(TimeSpan.FromMilliseconds(300));
            GpuGlassWindow.MapsMade.ShouldBe(made + 1);
            return 0;
        }));
}
