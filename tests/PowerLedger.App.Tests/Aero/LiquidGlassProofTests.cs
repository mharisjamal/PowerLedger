using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// 0.10.9, the owner's liquid glass: the glowing edge and the drop shadow measured against the recipe's CSS, pixel by
/// pixel, and (with PL_PROOF_DIR set to a folder holding the mockup's backgrounds rendered in Edge, ref\Main-bg.png and
/// the rest) the App's pages and controls drawn over the same backgrounds for a side-by-side with the mockup.
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]
public class LiquidGlassProofTests
{
    /// <summary>Where the proof renders go, or null: a proof is drawn only when asked for.</summary>
    private static string? Proof => Environment.GetEnvironmentVariable("PL_PROOF_DIR") is { Length: > 0 } dir && Directory.Exists(dir) ? dir : null;

    /// <summary>The CSS model of the inner glow, <c>inset 0 0 8px 1px</c> white at 70 %, at a pixel's middle: what the
    /// browser draws (Edge measured 95, 79, 62, 47, 34, 23, 15, 9, 5, 3, 1, 1 of 255 in from a straight edge).</summary>
    [Fact]
    public void The_glow_is_the_recipes_inset_shadows_at_every_pixel_of_a_straight_edge()
        => UiHarness.OnUi(() =>
        {
            byte[] edge = [95, 79, 62, 47, 34, 23, 15, 9, 5, 3, 1, 1, 0];
            for (var i = 0; i < edge.Length; i++)
                (GlassGlow.Glow(i + 0.5, 0.7) * 255).ShouldBe(edge[i], 3.5, $"the model, pixel {i} in");
            var (pixels, stride) = Draw(new GlassGlow { Width = 400, Height = 200 }, 400, 200, Colors.Black);
            for (var i = 0; i < edge.Length; i++)
            {
                ((double)pixels[(100 * stride) + (i * 4) + 2]).ShouldBe(edge[i], 4, $"the left edge, pixel {i} in");
                ((double)pixels[(i * stride) + (200 * 4) + 2]).ShouldBe(edge[i], 4, $"the top edge, pixel {i} in");
            }
        });

    /// <summary>The corner light, <c>inset 6px 6px 0 -6px</c>: a sharp crescent in the top-left corner, about two pixels
    /// thick at 45 degrees (Edge: 145 and 179 of 255 there over the glow), none in the other corners.</summary>
    [Fact]
    public void The_corner_light_is_a_crescent_in_the_top_left_corner_only()
        => UiHarness.OnUi(() =>
        {
            var (pixels, stride) = Draw(new GlassGlow { Width = 400, Height = 200 }, 400, 200, Colors.Black);
            byte At(int x, int y) => pixels[(y * stride) + (x * 4) + 2];
            Math.Max(At(8, 8), At(9, 9)).ShouldBeGreaterThan((byte)150, "lit on the diagonal, top left");
            Math.Max(At(391, 8), At(390, 9)).ShouldBeLessThan((byte)110, "only the glow top right");
            Math.Max(At(8, 191), At(9, 190)).ShouldBeLessThan((byte)110, "only the glow bottom left");
        });

    /// <summary>The recipe's drop shadow of an opaque piece: black at 37 % blurred by a Gaussian of deviation 46 (drop-
    /// shadow()'s third length is the deviation, measured in Edge), 8 left and 10 up.</summary>
    [Fact]
    public void The_drop_shadow_is_the_recipes_gaussian()
        => UiHarness.OnUi(() =>
        {
            var map = GlassShadow.DropMapFor(400, 200, 1, 0).ShouldNotBeNull();
            // Edge, at the middle of a 400 by 200 black pane on white: the shadow's alpha 10, 50 and 90 px left of it.
            var (pixels, stride) = Draw(new GlassShadow { Width = 400, Height = 200, Tint = Brushes.Black, Glow = 0, Casts = true }, 400, 200, Colors.White, room: 200);
            double Alpha(int x, int y) => 255 - pixels[((y + 200) * stride) + ((x + 200) * 4) + 2];
            Alpha(-10, 100).ShouldBe(0.1725 * 255, 3, "10 px out");
            Alpha(-50, 100).ShouldBe(0.0667 * 255, 3, "50 px out");
            Alpha(-90, 100).ShouldBe(0.0118 * 255, 2, "90 px out");
            Alpha(410, 100).ShouldBe(0.1216 * 255, 3, "10 px out on the right, the shadow thrown left");
            map.PixelWidth.ShouldBeLessThan(200, "a small bitmap, drawn stretched");
        });

