using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerLedger.App.Aero;
using Xunit.Abstractions;

namespace PowerLedger.App.Tests;

/// <summary>
/// The Aero window on the real screen over the mockup's background, its glass live, on the WPF effects path (before) and
/// the DirectComposition path (after), each grabbed with BitBlt and set beside Edge's render of the mockup. Opt-in: with
/// PL_PROOF_DIR set to a folder holding ref\Main-125.png and ref\Main-bg-125.png (the mockup's Main.html and Main-bg.html
/// in headless Edge at 125 %, 1440 by 900), on a 125 % display. The window is left out of capture while its glass is
/// live; once its capture is paused, it is let back in for the grab.
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]
public class LiquidGlassCompositionProofTests(ITestOutputHelper output)
{
    private static string? Proof => Environment.GetEnvironmentVariable("PL_PROOF_DIR") is { Length: > 0 } dir && Directory.Exists(dir) ? dir : null;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out System.Drawing.Point point);

    /// <summary>Glass only, in the mockup's CSS pixels: the sidebar under its list of PCs, above its foot.</summary>
    private static readonly Int32Rect Sidebar = new(24, 600, 224, 200);

    [Fact]
    public void A_pane_before_and_after_against_edge()
    {
        if (Proof is not { } dir) return;
        var lines = UiHarness.OnUi(() => ScreenCapture.Aware(() =>
        {
            var lines = new List<string>();
            // The pointer off the window, so no hover or tooltip of the App's shows in the backdrop or the grab.
            GetCursorPos(out var pointer);
            SetCursorPos(1915, 1075);
            var ground = Load(Path.Combine(dir, "ref", "Main-bg-125.png"));
            var edge = Load(Path.Combine(dir, "ref", "Main-125.png"));
            var image = new Image { Source = ground, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            var desk = ScreenCapture.Show(new Border { Background = Brushes.Black, Child = image }, 0, 0, 10, 10);
            var scale = VisualTreeHelper.GetDpi(desk).DpiScaleX;
            if (Math.Abs(scale - 1.25) > 0.01)
            {
                desk.Close();
                return new List<string> { $"display scale {scale}: the proof wants 125 %" };
            }
            (desk.Left, desk.Top, desk.Width, desk.Height) = (0, 0, ground.PixelWidth / scale, ground.PixelHeight / scale);
            (image.Width, image.Height) = (desk.Width, desk.Height);
            var grabs = new Dictionary<string, byte[]>();
            try
            {
                foreach (var path in new[] { "effects", "composition" })
                {
                    LiquidGlassSources.GpuAllowed = path == "composition";
                    using var saver = new FakeSaver();
                    var window = AeroHost.Window(AeroFixtures.Shell(saver), Theme.Dark);
                    LiquidGlassSources.Override = null;
                    (window.Left, window.Top, window.Width, window.Height, window.Topmost) = (0, 0, 1440, 900, true);
                    window.Show();
                    try
                    {
                        UiHarness.Pump(TimeSpan.FromSeconds(4));   // the reveal, and the glass's first frames
                        foreach (var source in LiquidGlassSources.Live.OfType<WindowGlassSource>())
                        {
                            source.Pause();
                            if (source.Image is BitmapSource picture)
                            {
                                var png = new PngBitmapEncoder();
                                png.Frames.Add(BitmapFrame.Create(picture));
                                using var file = File.Create(Path.Combine(dir, $"backdrop-{path}.png"));
                                png.Save(file);
                            }
                            source.Gpu?.Exclude(false);
                        }
                        SetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, CaptureNative.WDA_NONE);
                        UiHarness.Pump(TimeSpan.FromMilliseconds(600));
                        var height = Math.Min(1080, (int)Math.Round(900 * scale));
                        var grab = ScreenCapture.Grab(0, 0, (int)Math.Round(1440 * scale), height);
                        Save(grab, (int)Math.Round(1440 * scale), height, Path.Combine(dir, $"main-{path}.png"));
                        lines.Add($"{path}: sidebar glass against Edge: {Compare(grab, (int)Math.Round(1440 * scale), edge, Sidebar, scale, Path.Combine(dir, $"sidebar-{path}.png"))}");
                        grabs[path] = grab;
                    }
                    finally
                    {
                        window.CloseForSwitch();
                    }
                }
            }
            finally
            {
                LiquidGlassSources.GpuAllowed = true;
                desk.Close();
                SetCursorPos(pointer.X, pointer.Y);
            }
            if (grabs.Count == 2)
            {
                // The two paths against each other over the whole window: the same recipe, drawn twice.
                var (a, b) = (grabs["effects"], grabs["composition"]);
                var errors = Enumerable.Range(0, a.Length).Where(i => i % 4 != 3).Select(i => Math.Abs(a[i] - b[i])).ToList();
                lines.Add($"effects against composition, whole window: equal {errors.Count(e => e == 0) / (double)errors.Count:P1}, within 2 {errors.Count(e => e <= 2) / (double)errors.Count:P1}, within 8 {errors.Count(e => e <= 8) / (double)errors.Count:P1}, mean {errors.Average():F2}");
            }
            return lines;
        }));
        foreach (var line in lines) output.WriteLine(line);
        File.WriteAllLines(Path.Combine(dir, "composition-proof.txt"), lines);
    }

    private static BitmapSource Load(string path)
    {
        var frame = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        bgra.Freeze();
        return bgra;
    }

    /// <summary>The crop's channel differences, and the crop beside Edge's saved as one picture.</summary>
    private static string Compare(byte[] grab, int width, BitmapSource edge, Int32Rect css, double scale, string file)
    {
        int x = (int)Math.Round(css.X * scale), y = (int)Math.Round(css.Y * scale), w = (int)Math.Round(css.Width * scale), h = (int)Math.Round(css.Height * scale);
        var reference = new byte[edge.PixelWidth * edge.PixelHeight * 4];
        edge.CopyPixels(reference, edge.PixelWidth * 4, 0);
        var errors = new List<int>();
        var side = new byte[w * 2 * h * 4];
        for (var row = 0; row < h; row++)
        {
            for (var col = 0; col < w; col++)
            {
                for (var c = 0; c < 4; c++)
                {
                    var mine = grab[((y + row) * width + x + col) * 4 + c];
                    var theirs = reference[((y + row) * edge.PixelWidth + x + col) * 4 + c];
                    side[(row * w * 2 + col) * 4 + c] = c == 3 ? (byte)255 : mine;
                    side[(row * w * 2 + w + col) * 4 + c] = c == 3 ? (byte)255 : theirs;
                    if (c < 3) errors.Add(Math.Abs(mine - theirs));
                }
            }
        }
        Save(side, w * 2, h, file);
        double Share(int within) => errors.Count(e => e <= within) / (double)errors.Count;
        return $"mean {errors.Average():F2}, within 4 {Share(4):P1}, within 8 {Share(8):P1}, within 16 {Share(16):P1}, max {errors.Max()}";
    }

    private static void Save(byte[] pixels, int width, int height, string file)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        png.Save(stream);
    }
}
