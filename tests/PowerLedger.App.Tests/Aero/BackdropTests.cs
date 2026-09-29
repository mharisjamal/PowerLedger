using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan S G4 and G5: the backdrop decision, the wallpaper frosted once and held within the palette's bounds, the frost
/// lined up with the panes without a frame clock at rest, and the throttled parallax that stops once settled.
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]   // AeroMotion's override is one for the process (agent D's AeroMotionScope)
public class BackdropTests
{
    [Theory]
    [InlineData("Desktop", false, true, true, true, "SeeThrough")]
    [InlineData("Desktop", false, true, true, false, "SeeThrough")]
    [InlineData("Desktop", false, true, false, true, "Wallpaper")]
    [InlineData("Desktop", false, false, true, true, "Wallpaper")]
    [InlineData("Desktop", false, false, true, false, "Plain")]
    [InlineData("Desktop", false, true, false, false, "Plain")]
    [InlineData("Desktop", true, true, true, true, "Wallpaper")]
    [InlineData("Bloom", false, false, false, false, "Bloom")]
    [InlineData("Bloom", true, true, true, true, "Bloom")]
    [InlineData("Wallpaper", false, true, true, true, "Wallpaper")]
    [InlineData("Wallpaper", false, true, true, false, "Plain")]
    [InlineData("Plain", false, true, true, true, "Plain")]
    public void The_backdrop_is_see_through_only_where_windows_can_blur_it(string wanted, bool reduce, bool system, bool transparency, bool wallpaper, string expected)
        => BackdropRules.Choose(Enum.Parse<GlassBackdrop>(wanted), reduce, system, transparency, wallpaper).ShouldBe(Enum.Parse<BackdropKind>(expected));

    [Fact]
    public void The_wallpaper_fills_the_scene_as_uniform_to_fill_draws_it()
        => Fill();

    [Theory]
    [InlineData(0, "t", "t")]
    [InlineData(2, "t", "t")]
    [InlineData(3, "t", "t")]
    [InlineData(null, "t", "t")]
    [InlineData(1, "t", null)]
    [InlineData(2, null, null)]
    public void A_slideshow_shows_the_copy_windows_keeps_and_a_solid_colour_none(int? type, string? transcoded, string? expected)
        => WallpaperRules.Fallback(type, transcoded).ShouldBe(expected);

    private static void Fill()
    {
        var rect = WallpaperFrost.ImageRect(new Size(1000, 500), new Size(1920, 1200));
        rect.Width.ShouldBe(1000, 0.01);
        rect.Height.ShouldBe(625, 0.01);
        rect.X.ShouldBe(0, 0.01);
        rect.Y.ShouldBe(-62.5, 0.01);
    }

    [Fact]
    public void The_frost_blurs_and_holds_the_scene_within_the_palettes_bounds()
        => UiHarness.OnUi(() =>
        {
            var halves = Picture(960, 600, x => x < 480 ? Colors.White : Colors.Black);
            var brightest = (Color)ThemeManager.Palette(Look.Aero, Theme.Dark)["A.C.BackdropBrightest"];
            var darkest = (Color)ThemeManager.Palette(Look.Aero, Theme.Light)["A.C.BackdropDarkest"];

            var dark = Pixels(WallpaperFrost.Frost(halves, 10, 1.55, Colors.Black, brightest));
            dark.Max(Contrast.Luminance).ShouldBeLessThanOrEqualTo(Contrast.Luminance(brightest) + 0.002, "no brighter than the dark palette allows");
            var middle = dark[150 * WallpaperFrost.FrostWidth + WallpaperFrost.FrostWidth / 2];
            Contrast.Luminance(middle).ShouldBeGreaterThan(0.001, "the edge is blurred into a ramp");
            Contrast.Luminance(dark[150 * WallpaperFrost.FrostWidth + WallpaperFrost.FrostWidth - 1]).ShouldBe(0, 0.001, "black stays black");

            var light = Pixels(WallpaperFrost.Frost(halves, 10, 1.55, darkest, Colors.White));
            light.Min(Contrast.Luminance).ShouldBeGreaterThanOrEqualTo(Contrast.Luminance(darkest) - 0.002, "no darker than the light palette allows");

            var sharp = Pixels(WallpaperFrost.Frost(halves, 0, 1, Colors.Black, Colors.White));
            Contrast.Luminance(sharp[150 * WallpaperFrost.FrostWidth + WallpaperFrost.FrostWidth / 2 - 2]).ShouldBe(1, 0.01, "no frost, no blur");
        });