    /// <summary>An untinted pane's shadow is its glowing edge's: a CSS filter shadows what the piece paints, and its
    /// backdrop is not that (Edge: a piece with a backdrop filter only casts none). It is faint, about 0.7 % at most.</summary>
    [Fact]
    public void An_untinted_panes_shadow_is_its_edges_and_faint()
    {
        var map = UiHarness.OnUi(() => GlassShadow.DropMapFor(400, 200, 0, LiquidGlassRecipe.HighlightOpacity)!);
        var pixels = new byte[map.PixelWidth * map.PixelHeight * 4];
        UiHarness.OnUi(() => map.CopyPixels(pixels, map.PixelWidth * 4, 0));
        var most = Enumerable.Range(0, pixels.Length / 4).Max(i => pixels[(i * 4) + 3]);
        most.ShouldBeInRange((byte)1, (byte)3, "Edge: 0.66 % at the most (1.7 of 255)");
    }

    /// <summary>The glass pieces' edge and shadow at the scale the proof asks for, to PNGs beside the browser's.</summary>
    [Fact]
    public void The_glow_and_shadow_proof_renders()
    {
        if (Proof is not { } dir) return;
        UiHarness.OnUi(() =>
        {
            var out1 = Path.Combine(dir, "wpf");
            Directory.CreateDirectory(out1);
            foreach (var (scale, suffix) in new[] { (1.0, "1x"), (1.25, "125") })
            {
                GlassGlow.ScaleOverride = scale;
                // glow.html: a pane, a pill, each highlight alone, a small bubble and the grab bar, on black.
                var glow = new Canvas { Width = 1000, Height = 700, Background = Brushes.Black };
                void Glow(double x, double y, double w, double h, double r, bool corner = true, bool inner = true)
                {
                    FrameworkElement piece = new GlassGlow { CornerRadius = new CornerRadius(r), DrawsCorner = corner, DrawsInner = inner };
                    piece.Width = w;
                    piece.Height = h;
                    Canvas.SetLeft(piece, x);
                    Canvas.SetTop(piece, y);
                    glow.Children.Add(piece);
                }
                Glow(40, 40, 400, 200, 28);
                Glow(500, 40, 200, 50, 25);
                Glow(40, 280, 400, 200, 28, inner: false);
                Glow(500, 280, 400, 200, 28, corner: false);
                Glow(740, 40, 60, 32, 12);
                Glow(840, 40, 66, 7, 3.5);
                Save(glow, 1000, 700, scale, Path.Combine(out1, $"glow-{suffix}.png"));

                // shadow.html: an opaque black pane and pill casting the filter's shadow on white.
                var shadow = new Canvas { Width = 1000, Height = 800, Background = Brushes.White };
                void Cast(double x, double y, double w, double h, double r)
                {
                    var s = new GlassShadow { Width = w, Height = h, Tint = Brushes.Black, Glow = 0, Casts = true, CornerRadius = new CornerRadius(r) };
                    var body = new Border { Width = w, Height = h, Background = Brushes.Black, CornerRadius = new CornerRadius(r) };
                    foreach (var e in new FrameworkElement[] { s, body })
                    {
                        Canvas.SetLeft(e, x);
                        Canvas.SetTop(e, y);
                        shadow.Children.Add(e);
                    }
                }
                Cast(200, 200, 400, 200, 28);
                Cast(700, 200, 200, 50, 25);
                Save(shadow, 1000, 800, scale, Path.Combine(out1, $"shadow-{suffix}.png"));
                GlassGlow.ScaleOverride = null;
            }
        });
    }

