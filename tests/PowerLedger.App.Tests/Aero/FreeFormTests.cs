using System.Windows;
using System.Windows.Media;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// 0.10.1, Aero free-form over the desktop: the window's shape is the union of its glass surfaces in device pixels, a
/// pixel wider all round so each rim's soft edge is kept; a surface a scroll viewport cuts is cut straight there; the
/// window resizes at the shape's outer edges and at its grip; and it opens spread over the work area until the user
/// chooses the smaller centred layout, which it then remembers with its bounds.
/// </summary>
public class FreeFormTests
{
    private static readonly Rect Everywhere = new(-1e5, -1e5, 2e5, 2e5);

    [Fact]
    public void A_surface_becomes_its_rounded_rectangle_a_pixel_wider_all_round()
    {
        var pieces = RegionMath.Pieces([new Surface(new Rect(10, 20, 100, 50), 10, Everywhere)], 1, new Size(800, 600));

        var piece = pieces.ShouldHaveSingleItem();
        piece.ShouldBe(new RegionPiece(9, 19, 111, 71, 22, 9, 19, 111, 71));
        piece.Clipped.ShouldBeFalse();
    }

    [Fact]
    public void At_125_percent_the_rectangle_is_rounded_outward_and_the_corner_scaled()
    {
        var piece = RegionMath.Pieces([new Surface(new Rect(10.3, 20, 100, 50), 20, Everywhere)], 1.25, new Size(800, 600)).Single();

        piece.Left.ShouldBe(11, "floor(12.875) less the pixel");
        piece.Right.ShouldBe(139, "ceil(137.875) and the pixel");
        piece.Top.ShouldBe(24);
        piece.Bottom.ShouldBe(89);
        piece.Corner.ShouldBe(52, "the diameter: 2 by (20 at 1.25, and the pixel)");
    }

    [Fact]
    public void A_surface_a_viewport_cuts_is_cut_straight_along_the_viewport()
    {
        var piece = RegionMath.Pieces([new Surface(new Rect(0, 100, 200, 300), 20, new Rect(-50, 150, 1000, 1000))], 1, new Size(800, 600)).Single();

        piece.Top.ShouldBe(99, "the rounded rectangle keeps its own top");
        piece.ClipTop.ShouldBe(150, "and is cut where the viewport starts");
        (piece.ClipLeft, piece.ClipRight, piece.ClipBottom).ShouldBe((piece.Left, piece.Right, piece.Bottom), "edges the viewport doesn't cut keep the extra pixel");
        piece.Clipped.ShouldBeTrue();
    }

    [Fact]
    public void Surfaces_out_of_sight_or_empty_are_left_out()
    {
        RegionMath.Pieces(
        [
            new Surface(new Rect(0, 0, 100, 100), 10, new Rect(0, 200, 500, 500)),   // scrolled away
            new Surface(new Rect(0, 0, 0, 40), 10, Everywhere),                        // collapsed
            new Surface(new Rect(900, 0, 100, 100), 10, Everywhere),                   // off the window
        ], 1, new Size(800, 600)).ShouldBeEmpty();
    }

    [Fact]
    public void A_corner_never_asks_for_more_than_the_piece_is_high()
    {
        var piece = RegionMath.Pieces([new Surface(new Rect(0, 0, 100, 30), 999, Everywhere)], 1, new Size(800, 600)).Single();

        piece.Corner.ShouldBe(32, "a capsule: its height with the pixel above and below");
    }

    [Theory]
    [InlineData(102, 300, RegionMath.HtLeft)]
    [InlineData(897, 300, RegionMath.HtRight)]
    [InlineData(400, 51, RegionMath.HtTop)]
    [InlineData(400, 548, RegionMath.HtBottom)]
    [InlineData(103, 53, RegionMath.HtTopLeft)]
    [InlineData(895, 546, RegionMath.HtBottomRight)]
    [InlineData(400, 300, 0)]
    public void The_shapes_outer_edges_resize_the_window(int x, int y, int expected)
        => RegionMath.Edge(new Point(x, y), new Rect(100, 50, 800, 500), band: 6, corner: 16).ShouldBe(expected);