    [Fact]
    public void The_frost_keeps_the_hue_as_it_dims()
        => UiHarness.OnUi(() =>
        {
            var blue = Picture(200, 125, _ => Color.FromRgb(0x60, 0x90, 0xFF));
            var frosted = Pixels(WallpaperFrost.Frost(blue, 4, 1, Colors.Black, Color.FromRgb(0x22, 0x22, 0x22)))[1000];
            frosted.B.ShouldBeGreaterThan(frosted.R, "still blue");
            frosted.B.ShouldBeGreaterThan(frosted.G);
        });

    /// <summary>Plan V: the dark bright glass over the darkest of desktops (the black corner of a wallpaper under the
    /// sidebar) is still lit glass, as the demo's reads, never a near-black slab beside brighter panes: the frost is held
    /// above A.C.FrostDarkest as well as under A.C.FrostBrightest. The strict glass keeps black.</summary>
    [Fact]
    public void The_dark_bright_frost_lifts_black_to_its_floor_and_the_strict_one_keeps_it()
        => UiHarness.OnUi(() =>
        {
            var dark = ThemeManager.Palette(Look.Aero, Theme.Dark);
            var floor = (Color)dark["A.C.FrostDarkest"];
            var ceiling = (Color)dark["A.C.FrostBrightest"];
            Contrast.Luminance(floor).ShouldBeGreaterThan(Contrast.Luminance((Color)dark["A.C.BackdropDarkest"]) + 0.02, "the bright glass has a floor above black");
            Contrast.Luminance(floor).ShouldBeLessThan(Contrast.Luminance(ceiling));

            var black = Picture(200, 125, _ => Color.FromRgb(0x02, 0x03, 0x02));
            var lifted = Pixels(WallpaperFrost.Frost(black, 4, 1.55, floor, ceiling));
            lifted.Min(Contrast.Luminance).ShouldBe(Contrast.Luminance(floor), 0.003, "black lifted to the floor");
            var strict = Pixels(WallpaperFrost.Frost(black, 4, 1.55, (Color)dark["A.C.BackdropDarkest"], (Color)dark["A.C.BackdropBrightest"]));
            strict.Max(Contrast.Luminance).ShouldBeLessThan(0.002, "the strict glass lets black through");

            var green = Picture(200, 125, _ => Color.FromRgb(0x10, 0x30, 0x10));
            var tree = Pixels(WallpaperFrost.Frost(green, 4, 1, floor, ceiling))[1000];
            tree.G.ShouldBeGreaterThan(tree.R, "a dark green lifted is still green");
        });

    [Fact]
    public void The_frost_radius_follows_the_frost_token()
        => UiHarness.OnUi(() =>
        {
            WallpaperFrost.Radius(GlassSettings.Default, new Border()).ShouldBe(10, 0.01, "the demo's 26 px, a pixel of the copy about 2.6 on screen");
            var host = new Border();
            host.Resources["A.Glass.Frost"] = 0.0;
            WallpaperFrost.Radius(GlassSettings.Default, host).ShouldBe(0);
        });

    /// <summary>The wallpaper frosted once, off the UI thread, on the panes lined up with the scene; moved on layout;
    /// taken off for see-through or plain; and no frame clock left at rest.</summary>
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void The_wallpaper_is_frosted_once_and_lined_up_with_each_pane(string themeName)
        => UiHarness.OnUi(() =>
        {
            var theme = Enum.Parse<Theme>(themeName);
            var path = Wallpaper(theme);
            var window = AeroGlassSample.Window(theme);
            var scene = AeroGlassSample.SceneOf(window);
            var frost = new WallpaperFrost(window, scene);
            try
            {
                window.Show();
                window.UpdateLayout();
                var made = 0;
                frost.Made += () => made++;
                frost.Show(path, GlassSettings.Default, theme);
                for (var i = 0; i < 100 && made == 0; i++) UiHarness.Pump(TimeSpan.FromMilliseconds(30));
                made.ShouldBe(1, "frosted once");
                scene.Background.ShouldBeOfType<ImageBrush>().Stretch.ShouldBe(Stretch.UniformToFill);
                var pane = UiHarness.Find<GlassPanel>(window)!;
                var brush = pane.Frost.ShouldBeOfType<ImageBrush>();
                brush.ImageSource.ShouldBeSameAs(frost.Frosted);
                var expected = scene.TransformToVisual(pane).TransformBounds(WallpaperFrost.ImageRect(scene.RenderSize, new Size(1920, 1200)));
                brush.Viewport.X.ShouldBe(expected.X, 0.05);
                brush.Viewport.Width.ShouldBe(expected.Width, 0.05);
                UiHarness.Render(window, AeroGlassSample.Width, AeroGlassSample.Height, $"aero-glass-wallpaper-{themeName.ToLowerInvariant()}.png");

                frost.Show(path, GlassSettings.Default, theme);
                UiHarness.Pump(TimeSpan.FromMilliseconds(200));
                made.ShouldBe(1, "the same wallpaper, frost and theme are not frosted again");

                var before = brush.Viewport;
                pane.Margin = new Thickness(30, 0, 0, 0);
                window.UpdateLayout();
                brush.Viewport.X.ShouldBe(before.X - 30, 0.05, "a layout pass lines it up again");

                WallpaperFrost.RenderingHooks.ShouldBe(0, "nothing runs per frame at rest");
                frost.AlignFor(TimeSpan.FromMilliseconds(100));
                WallpaperFrost.RenderingHooks.ShouldBe(1);
                UiHarness.Pump(TimeSpan.FromMilliseconds(300));
                WallpaperFrost.RenderingHooks.ShouldBe(0, "the frame clock is let go once the time is up");

                frost.Show(null, GlassSettings.Default, theme);
                pane.Frost.ShouldBeNull("see-through or plain: the system backdrop or nothing does the blur");
            }
            finally
            {
                frost.Dispose();
                window.Close();
                File.Delete(path);
            }
        });

