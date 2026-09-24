using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan O M1-5: the Midnight window drawn to PNGs for a person to look at, in both themes, short, with the update card and
/// the tooltip; and what a person would check on it: the pill sits on the current item, the page follows the sidebar, a
/// short window keeps its sidebar and top bar and scrolls the page, and reduced motion takes the travel out.
/// </summary>
[Trait("Category", "UI")]
[Collection("Motion")]
public class MidnightRenderingTests
{
    [Fact]
    public void The_window_draws_the_dashboard_in_both_themes()
    {
        Directory.CreateDirectory(UiHarness.Folder);
        foreach (var theme in new[] { Theme.Dark, Theme.Light })
        {
            UiHarness.OnUi(() =>
            {
                using var saver = new FakeSaver();
                var shell = MidnightFixtures.Shell(saver);
                var window = MidnightFixtures.Window(shell, theme);
                window.Show();
                try
                {
                    UiHarness.Pump(TimeSpan.FromMilliseconds(1200));   // the live readout settles over 900 ms
                    window.ActualWidth.ShouldBe(1180);
                    UiHarness.Find<DashboardView>(window).ShouldNotBeNull("the Dashboard is the landing page");
                    UiHarness.Find<TextBlock>(window, text => text.Text == "Dashboard").ShouldNotBeNull();
                    UiHarness.Find<StatusPill>(window)!.Content.ShouldBe("Recording");
                    UiHarness.Find<Initials>(window).ShouldNotBeNull("the household's members sit in the header");
                    UiHarness.Find<TextBlock>(window, text => text.Text == Environment.MachineName).ShouldNotBeNull("the PC's name is in the top bar");
                    UiHarness.Find<Button>(window, button => AutomationProperties.GetName(button) == "Send feedback").ShouldNotBeNull();
                    window.Pill.Opacity.ShouldBe(1);
                    UiHarness.Render(window, (int)window.ActualWidth, (int)window.ActualHeight, $"midnight-dashboard-{theme}.png");
                }
                finally
                {
                    window.CloseForSwitch();
                }
            });
            new FileInfo(Path.Combine(UiHarness.Folder, $"midnight-dashboard-{theme}.png")).Length.ShouldBeGreaterThan(30_000);
        }
    }

