using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// The grab bar (visionOS's, top middle of the Aero stage): idle at a quarter, half with the pointer within 80 DIP of the
/// bar, whole on it; centred over the stage and never over the caption's buttons.
/// </summary>
public class GrabBarTests
{
    private static readonly Size Hit = new(GrabBar.HitWidth, GrabBar.HitHeight);

    [Fact]
    public void The_pointer_lights_the_bar_by_how_near_it_is()
    {
        GrabBar.GlowAt(Hit, null).ShouldBe(GrabGlow.Idle, "outside the window");
        GrabBar.GlowAt(Hit, new Point(60, 11)).ShouldBe(GrabGlow.Over, "on the bar");
        GrabBar.GlowAt(Hit, new Point(2, 2)).ShouldBe(GrabGlow.Over, "anywhere in its hit area");
        GrabBar.GlowAt(Hit, new Point(60, 14 + 79)).ShouldBe(GrabGlow.Near, "79 DIP under the bar");
        GrabBar.GlowAt(Hit, new Point(60, 14 + 81)).ShouldBe(GrabGlow.Idle, "81 DIP under it");
        GrabBar.GlowAt(Hit, new Point(88 + 50, 14 + 50)).ShouldBe(GrabGlow.Near, "71 DIP off its corner");
        GrabBar.GlowAt(Hit, new Point(88 + 60, 14 + 60)).ShouldBe(GrabGlow.Idle, "85 DIP off its corner");
        GrabBar.OpacityOf(GrabGlow.Idle).ShouldBe(.25);
        GrabBar.OpacityOf(GrabGlow.Near).ShouldBe(.5);
        GrabBar.OpacityOf(GrabGlow.Over).ShouldBe(1);
    }

    [Theory]
    [InlineData(1232, 800, 556)]   // room: centred
    [InlineData(672, 342, 210)]    // a narrow window: left of the buttons, 12 DIP clear
    [InlineData(100, 50, 0)]       // never off the caption
    public void The_bar_is_centred_unless_that_would_cover_the_buttons(double width, double buttonsLeft, double left)
        => AeroWindow.GrabLeft(width, GrabBar.HitWidth, buttonsLeft, AeroWindow.GrabGap).ShouldBe(left);
}

