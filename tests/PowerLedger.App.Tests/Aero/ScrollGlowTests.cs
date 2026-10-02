using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Aero's side scroll bar (0.10.8): hidden at rest, half with the pointer within 40 DIP of it or while the content
/// scrolls, whole on it or dragging; on every page whose content overflows, and nothing running at rest.
/// </summary>
public class ScrollGlowTests
{
    private static readonly Rect Bar = new(284, 0, ScrollGlow.Strip, 400);

    [Fact]
    public void The_pointer_scrolling_and_dragging_light_the_bar()
    {
        ScrollGlow.ShineAt(Bar, null, scrolling: false, dragging: false).ShouldBe(ScrollShine.Hidden, "at rest");
        ScrollGlow.ShineAt(Bar, new Point(100, 200), false, false).ShouldBe(ScrollShine.Hidden, "far off");
        ScrollGlow.ShineAt(Bar, new Point(284 - 39, 200), false, false).ShouldBe(ScrollShine.Near, "39 DIP from its edge");
        ScrollGlow.ShineAt(Bar, new Point(284 - 41, 200), false, false).ShouldBe(ScrollShine.Hidden, "41 DIP from it");
        ScrollGlow.ShineAt(Bar, new Point(290, 200), false, false).ShouldBe(ScrollShine.Over, "on it");
        ScrollGlow.ShineAt(Bar, null, scrolling: true, dragging: false).ShouldBe(ScrollShine.Near, "scrolling");
        ScrollGlow.ShineAt(Bar, new Point(10, 10), false, dragging: true).ShouldBe(ScrollShine.Over, "dragging, wherever the pointer");
        ScrollGlow.OpacityOf(ScrollShine.Hidden).ShouldBe(0);
        ScrollGlow.OpacityOf(ScrollShine.Near).ShouldBe(.55);   // the kit's "Near or scrolling" bar (0.10.9's audit)
        ScrollGlow.OpacityOf(ScrollShine.Over).ShouldBe(1);
    }
}

[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]
public class ScrollGlowWindowTests
{
    private static ScrollViewer PageScroller(AeroWindow window)
        => UiHarness.Find<ScrollViewer>(window.PageHost.Showing!, s => s.TemplatedParent is null)!;

    private static FrameworkElement Host(ScrollViewer viewer) => (FrameworkElement)viewer.Template.FindName("PART_VerticalHost", viewer);

    private static ScrollBar VBar(ScrollViewer viewer) => (ScrollBar)viewer.Template.FindName("PART_VerticalScrollBar", viewer);

    private static FrameworkElement Capsule(ScrollViewer viewer)
    {
        var thumb = VBar(viewer).Track.Thumb;
        return (FrameworkElement)thumb.Template.FindName("PART_Capsule", thumb);
    }

    /// <summary>The bar's strip, in the viewer's units.</summary>
    private static Rect Strip(ScrollViewer viewer)
    {
        var host = Host(viewer);
        return new Rect(host.TranslatePoint(new Point(0, 0), viewer), host.RenderSize);
    }

    private static void OnWindow(Action<AeroWindow, ShellViewModel> test, bool reduced = true)
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var shell = AeroFixtures.Shell(saver);
            var window = AeroHost.Window(shell);
            using var motion = AeroMotion.Force(reduced);
            window.Width = 1280;
            window.Height = 760;
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

    [Fact]
    public void Every_page_whose_content_overflows_has_the_bar_hidden_at_rest()
        => OnWindow((window, shell) =>
        {
            var overflowing = 0;
            foreach (var page in new[] { Page.Dashboard, Page.Breakdown, Page.Parts, Page.Insights, Page.Report, Page.Household, Page.Settings })
            {
                shell.Page = page;
                UiHarness.Pump(TimeSpan.FromMilliseconds(300));
                var viewer = PageScroller(window);
                var glow = ScrollGlow.Of(viewer);
                glow.ShouldNotBeNull($"{page} scrolls in Aero's viewer");
                if (viewer.ScrollableHeight <= 0) continue;
                overflowing++;
                VBar(viewer).Visibility.ShouldBe(Visibility.Visible, $"{page} overflows: its bar is there");
                glow.Vertical.ShouldBe(ScrollShine.Hidden, page.ToString());
                VBar(viewer).Opacity.ShouldBe(0, page.ToString());
                Host(viewer).Visibility.ShouldBe(Visibility.Hidden, $"{page}: no piece of the window at rest");
                var strip = Strip(viewer);
                (viewer.ActualWidth - strip.Right).ShouldBe(0, 0.5, "at the pane's right edge, over the content");
                var presenter = (FrameworkElement)viewer.Template.FindName("PART_ScrollContentPresenter", viewer);
                presenter.ActualWidth.ShouldBe(viewer.ActualWidth, 0.5, $"{page}: the bar takes no width");
            }
            shell.Page = Page.Settings;
            UiHarness.Pump(TimeSpan.FromMilliseconds(300));
            PageScroller(window).ScrollableHeight.ShouldBeGreaterThan(0, "Settings overflows at 760 high");
            overflowing.ShouldBeGreaterThan(0);
        });