    [Fact]
    public void It_opens_spread_with_the_centred_layout_ready_until_told_otherwise()
    {
        var fitted = new Bounds(320, 110, 1280, 860);

        var open = AeroLayout.Open(null, [new Bounds(0, 0, 1920, 1080)], fitted, new Extent(720, 480));

        open.Spread.ShouldBeTrue("as the demo: the panels spread over the desktop");
        open.Normal.ShouldBe(fitted);
    }

    [Fact]
    public void A_remembered_centred_layout_opens_where_it_was_left()
    {
        var saved = new AeroPlacement { Spread = false, Left = 1930, Top = 40, Width = 1000, Height = 700 };

        var open = AeroLayout.Open(saved, [new Bounds(0, 0, 1920, 1040), new Bounds(1920, 0, 1920, 1040)], new Bounds(320, 110, 1280, 860), new Extent(720, 480));

        open.Spread.ShouldBeFalse();
        open.Normal.ShouldBe(new Bounds(1930, 40, 1000, 700), "on the second screen, where it was");
    }

    [Theory]
    [InlineData(-5000, 40, 1000, 700)]   // a screen since unplugged
    [InlineData(100, 100, 300, 200)]     // smaller than the window may be
    [InlineData(1800, 40, 1000, 700)]    // only a sliver on screen
    public void Remembered_bounds_no_screen_can_show_give_way_to_the_centred_fit(double left, double top, double width, double height)
    {
        var fitted = new Bounds(320, 110, 1280, 860);
        var saved = new AeroPlacement { Spread = false, Left = left, Top = top, Width = width, Height = height };

        AeroLayout.Open(saved, [new Bounds(0, 0, 1920, 1040)], fitted, new Extent(720, 480)).Normal.ShouldBe(fitted);
    }

    [Fact]
    public void A_remembered_spread_keeps_its_centred_bounds_for_the_toggle()
    {
        var saved = new AeroPlacement { Spread = true, Left = 200, Top = 100, Width = 1100, Height = 760 };

        var open = AeroLayout.Open(saved, [new Bounds(0, 0, 1920, 1040)], new Bounds(320, 110, 1280, 860), new Extent(720, 480));

        open.Spread.ShouldBeTrue();
        open.Normal.ShouldBe(new Bounds(200, 100, 1100, 760));
    }