    /// <summary>Draws <paramref name="element"/> alone at 96 DPI over <paramref name="ground"/>, with
    /// <paramref name="room"/> round it, and hands back its pixels (BGRA) and their stride.</summary>
    private static (byte[] Pixels, int Stride) Draw(FrameworkElement element, int width, int height, Color ground, int room = 0)
    {
        var host = new Grid { Width = width + 2 * room, Height = height + 2 * room, Background = new SolidColorBrush(ground) };
        element.Margin = new Thickness(room);
        element.HorizontalAlignment = HorizontalAlignment.Left;
        element.VerticalAlignment = VerticalAlignment.Top;
        host.Children.Add(element);
        host.Measure(new Size(host.Width, host.Height));
        host.Arrange(new Rect(0, 0, host.Width, host.Height));
        host.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)host.Width, (int)host.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return (pixels, stride);
    }

    /// <summary>Lays <paramref name="visual"/> out at its size and saves it at <paramref name="scale"/> to a PNG.</summary>
    internal static void Save(FrameworkElement visual, int width, int height, double scale, string path)
    {
        visual.Measure(new Size(width, height));
        visual.Arrange(new Rect(0, 0, width, height));
        visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Round(width * scale), (int)Math.Round(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        Write(bitmap, path);
    }

    internal static void Write(BitmapSource bitmap, string path)
    {
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        png.Save(file);
    }

    /// <summary>
    /// Every Aero page, in both themes, at 1440 by 900, laid over the mockup's own background (Main's, rendered alone in
    /// Edge): the App's glass has no picture of what is behind it in a render but the one the harness hands the engine,
    /// and where the engine draws nothing yet, the background shows through as the see-through window shows the desktop.
    /// </summary>
    [Fact]
    public void Every_page_over_the_mockups_background()
    {
        if (Proof is not { } dir) return;
        var background = Path.Combine(dir, "ref", "Main-bg.png");
        if (!File.Exists(background)) return;
        var pages = Path.Combine(dir, "pages");
        Directory.CreateDirectory(pages);
        foreach (var theme in new[] { Theme.Dark, Theme.Light })
        {
            foreach (var page in new[] { Page.Dashboard, Page.Breakdown, Page.Parts, Page.Insights, Page.Report, Page.Household, Page.Settings })
            {
                using var saver = new FakeSaver();
                UiHarness.OnUi(() =>
                {
                    var ground = Load(background);
                    var window = AeroHost.Window(AeroFixtures.Shell(saver), theme);
                    using var motion = AeroMotion.Force(true);
                    window.Width = 1440;
                    window.Height = 900;
                    window.Show();
                    try
                    {
                        UiHarness.Pump(TimeSpan.FromMilliseconds(500));
                        window.Page = page;
                        UiHarness.Pump(TimeSpan.FromMilliseconds(500));
                        window.UpdateLayout();
                        Write(Over(ground, window, 1440, 900), Path.Combine(pages, $"{page}-{theme}.png"));
                    }
                    finally
                    {
                        window.CloseForSwitch();
                    }
                });
            }
        }
    }

    /// <summary><paramref name="visual"/> drawn over <paramref name="ground"/>, both at 96 DPI.</summary>
    internal static BitmapSource Over(BitmapSource ground, Visual visual, int width, int height)
    {
        var app = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        app.Render(visual);
        var both = new DrawingVisual();
        using (var dc = both.RenderOpen())
        {
            dc.DrawImage(ground, new Rect(0, 0, width, height));
            dc.DrawImage(app, new Rect(0, 0, width, height));
        }
        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        result.Render(both);
        result.Freeze();
        return result;
    }

    internal static BitmapSource Load(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