    [Fact]
    public void Hidden_near_and_over_show_the_bar_at_none_half_and_whole()
        => OnWindow((window, shell) =>
        {
            shell.Page = Page.Settings;
            UiHarness.Pump(TimeSpan.FromMilliseconds(300));
            var viewer = PageScroller(window);
            var glow = ScrollGlow.Of(viewer)!;
            var strip = Strip(viewer);
            var y = strip.Top + strip.Height / 2;

            glow.Track(new Point(strip.Left - 30, y));
            glow.Vertical.ShouldBe(ScrollShine.Near, "30 DIP from its edge");
            VBar(viewer).Opacity.ShouldBe(ScrollGlow.NearOpacity);
            Host(viewer).Visibility.ShouldBe(Visibility.Visible);
            Capsule(viewer).Width.ShouldBe(ScrollGlow.Thin);

            glow.Track(new Point(strip.Left + 8, y));
            glow.Vertical.ShouldBe(ScrollShine.Over);
            VBar(viewer).Opacity.ShouldBe(ScrollGlow.OverOpacity);
            Capsule(viewer).Width.ShouldBe(ScrollGlow.Wide, "wider on it");
            UiHarness.Pump(TimeSpan.FromMilliseconds(150));
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var centre = PresentationSource.FromVisual(window)!.CompositionTarget!.TransformToDevice
                .Transform(viewer.TranslatePoint(new Point(strip.Left + strip.Width / 2, y), window));
            RegionNative.Holds(hwnd, (int)Math.Round(centre.X), (int)Math.Round(centre.Y)).ShouldBeTrue("a shown bar is a piece of the window");

            glow.Track(new Point(strip.Left - 200, y));
            glow.Vertical.ShouldBe(ScrollShine.Hidden, "the pointer gone off");
            VBar(viewer).Opacity.ShouldBe(0);
            Host(viewer).Visibility.ShouldBe(Visibility.Hidden);

            viewer.ScrollToVerticalOffset(120);
            UiHarness.Pump(TimeSpan.FromMilliseconds(100));
            glow.Vertical.ShouldBe(ScrollShine.Near, "scrolling shows it");
            glow.Lingering.ShouldBeTrue();
            UiHarness.PumpUntil(() => glow.Vertical == ScrollShine.Hidden, TimeSpan.FromSeconds(3), "the bar to fade after the scroll");
            glow.Lingering.ShouldBeFalse();
            VBar(viewer).Opacity.ShouldBe(0);
        });

