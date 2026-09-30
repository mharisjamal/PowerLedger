using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerLedger.App.Aero;
using Shouldly;
using Xunit.Abstractions;

namespace PowerLedger.App.Tests;

/// <summary>The four passes: the blur kernel's maths, and the whole recipe drawn by WPF against Chromium's own pixels.</summary>
public class LiquidGlassShaderTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(2.5)]
    public void The_blur_kernel_is_skias_gaussian_read_in_bilinear_pairs(double sigma)
    {
        var (centre, pairs) = LiquidGlassEffects.BlurKernel(sigma);
        var radius = (int)Math.Ceiling(3 * sigma);
        // Unpack every pair back into its two texels: exactly the normalized discrete Gaussian over the radius.
        var weights = new double[radius + 1];
        weights[0] = centre;
        for (var p = 0; p < pairs.Length; p++)
        {
            var (offset, weight) = pairs[p];
            var first = 1 + 2 * p;
            var t = offset - first;   // bilinear: (1 - t) of the first texel, t of the next
            weights[first] += weight * (1 - t);
            if (first + 1 <= radius) weights[first + 1] += weight * t;
            else t.ShouldBe(0, 1e-12);
        }
        var total = Enumerable.Range(0, radius + 1).Sum(i => Math.Exp(-(double)i * i / (2 * sigma * sigma)) * (i == 0 ? 1 : 2));
        for (var i = 0; i <= radius; i++) weights[i].ShouldBe(Math.Exp(-(double)i * i / (2 * sigma * sigma)) / total, 1e-12);
        (centre + 2 * pairs.Sum(p => p.Weight)).ShouldBe(1, 1e-12);
        pairs.Length.ShouldBeLessThanOrEqualTo(10);
    }

    [Fact]
    public void A_deviation_of_zero_is_the_picture_unchanged()
    {
        var (centre, pairs) = LiquidGlassEffects.BlurKernel(0);
        centre.ShouldBe(1);
        pairs.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("a")]
    [InlineData("d")]
    public void The_whole_recipe_draws_as_chromium_does(string name)
    {
        var scale = ChromiumReference.Cases.Single(c => c.Name == name).Scale;
        var box = ChromiumReference.DeviceBox(name);
        var (chromium, w, h) = ChromiumReference.Load(name, "full");
        var drawn = UiHarness.OnUi(() => Draw(box, scale));
        // Away from the corners, where the clip's antialiasing is each renderer's own.
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
        output.WriteLine($"{name}: {w}x{h} at {scale}: channels equal {Share(0):P2}, within 1 {Share(1):P2}, within 2 {Share(2):P2}, within 8 {Share(8):P2}, mean {errors.Average():F3}, max {errors.Max()}");
        Share(1).ShouldBeGreaterThan(0.95);
        Share(8).ShouldBeGreaterThan(0.995);
    }

    /// <summary>The recipe over the reference page's picture, in a box of the case's device pixels, drawn to a bitmap.</summary>
    private static byte[] Draw((int X, int Y, int Width, int Height) box, double scale)
    {
        var picture = FakeGlassSource.Picture(box.Width, box.Height, (x, y) => ChromiumReference.Picture(box.X + x, box.Y + y, scale));
        var source = new FakeGlassSource(picture, new Rect(0, 0, box.Width, box.Height));
        var glass = new LiquidGlassBackdrop { Width = box.Width / scale, Height = box.Height / scale };
        var root = new Grid { Background = Brushes.Black };
        root.Children.Add(glass);
        VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
        root.Measure(new Size(glass.Width, glass.Height));
        root.Arrange(new Rect(0, 0, glass.Width, glass.Height));
        glass.Use(source);
        UiHarness.PumpUntil(() =>
        {
            root.UpdateLayout();
            if (glass.MapError is { } error) throw error;
            return glass.IsReady;
        }, TimeSpan.FromSeconds(20), "the source map");
        var bitmap = new RenderTargetBitmap(box.Width, box.Height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var pixels = new byte[box.Width * box.Height * 4];
        bitmap.CopyPixels(pixels, box.Width * 4, 0);
        System.IO.Directory.CreateDirectory(UiHarness.Folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = System.IO.File.Create(System.IO.Path.Combine(UiHarness.Folder, $"liquid-glass-{scale}.png"))) encoder.Save(file);
        return pixels;
    }
}