    [Fact]
    public void Where_the_window_was_left_is_saved_in_ui_json_and_read_back()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ui-{Guid.NewGuid():N}.json");
        try
        {
            var store = new UiPreferencesStore(path);
            var placed = new AeroPlacement { Spread = false, Left = 200, Top = 90, Width = 1300, Height = 860 };
            store.Save(UiPreferences.Default with { AeroWindow = placed });

            store.Load().AeroWindow.ShouldBe(placed);
            System.IO.File.WriteAllText(path, "{ \"AeroWindow\": { \"Width\": \"wide\" } }");
            store.Load().AeroWindow.ShouldBeNull("a damaged file loads as the defaults");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [InlineData(10, "Fill", 1920, -60, 1920, 1200)]
    [InlineData(6, "Fit", 2016, 0, 1728, 1080)]
    [InlineData(2, "Stretch", 1920, 0, 1920, 1080)]
    [InlineData(0, "Center", 1920, -60, 1920, 1200)]
    [InlineData(22, "Span", 0, -660, 3840, 2400)]
    [InlineData(99, "Unknown, as Fill", 1920, -60, 1920, 1200)]
    public void The_wallpaper_is_placed_on_the_screen_as_windows_places_it(int style, string name, double x, double y, double w, double h)
    {
        // The second of two 1920 by 1080 screens side by side; a 1920 by 1200 picture.
        var placed = WallpaperPlacement.Place(style, tile: false, new Rect(1920, 0, 1920, 1080), new Rect(0, 0, 3840, 1080), new Size(1920, 1200));

        placed.X.ShouldBe(x, 0.01, name);
        placed.Y.ShouldBe(y, 0.01, name);
        placed.Width.ShouldBe(w, 0.01, name);
        placed.Height.ShouldBe(h, 0.01, name);
    }
}

/// <summary>
/// 0.10.1 on a shown window: the shape holds the glass and leaves the gaps between the panes out of the window, the grip
/// and the shape's edges resize it ahead of WindowChrome, the layout is remembered as the window closes, and each top-bar
/// pill, which now floats over the desktop on its own, reads at 4.5:1 over the brightest backdrop the look allows.
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]
public class FreeFormWindowTests
{
    private const int HtClient = 1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    private static void OnWindow(Action<AeroWindow, ShellViewModel> test)
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var shell = AeroFixtures.Shell(saver);
            var window = AeroHost.Window(shell);
            using var reduced = AeroMotion.Force(true);
            window.Width = 1280;
            window.Height = 860;
            window.Show();
            try
            {
                UiHarness.Pump(TimeSpan.FromMilliseconds(400));
                test(window, shell);
            }
            finally
            {
                window.CloseForSwitch();
            }
        });

    private static (int X, int Y) Device(AeroWindow window, Point p)
    {
        var device = PresentationSource.FromVisual(window)!.CompositionTarget!.TransformToDevice.Transform(p);
        return ((int)Math.Round(device.X), (int)Math.Round(device.Y));
    }

    private static Point Middle(AeroWindow window, string name)
    {
        var element = (FrameworkElement)window.FindName(name);
        return element.TranslatePoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2), window);
    }

    [Fact]
    public void The_window_is_only_its_glass()
        => OnWindow((window, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            window.Shape.ShouldNotBeEmpty();
            bool Holds(Point p)
            {
                var (x, y) = Device(window, p);
                return RegionNative.Holds(hwnd, x, y);
            }

            Holds(Middle(window, "Side")).ShouldBeTrue("the sidebar");
            Holds(Middle(window, "SearchGlass")).ShouldBeTrue("the search pill");
            Holds(Middle(window, "BellPill")).ShouldBeTrue("the bell, on the top bar's glass group");
            Holds(Middle(window, "MaximizeButton")).ShouldBeTrue("a caption button, on the window buttons' group");
            Holds(new Point(5, 5)).ShouldBeFalse("the window's corner, outside every surface");
            var side = (FrameworkElement)window.FindName("Side");
            var sideRight = side.TranslatePoint(new Point(side.ActualWidth, 0), window).X;
            Holds(new Point(sideRight + 9, 400)).ShouldBeFalse("the gap between the sidebar and the page");
            var group = (FrameworkElement)window.FindName("TopGroup");
            var groupLeft = group.TranslatePoint(new Point(0, 0), window);
            Holds(new Point(groupLeft.X - 5, groupLeft.Y + group.ActualHeight / 2)).ShouldBeFalse("the gap between the search and the group");
        });

    /// <summary>WindowChrome takes a window's region off as it extends the glass frame (0.10.4, the window drawn with its
    /// alpha): the shape comes back after, and at rest it is never given again.</summary>
    [Fact]
    public void The_shape_comes_back_after_windowchrome_takes_it_off_and_rests()
        => OnWindow((window, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            bool Corner() => RegionNative.Holds(hwnd, 5, 5);
            Corner().ShouldBeFalse("the shape is on");
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
            chrome.GlassFrameThickness = new Thickness(0);
            chrome.GlassFrameThickness = new Thickness(-1);
            UiHarness.Pump(TimeSpan.FromMilliseconds(400));
            Corner().ShouldBeFalse("the shape is put back");
            var given = RegionNative.Applied;
            UiHarness.Pump(TimeSpan.FromMilliseconds(800));
            RegionNative.Applied.ShouldBe(given, "no shape is given at rest");
        });

    [Fact]
    public void The_grip_and_the_shapes_edges_resize_the_window_ahead_of_its_chrome()
        => OnWindow((window, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            int Hit(Point p)
            {
                var screen = window.PointToScreen(p);
                var packed = ((int)Math.Round(screen.Y) << 16) | ((int)Math.Round(screen.X) & 0xFFFF);
                return (int)SendMessage(hwnd, RegionNative.WM_NCHITTEST, IntPtr.Zero, new IntPtr(packed));
            }

            Hit(Middle(window, "Grip")).ShouldBe(RegionMath.HtBottomRight);
            var side = (FrameworkElement)window.FindName("Side");
            var sideLeft = side.TranslatePoint(new Point(0, side.ActualHeight / 2), window);
            Hit(new Point(sideLeft.X + 2, sideLeft.Y)).ShouldBe(RegionMath.HtLeft, "the sidebar's outer edge");
            Hit(Middle(window, "Side")).ShouldBe(HtClient, "the glass inside the edge is the window's own");

            window.WindowState = WindowState.Maximized;
            UiHarness.Pump(TimeSpan.FromMilliseconds(200));
            window.Grip.IsVisible.ShouldBeFalse("spread over the work area, nothing resizes");
            window.HitResize(new Point(sideLeft.X + 2, sideLeft.Y)).ShouldBe(0);
            window.WindowState = WindowState.Normal;
        });

    [Fact]
    public void The_layout_and_the_smaller_bounds_are_remembered_as_it_closes()
        => OnWindow((window, shell) =>
        {
            window.ToggleLayout();
            UiHarness.Pump(TimeSpan.FromMilliseconds(200));
            window.Spread.ShouldBeTrue();
            shell.Settings.AeroPlacement.ShouldNotBeNull().Spread.ShouldBeTrue();
            window.ToggleLayout();
            UiHarness.Pump(TimeSpan.FromMilliseconds(200));
            var placed = shell.Settings.AeroPlacement.ShouldNotBeNull();
            placed.Spread.ShouldBeFalse();
            placed.Width.ShouldBe(1280, 1);
            placed.Height.ShouldBe(860, 1);
        });

    /// <summary>The shape follows the panes by the frame only while the intro moves them, then lets the frame clock go.</summary>
    [Fact]
    public void The_shape_follows_the_intro_by_the_frame_then_rests()
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var window = AeroHost.Window(AeroFixtures.Shell(saver));
            using var motion = AeroMotion.Force(false);
            window.Width = 1280;
            window.Height = 860;
            window.Show();
            try
            {
                UiHarness.Pump(TimeSpan.FromMilliseconds(100));
                AeroWindow.ShapeHooks.ShouldBe(1, "the panes rise on transforms, which no layout pass reports");
                UiHarness.PumpUntil(() => AeroWindow.ShapeHooks == 0, TimeSpan.FromSeconds(5), "the intro to settle");
                window.Shape.ShouldNotBeEmpty();
            }
            finally
            {
                window.CloseForSwitch();
            }
            AeroWindow.ShapeHooks.ShouldBe(0);
        });

    /// <summary>0.10.9, the owner's recipe: every glass piece, the top bar's groups among them, is untinted glass by default
    /// in either theme, whatever is behind it. On the strict glass (Increase contrast, Reduce transparency) the tint is as
    /// dense as the words need at 4.5:1 over a white window and a black one.</summary>
    [Theory]
    [InlineData("Dark", "Tinted", false)]
    [InlineData("Dark", "Clear", false)]
    [InlineData("Dark", "Tinted", true)]
    [InlineData("Light", "Tinted", false)]
    [InlineData("Light", "Tinted", true)]
    [InlineData("Dark", "Dark", true)]
    [InlineData("Light", "Dark", true)]
    public void A_top_bar_group_is_the_recipes_glass_and_reads_on_the_strict_glass(string themeName, string style, bool increaseContrast)
        => UiHarness.OnUi(() =>
        {
            var theme = Enum.Parse<Theme>(themeName);
            var settings = GlassSettings.Default with { Style = Enum.Parse<GlassStyle>(style), IncreaseContrast = increaseContrast };
            var map = GlassMaterial.Map(settings, theme);
            Color C(string key) => (Color)map[key];
            if (!increaseContrast)
            {
                if (settings.Style is GlassStyle.Clear or GlassStyle.Tinted) C("A.C.GlassTintTop").A.ShouldBe((byte)0, "the recipe has no tint");
                return;
            }
            foreach (var behind in new[] { Colors.White, Colors.Black })
            {
                var glass = Contrast.Over(C("A.C.GlassTintTop"), behind);
                foreach (var fill in new[] { glass, Contrast.Over(C("A.C.GlassHover"), glass) })
                {
                    Contrast.Ratio(Contrast.Over(C("A.C.Text"), fill), fill).ShouldBeGreaterThanOrEqualTo(GlassMaterial.StrictContrast, $"a group's words over {behind}, {style}, {theme}");
                }
            }
        });

    /// <summary>The default glass is the recipe's, untinted, in both themes.</summary>
    [Fact]
    public void The_default_glass_has_no_tint()
        => UiHarness.OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                var tint = (LinearGradientBrush)GlassMaterial.Map(GlassSettings.Default, theme)["A.B.GlassTint"];
                tint.GradientStops.ShouldAllBe(stop => stop.Color.A == 0, theme.ToString());
            }
        });

    /// <summary>The mockup's text shadow (.t: 0 1px 2px, navy at 28 %) lies once under a piece's content: a piece on another's
    /// glass takes none of its own, which the outer one's already reaches.</summary>
    [Fact]
    public void The_text_carries_the_mockups_text_shadow_and_nothing_else_does()
        => UiHarness.OnUi(() =>
        {
            var window = AeroHost.Dressed(new Window { Width = 420, Height = 140, WindowStyle = WindowStyle.None, Left = -20000, ShowInTaskbar = false, ShowActivated = false }, Theme.Dark);
            var settings = GlassSettings.Default;
            using var material = new GlassMaterial(window, () => settings, () => Theme.Dark);
            var text = new System.Windows.Controls.TextBlock { Text = "+12%" };
            var icon = new Icon { Data = System.Windows.Media.Geometry.Parse("M0,0 L10,10"), Size = 12 };
            var inner = new GlassPanel { Content = new System.Windows.Controls.StackPanel { Children = { text, icon } }, Margin = new Thickness(20) };
            var pane = new GlassPanel { Content = inner, HasShadow = false };
            window.Content = pane;
            try
            {
                window.Show();
                window.UpdateLayout();
                UiHarness.Pump(TimeSpan.FromMilliseconds(50));
                // 0.10.9: the mockup's .t and .sub shadow text alone, so the text carries it, not the piece's content.
                var shadow = text.Effect.ShouldBeOfType<System.Windows.Media.Effects.DropShadowEffect>();
                (shadow.ShadowDepth, Math.Round(shadow.Opacity, 2)).ShouldBe((1d, 0.28));
                icon.Effect.ShouldBeNull("an icon has none");
                inner.IsNested.ShouldBeTrue();
                pane.Template.FindName("PART_Content", pane).ShouldBeOfType<System.Windows.Controls.ContentPresenter>().Effect.ShouldBeNull("never the whole content");
                inner.Template.FindName("PART_Content", inner).ShouldBeOfType<System.Windows.Controls.ContentPresenter>().Effect.ShouldBeNull();
            }
            finally
            {
                window.Close();
            }
        });
}