    [Fact]
    public void The_parallax_steps_at_thirty_hertz_toward_the_pointer_and_stops_once_settled()
        => UiHarness.OnUi(() =>
        {
            using var reduced = AeroMotion.Force(false);
            var window = new Window();
            var scene = new Border();
            var tilt = new Grid();
            var moves = 0;
            var parallax = new Parallax(window, scene, tilt, () => moves++) { Enabled = true };
            try
            {
                Parallax.Step.ShouldBe(1 - 0.94 * 0.94, 1e-12, "the HTML's 6 % a frame at 60 Hz, at 30 Hz");
                parallax.Running.ShouldBeFalse("a still pointer costs nothing");
                parallax.PointAt(0.5, 0.5);
                parallax.Running.ShouldBeTrue();
                parallax.Tick();
                parallax.Offset.X.ShouldBeLessThan(0, "the scene drifts against the pointer");
                parallax.Skew.Y.ShouldBeGreaterThan(0, "the panes tilt toward it");
                for (var i = 0; i < 400 && parallax.Running; i++) parallax.Tick();
                parallax.Running.ShouldBeFalse("settled, the timer stops");
                parallax.Offset.X.ShouldBe(-Parallax.ShiftX / 2, 0.01);
                parallax.Offset.Y.ShouldBe(-Parallax.ShiftY / 2, 0.01);
                moves.ShouldBeGreaterThan(0, "each step lines the frost up");
                ((TransformGroup)scene.RenderTransform).Children.OfType<ScaleTransform>().Single().ScaleX.ShouldBe(Parallax.SceneScale);

                using (AeroMotion.Force(true))
                {
                    UiHarness.Pump(TimeSpan.FromMilliseconds(30));
                    parallax.Offset.ShouldBe((0.0, 0.0), "reduced motion: back to rest with no travel");
                    parallax.PointAt(-0.5, 0.2);
                    parallax.Running.ShouldBeFalse("and no stepping at all");
                }
                parallax.Enabled = false;
                parallax.Offset.ShouldBe((0.0, 0.0));
                parallax.Skew.ShouldBe((0.0, 0.0));
                WallpaperFrost.RenderingHooks.ShouldBe(0);
            }
            finally
            {
                parallax.Dispose();
                window.Close();
            }
        });

    [Theory]
    [InlineData(0.0, 0.5)]
    [InlineData(0.3, -0.2)]
    public void Approach_moves_part_of_the_way_and_snaps_once_close(double from, double to)
    {
        var next = Parallax.Approach(from, to);
        Math.Abs(to - next).ShouldBeLessThan(Math.Abs(to - from));
        Parallax.Approach(to - 0.0001, to).ShouldBe(to);
    }