    [Fact]
    public void Lighting_and_scrolling_leave_no_timer_frame_handler_or_clock_running()
        => UiHarness.OnUi(() =>
        {
            var handlers = FrameClock.RenderingHandlers;
            using var saver = new FakeSaver();
            var shell = AeroFixtures.Shell(saver);
            var window = AeroHost.Window(shell);
            using var motion = AeroMotion.Force(false);
            window.Width = 1280;
            window.Height = 760;
            window.Show();
            try
            {
                UiHarness.PumpUntil(() => AeroWindow.ShapeHooks == 0 && FrameClock.RenderingHandlers == handlers && !window.IntroPending,
                    TimeSpan.FromSeconds(10), "the intro to settle");
                shell.Page = Page.Settings;
                UiHarness.PumpUntil(() => AeroWindow.ShapeHooks == 0 && FrameClock.RenderingHandlers == handlers, TimeSpan.FromSeconds(10), "the page to land");
                UiHarness.Pump(TimeSpan.FromMilliseconds(1500));
                var viewer = PageScroller(window);
                var glow = ScrollGlow.Of(viewer)!;
                var strip = Strip(viewer);
                var before = FrameClock.Active();

                glow.Track(new Point(strip.Left + 8, strip.Top + 40));
                UiHarness.Pump(TimeSpan.FromMilliseconds(AeroMotion.ScrollFade + 250));
                VBar(viewer).Opacity.ShouldBe(1, 0.001, "eased to whole");
                for (var x = 0; x < 50; x++) glow.Track(new Point(strip.Left + 4 + x % 8, strip.Top + 40 + x));
                glow.Track(null);
                glow.Scrolled();
                UiHarness.PumpUntil(() => glow.Vertical == ScrollShine.Hidden && Host(viewer).Visibility == Visibility.Hidden,
                    TimeSpan.FromSeconds(4), "the bar to fade out after the scroll");
                UiHarness.Pump(TimeSpan.FromMilliseconds(AeroMotion.ScrollFade + 100));
                VBar(viewer).Opacity.ShouldBe(0, 0.001);
                glow.Lingering.ShouldBeFalse("the timer stops once it fires");
                FrameClock.ActiveSince(before).Select(FrameClock.Describe).ShouldBeEmpty("every fade has ended");
                FrameClock.RenderingHandlers.ShouldBe(handlers, "nothing asks for every frame");
            }
            finally
            {
                window.CloseForSwitch();
            }
        });

    /// <summary>The bar hidden, near and on it, over a desktop-like scene at 4x, to aero-scrollbar.png.</summary>
    [Fact]
    public void The_bar_draws_hidden_near_and_over()
        => UiHarness.OnUi(() =>
        {
            const int each = 60, height = 120;
            var window = AeroHost.Dressed(new Window
            {
                Width = 3 * each, Height = height, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Left = -20000,
                ShowInTaskbar = false, ShowActivated = false, SizeToContent = SizeToContent.Manual,
            }, Theme.Dark);
            using var material = new GlassMaterial(window, () => GlassSettings.Default, () => Theme.Dark);
            using var reduced = AeroMotion.Force(true);   // after the glass, which sets the override from its settings
            var row = new UniformGrid { Rows = 1 };
            var viewers = new ScrollViewer[3];
            for (var i = 0; i < 3; i++)
            {
                viewers[i] = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new Border { Height = height * 3 } };
                row.Children.Add(viewers[i]);
            }
            window.Content = new Border
            {
                Background = new LinearGradientBrush(Color.FromRgb(0x1D, 0x3B, 0x6E), Color.FromRgb(0x5A, 0x3D, 0x7A), 90),
                Child = row,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                var glows = viewers.Select(v => ScrollGlow.Of(v)!).ToArray();
                glows[0].Track(null);
                glows[1].Track(new Point(each - ScrollGlow.Strip - 20, 20));
                glows[2].Track(new Point(each - 8, 20));
                window.UpdateLayout();
                glows.Select(g => g.Vertical).ShouldBe([ScrollShine.Hidden, ScrollShine.Near, ScrollShine.Over]);
                var bitmap = new RenderTargetBitmap(3 * each * 4, height * 4, 384, 384, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(UiHarness.Folder);
                using (var file = File.Create(Path.Combine(UiHarness.Folder, "aero-scrollbar.png"))) png.Save(file);

                int Luma(int column)
                {
                    var pixel = new byte[4];
                    bitmap.CopyPixels(new Int32Rect(((column + 1) * each - 7) * 4, 20 * 4, 1, 1), pixel, 4, 0);
                    return (pixel[0] + pixel[1] + pixel[2]) / 3;
                }

                int Ground(int column)
                {
                    var pixel = new byte[4];
                    bitmap.CopyPixels(new Int32Rect(((column + 1) * each - 30) * 4, 20 * 4, 1, 1), pixel, 4, 0);
                    return (pixel[0] + pixel[1] + pixel[2]) / 3;
                }

                Math.Abs(Luma(0) - Ground(0)).ShouldBeLessThanOrEqualTo(2, "hidden shows only the scene");
                Luma(1).ShouldBeGreaterThan(Ground(1) + 3, "near is there");
                (Luma(2) - Ground(2)).ShouldBeGreaterThan(Luma(1) - Ground(1), "over is brighter");
            }
            finally
            {
                window.Close();
            }
        });
}
