using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using PowerLedger.App.Aero;
using Shouldly;
using AeroDashboard = PowerLedger.App.Aero.DashboardView;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan U: what the Aero window costs between readings. Measured on the real App, a reading a second kept WPF drawing at
/// 60 frames a second (the Last minute line's 1 s slide and the digits' 600 ms roll on every reading), the pulse's clocks
/// ran on after it went still, and the intro redrew the daily chart's text on every frame. A reading is now one redraw,
/// and these tests hold it there by reading WPF's own frame clock (<see cref="FrameClock"/>).
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]   // AeroMotion's override is one for the process
public class AeroIdleTests
{
    [Fact]
    public void A_live_reading_settles_with_no_animation_or_frame_handler_left_running()
        => UiHarness.OnUi(() =>
        {
            var before = FrameClock.Active();
            var handlers = FrameClock.RenderingHandlers;
            using var saver = new FakeSaver();
            var shell = AeroFixtures.Shell(saver, out var link);
            var window = AeroHost.Window(shell);
            using var motion = AeroMotion.Force(false);   // after the window: its glass sets the override from Settings
            window.Width = 1440;
            window.Height = 900;
            window.Show();
            try
            {
                UiHarness.PumpUntil(() => !window.IntroPending, TimeSpan.FromSeconds(10), "the intro to begin");
                var view = (AeroDashboard)window.PageHost.Showing!;
                var live = UiHarness.Find<LiveChart>(view)!;
                var daily = UiHarness.Find<DailyChart>(view)!;
                var pie = UiHarness.Find<PieChart3D>(view)!;
                UiHarness.PumpUntil(() => live.Reveal == 1 && daily.Reveal == 1 && pie.Rise == 1 && FrameClock.ActiveSince(before).Count == 0
                    && FrameClock.RenderingHandlers == handlers, TimeSpan.FromSeconds(20), "the intro to settle");
                var roll = UiHarness.Find<RollingNumber>(view, r => r.Name == "NowRoll")!;
                var (shown, samples) = (roll.Text, live.Samples);

                link.Push(Frames.At(MidnightFixtures.Now.AddSeconds(1), totalW: 97.4));
                // Longer than any tween a reading may have and shorter than the old roll (600 ms) and slide (1 s): a clock
                // a reading starts has had its first tick and is still running if it is one of those.
                UiHarness.Pump(TimeSpan.FromMilliseconds(400));

                roll.Text.ShouldNotBe(shown, "the reading reached the Dashboard");
                live.Samples.ShouldNotBeSameAs(samples);
                FrameClock.ActiveSince(before).Select(FrameClock.Describe).ShouldBeEmpty("a reading is one redraw: nothing left animating");
                FrameClock.RenderingHandlers.ShouldBe(handlers, "and nothing asking for every frame");
                WallpaperFrost.RenderingHooks.ShouldBe(0);
            }
            finally
            {
                window.CloseForSwitch();
            }
        });

    [Fact]
    public void A_live_value_changes_the_digits_in_place()
        => UiHarness.OnUi(() =>
        {
            using var _ = AeroMotion.Force(false);
            var number = new RollingNumber();
            number.Set(3, animate: false);
            number.Value = 58;
            number.Text.ShouldBe("58");
            number.Rolled.ShouldBe([5.0, 8.0], "there at once");
            number.Children.OfType<Canvas>().Select(c => ((StackPanel)c.Children[0]).RenderTransform)
                .ShouldAllBe(t => !((System.Windows.Media.Animation.Animatable)t).HasAnimatedProperties, "no roll for a live reading");
        });

    [Fact]
    public void A_pulse_that_stops_stops_its_clocks_too()
        => UiHarness.OnUi(() =>
        {
            using var _ = AeroMotion.Force(false);
            var before = FrameClock.Active();
            var dot = new PulseDot { Size = 9 };
            dot.Breathe(true);
            UiHarness.Pump(TimeSpan.FromMilliseconds(100));
            dot.Breathing.ShouldBeTrue();
            FrameClock.ActiveSince(before).Count.ShouldBe(2, "the ring's grow and fade");
            dot.Breathe(false);
            UiHarness.Pump(TimeSpan.FromMilliseconds(100));
            dot.Breathing.ShouldBeFalse();
            FrameClock.ActiveSince(before).Select(FrameClock.Describe).ShouldBeEmpty("a detached clock that repeats for ever would tick on until a collection");
        });

    [Fact]
    public void A_mouse_move_under_a_still_pointer_leaves_the_pulse_asleep()
        => UiHarness.OnUi(() =>
        {
            var dot = new PulseDot();
            dot.PointerAt(new Point(40, 12));
            dot.Awake.ShouldBeTrue("the pointer came");
            dot.Sleep();
            dot.PointerAt(new Point(40, 12));
            dot.Awake.ShouldBeFalse("WPF raises a move on each layout change under a still pointer; a reading's is no use");
            dot.PointerAt(new Point(41, 12));
            dot.Awake.ShouldBeTrue("the pointer moved");
            dot.Sleep();
        });

    /// <summary>0.10.9: a piece's shadows and its glowing edge are worked out once into small images and drawn as they are:
    /// no effect runs on them, so a live reading inside a pane re-blurs nothing; a piece of the same shape draws the same
    /// images again.</summary>
    [Fact]
    public void A_panes_shadow_and_edge_are_worked_out_once_with_no_effect()
        => UiHarness.OnUi(() =>
        {
            var window = AeroHost.Dressed(new Window { Width = 400, Height = 300, Left = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None }, Theme.Dark);
            window.Resources[GlassMaterial.GlowKey] = 0.7;
            var pane = new GlassPanel { Width = 200, Height = 120, Content = new TextBlock { Text = "Power now" }, Background = Brushes.Black };
            window.Content = new Grid { Children = { pane } };
            try
            {
                window.Show();
                UiHarness.Pump(TimeSpan.FromMilliseconds(50));
                var shadow = (GlassShadow)pane.Template.FindName("PART_Shadow", pane);
                var glow = (GlassGlow)pane.Template.FindName("PART_Glow", pane);
                shadow.Effect.ShouldBeNull();
                glow.Effect.ShouldBeNull();
                var map = shadow.DropMap.ShouldNotBeNull("a tinted pane casts the recipe's shadow");
                (map.PixelWidth * map.PixelHeight).ShouldBeLessThan(200 * 120, "a small image, drawn stretched");
                ((TextBlock)pane.Content).Text = "146 W";
                UiHarness.Pump(TimeSpan.FromMilliseconds(50));
                shadow.DropMap.ShouldBeSameAs(map, "a new reading draws the same shadow");
                var scale = VisualTreeHelper.GetDpi(glow).DpiScaleX;
                GlassGlow.For(200, 120, 28, 0.7, scale).ShouldBeSameAs(GlassGlow.For(200, 120, 28, 0.7, scale), "one image for a shape");
            }
            finally
            {
                window.Close();
            }
        });
}