    /// <summary>A plain backdrop needs no wallpaper and no DWM: the window paints the palette's plain ground.</summary>
    [Fact]
    public void A_plain_backdrop_paints_the_palettes_ground()
        => UiHarness.OnUi(() =>
        {
            var window = AeroGlassSample.Window(Theme.Dark);
            var settings = new GlassSettings { Backdrop = GlassBackdrop.Plain };
            using var material = new GlassMaterial(window, () => settings, () => Theme.Dark);
            var backdrop = new Backdrop(window, AeroGlassSample.SceneOf(window), material, () => Theme.Dark);
            try
            {
                window.Show();
                backdrop.Kind.ShouldBe(BackdropKind.Plain);
                ((SolidColorBrush)AeroGlassSample.SceneOf(window).Background).Color.ShouldBe((Color)ThemeManager.Palette(Look.Aero, Theme.Dark)["A.C.Plain"]);
                UiHarness.Find<GlassPanel>(window)!.Frost.ShouldBeNull();
                backdrop.Parallax.Enabled.ShouldBeFalse("nothing of its own to drift");
            }
            finally
            {
                backdrop.Dispose();
                window.Close();
            }
        });

    /// <summary>Plan S G5: the glass system at rest adds no CompositionTarget.Rendering handler and runs no timer: a window
    /// with the material, the wallpaper frost and the parallax on, shown and left alone, asks for no frames.</summary>
    [Fact]
    public void At_rest_the_glass_asks_for_no_frames()
        => UiHarness.OnUi(() =>
        {
            using var motion = AeroMotion.Force(false);
            var before = RenderingHandlers();
            var path = Wallpaper(Theme.Dark);
            var window = AeroGlassSample.Window(Theme.Dark);
            var scene = AeroGlassSample.SceneOf(window);
            using var material = new GlassMaterial(window, () => GlassSettings.Default, () => Theme.Dark);
            var frost = new WallpaperFrost(window, scene);
            var parallax = new Parallax(window, scene, null, frost.Align) { Enabled = true };
            try
            {
                window.Show();
                var made = false;
                frost.Made += () => made = true;
                frost.Show(path, GlassSettings.Default, Theme.Dark);
                for (var i = 0; i < 100 && !made; i++) UiHarness.Pump(TimeSpan.FromMilliseconds(30));
                made.ShouldBeTrue();
                frost.AlignFor(TimeSpan.FromMilliseconds(60));
                RenderingHandlers().ShouldBe(before + 1, "the intro's alignment runs by the frame, for its time only");
                parallax.PointAt(0.3, -0.2);
                parallax.Running.ShouldBeTrue("the parallax steps on its 30 Hz timer while the pointer moves");
                UiHarness.Pump(TimeSpan.FromMilliseconds(250));
                for (var i = 0; i < 400 && parallax.Running; i++) UiHarness.Pump(TimeSpan.FromMilliseconds(40));
                UiHarness.Pump(TimeSpan.FromMilliseconds(100));
                parallax.Running.ShouldBeFalse("settled, the parallax stops its timer");
                WallpaperFrost.RenderingHooks.ShouldBe(0);
                RenderingHandlers().ShouldBe(before, "at rest, no handler asks for frames");
            }
            finally
            {
                parallax.Dispose();
                frost.Dispose();
                window.Close();
                File.Delete(path);
            }
        });

    /// <summary>How many handlers CompositionTarget.Rendering has on this thread: WPF keeps them on the dispatcher's
    /// MediaContext, whose event is not public.</summary>
    internal static int RenderingHandlers()
    {
        var type = typeof(CompositionTarget).Assembly.GetType("System.Windows.Media.MediaContext", throwOnError: true)!;
        var from = type.GetMethod("From", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var context = from.Invoke(null, [System.Windows.Threading.Dispatcher.CurrentDispatcher])!;
        var field = type.GetField("Rendering", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("MediaContext keeps its Rendering handlers elsewhere in this WPF.");
        return (field.GetValue(context) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    private static BitmapSource Picture(int width, int height, Func<int, Color> at)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var c = at(x);
                var i = (y * width + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (c.B, c.G, c.R, 255);
            }
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static Color[] Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return Enumerable.Range(0, bitmap.PixelWidth * bitmap.PixelHeight).Select(i => Color.FromRgb(pixels[i * 4 + 2], pixels[i * 4 + 1], pixels[i * 4])).ToArray();
    }

    /// <summary>A 1920 by 1200 wallpaper like the demo's scene, with a bright bloom, as a PNG in the test's TEMP.</summary>
    private static string Wallpaper(Theme theme)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(AeroGlassSample.Scene(theme), null, new Rect(0, 0, 1920, 1200));
            context.DrawEllipse(new RadialGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255)), null, new Point(1300, 300), 420, 300);
        }
        var bitmap = new RenderTargetBitmap(1920, 1200, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(Path.GetTempPath(), $"aero-wallpaper-{theme}-{Guid.NewGuid():N}.png");
        using (var file = File.Create(path)) png.Save(file);
        return path;
    }
}
