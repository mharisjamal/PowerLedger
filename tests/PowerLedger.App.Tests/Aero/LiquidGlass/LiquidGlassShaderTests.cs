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

    /// <summary>The recipe on the CPU (what a piece inside another shows, drawn once) against Chromium's own pixels.</summary>
    [Theory]
    [InlineData("a")]
    [InlineData("d")]
    [InlineData("e")]
    public void On_the_cpu_the_recipe_draws_as_chromium_does(string name)
    {
        var scale = ChromiumReference.Cases.Single(c => c.Name == name).Scale;
        var box = ChromiumReference.DeviceBox(name);
        var (chromium, w, h) = ChromiumReference.Load(name, "full");
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (r, g, b) = ChromiumReference.Picture(box.X + x, box.Y + y, scale);
                (pixels[(y * w + x) * 4], pixels[(y * w + x) * 4 + 1], pixels[(y * w + x) * 4 + 2], pixels[(y * w + x) * 4 + 3]) = (b, g, r, 255);
            }
        }
        var drawn = GlassRecipeCpu.Draw(pixels, w, h, scale, LiquidGlassRecipe.Brightness, LiquidGlassRecipe.BlurDeviation * scale, LiquidGlassRecipe.Scale);
        var errors = Compare(drawn, chromium, w, h, scale);
        output.WriteLine($"{name}: {w}x{h} at {scale} on the CPU: {Report(errors)}");
        Share(errors, 1).ShouldBeGreaterThan(0.98);
        Share(errors, 8).ShouldBeGreaterThan(0.995);
    }

    /// <summary>
    /// The mockup's Energy pane over the mockup's own background, which holds a white window: Edge bends that window into
    /// white blotches inside the pane, and so must the engine, no more and no less (scripts/liquid-glass/dashboard.html,
    /// its "none" and "alone" shots at display scale 1).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    public void Over_the_mockups_background_a_pane_draws_as_edge_does(double scale)
    {
        var (errors, w, h) = UiHarness.OnUi(() =>
        {
            var (background, w, h) = ChromiumReference.LoadFile($"dash-{scale:0.##}-bg");
            var drawn = Draw((0, 0, w, h), scale, ChromiumReference.Bitmap(background, w, h));
            var (edge, _, _) = ChromiumReference.LoadFile($"dash-{scale:0.##}-full");
            return (Compare(drawn, edge, w, h, scale), w, h);
        });
        output.WriteLine($"Energy pane, {w}x{h} at {scale}: {Report(errors)}");
        Share(errors, 1).ShouldBeGreaterThan(0.95);
        Share(errors, 8).ShouldBeGreaterThan(0.995);
    }

    /// <summary>
    /// A piece inside another (the mockup's bars in the Energy pane) reads nothing of the screen: in Chromium a
    /// backdrop-filter element is a backdrop root, so what a piece inside it reads is only what the outer piece's own
    /// content painted before it. Edge draws the pane with its fourteen glass bars pixel for pixel as the pane alone, so
    /// the engine's bars must draw nothing, letting the pane's glass show.
    /// </summary>
    [Fact]
    public void A_piece_inside_another_reads_nothing_as_in_chromium()
    {
        var errors = UiHarness.OnUi(() =>
        {
            var (background, w, h) = ChromiumReference.LoadFile("dash-1-bg");
            var nested = new List<LiquidGlassBackdrop>();
            var drawn = Draw((0, 0, w, h), 1, ChromiumReference.Bitmap(background, w, h), root =>
            {
                // As the look lays a piece: the backdrop the bottom layer of the piece's element, its content above.
                var content = new Canvas();
                int[] heights = [96, 136, 115, 186, 149, 215, 170, 126, 200, 232, 180, 255, 224, 300];
                for (var i = 0; i < heights.Length; i++)
                {
                    var bar = new LiquidGlassBackdrop { CornerRadius = new CornerRadius(14), Width = 40, Height = heights[i] };
                    var element = new Grid { Children = { bar } };
                    Canvas.SetLeft(element, 42 + i * 49);
                    Canvas.SetTop(element, 494 - 60 - heights[i]);
                    content.Children.Add(element);
                    nested.Add(bar);
                }
                root.Children.Add(content);
            });
            foreach (var bar in nested)
            {
                bar.IsNested.ShouldBeTrue();
                bar.Kind.ShouldBe(LiquidGlassSourceKind.Inside);
            }
            var (edge, _, _) = ChromiumReference.LoadFile("dash-1-full");
            return Compare(drawn, edge, w, h, 1);
        });
        output.WriteLine($"Energy pane with its 14 bars, 752x494 at 1: {Report(errors)}");
        Share(errors, 1).ShouldBeGreaterThan(0.95);
        Share(errors, 8).ShouldBeGreaterThan(0.995);
    }

    /// <summary>
    /// Glass inside glass reads the outer piece's content beneath it, as Chromium does (scripts/liquid-glass/nested.html):
    /// the mockup's ring with its glass hole bends the ring's colours; a bubble on a well reads the well's tint and lays the
    /// result over it; a bubble on bare glass, and the segmented capsule with its Day bubble, read nothing.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    public void Glass_inside_glass_bends_the_outer_content_beneath_it_as_edge_does(double scale)
    {
        var (errors, w, h, kinds, rings) = UiHarness.OnUi(() =>
        {
            var (background, w, h) = ChromiumReference.LoadFile($"nest-{scale:0.##}-bg");
            var (ring, rw, rh) = ChromiumReference.LoadFile($"ring-{scale:0.##}");
            var inner = new List<LiquidGlassBackdrop>();
            var drawn = Draw((0, 0, w, h), scale, ChromiumReference.Bitmap(background, w, h), root => root.Children.Add(Nest(ChromiumReference.Bitmap(ring, rw, rh, 96 * scale), inner)));
            var (edge, _, _) = ChromiumReference.LoadFile($"nest-{scale:0.##}-full");
            // The ring hole on its own, where the bending shows.
            var hole = new List<int>();
            int x0 = (int)Math.Round(100 * scale), y0 = (int)Math.Round(100 * scale), d = (int)Math.Round(100 * scale);
            for (var y = y0 + d / 4; y < y0 + 3 * d / 4; y++)
            {
                for (var x = x0 + d / 4; x < x0 + 3 * d / 4; x++)
                {
                    for (var c = 0; c < 3; c++) hole.Add(Math.Abs(drawn[(y * w + x) * 4 + c] - edge[(y * w + x) * 4 + c]));
                }
            }
            return (Compare(drawn, edge, w, h, scale), w, h, inner.Select(p => p.Kind).ToList(), hole);
        });
        output.WriteLine($"Glass inside glass, {w}x{h} at {scale}: {Report(errors)}");
        output.WriteLine($"  the ring hole's middle: {Report(rings)}");
        kinds.ShouldAllBe(k => k == LiquidGlassSourceKind.Inside);
        Share(errors, 1).ShouldBeGreaterThan(0.95);
        Share(errors, 8).ShouldBeGreaterThan(0.995);
        Share(rings, 8).ShouldBeGreaterThan(0.95);
    }

    /// <summary>nested.html's content, in units: the ring and its hole, a well with a bubble, a bubble, the capsule with
    /// its Day bubble; each inner piece as the look lays one (its backdrop the bottom layer of its element).</summary>
    private static Canvas Nest(BitmapSource ring, List<LiquidGlassBackdrop> inner)
    {
        Grid Piece(double width, double height, double radius, UIElement? content = null)
        {
            var backdrop = new LiquidGlassBackdrop { CornerRadius = new CornerRadius(radius) };
            inner.Add(backdrop);
            var element = new Grid { Width = width, Height = height, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Children = { backdrop } };
            if (content != null) element.Children.Add(content);
            return element;
        }
        UIElement At(UIElement e, double x, double y)
        {
            Canvas.SetLeft(e, x);
            Canvas.SetTop(e, y);
            return e;
        }
        var picture = new Image { Source = ring, Width = 180, Height = 180, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.NearestNeighbor);
        var well = new Border { Width = 272, Height = 120, CornerRadius = new CornerRadius(22), Background = new SolidColorBrush(Color.FromArgb(36, 0, 10, 40)) };
        var onWell = Piece(131, 60, 20);
        onWell.Margin = new Thickness(16, 30, 0, 0);
        well.Child = new Grid { Children = { onWell } };
        var day = Piece(72, 34, 17);
        day.Margin = new Thickness(4, 4, 0, 0);
        return new Canvas
        {
            Children =
            {
                At(picture, 60, 60), At(Piece(100, 100, 50), 100, 100), At(well, 320, 60), At(Piece(131, 60, 20), 320, 220),
                At(Piece(232, 42, 21, day), 480, 220),
            },
        };
    }

    [Fact]
    public void A_piece_beside_another_or_over_it_in_the_same_element_is_not_nested()
        => UiHarness.OnUi(() =>
        {
            var first = new LiquidGlassBackdrop();
            var over = new LiquidGlassBackdrop();
            var beside = new LiquidGlassBackdrop();
            var root = new Grid { Children = { new Grid { Children = { first, over } }, new Grid { Children = { beside } } } };
            root.Measure(new Size(200, 100));
            root.Arrange(new Rect(0, 0, 200, 100));
            first.IsNested.ShouldBeFalse();
            over.IsNested.ShouldBeFalse();
            beside.IsNested.ShouldBeFalse();
            return 0;
        });

    /// <summary>
    /// 0.10.9 (the merge of the mockup's layout with the engine): the intro glides and fades each pane's content, its glass
    /// bubbles, bars and ring hole with it. What lies beneath a piece inside another moves and fades with it, so its picture
    /// is the same and nothing is drawn again; drawn per frame for every nested piece (22 on the Dashboard), the intro ran at
    /// 700 ms a frame. A fade over both is the piece's to take once, as an opacity group's is in the browser, never twice.
    /// </summary>
    [Fact]
    public void Glass_inside_glass_draws_nothing_again_when_its_content_moves_and_fades_with_it()
        => UiHarness.OnUi(() =>
        {
            var piece = new LiquidGlassBackdrop { Width = 60, Height = 40, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(30, 20, 0, 0) };
            var well = new Border { Background = new SolidColorBrush(Color.FromRgb(200, 40, 90)), Width = 80, Height = 50, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(20, 10, 0, 0) };
            var glide = new TranslateTransform();
            var content = new Grid { RenderTransform = glide, Children = { well, new Grid { Children = { piece } } } };
            var outer = new Grid { Width = 200, Height = 100, Children = { content } };
            outer.Measure(new Size(200, 100));
            outer.Arrange(new Rect(0, 0, 200, 100));
            byte[] Pixels(ImageSource image)
            {
                var bitmap = (BitmapSource)image;
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                return pixels;
            }
            using var source = new InsideGlassSource(piece, outer);
            source.Refresh().ShouldBeTrue("the first picture");
            var rest = Pixels(source.Image!);

            glide.X = 24;
            glide.Y = -9;
            content.Opacity = 0.4;
            source.Refresh().ShouldBeFalse("the content beneath moved and faded with the piece: the same picture");
            using var again = new InsideGlassSource(piece, outer);
            again.Refresh().ShouldBeTrue();
            Pixels(again.Image!).ShouldBe(rest, "the fade is the piece's to take, not its picture's");

            well.Margin = new Thickness(26, 10, 0, 0);
            outer.UpdateLayout();
            source.Refresh().ShouldBeTrue("the well moved beneath the piece");
            return 0;
        });

    /// <summary>While something animates, the nested pieces share a few milliseconds of each frame: the first is always
    /// looked at, then the rest only while the frame's share lasts; a new frame starts afresh.</summary>
    [Fact]
    public void Nested_pieces_share_a_budget_of_each_frame()
    {
        var ms = System.Diagnostics.Stopwatch.Frequency / 1000;
        var frame = TimeSpan.FromMilliseconds(123_456);
        InsideGlassBudget.May(frame).ShouldBeTrue("the frame's first piece");
        InsideGlassBudget.Spend(frame, 30 * ms);
        InsideGlassBudget.May(frame).ShouldBeFalse("one piece took the whole share");
        var next = frame + TimeSpan.FromMilliseconds(16);
        InsideGlassBudget.May(next).ShouldBeTrue("a new frame");
        InsideGlassBudget.Spend(next, 2 * ms);
        InsideGlassBudget.May(next).ShouldBeTrue("2 of 5 ms spent");
        InsideGlassBudget.Spend(next, 4 * ms);
        InsideGlassBudget.May(next).ShouldBeFalse("6 of 5 ms spent");
    }

    private static List<int> Compare(byte[] drawn, byte[] edge, int w, int h, double scale)
    {
        // Away from the corners, where the clip's antialiasing is each renderer's own.
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
        return errors;
    }

    private static double Share(List<int> errors, int within) => errors.Count(e => e <= within) / (double)errors.Count;

    private static string Report(List<int> errors)
        => $"channels equal {Share(errors, 0):P2}, within 1 {Share(errors, 1):P2}, within 2 {Share(errors, 2):P2}, within 8 {Share(errors, 8):P2}, mean {errors.Average():F3}, max {errors.Max()}";

    /// <summary>The recipe over the reference page's picture, in a box of the case's device pixels, drawn to a bitmap.</summary>
    private static byte[] Draw((int X, int Y, int Width, int Height) box, double scale)
        => Draw(box, scale, FakeGlassSource.Picture(box.Width, box.Height, (x, y) => ChromiumReference.Picture(box.X + x, box.Y + y, scale)));

    /// <summary>The recipe over <paramref name="picture"/> (the box's own device pixels), drawn to a bitmap; <paramref name="dress"/>
    /// adds to the piece's element, over its backdrop.</summary>
    private static byte[] Draw((int X, int Y, int Width, int Height) box, double scale, BitmapSource picture, Action<Grid>? dress = null)
    {
        var source = new FakeGlassSource(picture, new Rect(0, 0, box.Width, box.Height));
        var glass = new LiquidGlassBackdrop { Width = box.Width / scale, Height = box.Height / scale };
        // The page (black) and on it the piece's element: its backdrop at the bottom, then what dress adds.
        var element = new Grid { Children = { glass } };
        var root = new Grid { Background = Brushes.Black, Children = { element } };
        dress?.Invoke(element);
        VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
        root.Measure(new Size(glass.Width, glass.Height));
        root.Arrange(new Rect(0, 0, glass.Width, glass.Height));
        glass.Use(source);
        void Others(DependencyObject node)
        {
            if (node is LiquidGlassBackdrop piece && !ReferenceEquals(piece, glass)) piece.Use(source);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Others(VisualTreeHelper.GetChild(node, i));
        }
        Others(root);
        var pieces = new List<LiquidGlassBackdrop>();
        void All(DependencyObject node)
        {
            if (node is LiquidGlassBackdrop piece) pieces.Add(piece);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) All(VisualTreeHelper.GetChild(node, i));
        }
        All(root);
        UiHarness.PumpUntil(() =>
        {
            root.UpdateLayout();
            foreach (var piece in pieces)
            {
                if (piece.MapError is { } error) throw error;
            }
            return pieces.All(p => p.IsReady);
        }, TimeSpan.FromSeconds(20), "the source maps");
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