    [Fact]
    public void The_pill_slides_to_each_item_the_sidebar_chooses_and_the_page_follows()
    {
        Directory.CreateDirectory(UiHarness.Folder);
        UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var shell = MidnightFixtures.Shell(saver);
            var window = MidnightFixtures.Window(shell);
            window.Show();
            try
            {
                UiHarness.Pump(TimeSpan.FromMilliseconds(300));
                var canvas = UiHarness.Find<Grid>(window, grid => grid.Name == "Nav")!;   // the pill's canvas fills this cell
                foreach (var (label, page, view) in new[]
                {
                    ("History", Page.Breakdown, typeof(Midnight.HistoryView)),
                    ("Report", Page.Report, typeof(Midnight.ReportView)),
                    ("Household", Page.Household, typeof(Midnight.HouseholdView)),
                    ("Settings", Page.Settings, typeof(Midnight.SettingsView)),
                    ("Dashboard", Page.Dashboard, typeof(DashboardView)),
                })
                {
                    var item = UiHarness.Find<RadioButton>(window, button => Equals(button.Content, label))!;
                    item.IsChecked = true;
                    shell.Page.ShouldBe(page, label);
                    var at = item.TransformToAncestor(canvas).Transform(new Point(0, 0));
                    // The travel is 220 ms once the animation ticks, which under load waits on the new page's first layout.
                    for (var waited = 0; waited < 5000 && Math.Abs(Canvas.GetTop(window.Pill) - at.Y) > 0.5; waited += 100) UiHarness.Pump(TimeSpan.FromMilliseconds(100));
                    Canvas.GetTop(window.Pill).ShouldBe(at.Y, 0.5, $"the pill under {label}");
                    Canvas.GetLeft(window.Pill).ShouldBe(at.X, 0.5, $"the pill under {label}");
                    window.Pill.ActualWidth.ShouldBe(item.ActualWidth, 0.5);
                    UiHarness.Find<FrameworkElement>(window, element => element.GetType() == view).ShouldNotBeNull($"the {label} page");
                    if (page != Page.Dashboard) UiHarness.Render(window, (int)window.ActualWidth, (int)window.ActualHeight, $"midnight-window-{label.ToLowerInvariant()}-Dark.png");
                }
            }
            finally
            {
                window.CloseForSwitch();
            }
        });
    }

    [Fact]
    public void A_short_window_keeps_its_sidebar_and_top_bar_and_scrolls_the_page()
    {
        Directory.CreateDirectory(UiHarness.Folder);
        UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var shell = MidnightFixtures.Shell(saver, MidnightFixtures.ReadyUpdate());
            var window = MidnightFixtures.Window(shell, Theme.Dark, shell.Updates);
            window.FitTo(new Bounds(-20000, 0, 880 + 2 * WindowFit.Margin, 560 + 2 * WindowFit.Margin));   // a small screen, far to the side
            window.Show();
            try
            {
                UiHarness.Pump(TimeSpan.FromMilliseconds(600));
                window.ActualWidth.ShouldBe(880);
                window.ActualHeight.ShouldBe(560);
                var sidebar = UiHarness.Find<Border>(window, border => border.Name == "Sidebar")!;
                sidebar.ActualHeight.ShouldBe(window.ActualHeight - 2, 1, "the sidebar runs the window's height inside the frame");
                var topBar = UiHarness.Find<Border>(window, border => border.Name == "TopBar")!;
                topBar.ActualHeight.ShouldBe(56);
                UiHarness.Find<Border>(window, border => AutomationProperties.GetName(border) == "Update")!.IsVisible.ShouldBeTrue("the update card is in the foot");
                UiHarness.Find<Button>(window, button => AutomationProperties.GetName(button) == "Send feedback")!.IsVisible.ShouldBeTrue();
                var scroller = UiHarness.Find<ScrollViewer>(UiHarness.Find<DashboardView>(window)!)!;
                scroller.ViewportHeight.ShouldBeLessThan(window.ActualHeight - 56 - 60, "the page, not the shell, is what scrolls");
                UiHarness.Render(window, (int)window.ActualWidth, (int)window.ActualHeight, "midnight-short-dashboard.png");
            }
            finally
            {
                window.CloseForSwitch();
            }
        });
        new FileInfo(Path.Combine(UiHarness.Folder, "midnight-short-dashboard.png")).Length.ShouldBeGreaterThan(20_000);
    }

    [Fact]
    public void Under_reduced_motion_the_pill_jumps_and_the_page_change_keeps_only_its_fade()
        => UiHarness.OnUi(() =>
        {
            using var reduced = Motion.Force(reduced: true);
            using var saver = new FakeSaver();
            var shell = MidnightFixtures.Shell(saver);
            var window = MidnightFixtures.Window(shell);
            window.Show();
            try
            {
                UiHarness.Pump(TimeSpan.FromMilliseconds(200));
                var canvas = UiHarness.Find<Grid>(window, grid => grid.Name == "Nav")!;
                var item = UiHarness.Find<RadioButton>(window, button => Equals(button.Content, "Report"))!;
                item.IsChecked = true;
                window.UpdateLayout();
                var at = item.TransformToAncestor(canvas).Transform(new Point(0, 0));
                Canvas.GetTop(window.Pill).ShouldBe(at.Y, 0.5, "with no travel the pill is there at once");
                var pages = UiHarness.Find<PageHost>(window)!;
                var transform = (System.Windows.Media.TranslateTransform)pages.Showing.RenderTransform;
                transform.Y.ShouldBe(0, "nothing rises under reduced motion");
            }
            finally
            {
                window.CloseForSwitch();
            }
        });

    [Fact]
    public void Closing_hides_to_the_tray_and_the_switch_closes_for_good()
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var shell = MidnightFixtures.Shell(saver);
            var window = MidnightFixtures.Window(shell);
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Show();
            window.Close();
            window.IsVisible.ShouldBeFalse("closing hides");
            closed.ShouldBeFalse("but the window lives on, for the tray");
            window.Show();
            window.IsVisible.ShouldBeTrue();
            ((IShellWindow)window).Page = Page.Now;
            shell.Page.ShouldBe(Page.Dashboard, "Classic's Now arrives as the Dashboard");
            window.CloseForSwitch();
            closed.ShouldBeTrue();
        });
}
