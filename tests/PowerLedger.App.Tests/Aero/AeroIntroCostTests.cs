using System.Windows;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan U: what the intro costs a frame. Measured on the real App, the intro ran at about 10 frames a second; the daily
/// chart's wipe redrew its axis and labels on every frame, and the month's bar grew on its width, a layout pass a frame.
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]   // AeroMotion's override is one for the process
public class AeroIntroCostTests
{
    [Fact]
    public void The_daily_wipe_moves_a_clip_and_draws_nothing_again()
        => UiHarness.OnUi(() =>
        {
            var window = AeroHost.Dressed(new Window { Width = 700, Height = 260, Left = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None }, Theme.Dark);
            var chart = new DailyChart
            {
                Days = [.. Enumerable.Range(1, 12).Select(d => new DailyDay(new DateOnly(2026, 9, d), 0.2 + d * 0.01, null))],
                Reveal = 0,
            };
            window.Content = chart;
            try
            {
                window.Show();
                UiHarness.Pump(TimeSpan.FromMilliseconds(50));
                var draws = chart.Draws;
                draws.ShouldBeGreaterThan(0);
                chart.Reveal = .5;
                window.UpdateLayout();
                chart.Wipe.Width.ShouldBe(chart.ActualWidth / 2, 0.01);
                chart.Reveal = .9;
                window.UpdateLayout();
                chart.Draws.ShouldBe(draws, "the wipe is the clip's, not a redraw of the axis and its text");
                chart.Reveal = 1;
                window.UpdateLayout();
                chart.Draws.ShouldBe(draws + 1, "the tip comes once the wipe is done");
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public void The_months_bar_grows_on_a_scale_with_its_width_already_set()
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var window = AeroHost.Window(AeroFixtures.Shell(saver));
            using var motion = AeroMotion.Force(false);   // after the window: its glass sets the override from Settings
            window.Width = 1440;
            window.Height = 900;
            window.Show();
            try
            {
                var view = (PowerLedger.App.Aero.DashboardView)window.PageHost.Showing!;
                var fill = (FrameworkElement)view.FindName("MonthFill");
                var bar = (FrameworkElement)view.FindName("MonthBar");
                UiHarness.PumpUntil(() => fill.RenderTransform is System.Windows.Media.ScaleTransform { HasAnimatedProperties: true },
                    TimeSpan.FromSeconds(10), "the bar to start growing");
                fill.HasAnimatedProperties.ShouldBeFalse("no width animated, so no layout pass a frame");
                fill.Width.ShouldBe(bar.ActualWidth * 8 / 30, 1, "the bar's own width is its end, the 8th of 30");
            }
            finally
            {
                window.CloseForSwitch();
            }
        });
}
