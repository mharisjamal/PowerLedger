using System.Runtime.InteropServices;
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
/// The live backdrop on the real screen: a plain window of one colour (the "desk", captured like any other window) with
/// a window of ours in front of it, of another colour, holding a liquid glass piece. The glass's picture must show the
/// desk and never our window, stop when our window hides, and take no frames while nothing behind changes.
/// </summary>
public class LiquidGlassCaptureTests(ITestOutputHelper output)
{
    private static readonly Color Desk = Color.FromRgb(0x20, 0xC0, 0xB0);
    private static readonly Color Ours = Color.FromRgb(0xF0, 0x10, 0xE0);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

    private sealed class Scene(Window desk, Window window, LiquidGlassBackdrop glass) : IDisposable
    {
        public Window Desk => desk;

        public Window Window => window;

        public LiquidGlassBackdrop Glass => glass;

        public WindowGlassSource Source => LiquidGlassSources.Live.OfType<WindowGlassSource>().Single();

        public IntPtr Handle => new WindowInteropHelper(window).Handle;

        public void Dispose()
        {
            window.Close();
            desk.Close();
            LiquidGlassSources.AllowScreenshots = false;
        }
    }

    /// <summary>The desk, 800 by 600 at 100, 100, and our window, 300 by 200 in the middle of it, with glass filling it.</summary>
    private static Scene Show()
    {
        LiquidGlassSources.Override = null;
        LiquidGlassSources.ExcludeFromCapture = true;
        LiquidGlassSources.AllowScreenshots = false;
        var desk = ScreenCapture.Show(new Border { Background = new SolidColorBrush(Desk) }, 100, 100, 800, 600);
        var glass = new LiquidGlassBackdrop { CornerRadius = new CornerRadius(0) };
        var window = ScreenCapture.Show(new Grid { Background = new SolidColorBrush(Ours), Children = { glass } }, 350, 300, 300, 200);
        return new Scene(desk, window, glass);
    }

