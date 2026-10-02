using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PowerLedger.App.Aero;
using Shouldly;
using AeroDashboard = PowerLedger.App.Aero.DashboardView;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan W (0.10.3, the approved demo exactly): the intro's figures count up, as the demo's do, and let go when done; the
/// demo's own controls, Replay intro and Play tour (in Settings since 0.10.9, off the window's chrome): one plays the
/// opening again, the other pushes the camera in on the Dashboard's panes in turn.
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]
public class AeroDemoControlsTests
{
    private static (AeroWindow Window, ShellViewModel Shell, IDisposable Motion) Open()
    {
        var saver = new FakeSaver();
        var shell = AeroFixtures.Shell(saver);
        var window = AeroHost.Window(shell);
        var motion = AeroMotion.Force(false);   // after the window: its glass sets the override from Settings
        window.Width = 1440;
        window.Height = 900;
        window.Closed += (_, _) => saver.Dispose();
        window.Show();
        return (window, shell, motion);
    }

    private static AeroDashboard Settle(AeroWindow window, IReadOnlyList<System.Windows.Media.Animation.Clock> before)
    {
        UiHarness.PumpUntil(() => !window.IntroPending && window.PageHost.Showing is AeroDashboard, TimeSpan.FromSeconds(10), "the intro to begin");
        var view = (AeroDashboard)window.PageHost.Showing!;
        UiHarness.PumpUntil(() => view.Count == 1 && FrameClock.ActiveSince(before).Count == 0, TimeSpan.FromSeconds(20), "the intro to settle");
        return view;
    }

    [Fact]
    public void The_intros_figures_count_up_from_nothing_and_the_count_lets_go()
        => UiHarness.OnUi(() =>
        {
            var before = FrameClock.Active();
            var (window, _, motion) = Open();
            try
            {
                UiHarness.PumpUntil(() => !window.IntroPending, TimeSpan.FromSeconds(10), "the intro to begin");
                var view = (AeroDashboard)window.PageHost.Showing!;
                var roll = UiHarness.Find<RollingNumber>(view, r => r.Name == "NowRoll")!;
                var month = (TrackedText)view.FindName("MonthBig");
                view.Count.ShouldBe(0, "the figures wait at nothing while the panes rise");
                roll.Text.ShouldBe("0");
                month.Text.ShouldBe("$0.00");
                UiHarness.PumpUntil(() => view.Count is > 0.2 and < 0.8, TimeSpan.FromSeconds(5), "the count to be under way");
                int.Parse(roll.Text).ShouldBeLessThan(60, "part of the way to the reading");

                UiHarness.PumpUntil(() => view.Count == 1 && FrameClock.ActiveSince(before).Count == 0, TimeSpan.FromSeconds(20), "the intro to settle");
                roll.Text.ShouldBe("60");
                month.Text.ShouldBe("$0.47");
                ((TextBlock)view.FindName("TodayKwh")).Text.ShouldBe("0.284");
                view.HasAnimatedProperties.ShouldBeFalse("the count let go of its clock");
            }
            finally
            {
                window.CloseForSwitch();
                motion.Dispose();
            }
        });

