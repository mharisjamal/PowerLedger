using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// 0.10.6: the reveal plays every time the window is shown (at start, from the tray, after a look switch), only once
/// the window has painted with its panes held at the start, so a slow start can't swallow it; under reduced motion
/// nothing is held and nothing travels.
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]
public class AeroRevealTests
{
    /// <summary>The panes' opacity on the first frame drawn after <paramref name="show"/>.</summary>
    private static (double Side, double Now) FirstFrame(AeroWindow window, Action show)
    {
        (double, double)? seen = null;
        void Frame(object? sender, EventArgs e)
        {
            if (seen is not null || !window.IsVisible) return;
            var now = (window.PageHost.Showing as FrameworkElement)?.FindName("PNow") as FrameworkElement;
            seen = (((FrameworkElement)window.FindName("Side")).Opacity, now?.Opacity ?? -1);
        }
        CompositionTarget.Rendering += Frame;
        try
        {
            show();
            UiHarness.PumpUntil(() => seen is not null, TimeSpan.FromSeconds(5), "a frame");
        }
        finally
        {
            CompositionTarget.Rendering -= Frame;
        }
        return seen!.Value;
    }

    [Fact]
    public void The_panes_are_held_before_the_first_frame_and_rise_once_it_has_painted()
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var window = AeroHost.Window(AeroFixtures.Shell(saver));
            using var motion = AeroMotion.Force(false);
            try
            {
                var (side, now) = FirstFrame(window, window.Show);
                side.ShouldBe(0, "held: nothing flashes in fully formed");
                now.ShouldBe(0, "the Dashboard's panes too");
                window.Reveals.ShouldBe(0, "the reveal waits for a painted frame");
                UiHarness.PumpUntil(() => window.Reveals == 1, TimeSpan.FromSeconds(5), "the reveal");
                window.RevealHeld.ShouldBeFalse();
                UiHarness.PumpUntil(() => ((FrameworkElement)window.FindName("Side")).Opacity >= 1, TimeSpan.FromSeconds(5), "the sidebar in");
            }
            finally
            {
                window.CloseForSwitch();
            }
        });

    [Fact]
    public void Shown_again_as_from_the_tray_it_replays_the_reveal()
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var window = AeroHost.Window(AeroFixtures.Shell(saver));
            using var motion = AeroMotion.Force(false);
            try
            {
                window.Show();
                UiHarness.PumpUntil(() => window.Reveals == 1, TimeSpan.FromSeconds(5), "the first reveal");
                UiHarness.Pump(TimeSpan.FromMilliseconds(1500));
                window.Hide();
                UiHarness.Pump(TimeSpan.FromMilliseconds(100));
                var (side, now) = FirstFrame(window, window.Show);
                side.ShouldBe(0, "held again before its first frame");
                now.ShouldBe(0);
                UiHarness.PumpUntil(() => window.Reveals == 2, TimeSpan.FromSeconds(5), "the reveal again");
                AeroWindow.ShapeHooks.ShouldBe(1, "the shape follows the panes while they rise");
            }
            finally
            {
                window.CloseForSwitch();
            }
        });

    [Fact]
    public void Under_reduced_motion_nothing_is_held_and_nothing_travels()
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var window = AeroHost.Window(AeroFixtures.Shell(saver));
            using var motion = AeroMotion.Force(true);
            try
            {
                window.Show();
                UiHarness.PumpUntil(() => window.Reveals == 1, TimeSpan.FromSeconds(5), "the panes shown");
                window.RevealHeld.ShouldBeFalse();
                var side = (FrameworkElement)window.FindName("Side");
                side.RenderTransform.Value.OffsetY.ShouldBe(0, "no rise");
                UiHarness.PumpUntil(() => side.Opacity >= 1, TimeSpan.FromSeconds(2), "at most a short fade");
                ((FrameworkElement)window.FindName("TopBar")).Opacity.ShouldBe(1);
            }
            finally
            {
                window.CloseForSwitch();
            }
        });

    /// <summary>The household button's menu opens: its rule names its own style, so the menu's item style never reaches it.</summary>
    [Fact]
    public void The_household_menu_opens_with_its_rule()
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var window = AeroHost.Window(AeroFixtures.Shell(saver));
            using var motion = AeroMotion.Force(true);
            try
            {
                window.Show();
                UiHarness.Pump(TimeSpan.FromMilliseconds(200));
                ((Button)window.FindName("HouseholdButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                UiHarness.PumpUntil(() => window.OpenMenu is { IsOpen: true }, TimeSpan.FromSeconds(5), "the menu");
                window.OpenMenu!.Items.OfType<Separator>().ShouldHaveSingleItem();
                window.OpenMenu.IsOpen = false;
            }
            finally
            {
                window.CloseForSwitch();
            }
        });
}