    private static (int Desk, int Ours, int Other) Count(BitmapSource picture)
    {
        var bgra = new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        int desk = 0, ours = 0, other = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            bool Is(Color c) => Math.Abs(pixels[i + 2] - c.R) <= 2 && Math.Abs(pixels[i + 1] - c.G) <= 2 && Math.Abs(pixels[i] - c.B) <= 2;
            if (Is(Desk)) desk++;
            else if (Is(Ours)) ours++;
            else other++;
        }
        return (desk, ours, other);
    }

    [Fact]
    public void The_glass_shows_what_is_behind_its_window_and_never_the_window()
        => UiHarness.OnUi(() => ScreenCapture.Aware(() =>
        {
            using var scene = Show();
            UiHarness.PumpUntil(() => scene.Glass.Kind == LiquidGlassSourceKind.Live, TimeSpan.FromSeconds(10), "the live picture");
            UiHarness.Pump(TimeSpan.FromMilliseconds(300));
            GetWindowDisplayAffinity(scene.Handle, out var affinity).ShouldBeTrue();
            affinity.ShouldBe(CaptureNative.WDA_EXCLUDEFROMCAPTURE);
            var source = scene.Source;
            GetWindowRect(scene.Handle, out var r);
            // Under the window itself (the source reaches a margin round it): all desk, none of ours.
            var under = new CroppedBitmap((BitmapSource)source.Image!, new Int32Rect(r.Left - (int)source.ScreenBounds.X, r.Top - (int)source.ScreenBounds.Y, r.Right - r.Left, r.Bottom - r.Top));
            var (desk, ours, other) = Count(under);
            output.WriteLine($"under the window: desk {desk}, ours {ours}, other {other}; source {source.ScreenBounds}, window {r.Left},{r.Top},{r.Right},{r.Bottom}");
            ours.ShouldBe(0);
            desk.ShouldBe((r.Right - r.Left) * (r.Bottom - r.Top));
            // End to end: the piece draws the live desk through the recipe, brightness(1.1) and all (a flat colour
            // neither blurs nor moves).
            UiHarness.PumpUntil(() => scene.Glass.IsReady, TimeSpan.FromSeconds(20), "the source map");
            var dpi = VisualTreeHelper.GetDpi(scene.Glass);
            int w = (int)Math.Round(scene.Glass.ActualWidth * dpi.DpiScaleX), h = (int)Math.Round(scene.Glass.ActualHeight * dpi.DpiScaleY);
            var drawn = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            drawn.Render(scene.Glass);
            var pixels = new byte[w * h * 4];
            drawn.CopyPixels(pixels, w * 4, 0);
            var centre = ((h / 2) * w + w / 2) * 4;
            output.WriteLine($"the glass draws {pixels[centre + 2]},{pixels[centre + 1]},{pixels[centre]} over the desk's {Desk.R},{Desk.G},{Desk.B}");
            ((int)pixels[centre + 2]).ShouldBeInRange((int)(Desk.R * 1.1) - 3, (int)(Desk.R * 1.1) + 3);
            ((int)pixels[centre + 1]).ShouldBeInRange((int)(Desk.G * 1.1) - 3, (int)(Desk.G * 1.1) + 3);
            ((int)pixels[centre]).ShouldBeInRange((int)(Desk.B * 1.1) - 3, (int)(Desk.B * 1.1) + 3);
            return 0;
        }));

    [Fact]
    public void Hidden_or_minimised_the_window_takes_no_frames_and_at_rest_nothing_is_delivered()
        => UiHarness.OnUi(() => ScreenCapture.Aware(() =>
        {
            using var scene = Show();
            UiHarness.PumpUntil(() => scene.Glass.Kind == LiquidGlassSourceKind.Live, TimeSpan.FromSeconds(10), "the live picture");
            UiHarness.Pump(TimeSpan.FromMilliseconds(500));
            var subscriber = scene.Source.Subscriber!;
            var session = subscriber.Session;
            // At rest: the desk doesn't change, so nothing reaches the glass.
            var before = subscriber.Deliveries;
            UiHarness.Pump(TimeSpan.FromSeconds(2));
            var atRest = subscriber.Deliveries - before;
            // Something behind changes: it reaches the glass.
            ((Border)scene.Desk.Content).Background = new SolidColorBrush(Color.FromRgb(0x30, 0x40, 0xD0));
            UiHarness.PumpUntil(() => subscriber.Deliveries > before + atRest, TimeSpan.FromSeconds(5), "the desk's new colour");
            // Hidden: the duplication is let go and no frames are taken.
            scene.Window.Hide();
            UiHarness.PumpUntil(() => !session.Running, TimeSpan.FromSeconds(5), "the capture to stop");
            var frames = session.FramesAcquired;
            ((Border)scene.Desk.Content).Background = new SolidColorBrush(Desk);
            UiHarness.Pump(TimeSpan.FromSeconds(1));
            var hidden = session.FramesAcquired - frames;
            // Shown again: live again.
            scene.Window.Show();
            UiHarness.PumpUntil(() => session.Running && subscriber.Active, TimeSpan.FromSeconds(5), "the capture to start again");
            // Minimised: stops again.
            scene.Window.WindowState = WindowState.Minimized;
            UiHarness.PumpUntil(() => !session.Running, TimeSpan.FromSeconds(5), "the capture to stop when minimised");
            output.WriteLine($"deliveries at rest over 2 s: {atRest}; frames while hidden over 1 s: {hidden}");
            atRest.ShouldBe(0);
            hidden.ShouldBe(0);
            return 0;
        }));

    [Fact]
    public void The_picture_follows_the_window_as_it_moves()
        => UiHarness.OnUi(() => ScreenCapture.Aware(() =>
        {
            using var scene = Show();
            UiHarness.PumpUntil(() => scene.Glass.Kind == LiquidGlassSourceKind.Live, TimeSpan.FromSeconds(10), "the live picture");
            var first = scene.Source.ScreenBounds;
            scene.Window.Left += 120;
            scene.Window.Top += 40;
            GetWindowRect(scene.Handle, out var r);
            UiHarness.PumpUntil(() => scene.Source.ScreenBounds.Contains(new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)) && scene.Source.ScreenBounds != first,
                TimeSpan.FromSeconds(5), "the picture to follow");
            output.WriteLine($"before {first}, after {scene.Source.ScreenBounds}, window {r.Left},{r.Top}");
            return 0;
        }));

    [Fact]
    public void Allowing_screenshots_clears_the_affinity_and_shows_the_wallpaper_and_back()
        => UiHarness.OnUi(() => ScreenCapture.Aware(() =>
        {
            using var scene = Show();
            UiHarness.PumpUntil(() => scene.Glass.Kind == LiquidGlassSourceKind.Live, TimeSpan.FromSeconds(10), "the live picture");
            LiquidGlassSources.AllowScreenshots = true;
            GetWindowDisplayAffinity(scene.Handle, out var affinity).ShouldBeTrue();
            affinity.ShouldBe(CaptureNative.WDA_NONE);
            var wallpaper = AeroNative.WallpaperPath() != null ? LiquidGlassSourceKind.Wallpaper : LiquidGlassSourceKind.None;
            scene.Glass.Kind.ShouldBe(wallpaper);
            scene.Source.Subscriber.ShouldBeNull();
            LiquidGlassSources.AllowScreenshots = false;
            GetWindowDisplayAffinity(scene.Handle, out affinity).ShouldBeTrue();
            affinity.ShouldBe(CaptureNative.WDA_EXCLUDEFROMCAPTURE);
            UiHarness.PumpUntil(() => scene.Glass.Kind == LiquidGlassSourceKind.Live, TimeSpan.FromSeconds(10), "the live picture again");
            return 0;
        }));

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out CaptureNative.RECT rect);
}