    [Fact]
    public void Under_reduced_motion_the_figures_are_simply_there()
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var window = AeroHost.Window(AeroFixtures.Shell(saver));
            using var motion = AeroMotion.Force(true);
            window.Show();
            try
            {
                UiHarness.PumpUntil(() => !window.IntroPending, TimeSpan.FromSeconds(10), "the intro to begin");
                var view = (AeroDashboard)window.PageHost.Showing!;
                view.Count.ShouldBe(1);
                ((TrackedText)view.FindName("MonthBig")).Text.ShouldBe("$0.47");
            }
            finally
            {
                window.CloseForSwitch();
            }
        });

    /// <summary>0.10.9: the mockup's chrome has no Replay intro or Play tour; they are Settings' buttons, and the window's
    /// minimise, maximise and close sit on the top bar's capsule after Home.</summary>
    [Fact]
    public void The_demos_controls_are_in_settings_and_the_windows_buttons_on_the_top_bar()
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var shell = AeroFixtures.Shell(saver);
            var window = AeroHost.Window(shell);
            try
            {
                window.Show();
                UiHarness.Pump(TimeSpan.FromMilliseconds(100));
                window.FindName("DemoControls").ShouldBeNull();
                window.FindName("CaptionButtons").ShouldBeNull();
                var group = (GlassPanel)window.FindName("TopGroup");
                var buttons = MidnightHost.AllOf<Button>((DependencyObject)window.FindName("WindowButtons")).ToList();
                buttons.Select(AutomationProperties.GetName).ShouldBe(["Minimize", "Maximize", "Close"]);
                buttons.ShouldAllBe(button => button.Style == window.FindResource("A.WindowBtn"));
                MidnightHost.AllOf<Button>(group).Select(AutomationProperties.GetName).ShouldBe(["Approvals and alerts", "Household", "Minimize", "Maximize", "Close"]);

                shell.Page = Page.Settings;
                UiHarness.PumpUntil(() => window.PageHost.Showing is PowerLedger.App.Aero.SettingsView { IsLoaded: true }, TimeSpan.FromSeconds(5), "Settings");
                var settings = window.PageHost.Showing!;
                MidnightHost.AllOf<Button>(settings).Select(AutomationProperties.GetName).ShouldContain("Replay intro");
                MidnightHost.AllOf<Button>(settings).Select(AutomationProperties.GetName).ShouldContain("Play tour");
            }
            finally
            {
                window.CloseForSwitch();
            }
        });

    [Fact]
    public void Replay_intro_plays_the_opening_again_counts_again_and_settles()
        => UiHarness.OnUi(() =>
        {
            var before = FrameClock.Active();
            var (window, _, motion) = Open();
            try
            {
                var view = Settle(window, before);
                window.ReplayIntro();
                window.IntroPending.ShouldBeTrue();
                view.Count.ShouldBe(0);
                view.Panes.ShouldAllBe(p => p.HasAnimatedProperties, "the panes rise again");
                UiHarness.PumpUntil(() => !window.IntroPending, TimeSpan.FromSeconds(5), "the replay to begin");
                UiHarness.PumpUntil(() => view.Count == 1 && FrameClock.ActiveSince(before).Count == 0, TimeSpan.FromSeconds(20), "the replay to settle");
                view.Panes.ShouldAllBe(p => p.Opacity == 1);
            }
            finally
            {
                window.CloseForSwitch();
                motion.Dispose();
            }
        });

    [Fact]
    public void Replay_intro_from_another_page_brings_the_dashboard_up_and_plays_on_it()
        => UiHarness.OnUi(() =>
        {
            var before = FrameClock.Active();
            var (window, shell, motion) = Open();
            try
            {
                Settle(window, before);
                shell.Page = Page.Settings;
                UiHarness.Pump(TimeSpan.FromMilliseconds(300));
                window.ReplayIntro();
                shell.Page.ShouldBe(Page.Dashboard);
                UiHarness.PumpUntil(() => window.PageHost.Showing is AeroDashboard { IsLoaded: true }, TimeSpan.FromSeconds(5), "the Dashboard");
                var view = (AeroDashboard)window.PageHost.Showing!;
                view.Count.ShouldBeLessThan(1, "its figures count up with the replay");
                UiHarness.PumpUntil(() => view.Count == 1 && FrameClock.ActiveSince(before).Count == 0, TimeSpan.FromSeconds(20), "the replay to settle");
            }
            finally
            {
                window.CloseForSwitch();
                motion.Dispose();
            }
        });

    [Fact]
    public void Play_tour_pushes_the_camera_in_on_each_pane_in_turn_and_stopping_leaves_it_where_it_is()
        => UiHarness.OnUi(() =>
        {
            var before = FrameClock.Active();
            var (window, _, motion) = Open();
            try
            {
                var view = Settle(window, before);
                window.PlayTour();
                var now = (GlassPanel)view.FindName("PNow");
                var month = (GlassPanel)view.FindName("PMonth");
                UiHarness.PumpUntil(() => view.Focused == now, TimeSpan.FromSeconds(2), "the camera on Power now");
                view.Touring.ShouldBeTrue();
                UiHarness.PumpUntil(() => view.Focused == month, TimeSpan.FromMilliseconds(AeroMotion.TourStep + 2000), "the camera on This month");
                view.StopTour();
                view.Touring.ShouldBeFalse();
                UiHarness.Pump(TimeSpan.FromMilliseconds(AeroMotion.TourStep + 200));
                view.Focused.ShouldBe(month, "a stopped tour goes no further");
                view.Unfocus();
            }
            finally
            {
                window.CloseForSwitch();
                motion.Dispose();
            }
        });

    [Fact]
    public void Replay_intro_ends_a_tour_and_brings_the_camera_back()
        => UiHarness.OnUi(() =>
        {
            var before = FrameClock.Active();
            var (window, _, motion) = Open();
            try
            {
                var view = Settle(window, before);
                window.PlayTour();
                UiHarness.PumpUntil(() => view.Focused is not null, TimeSpan.FromSeconds(2), "the tour to start");
                window.ReplayIntro();
                view.Touring.ShouldBeFalse();
                view.Focused.ShouldBeNull();
            }
            finally
            {
                window.CloseForSwitch();
                motion.Dispose();
            }
        });
}
