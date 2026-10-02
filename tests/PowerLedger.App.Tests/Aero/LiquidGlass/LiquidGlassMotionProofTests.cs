using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerLedger.App.Aero;
using Shouldly;
using Xunit.Abstractions;

namespace PowerLedger.App.Tests;

/// <summary>
/// The glass while its window moves, recorded off the screen on every frame Windows composes. The desktop is a still
/// picture (MonitorCapture.StillPicture, shown full screen by a window under ours too) whose red rises one step a pixel
/// across and whose blue tells the glass (brightened by 1.1) from the desktop, so each recorded frame says where the
/// glass's content was read from, and where the glass itself lies against the window's red frame. The window steps 12
/// device pixels each composed frame (a quick drag), and both lags are given in frames: the content's (the picture
/// under the glass read for an old place) and the glass window's (the glass lying where the window was). Opt in with
/// PL_PROOF_DIR, which receives a strip of frames and the numbers; PL_RECORDER names the recorder (ScreenRecorder).
/// </summary>
[Trait("Category", "UI")]
public class LiquidGlassMotionProofTests(ITestOutputHelper output)
{
    private const int Step = 12, Steps = 70, Border = 6, Width = 300, Height = 200;

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    private static string? Proof => Environment.GetEnvironmentVariable("PL_PROOF_DIR");

