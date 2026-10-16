using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerLedger.App.Aero;
using Shouldly;
using Xunit.Abstractions;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan AD in the harness: a pane of Aero glass with text and an icon, its engine handed a white, a black, and a half
/// black, half white backdrop (the CPU path, as the live capture hands it the screen), drawn over that backdrop; the ink's
/// contrast is measured on the drawn pixels against the same spot drawn with no ink. Renders go to
/// %TEMP%\powerledger-renders.
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]
public class GlassInkRenderingTests(ITestOutputHelper output)
{
    private const int W = 480, H = 240;

    private sealed record Scene(Window Window, GlassPanel Pane, FakeGlassSource Source, GlassMaterial Material);

    private sealed record Words(TextBlock Title, TextBlock Sub, Icon Icon, StackPanel Block);

    /// <summary>A title in the look's ink (inherited from the pane), a quieter line (the second step, by reference) and an
    /// icon (inheriting).</summary>
    private static Words Block(string title)
    {
        var heading = new TextBlock { Text = title, FontSize = 26, FontWeight = FontWeights.SemiBold };
        var sub = new TextBlock { Text = "the quieter line", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "A.B.Text2");
        var icon = new Icon { Size = 40, StrokeWidth = 4, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        return new Words(heading, sub, icon, new StackPanel { Children = { heading, sub, icon } });
    }

    /// <summary>A pane holding <paramref name="content"/> over a backdrop of <paramref name="pixel"/>, shown off screen and
    /// settled.</summary>
    private static Scene Show(Func<int, int, (byte, byte, byte)> pixel, UIElement content)
    {
        var ground = FakeGlassSource.Picture(W, H, pixel);
        FakeGlassSource? fake = null;
        LiquidGlassSources.Override = source => fake = (FakeGlassSource)LiquidGlassProofTests.Behind(ground, source, W, H);
        var pane = new GlassPanel { Margin = new Thickness(30), Padding = new Thickness(28, 20, 28, 20), Content = content };
        var window = AeroHost.Dressed(new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = 0, Width = W, Height = H, Background = Brushes.Transparent, Content = pane, UseLayoutRounding = true,
        }, Theme.Dark);
        foreach (var icon in Icons(content)) icon.Data = (System.Windows.Media.Geometry)window.FindResource("A.I.Check");
        var material = new GlassMaterial(window, () => GlassSettings.Default, () => Theme.Dark);
        window.Show();
        UiHarness.Pump(TimeSpan.FromMilliseconds(500));
        window.UpdateLayout();
        return new Scene(window, pane, fake!, material);
    }

    private static IEnumerable<Icon> Icons(UIElement content) => content switch
    {
        Icon icon => [icon],
        Panel panel => panel.Children.OfType<UIElement>().SelectMany(Icons),
        ContentControl { Content: UIElement inner } => Icons(inner),
        _ => [],
    };

    private static void Close(Scene scene)
    {
        scene.Material.Dispose();
        scene.Window.Close();
        LiquidGlassSources.Override = null;
        UiHarness.NoCapture();
    }