[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]
public class GrabBarWindowTests
{
    private static void OnWindow(Action<AeroWindow> test, double width = 1280, bool reduced = true)
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var window = AeroHost.Window(AeroFixtures.Shell(saver));
            using var motion = AeroMotion.Force(reduced);
            window.Width = width;
            window.Height = 860;
            window.Show();
            try
            {
                UiHarness.Pump(TimeSpan.FromMilliseconds(400));
                test(window);
            }
            finally
            {
                window.CloseForSwitch();
            }
        });

    private static Rect Bounds(AeroWindow window, FrameworkElement element)
        => element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));

    private static FrameworkElement Bar(GrabBar grab) => (FrameworkElement)grab.Template.FindName("PART_Bar", grab);

    private static FrameworkElement Bright(GrabBar grab) => (FrameworkElement)grab.Template.FindName("PART_Bright", grab);

    [Theory]
    [InlineData(1280)]
    [InlineData(720)]
    public void The_bar_is_a_piece_of_the_windows_shape_at_the_top_middle_clear_of_every_control(double width)
        => OnWindow(window =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var toDevice = PresentationSource.FromVisual(window)!.CompositionTarget!.TransformToDevice;
            bool Holds(Point p)
            {
                var d = toDevice.Transform(p);
                return RegionNative.Holds(hwnd, (int)Math.Round(d.X), (int)Math.Round(d.Y));
            }

            var grab = Bounds(window, window.GrabBar);
            grab.Width.ShouldBe(GrabBar.HitWidth, 1);
            grab.Height.ShouldBe(GrabBar.HitHeight, 1, "to the device pixel");
            Holds(new Point(grab.Left + grab.Width / 2, grab.Top + grab.Height / 2)).ShouldBeTrue("the bar");
            Holds(new Point(grab.Left + 4, grab.Top + grab.Height / 2)).ShouldBeTrue("its hit area's end");
            Holds(new Point(grab.Left - 6, grab.Top + grab.Height / 2)).ShouldBeFalse("beside it, the desktop");
            grab.Bottom.ShouldBeLessThanOrEqualTo(Bounds(window, (FrameworkElement)window.FindName("TopBar")).Top + 0.5, "above the top bar row (the mockup's, 6 to 28 down)");
            var buttons = Bounds(window, (FrameworkElement)window.FindName("CaptionButtons"));
            grab.IntersectsWith(buttons).ShouldBeFalse("never over the caption's buttons");
            if (width >= 1100) (grab.Left + grab.Width / 2).ShouldBe(window.ActualWidth / 2, 1, "centred over the stage");
            AutomationProperties(window.GrabBar).ShouldBe("Move window");
            window.GrabBar.Cursor.ShouldBe(Cursors.SizeAll);
        }, width);

    private static string AutomationProperties(DependencyObject d) => System.Windows.Automation.AutomationProperties.GetName(d);

    [Fact]
    public void Idle_near_and_over_light_the_bar_to_a_quarter_half_and_whole()
        => OnWindow(window =>
        {
            var grab = window.GrabBar;
            grab.Track(null);
            Bar(grab).Opacity.ShouldBe(GrabBar.IdleOpacity);
            Bright(grab).Opacity.ShouldBe(0);
            grab.Track(new Point(60, 60));
            grab.Glow.ShouldBe(GrabGlow.Near);
            Bar(grab).Opacity.ShouldBe(GrabBar.NearOpacity);
            grab.Track(new Point(60, 11));
            grab.Glow.ShouldBe(GrabGlow.Over);
            Bar(grab).Opacity.ShouldBe(GrabBar.OverOpacity);
            Bright(grab).Opacity.ShouldBe(1, "a subtle brighten on it");
            grab.Track(null);
            Bar(grab).Opacity.ShouldBe(GrabBar.IdleOpacity, "the pointer gone over a gap, out of the window");
        });

    [Fact]
    public void Pressing_the_bar_starts_the_windows_move_and_a_double_click_toggles_the_layout()
        => OnWindow(window =>
        {
            var grab = window.GrabBar;
            var moves = new List<Window>();
            grab.StartMove = moves.Add;
            var press = Press(1);
            grab.RaiseEvent(press);
            moves.ShouldBe([window], "Windows' own move loop, on this window");
            press.Handled.ShouldBeTrue("the window's glass drag stays out of it");

            window.Spread.ShouldBeFalse();
            grab.RaiseEvent(Press(2));
            UiHarness.Pump(TimeSpan.FromMilliseconds(100));
            window.Spread.ShouldBeTrue("a double click spreads the window");
            moves.Count.ShouldBe(1);
            window.ToggleLayout();
        });

    [Fact]
    public void The_arrow_keys_move_the_window_ten_at_a_time()
        => OnWindow(window =>
        {
            var grab = window.GrabBar;
            var (left, top) = (window.Left, window.Top);
            void Key(Key key) => grab.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(grab)!, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
            Key(System.Windows.Input.Key.Right);
            Key(System.Windows.Input.Key.Down);
            Key(System.Windows.Input.Key.Down);
            window.Left.ShouldBe(left + 10, 1);
            window.Top.ShouldBe(top + 20, 1, "to the device pixel");
            grab.Focusable.ShouldBeTrue();
        });

    [Fact]
    public void Lighting_the_bar_leaves_no_frame_handler_or_clock_running()
        => UiHarness.OnUi(() =>
        {
            var handlers = FrameClock.RenderingHandlers;
            using var saver = new FakeSaver();
            var window = AeroHost.Window(AeroFixtures.Shell(saver));
            using var motion = AeroMotion.Force(false);
            window.Width = 1280;
            window.Height = 860;
            window.Show();
            try
            {
                UiHarness.PumpUntil(() => AeroWindow.ShapeHooks == 0 && FrameClock.RenderingHandlers == handlers && !window.IntroPending,
                    TimeSpan.FromSeconds(10), "the intro to settle");
                var before = FrameClock.Active();
                var grab = window.GrabBar;
                grab.Track(new Point(60, 60));
                grab.Track(new Point(60, 11));
                UiHarness.Pump(TimeSpan.FromMilliseconds(AeroMotion.Grab + 250));
                Bar(grab).Opacity.ShouldBe(GrabBar.OverOpacity, 0.001, "eased to whole");
                FrameClock.ActiveSince(before).Select(FrameClock.Describe).ShouldBeEmpty("the fade has ended");
                FrameClock.RenderingHandlers.ShouldBe(handlers, "nothing asks for every frame");
                for (var x = 0; x < 50; x++) grab.Track(new Point(20 + x, 11));   // a stream of moves on the bar
                FrameClock.ActiveSince(before).ShouldBeEmpty("moves that keep the glow start nothing");
            }
            finally
            {
                window.CloseForSwitch();
            }
        });

    /// <summary>The bar at rest, near and on it, over a desktop-like scene at 4x, to aero-grabbar.png.</summary>
    [Fact]
    public void The_bar_draws_idle_near_and_over()
        => UiHarness.OnUi(() =>
        {
            const int width = 150, height = 3 * 34;
            var window = AeroHost.Dressed(new Window
            {
                Width = width, Height = height, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Left = -20000,
                ShowInTaskbar = false, ShowActivated = false, SizeToContent = SizeToContent.Manual,
            }, Theme.Dark);
            using var material = new GlassMaterial(window, () => GlassSettings.Default, () => Theme.Dark);
            using var reduced = AeroMotion.Force(true);   // after the glass, which sets the override from its settings
            var bars = new StackPanel();
            var grabs = new[] { new GrabBar(), new GrabBar(), new GrabBar() };
            foreach (var grab in grabs)
            {
                grab.Style = (Style)window.FindResource("A.GrabBar");
                bars.Children.Add(new Grid { Height = 34, Children = { grab } });
            }
            window.Content = new Border
            {
                Background = new LinearGradientBrush(Color.FromRgb(0x1D, 0x3B, 0x6E), Color.FromRgb(0x5A, 0x3D, 0x7A), 0),
                Child = bars,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                grabs[0].Track(null);
                grabs[1].Track(new Point(60, 60));
                grabs[2].Track(new Point(60, 11));
                window.UpdateLayout();
                grabs.Select(g => g.Glow).ShouldBe([GrabGlow.Idle, GrabGlow.Near, GrabGlow.Over]);
                var bitmap = new RenderTargetBitmap(width * 4, height * 4, 384, 384, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(UiHarness.Folder);
                using (var file = File.Create(Path.Combine(UiHarness.Folder, "aero-grabbar.png"))) png.Save(file);

                byte Luma(int row)
                {
                    var pixel = new byte[4];
                    bitmap.CopyPixels(new Int32Rect(width * 2, (row * 34 + 17) * 4, 1, 1), pixel, 4, 0);
                    return (byte)((pixel[0] + pixel[1] + pixel[2]) / 3);
                }

                var backdrop = new byte[4];
                bitmap.CopyPixels(new Int32Rect(width * 2, 2 * 4, 1, 1), backdrop, 4, 0);
                var ground = (backdrop[0] + backdrop[1] + backdrop[2]) / 3;
                // Each step lights the bar more over the dark scene.
                Luma(0).ShouldBeGreaterThan((byte)ground, "idle is faint but there");
                Luma(1).ShouldBeGreaterThan(Luma(0), "near is brighter");
                Luma(2).ShouldBeGreaterThan(Luma(1), "over is brightest");
            }
            finally
            {
                window.Close();
            }
        });

    private static MouseButtonEventArgs Press(int clicks)
    {
        var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
        typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(press, clicks, BindingFlags.NonPublic | BindingFlags.Instance, null, null, null);
        return press;
    }
}
