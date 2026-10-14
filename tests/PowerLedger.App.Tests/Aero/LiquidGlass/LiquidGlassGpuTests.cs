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

    /// <summary>
    /// The owner's bare desktop: nothing behind the glass changes, and the window shows again while the pointer moves.
    /// Desktop duplication's first frame after it opens is the pointer's alone, its picture empty (all zeros); a frame
    /// that only moves the pointer has no picture either. The glass must show the still desktop all the same, at once.
    /// </summary>
    [Fact]
    public void On_the_gpu_a_still_desktop_shows_when_the_window_shows_again_with_the_pointer_moving()
        => UiHarness.OnUi(() => ScreenCapture.Aware(() =>
        {
            LiquidGlassSources.Override = null;
            LiquidGlassSources.ExcludeFromCapture = true;
            LiquidGlassSources.AllowScreenshots = false;
            LiquidGlassSources.GpuAllowed = true;
            // The desktop, perfectly still: one colour over the whole monitor, over everything else on it.
            var desk = ScreenCapture.Show(new Border { Background = new SolidColorBrush(Color.FromRgb(46, 139, 87)) }, 0, 0, 10, 10);
            desk.Topmost = true;
            (desk.Left, desk.Top, desk.Width, desk.Height) = (SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
            var glass = new LiquidGlassBackdrop { CornerRadius = new CornerRadius(0), Width = 240, Height = 160, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            var window = ScreenCapture.Show(new Grid { Background = Brushes.Magenta, Children = { glass } }, 300, 200, 240, 160);
            window.Topmost = true;
            try
            {
                UiHarness.PumpUntil(() => glass.Composed && Gpu(glass).Frames > 0, TimeSpan.FromSeconds(20), "a frame on the GPU path");
                var session = Gpu(glass).Session;
                window.Hide();
                UiHarness.PumpUntil(() => !session.Running, TimeSpan.FromSeconds(5), "the duplication let go");
                // The desktop changes while the glass is away (the wallpaper changed, the windows went down), then stays still.
                ((Border)desk.Content).Background = new SolidColorBrush(Color.FromRgb(70, 130, 180));
                UiHarness.Pump(TimeSpan.FromMilliseconds(500));
                window.Show();
                // The pointer moving as the window comes back: frames with the pointer and no picture.
                GetCursorPos(out var at);
                for (var i = 0; i < 30; i++)
                {
                    SetCursorPos(900 + (i % 2) * 40, 700);
                    UiHarness.Pump(TimeSpan.FromMilliseconds(10));
                }
                SetCursorPos(at.X, at.Y);
                UiHarness.Pump(TimeSpan.FromMilliseconds(800));
                var read = Gpu(glass).ReadAsync(glass);
                UiHarness.PumpUntil(() => read.IsCompleted, TimeSpan.FromSeconds(10), "the read back");
                var pixels = read.Result;
                var shown = 0;
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    // The new colour brightened by 1.1: (77, 143, 198), within the 8 bit linearRGB trip's steps.
                    if (pixels[i + 3] == 255 && Math.Abs(pixels[i + 2] - 77) <= 3 && Math.Abs(pixels[i + 1] - 143) <= 3 && Math.Abs(pixels[i] - 198) <= 3) shown++;
                }
                output.WriteLine($"pixels showing the still desktop: {shown} of {pixels.Length / 4}");
                (shown / (double)(pixels.Length / 4)).ShouldBeGreaterThan(0.99);
            }
            finally
            {
                window.Close();
                desk.Close();
            }
            return 0;
        }));

    /// <summary>The owner's bare desktop as the app starts: nothing behind the glass changes, and the pointer moves as
    /// the window comes up (the glass's first half second copies the whole desktop on every frame).</summary>
    [Fact]
    public void On_the_gpu_a_still_desktop_shows_when_the_window_first_shows_with_the_pointer_moving()
        => UiHarness.OnUi(() => ScreenCapture.Aware(() =>
        {
            LiquidGlassSources.Override = null;
            LiquidGlassSources.ExcludeFromCapture = true;
            LiquidGlassSources.AllowScreenshots = false;
            LiquidGlassSources.GpuAllowed = true;
            var desk = ScreenCapture.Show(new Border { Background = new SolidColorBrush(Color.FromRgb(70, 130, 180)) }, 0, 0, 10, 10);
            desk.Topmost = true;
            (desk.Left, desk.Top, desk.Width, desk.Height) = (SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
            UiHarness.Pump(TimeSpan.FromMilliseconds(500));
            GetCursorPos(out var at);
            var glass = new LiquidGlassBackdrop { CornerRadius = new CornerRadius(0), Width = 240, Height = 160, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            var window = ScreenCapture.Show(new Grid { Background = Brushes.Magenta, Children = { glass } }, 300, 200, 240, 160);
            window.Topmost = true;
            try
            {
                for (var i = 0; i < 80; i++)
                {
                    SetCursorPos(900 + (i % 2) * 40, 700);
                    UiHarness.Pump(TimeSpan.FromMilliseconds(10));
                }
                SetCursorPos(at.X, at.Y);
                UiHarness.Pump(TimeSpan.FromMilliseconds(800));
                var read = Gpu(glass).ReadAsync(glass);
                UiHarness.PumpUntil(() => read.IsCompleted, TimeSpan.FromSeconds(10), "the read back");
                var pixels = read.Result;
                var shown = 0;
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    if (pixels[i + 3] == 255 && Math.Abs(pixels[i + 2] - 77) <= 3 && Math.Abs(pixels[i + 1] - 143) <= 3 && Math.Abs(pixels[i] - 198) <= 3) shown++;
                }
                output.WriteLine($"pixels showing the still desktop: {shown} of {pixels.Length / 4}");
                (shown / (double)(pixels.Length / 4)).ShouldBeGreaterThan(0.99);
            }
            finally
            {
                window.Close();
                desk.Close();
            }
            return 0;
        }));

    /// <summary>
    /// Another window put between a window and its glass (Win+D raises the desktop over windows it can't minimise, as the
    /// glass's own window is; the shell, a launcher, a stray SetWindowPos) while the window itself doesn't move: the glass
    /// goes straight back directly beneath its window, or it would stay hidden behind the other.
    /// </summary>
    [Fact]
    public void The_glass_goes_back_beneath_its_window_when_another_slips_between_them()
        => UiHarness.OnUi(() => ScreenCapture.Aware(() =>
        {
            var picture = FakeGlassSource.Picture(400, 300, (x, y) => ((byte)x, (byte)y, 90));
            using var scene = Show(picture, 300, 200, 300, 200);
            UiHarness.PumpUntil(() => scene.Glass.Composed && Gpu(scene).Frames > 0, TimeSpan.FromSeconds(20), "a frame on the GPU path");
            var owner = new WindowInteropHelper(scene.Window).Handle;
            var companion = Gpu(scene).CompanionHwnd;
            GetWindow(companion, 3).ShouldBe(owner);
            var between = ScreenCapture.Show(new Border { Background = Brushes.Navy }, 0, 0, 10, 10);
            try
            {
                var hwnd = new WindowInteropHelper(between).Handle;
                SetWindowPos(hwnd, owner, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // just under the window: over its glass
                UiHarness.PumpUntil(() => GetWindow(companion, 3) == owner, TimeSpan.FromSeconds(2), "the glass back beneath its window");
            }
            finally
            {
                between.Close();
            }
            return 0;
        }));

    /// <summary>
    /// The owner's Win+M: every window minimised, then the app restored as the taskbar restores it (or shown again from
    /// the tray), the other windows left down, so only the still desktop is behind it. The glass's window must come back
    /// with it, shown, directly beneath it, and the glass must show the desktop as it is now, not the window that was
    /// behind before; all with nothing moving.
    /// </summary>
    [Theory]
    [InlineData("taskbar")]
    [InlineData("tray")]
    public void After_minimise_all_the_glass_comes_back_with_its_window_over_the_still_desktop(string path)
        => UiHarness.OnUi(() => ScreenCapture.Aware(() =>
        {
            LiquidGlassSources.Override = null;
            LiquidGlassSources.ExcludeFromCapture = true;
            LiquidGlassSources.AllowScreenshots = false;
            LiquidGlassSources.GpuAllowed = true;
            // The desktop (still, steel blue), another app's window over it (orange), and ours over both.
            var desk = ScreenCapture.Show(new Border { Background = new SolidColorBrush(Color.FromRgb(70, 130, 180)) }, 0, 0, 10, 10);
            (desk.Left, desk.Top, desk.Width, desk.Height) = (SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
            var other = ScreenCapture.Show(new Border { Background = Brushes.DarkOrange }, 200 / 1.0, 150 / 1.0, 600, 400);
            var glass = new LiquidGlassBackdrop { CornerRadius = new CornerRadius(0), Width = 240, Height = 160, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            var window = ScreenCapture.Show(new Grid { Background = Brushes.Magenta, Children = { glass } }, 300, 200, 240, 160);
            try
            {
                UiHarness.PumpUntil(() => glass.Composed && Gpu(glass).Frames > 0, TimeSpan.FromSeconds(20), "a frame on the GPU path");
                UiHarness.Pump(TimeSpan.FromMilliseconds(500));
                var owner = new WindowInteropHelper(window).Handle;
                var companion = Gpu(glass).CompanionHwnd;
                // Win+M: everything down. (Or the app to the tray, the other window down.)
                if (path == "taskbar") ShowWindow(owner, 6);   // SW_MINIMIZE
                else window.Hide();
                ShowWindow(new WindowInteropHelper(other).Handle, 6);
                UiHarness.Pump(TimeSpan.FromMilliseconds(800));
                IsWindowVisible(companion).ShouldBeFalse("the glass goes away with its window");
                var log = new List<string>();
                LiquidGlassSources.Log = log.Add;
                // Back: the taskbar's restore (or the tray's show), the other window left down.
                if (path == "taskbar")
                {
                    ShowWindow(owner, 9);   // SW_RESTORE
                    SetForegroundWindow(owner);
                }
                else
                {
                    window.Show();
                }
                UiHarness.Pump(TimeSpan.FromMilliseconds(1000));
                companion = Gpu(glass).CompanionHwnd;
                IsWindowVisible(companion).ShouldBeTrue("the glass back with its window");
                GetWindow(companion, 3).ShouldBe(owner, "directly beneath its window");
                var read = Gpu(glass).ReadAsync(glass);
                UiHarness.PumpUntil(() => read.IsCompleted, TimeSpan.FromSeconds(10), "the read back");
                var pixels = read.Result;
                var shown = 0;
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    // The still desktop brightened by 1.1: (77, 143, 198), not the orange window that was there before.
                    if (pixels[i + 3] == 255 && Math.Abs(pixels[i + 2] - 77) <= 3 && Math.Abs(pixels[i + 1] - 143) <= 3 && Math.Abs(pixels[i] - 198) <= 3) shown++;
                }
                output.WriteLine($"{path}: pixels showing the still desktop {shown} of {pixels.Length / 4}");
                (shown / (double)(pixels.Length / 4)).ShouldBeGreaterThan(0.99);
                // A line for the App's log on what the glass did as its window came back.
                UiHarness.PumpUntil(() => log.Count > 0, TimeSpan.FromSeconds(3), "the glass's line");
                output.WriteLine(log[0]);
                log[0].ShouldContain("shown, beneath its window");
            }
            finally
            {
                LiquidGlassSources.Log = null;
                window.Close();
                other.Close();
                desk.Close();
            }
            return 0;
        }));

    /// <summary>
    /// The owner's Win+M with the app itself: the Aero window and another window minimised, the Aero window restored
    /// as the taskbar restores it (or hidden to the tray and shown again, its reveal and all), only the still desktop
    /// behind it. Every composed piece must show the desktop as it is now, and the glass's window must be back beneath it.
    /// </summary>
    [Theory]
    [InlineData("taskbar")]
    [InlineData("tray")]
    public void After_minimise_all_the_aero_window_s_glass_shows_the_still_desktop(string path)
        => UiHarness.OnUi(() => ScreenCapture.Aware(() =>
        {
            LiquidGlassSources.ExcludeFromCapture = true;
            LiquidGlassSources.AllowScreenshots = false;
            LiquidGlassSources.GpuAllowed = true;
            var desk = ScreenCapture.Show(new Border { Background = new SolidColorBrush(Color.FromRgb(70, 130, 180)) }, 0, 0, 10, 10);
            (desk.Left, desk.Top, desk.Width, desk.Height) = (SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
            var other = ScreenCapture.Show(new Border { Background = Brushes.DarkOrange }, 20, 20, 1200, 760);
            using var saver = new FakeSaver();
            var window = AeroHost.Window(AeroFixtures.Shell(saver), Theme.Dark);
            LiquidGlassSources.Override = null;
            (window.Left, window.Top, window.Width, window.Height, window.Topmost) = (60, 40, 1100, 700, true);
            window.Show();
            try
            {
                UiHarness.Pump(TimeSpan.FromSeconds(4));   // the reveal, and the glass over the other window
                var owner = new WindowInteropHelper(window).Handle;
                if (path == "taskbar") ShowWindow(owner, 6);   // SW_MINIMIZE
                else window.Hide();
                ShowWindow(new WindowInteropHelper(other).Handle, 6);
                UiHarness.Pump(TimeSpan.FromMilliseconds(800));
                if (path == "taskbar")
                {
                    ShowWindow(owner, 9);   // SW_RESTORE
                    SetForegroundWindow(owner);
                }
                else
                {
                    window.Show();
                }
                UiHarness.Pump(TimeSpan.FromSeconds(3));   // the reveal again (from the tray), and the glass's frames
                var gpu = LiquidGlassSources.Live.OfType<WindowGlassSource>().Select(s => s.Gpu).Single(g => g != null)!;
                IsWindowVisible(gpu.CompanionHwnd).ShouldBeTrue("the glass back with its window");
                GetWindow(gpu.CompanionHwnd, 3).ShouldBe(owner, "directly beneath its window");
                var pieces = new List<LiquidGlassBackdrop>();
                void Walk(DependencyObject node)
                {
                    if (node is LiquidGlassBackdrop { Composed: true } piece) pieces.Add(piece);
                    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Walk(VisualTreeHelper.GetChild(node, i));
                }
                Walk(window);
                pieces.ShouldNotBeEmpty();
                var stale = new List<string>();
                foreach (var piece in pieces)
                {
                    var read = gpu.ReadAsync(piece);
                    UiHarness.PumpUntil(() => read.IsCompleted, TimeSpan.FromSeconds(10), "the read back");
                    var pixels = read.Result;
                    int inside = 0, shown = 0;
                    for (var i = 0; i < pixels.Length; i += 4)
                    {
                        if (pixels[i + 3] != 255) continue;   // the corners and what clips leave
                        inside++;
                        if (Math.Abs(pixels[i + 2] - 77) <= 3 && Math.Abs(pixels[i + 1] - 143) <= 3 && Math.Abs(pixels[i] - 198) <= 3) shown++;
                    }
                    if (inside > 0 && shown < inside * 0.99) stale.Add($"{piece.ActualWidth:0}x{piece.ActualHeight:0}: {shown} of {inside}");
                }
                output.WriteLine($"{path}: {pieces.Count} composed pieces, not showing the still desktop: {(stale.Count == 0 ? "none" : string.Join(", ", stale))}");
                stale.ShouldBeEmpty();
            }
            finally
            {
                window.CloseForSwitch();
                other.Close();
                desk.Close();
            }
            return 0;
        }));

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    private static GpuGlassWindow Gpu(LiquidGlassBackdrop glass)
        => LiquidGlassSources.Live.OfType<WindowGlassSource>().Select(s => s.Gpu).First(g => g != null && g.Composes(glass))!;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point point);

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

    /// <summary>
    /// Nothing is drawn at rest or for our own window's repainting, and a change behind is. Any other window repainting
    /// across the glass's box (on a working desktop: a terminal, a chat app) is drawn without a compare by design, as a
    /// video behind is: Windows' dirty rectangles include what our windows cover, and the 2026-10-01 runs saw them reach
    /// 23 to 1334 by 37 to 811 on every frame here. So the count that must stay at nothing is the frames no change
    /// outside our windows asked for (<see cref="GpuGlassWindow.ForeignFrames"/>).
    /// </summary>
    [Fact]
    public void On_the_gpu_nothing_is_drawn_at_rest_or_while_hidden_and_a_change_behind_is()
        => UiHarness.OnUi(() => ScreenCapture.Aware(() =>
        {
            var picture = FakeGlassSource.Picture(400, 300, (x, y) => ((byte)x, (byte)y, 90));
            using var scene = Show(picture, 300, 200, 300, 200);
            UiHarness.PumpUntil(() => scene.Glass.OnGpu && Gpu(scene).Frames > 0, TimeSpan.FromSeconds(20), "a frame on the GPU path");
            UiHarness.Pump(TimeSpan.FromMilliseconds(500));
            var gpu = Gpu(scene);
            long Own() => gpu.Frames - gpu.ForeignFrames;
            var (before, foreignBefore) = (Own(), gpu.ForeignFrames);
            UiHarness.Pump(TimeSpan.FromSeconds(2));
            var atRest = Own() - before;
            var foreign = gpu.ForeignFrames - foreignBefore;
            // Something behind changes (the desk, wholly under our window): drawn, as the glass read back shows (a frame
            // another window's change asked for draws it too, so the count alone can't tell).
            ((Border)scene.Desk.Content).Child = new Border { Background = Brushes.OrangeRed };
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var orange = false;
            while (!orange && watch.Elapsed < TimeSpan.FromSeconds(5))
            {
                var read = gpu.ReadAsync(scene.Glass);
                UiHarness.PumpUntil(() => read.IsCompleted, TimeSpan.FromSeconds(10), "the read back");
                var middle = read.Result.Length / 2 / 4 * 4;
                orange = read.Result[middle + 2] > 200 && read.Result[middle + 1] < 120 && read.Result[middle] < 60;   // BGRA: OrangeRed, brightened
                if (!orange) UiHarness.Pump(TimeSpan.FromMilliseconds(100));
            }
            orange.ShouldBeTrue("the change behind drawn");
            // Our own window repainting over the glass: compared away, not drawn.
            var settled = Own();
            var grid = (Grid)scene.Window.Content;
            var flicker = new Border { Background = Brushes.Lime, Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            grid.Children.Add(flicker);
            for (var i = 0; i < 10; i++)
            {
                flicker.Background = i % 2 == 0 ? Brushes.Lime : Brushes.Blue;
                UiHarness.Pump(TimeSpan.FromMilliseconds(100));
            }
            var ownRepaints = Own() - settled;
            // Hidden: the duplication is let go, and nothing is drawn.
            var session = gpu.Session;
            scene.Window.Hide();
            UiHarness.PumpUntil(() => !session.Running, TimeSpan.FromSeconds(5), "the capture to stop");
            var frames = session.FramesAcquired;
            ((Border)scene.Desk.Content).Child = new Border { Background = Brushes.Navy };
            UiHarness.Pump(TimeSpan.FromSeconds(1));
            var hidden = session.FramesAcquired - frames;
            output.WriteLine($"frames at rest over 2 s: {atRest} (and {foreign} for other windows' changes); while our window repainted 10 times: {ownRepaints}; taken while hidden over 1 s: {hidden}");
            atRest.ShouldBe(0);
            ownRepaints.ShouldBe(0);
            hidden.ShouldBe(0);
            return 0;
        }));

    /// <summary>
    /// A still desktop: the capture thread waits for Windows' next frame a quarter of a second at a time, not one display
    /// refresh at a time (perf review P8: 60 wakeups a second for nothing), and what the window asks of its glass meanwhile
    /// (here a read; a piece new, moved or changed) is served on the window's own thread at its next turn, not at the
    /// capture's next look.
    /// </summary>
    [Fact]
    public void At_rest_the_capture_waits_long_for_a_frame_and_a_want_is_served_at_once()
        => UiHarness.OnUi(() => ScreenCapture.Aware(() =>
        {
            var picture = FakeGlassSource.Picture(400, 300, (x, y) => ((byte)x, (byte)y, 90));
            using var scene = Show(picture, 300, 200, 300, 200);
            UiHarness.PumpUntil(() => scene.Glass.Composed && Gpu(scene).Frames > 0, TimeSpan.FromSeconds(20), "a frame on the GPU path");
            UiHarness.Pump(TimeSpan.FromMilliseconds(500));
            var session = Gpu(scene).Session;
            var (timeouts, watch) = (session.Timeouts, System.Diagnostics.Stopwatch.StartNew());
            UiHarness.Pump(TimeSpan.FromSeconds(2));
            var empty = session.Timeouts - timeouts;
            var seconds = watch.Elapsed.TotalSeconds;
            var read = Gpu(scene).ReadAsync(scene.Glass);
            UiHarness.Pump(TimeSpan.FromMilliseconds(1));
            output.WriteLine($"looks that found nothing over {seconds:0.0} s: {empty}; the read answered at the window's next turn: {read.IsCompleted}");
            empty.ShouldBeLessThanOrEqualTo((long)Math.Ceiling(seconds / MonitorCapture.IdleLook.TotalSeconds) + 1);
            read.IsCompleted.ShouldBeTrue();
            read.Result.Length.ShouldBe(300 * 200 * 4);
            return 0;
        }));

    /// <summary>A pane resized with its window, the overlay's pill following its reading, a snap: one move map serves a
    /// piece at every size (the mirror into the box is the shader's), so a new size makes no map and is drawn as soon as
    /// the layout pass that gave it is done.</summary>
    [Fact]
    public void A_resized_piece_makes_no_map_and_is_drawn_after_its_layout_pass()
        => UiHarness.OnUi(() => ScreenCapture.Aware(() =>
        {
            var picture = FakeGlassSource.Picture(400, 300, (x, y) => ((byte)x, (byte)y, 90));
            using var scene = Show(picture, 300, 200, 300, 200);
            UiHarness.PumpUntil(() => scene.Glass.OnGpu && Gpu(scene).Frames > 0, TimeSpan.FromSeconds(20), "a frame on the GPU path");
            UiHarness.Pump(TimeSpan.FromMilliseconds(300));
            var made = GpuGlassWindow.MapsMade;
            var wide = scene.Glass.Width;
            foreach (var width in new[] { wide - 20, wide - 47, wide - 3, wide })
            {
                var frames = Gpu(scene).Frames;
                scene.Glass.Width = width;
                scene.Window.UpdateLayout();
                // The layout pass done, the dispatcher's next turn draws it.
                UiHarness.Pump(TimeSpan.FromMilliseconds(1));
                Gpu(scene).Frames.ShouldBeGreaterThan(frames, $"drawn at {width}");
            }
            GpuGlassWindow.MapsMade.ShouldBe(made);
            return 0;
        }));

    /// <summary>A window moved (a drag, a snap) has its glass drawn at the new place before the move returns: from the
    /// desktop copy, with no wait for the capture's next frame, and before the glass's window follows it.</summary>
    [Fact]
    public void A_moved_window_s_glass_is_drawn_before_the_move_returns()
        => UiHarness.OnUi(() => ScreenCapture.Aware(() =>
        {
            var picture = FakeGlassSource.Picture(400, 300, (x, y) => ((byte)x, (byte)y, 90));
            using var scene = Show(picture, 300, 200, 300, 200);
            UiHarness.PumpUntil(() => scene.Glass.Composed && Gpu(scene).Frames > 0, TimeSpan.FromSeconds(20), "a frame on the GPU path");
            UiHarness.Pump(TimeSpan.FromMilliseconds(300));
            for (var i = 1; i <= 5; i++)
            {
                var frames = Gpu(scene).Frames;
                scene.Window.Left += 7 / VisualTreeHelper.GetDpi(scene.Window).DpiScaleX;   // SetWindowPos, handled within
                Gpu(scene).Frames.ShouldBeGreaterThan(frames, $"move {i}");
            }
            return 0;
        }));
}