    /// <summary>The window drawn over its backdrop at 96 DPI, as BGRA.</summary>
    private static (byte[] Pixels, BitmapSource Bitmap) Draw(Scene scene)
    {
        var bitmap = LiquidGlassProofTests.Over((BitmapSource)scene.Source.Image!, scene.Window, W, H);
        var pixels = new byte[W * H * 4];
        new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, W * 4, 0);
        return (pixels, bitmap);
    }

    /// <summary>The best contrast any pixel of <paramref name="element"/>'s box makes against the same pixel drawn with the
    /// ink hidden: the ink's strokes against the glass they sit on.</summary>
    private static double InkContrast(Scene scene, FrameworkElement element, byte[] with, byte[] without)
    {
        var box = element.TransformToAncestor(scene.Window).TransformBounds(new Rect(element.RenderSize));
        var best = 1.0;
        for (var y = (int)box.Top; y < (int)box.Bottom; y++)
        {
            for (var x = (int)box.Left; x < (int)box.Right; x++)
            {
                var at = (y * W + x) * 4;
                var ink = Color.FromRgb(with[at + 2], with[at + 1], with[at]);
                var ground = Color.FromRgb(without[at + 2], without[at + 1], without[at]);
                best = Math.Max(best, Contrast.Ratio(ink, ground));
            }
        }
        return best;
    }

    /// <summary>Each block's title, quieter line and icon measured, and the glass beside the first block's title.</summary>
    private List<(double Title, double Sub, double Icon, Color Ground)> Measure(Scene scene, string name, params Words[] blocks)
    {
        var (with, bitmap) = Draw(scene);
        LiquidGlassProofTests.Write(bitmap, System.IO.Path.Combine(UiHarness.Folder, $"aero-ink-{name}.png"));
        var inks = blocks.SelectMany(b => new UIElement[] { b.Title, b.Sub, b.Icon }).ToList();
        foreach (var e in inks) e.Opacity = 0;
        var (without, _) = Draw(scene);
        foreach (var e in inks) e.Opacity = 1;
        var results = new List<(double, double, double, Color)>();
        foreach (var block in blocks)
        {
            var beside = block.Sub.TransformToAncestor(scene.Window).Transform(new Point(block.Sub.ActualWidth / 2, block.Sub.ActualHeight + 4));
            var at = ((int)beside.Y * W + (int)beside.X) * 4;
            var ground = Color.FromRgb(without[at + 2], without[at + 1], without[at]);
            var result = (InkContrast(scene, block.Title, with, without), InkContrast(scene, block.Sub, with, without), InkContrast(scene, block.Icon, with, without), ground);
            output.WriteLine($"{name}, '{block.Title.Text}': glass {ground}, title {result.Item1:0.0}:1, quieter line {result.Item2:0.0}:1, icon {result.Item3:0.0}:1");
            results.Add(result);
        }
        return results;
    }

    [Fact]
    public void Over_a_white_backdrop_the_ink_turns_dark_and_reads()
        => UiHarness.OnUi(() =>
        {
            var words = Block("Readable");
            var scene = Show((_, _) => (255, 255, 255), words.Block);
            // After the material, which sets the Glass settings' own Reduce motion (none: Windows').
            using var motion = AeroMotion.Force(true);
            try
            {
                scene.Pane.Ink.Laid.ShouldBe(true, "dark ink laid on the pane");
                ((SolidColorBrush)words.Title.Foreground).Color.ShouldBe(GlassInk.Family(dark: true, strict: false)["A.C.Text"]);
                words.Title.Effect.ShouldBeNull("no text shadow under dark ink");
                var (title, sub, icon, ground) = Measure(scene, "white", words)[0];
                Contrast.Ratio(Colors.White, ground).ShouldBeLessThan(1.3, "the look's white ink would all but vanish here");
                title.ShouldBeGreaterThanOrEqualTo(4.5);
                sub.ShouldBeGreaterThanOrEqualTo(4.5);
                icon.ShouldBeGreaterThanOrEqualTo(3);
            }
            finally
            {
                Close(scene);
            }
        });

    [Fact]
    public void Over_a_black_backdrop_the_ink_is_the_looks_own_white_and_reads()
        => UiHarness.OnUi(() =>
        {
            var words = Block("Readable");
            var scene = Show((_, _) => (0, 0, 0), words.Block);
            using var motion = AeroMotion.Force(true);
            try
            {
                scene.Pane.Ink.Laid.ShouldBeNull("nothing laid: the look as it was");
                scene.Pane.Resources.MergedDictionaries.ShouldBeEmpty();
                scene.Pane.Resources.Count.ShouldBe(0);
                words.Title.Foreground.ShouldBeSameAs(scene.Window.FindResource("A.B.Text"));
                words.Title.Effect.ShouldNotBeNull("the look's text shadow kept");
                var (title, sub, icon, _) = Measure(scene, "black", words)[0];
                title.ShouldBeGreaterThanOrEqualTo(4.5);
                sub.ShouldBeGreaterThanOrEqualTo(4.5);
                icon.ShouldBeGreaterThanOrEqualTo(3);
            }
            finally
            {
                Close(scene);
            }
        });

    /// <summary>A mid grey behind (too dark for the dark ink, too bright for the look's 60 % step): the white ink is laid
    /// held, every step as dense as it must be, and the quietest line reads at 3:1.</summary>
    [Fact]
    public void Over_a_mid_grey_the_white_ink_is_held()
        => UiHarness.OnUi(() =>
        {
            var words = Block("Held");
            words.Sub.SetResourceReference(TextBlock.ForegroundProperty, "A.B.Text3");
            var scene = Show((_, _) => (112, 112, 112), words.Block);
            using var motion = AeroMotion.Force(true);
            try
            {
                var tone = Contrast.Luminance(MidnightHost.AllOf<LiquidGlassBackdrop>(scene.Window).Single().Tone!.Value);
                output.WriteLine($"the glass's tone: luminance {tone:0.000}");
                tone.ShouldBeInRange(GlassInk.HeldFrom, GlassInk.ToLight);
                scene.Pane.Ink.Laid.ShouldBe(false, "the white ink laid, held");
                ((SolidColorBrush)words.Sub.Foreground).Color.ShouldBe(GlassInk.Family(dark: false, strict: false)["A.C.Text3"]);
                var (title, sub, icon, _) = Measure(scene, "grey", words)[0];
                title.ShouldBeGreaterThanOrEqualTo(4);
                sub.ShouldBeGreaterThanOrEqualTo(3, "the quietest step");
                icon.ShouldBeGreaterThanOrEqualTo(3);
            }
            finally
            {
                Close(scene);
            }
        });

    /// <summary>A pane over the edge of a white window, half over a black desktop: the pane takes the dark ink of its mean,
    /// and the words over the black side take the look's white again, so both sides read.</summary>
    [Fact]
    public void Over_the_edge_of_a_white_window_each_side_reads()
        => UiHarness.OnUi(() =>
        {
            var (left, right) = (Block("Dark side"), Block("Bright side"));
            Grid.SetColumn(right.Block, 1);
            var content = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition() }, Children = { left.Block, right.Block } };
            right.Block.Margin = new Thickness(24, 0, 0, 0);
            var scene = Show((x, _) => x < W / 2 ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255), content);
            using var motion = AeroMotion.Force(true);
            try
            {
                scene.Pane.Ink.Laid.ShouldBe(true, "the pane's mean is bright: dark ink");
                scene.Pane.Ink.Zones.ShouldBeGreaterThan(0, "the dark side's words take white ink of their own");
                ((SolidColorBrush)left.Title.Foreground).Color.ShouldBe(Colors.White);
                ((SolidColorBrush)right.Title.Foreground).Color.ShouldBe(GlassInk.Family(dark: true, strict: false)["A.C.Text"]);
                left.Title.Effect.ShouldNotBeNull("white ink keeps the look's text shadow");
                right.Title.Effect.ShouldBeNull();
                var measured = Measure(scene, "edge", left, right);
                foreach (var (title, sub, icon, _) in measured)
                {
                    title.ShouldBeGreaterThanOrEqualTo(4.5);
                    sub.ShouldBeGreaterThanOrEqualTo(4.5);
                    icon.ShouldBeGreaterThanOrEqualTo(3);
                }
                // The white window moves off: the whole pane over black, one ink again and nothing laid inside.
                scene.Source.Image = FakeGlassSource.Picture(W, H, (_, _) => (0, 0, 0));
                scene.Source.Raise();
                UiHarness.PumpUntil(() => scene.Pane.Ink.Laid == null && scene.Pane.Ink.Zones == 0, TimeSpan.FromSeconds(2), "one ink again");
                UiHarness.Pump(TimeSpan.FromMilliseconds(100));
                foreach (var element in new FrameworkElement[] { left.Block, left.Title, left.Sub, left.Icon })
                {
                    element.Resources.MergedDictionaries.ShouldBeEmpty();
                    element.Resources.Count.ShouldBe(0);
                }
                left.Block.ReadLocalValue(TextBlock.ForegroundProperty).ShouldBe(DependencyProperty.UnsetValue, "no pointer at a laid brush left");
                left.Title.ReadLocalValue(TextBlock.ForegroundProperty).ShouldBe(DependencyProperty.UnsetValue);
                left.Title.Foreground.ShouldBeSameAs(scene.Window.FindResource("A.B.Text"));
            }
            finally
            {
                Close(scene);
            }
        });

    /// <summary>Glass on glass (a bubble on a pane, the mockup's change against yesterday): each bubble reads the pane's
    /// glass where it lies, so a bubble over the dark side and one over the bright side each take the ink that reads.</summary>
    [Fact]
    public void A_bubble_on_a_pane_takes_the_ink_of_where_it_lies()
        => UiHarness.OnUi(() =>
        {
            GlassPanel Bubble(Words words) => new()
            {
                CornerRadius = new CornerRadius(20), Bubble = true, Padding = new Thickness(14, 8, 14, 8), HorizontalAlignment = HorizontalAlignment.Left,
                Content = words.Block,
            };
            var (left, right) = (Block("Dark side"), Block("Bright side"));
            var (leftBubble, rightBubble) = (Bubble(left), Bubble(right));
            Grid.SetColumn(rightBubble, 1);
            rightBubble.Margin = new Thickness(24, 0, 0, 0);
            var content = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition() }, Children = { leftBubble, rightBubble } };
            var scene = Show((x, _) => x < W / 2 ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255), content);
            using var motion = AeroMotion.Force(true);
            try
            {
                UiHarness.PumpUntil(() => leftBubble.IsNested && rightBubble.IsNested, TimeSpan.FromSeconds(2), "the bubbles on the pane");
                UiHarness.Pump(TimeSpan.FromMilliseconds(400));
                ((SolidColorBrush)left.Title.Foreground).Color.ShouldBe(Colors.White, "the bubble over the dark side: white");
                ((SolidColorBrush)right.Title.Foreground).Color.ShouldBe(GlassInk.Family(dark: true, strict: false)["A.C.Text"], "over the bright side: dark");
                foreach (var (title, sub, icon, _) in Measure(scene, "bubbles", left, right))
                {
                    title.ShouldBeGreaterThanOrEqualTo(4.5);
                    sub.ShouldBeGreaterThanOrEqualTo(4.5);
                    icon.ShouldBeGreaterThanOrEqualTo(3);
                }
            }
            finally
            {
                Close(scene);
            }
        });

    /// <summary>The backdrop turning from white to black and back: the ink eases (not at once, with motion), holds through
    /// the band, and lets go once it is the look's own again.</summary>
    [Fact]
    public void The_ink_eases_between_backdrops_and_holds_in_the_band()
        => UiHarness.OnUi(() =>
        {
            var words = Block("Readable");
            var scene = Show((_, _) => (255, 255, 255), words.Block);
            using var motion = AeroMotion.Force(false);
            try
            {
                scene.Pane.Ink.Laid.ShouldBe(true);
                var dark = GlassInk.Family(dark: true, strict: false)["A.C.Text"];
                ((SolidColorBrush)words.Title.Foreground).Color.ShouldBe(dark, "the first ink is laid at once");
                // A grey in the band: the dark ink holds.
                var band = (byte)Math.Round(GlassInk.GroundAt((GlassInk.ToDark + GlassInk.ToLight) / 2).R / LiquidGlassRecipe.Brightness);
                scene.Source.Image = FakeGlassSource.Picture(W, H, (_, _) => (band, band, band));
                scene.Source.Raise();
                UiHarness.Pump(TimeSpan.FromMilliseconds(400));
                scene.Pane.Ink.Laid.ShouldBe(true, "held in the band");
                // Black: eased back to white, then let go.
                scene.Source.Image = FakeGlassSource.Picture(W, H, (_, _) => (0, 0, 0));
                scene.Source.Raise();
                UiHarness.PumpUntil(() => scene.Pane.Ink.Laid == null, TimeSpan.FromSeconds(2), "the dark ink let go");
                var seen = new List<Color>();
                UiHarness.PumpUntil(() =>
                {
                    seen.Add(((SolidColorBrush)words.Title.Foreground).Color);
                    return scene.Pane.Resources.MergedDictionaries.Count == 0;
                }, TimeSpan.FromSeconds(2), "the pane back on the look's own ink");
                seen.ShouldContain(c => c != dark && c != Colors.White, "eased, not switched");
                words.Title.Foreground.ShouldBeSameAs(scene.Window.FindResource("A.B.Text"));
                // The same grey from the white side: the white ink holds too, held (its quieter steps denser: the look's
                // own 60 % step reads under 3:1 there).
                scene.Source.Image = FakeGlassSource.Picture(W, H, (_, _) => (band, band, band));
                scene.Source.Raise();
                UiHarness.Pump(TimeSpan.FromMilliseconds(400));
                scene.Pane.Ink.Laid.ShouldBe(false, "white held in the band");
                ((SolidColorBrush)words.Title.Foreground).Color.ShouldBe(Colors.White);
            }
            finally
            {
                Close(scene);
            }
        });
}
