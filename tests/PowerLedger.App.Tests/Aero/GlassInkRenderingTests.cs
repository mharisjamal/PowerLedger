using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerLedger.App.Aero;
using Shouldly;
using Xunit.Abstractions;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan AD in the harness: a pane of Aero glass with text and an icon, its engine handed a white and then a black
/// backdrop (the CPU path, as the live capture hands it the screen), drawn over that backdrop; the ink's contrast is
/// measured on the drawn pixels against the same spot drawn with no ink. Renders go to %TEMP%\powerledger-renders.
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]
public class GlassInkRenderingTests(ITestOutputHelper output)
{
    private const int W = 480, H = 240;

    private sealed record Scene(Window Window, GlassPanel Pane, TextBlock Title, TextBlock Sub, Icon Icon, FakeGlassSource Source, GlassMaterial Material);

    /// <summary>The pane over a backdrop of <paramref name="grey"/>, shown off screen and settled.</summary>
    private static Scene Show(byte grey)
    {
        var ground = FakeGlassSource.Picture(W, H, (_, _) => (grey, grey, grey));
        FakeGlassSource? fake = null;
        LiquidGlassSources.Override = source =>
        {
            var behind = (FakeGlassSource)LiquidGlassProofTests.Behind(ground, source, W, H);
            return fake = behind;
        };
        var title = new TextBlock { Text = "Readable", FontSize = 28, FontWeight = FontWeights.SemiBold };
        var sub = new TextBlock { Text = "the quieter line", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "A.B.Text2");
        var icon = new Icon { Size = 40, StrokeWidth = 4, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        var pane = new GlassPanel
        {
            Margin = new Thickness(30), Padding = new Thickness(28, 20, 28, 20),
            Content = new StackPanel { Children = { title, sub, icon } },
        };
        var window = AeroHost.Dressed(new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = 0, Width = W, Height = H, Background = Brushes.Transparent, Content = pane, UseLayoutRounding = true,
        }, Theme.Dark);
        icon.Data = (System.Windows.Media.Geometry)window.FindResource("A.I.Check");
        var material = new GlassMaterial(window, () => GlassSettings.Default, () => Theme.Dark);
        window.Show();
        UiHarness.Pump(TimeSpan.FromMilliseconds(500));
        window.UpdateLayout();
        return new Scene(window, pane, title, sub, icon, fake!, material);
    }

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

    private (double Title, double Sub, double Icon, Color Ground) Measure(Scene scene, string name)
    {
        var (with, bitmap) = Draw(scene);
        LiquidGlassProofTests.Write(bitmap, System.IO.Path.Combine(UiHarness.Folder, $"aero-ink-{name}.png"));
        foreach (var e in new UIElement[] { scene.Title, scene.Sub, scene.Icon }) e.Opacity = 0;
        var (without, _) = Draw(scene);
        foreach (var e in new UIElement[] { scene.Title, scene.Sub, scene.Icon }) e.Opacity = 1;
        var middle = scene.Pane.TransformToAncestor(scene.Window).Transform(new Point(scene.Pane.ActualWidth - 20, scene.Pane.ActualHeight / 2));
        var at = ((int)middle.Y * W + (int)middle.X) * 4;
        var ground = Color.FromRgb(without[at + 2], without[at + 1], without[at]);
        var result = (InkContrast(scene, scene.Title, with, without), InkContrast(scene, scene.Sub, with, without), InkContrast(scene, scene.Icon, with, without), ground);
        output.WriteLine($"{name}: glass {ground}, title {result.Item1:0.0}:1, quieter line {result.Item2:0.0}:1, icon {result.Item3:0.0}:1");
        return result;
    }

    [Fact]
    public void Over_a_white_backdrop_the_ink_turns_dark_and_reads()
        => UiHarness.OnUi(() =>
        {
            using var motion = AeroMotion.Force(true);
            var scene = Show(255);
            try
            {
                scene.Pane.Ink.Laid.ShouldBe(true, "dark ink laid on the pane");
                ((SolidColorBrush)scene.Title.Foreground).Color.ShouldBe(GlassInk.Family(dark: true, strict: false)["A.C.Text"]);
                scene.Title.Effect.ShouldBeNull("no text shadow under dark ink");
                var (title, sub, icon, ground) = Measure(scene, "white");
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
            using var motion = AeroMotion.Force(true);
            var scene = Show(0);
            try
            {
                scene.Pane.Ink.Laid.ShouldBeNull("nothing laid: the look as it was");
                scene.Pane.Resources.MergedDictionaries.ShouldBeEmpty();
                scene.Title.Foreground.ShouldBeSameAs(scene.Window.FindResource("A.B.Text"));
                scene.Title.Effect.ShouldNotBeNull("the look's text shadow kept");
                var (title, sub, icon, _) = Measure(scene, "black");
                title.ShouldBeGreaterThanOrEqualTo(4.5);
                sub.ShouldBeGreaterThanOrEqualTo(4.5);
                icon.ShouldBeGreaterThanOrEqualTo(3);
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
            using var motion = AeroMotion.Force(false);
            var scene = Show(255);
            try
            {
                scene.Pane.Ink.Laid.ShouldBe(true);
                var dark = GlassInk.Family(dark: true, strict: false)["A.C.Text"];
                ((SolidColorBrush)scene.Title.Foreground).Color.ShouldBe(dark, "the first ink is laid at once");
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
                    seen.Add(((SolidColorBrush)scene.Title.Foreground).Color);
                    return scene.Pane.Resources.MergedDictionaries.Count == 0;
                }, TimeSpan.FromSeconds(2), "the pane back on the look's own ink");
                seen.ShouldContain(c => c != dark && c != Colors.White, "eased, not switched");
                scene.Title.Foreground.ShouldBeSameAs(scene.Window.FindResource("A.B.Text"));
                // The same grey from the white side: the white ink holds too.
                scene.Source.Image = FakeGlassSource.Picture(W, H, (_, _) => (band, band, band));
                scene.Source.Raise();
                UiHarness.Pump(TimeSpan.FromMilliseconds(400));
                scene.Pane.Ink.Laid.ShouldBeNull("held in the band");
            }
            finally
            {
                Close(scene);
            }
        });
}