    [Theory]
    [InlineData("move")]
    [InlineData("resize")]
    public void A_moving_or_resized_window_s_glass_keeps_up_with_it_frame_by_frame(string motion)
    {
        if (Proof is not { } dir || Environment.GetEnvironmentVariable("PL_RECORDER") is not { } recorderExe) return;
        var result = UiHarness.OnUi(() => ScreenCapture.Aware(() =>
        {
            const int screenWidth = 1920, screenHeight = 1080;
            var picture = FakeGlassSource.Picture(screenWidth, screenHeight, (x, y) => ((byte)(60 + x % 150), (byte)(60 + y % 150), (byte)(x / 150 % 2 == 0 ? 128 : 188)));
            MonitorCapture.StillPicture = picture;
            LiquidGlassSources.Override = null;
            LiquidGlassSources.ExcludeFromCapture = false;
            LiquidGlassSources.AllowScreenshots = false;
            LiquidGlassSources.GpuAllowed = true;
            var desk = ScreenCapture.Show(new Image { Source = picture, Stretch = Stretch.None }, 0, 0, 10, 10);
            var scale = VisualTreeHelper.GetDpi(desk).DpiScaleX;
            (desk.Width, desk.Height) = (screenWidth / scale, screenHeight / scale);
            RenderOptions.SetBitmapScalingMode(desk, BitmapScalingMode.NearestNeighbor);
            var glass = new LiquidGlassBackdrop { CornerRadius = new CornerRadius(0), Scale = 0 };
            var frame = new Border { BorderBrush = Brushes.Red, BorderThickness = new Thickness(Border / scale), Child = glass };
            var window = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, Topmost = true,
                ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, UseLayoutRounding = true,
                Left = 200 / scale, Top = 300 / scale, Width = Width / scale, Height = Height / scale, Content = frame,
            };
            window.Show();
            ScreenRecorder? recorder = null;
            try
            {
                UiHarness.PumpUntil(() => glass.Composed, TimeSpan.FromSeconds(20), "the glass composed");
                UiHarness.Pump(TimeSpan.FromMilliseconds(800));
                recorder = new ScreenRecorder(recorderExe, Path.Combine(dir, $"{motion}.raw"), 150, 280, 1400, 240, 20);
                UiHarness.Pump(TimeSpan.FromMilliseconds(300));
                var moved = new List<(long Time, int Left)>();
                for (var i = 1; i <= Steps; i++)
                {
                    if (motion == "move") window.Left = (200 + i * Step) / scale;
                    else window.Width = (Width + i * Step) / scale;
                    moved.Add((System.Diagnostics.Stopwatch.GetTimestamp(), 200 + i * Step));
                    // The layout pass and what follows it, as between two steps of the system's own sizing loop.
                    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                    DwmFlush();
                }
                UiHarness.Pump(TimeSpan.FromMilliseconds(500));
                recorder.Stop();
                var state = string.Join(" | ", LiquidGlassSources.Live.OfType<WindowGlassSource>().Select(s => s.Gpu?.State ?? "no gpu"));
                File.WriteAllText(Path.Combine(dir, $"{motion}-state.txt"), $"{state}{Environment.NewLine}refused {MonitorCapture.Refused}");
                return Analyse(recorder, dir, motion);
            }
            finally
            {
                recorder?.Dispose();
                window.Close();
                desk.Close();
                MonitorCapture.StillPicture = null;
                LiquidGlassSources.ExcludeFromCapture = true;
            }
        }));
        output.WriteLine(result.Report);
        result.WorstContent.ShouldBeLessThanOrEqualTo(1.05);   // a frame at most (and a tenth of a pixel of rounding)
        result.WorstWindow.ShouldBeLessThanOrEqualTo(1.05);
    }

    /// <summary>Per recorded frame: where the window's frame is, where the glass begins, and where its content was read.</summary>
    private static (string Report, double WorstContent, double WorstWindow) Analyse(ScreenRecorder recorder, string dir, string motion)
    {
        int w = recorder.Width, h = recorder.Height, row = 300 + Height / 2 - 280;
        var lines = new List<string>();
        var rows = new List<(int Owner, int Glass, double Content)>();
        var strip = new List<byte[]>();
        foreach (var (_, pixels) in recorder.Frames)
        {
            byte R(int x) => pixels[(row * w + x) * 4 + 2];
            byte G(int x) => pixels[(row * w + x) * 4 + 1];
            byte B(int x) => pixels[(row * w + x) * 4];
            var owner = -1;
            for (var x = 0; x < w && owner < 0; x++)
            {
                if (R(x) > 240 && G(x) < 30 && B(x) < 30) owner = x;
            }
            if (owner < 0) continue;
            var ownerEnd = -1;
            for (var x = w - 1; x > owner && ownerEnd < 0; x--)
            {
                if (R(x) > 240 && G(x) < 30 && B(x) < 30) ownerEnd = x;
            }
            // The glass: brightened blue (141 or 207), against the desktop's 128 or 188.
            var glassStart = -1;
            for (var x = owner + Border; x < ownerEnd && glassStart < 0; x++)
            {
                if (B(x) is >= 136 and <= 150 or >= 198 and <= 216) glassStart = x;
            }
            var glassEnd = -1;
            for (var x = ownerEnd - Border; x > owner && glassEnd < 0; x--)
            {
                if (B(x) is >= 136 and <= 150 or >= 198 and <= 216) glassEnd = x;
            }
            // Where the content was read: the red, unbrightened, against where it shows, a step of 150 apart.
            var offsets = new List<double>();
            for (var x = owner + Border + 20; x < ownerEnd - Border - 20; x++)
            {
                if (B(x) is not (>= 136 and <= 150 or >= 198 and <= 216)) continue;
                var screenX = 150 + x;
                var read = R(x) / 1.1 - 60;
                if (read < 4 || read > 146) continue;   // the blur across a step of the picture
                var shown = screenX % 150;
                var d = shown - read;
                d -= 150 * Math.Round(d / 150);
                offsets.Add(d);
            }
            if (glassStart < 0 || offsets.Count < 20) continue;
            offsets.Sort();
            var content = offsets[offsets.Count / 2];
            // Moving: the glass's left edge against the frame's; resizing: its right edge, against the frame's right.
            var off = motion == "move" ? glassStart - owner - Border : ownerEnd - Border - glassEnd;
            rows.Add((owner, owner + Border + off, content));
            lines.Add($"window {150 + owner} to {150 + ownerEnd}, glass {150 + glassStart} to {150 + glassEnd} ({off:+0;-0;0} px off the frame), content read {content:+0.0;-0.0;0} px away");
            strip.Add(pixels);
        }
        var moving = rows.Skip(2).Take(Math.Max(0, rows.Count - 4)).ToList();
        double worstContent = moving.Count == 0 ? 99 : moving.Max(r => Math.Abs(r.Content)) / Step;
        double worstWindow = moving.Count == 0 ? 99 : moving.Max(r => Math.Abs(r.Glass - r.Owner - Border)) / (double)Step;
        double meanContent = moving.Count == 0 ? 99 : moving.Average(r => Math.Abs(r.Content)) / Step;
        double meanWindow = moving.Count == 0 ? 99 : moving.Average(r => Math.Abs(r.Glass - r.Owner - Border)) / (double)Step;
        var report = $"{recorder.Frames.Count} frames recorded, {rows.Count} with the window; content lag mean {meanContent:0.00}, worst {worstContent:0.00} frames; glass window lag mean {meanWindow:0.00}, worst {worstWindow:0.00} frames";
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, $"{motion}.txt"), [report, .. lines]);
        // The strip: eight frames from the middle of the drag, each the window's band, one under the other.
        var picked = strip.Skip(Math.Max(0, strip.Count / 2 - 4)).Take(8).ToList();
        if (picked.Count > 0)
        {
            var band = 90;
            var all = new byte[w * band * picked.Count * 4];
            for (var i = 0; i < picked.Count; i++)
            {
                for (var y = 0; y < band; y++) Array.Copy(picked[i], ((row - band / 2 + y) * w) * 4, all, ((i * band + y) * w) * 4, w * 4);
            }
            var image = BitmapSource.Create(w, band * picked.Count, 96, 96, PixelFormats.Bgra32, null, all, w * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(dir, $"{motion}-strip.png"));
            encoder.Save(file);
        }
        return (report, worstContent, worstWindow);
    }
}
