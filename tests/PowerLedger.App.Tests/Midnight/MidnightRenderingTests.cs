using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
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
                    var view = UiHarness.Find<DashboardView>(window)!;
                    UiTree.Descendants<HatchBar>(UiHarness.Find<ItemsControl>(view)!).Count().ShouldBe(3, "three KPI cards, a bar each");
                    Pills(view, "Power over time").ShouldBe(["1H", "1D", "1W", "1M", "1Y", "All"]);
                    Checked(view, "Power over time").ShouldBe("1D");
                    Pills(view, "Where the power went").ShouldBe(["Today", "7 days", "30 days"]);
                    Checked(view, "Where the power went").ShouldBe("Today");
                    UiTree.Descendants<ShareBar>(Card(view, "Where the power went")).Count().ShouldBe(4, "a row a part");
                    UiTree.Descendants<Border>(view).ShouldNotContain(border => border.Style == window.Resources["M.Glass"], "glass stays on the top bar and tooltips");
                    UiHarness.Render(window, (int)window.ActualWidth, (int)window.ActualHeight, $"midnight-dashboard-{theme}.png");
                    var scroller = UiHarness.Find<ScrollViewer>(view)!;
                    scroller.ScrollToEnd();
                    window.UpdateLayout();
                    scroller.VerticalOffset.ShouldBeGreaterThan(0, "the page runs past the window, and scrolls");
                    UiHarness.Render(window, (int)window.ActualWidth, (int)window.ActualHeight, $"midnight-dashboard-end-{theme}.png");
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
                    ("History", Page.Breakdown, typeof(BreakdownView)),
                    ("Report", Page.Report, typeof(ReportView)),
                    ("Household", Page.Household, typeof(HouseholdView)),
                    ("Settings", Page.Settings, typeof(SettingsView)),
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
                    if (page == Page.Household) UiHarness.Render(window, (int)window.ActualWidth, (int)window.ActualHeight, "midnight-household-classic-view-Dark.png");
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
    public void The_chart_tooltip_opens_at_the_middle_point_in_the_glass_and_the_cards_stand_three_across()
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
                UiHarness.Pump(TimeSpan.FromMilliseconds(600));
                var view = UiHarness.Find<DashboardView>(window)!;
                UiHarness.Find<System.Windows.Controls.Primitives.UniformGrid>(view)!.Columns.ShouldBe(0, "three cards across at 1180 wide");
                UiHarness.Find<HatchBar>(view).ShouldNotBeNull();
                var chart = UiHarness.Find<AreaChart>(view)!;
                chart.Model.Buckets.Count.ShouldBeGreaterThan(100, "today's readings are on the chart");
                chart.Hover(chart.Model.Capacity / 2);   // the middle of the day's width: noon
                UiHarness.Pump(TimeSpan.FromMilliseconds(300));
                chart.Tip.ShouldNotBeNull();
                chart.Tip.IsOpen.ShouldBeTrue();
                chart.Tip.Content.ShouldBeOfType<string>().ShouldMatch(@"^12:00 PM · \d+ W$");
                UiHarness.Find<Border>(chart.Tip, border => border.Style == window.Resources["M.Glass"]).ShouldNotBeNull("the tooltip wears the glass");
                WithTip(window, chart, "midnight-dashboard-hover-Dark.png");
                UiHarness.Render(chart.Tip, (int)Math.Ceiling(chart.Tip.ActualWidth), (int)Math.Ceiling(chart.Tip.ActualHeight), "midnight-dashboard-tooltip-Dark.png");
                chart.Hover(-1);
                chart.Tip.IsOpen.ShouldBeFalse();
            }
            finally
            {
                window.CloseForSwitch();
            }
        });
        new FileInfo(Path.Combine(UiHarness.Folder, "midnight-dashboard-tooltip-Dark.png")).Length.ShouldBeGreaterThan(500);
    }

    [Theory]
    [InlineData(1100, 0)]
    [InlineData(1099, 1)]
    public void The_cards_stand_three_across_from_1100_wide_and_one_under_another_below(int width, int columns)
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var window = MidnightFixtures.Window(MidnightFixtures.Shell(saver));
            window.FitTo(new Bounds(-20000, 0, width + 2 * WindowFit.Margin, 900 + 2 * WindowFit.Margin));
            window.Show();
            try
            {
                UiHarness.Pump(TimeSpan.FromMilliseconds(300));
                window.ActualWidth.ShouldBe(width, 1);
                UiHarness.Find<System.Windows.Controls.Primitives.UniformGrid>(UiHarness.Find<DashboardView>(window)!)!.Columns.ShouldBe(columns);
            }
            finally
            {
                window.CloseForSwitch();
            }
        });

    [Fact]
    public void At_the_narrowest_the_cards_go_one_under_another_and_the_table_still_fits()
    {
        Directory.CreateDirectory(UiHarness.Folder);
        UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var shell = MidnightFixtures.Shell(saver);
            var window = MidnightFixtures.Window(shell, Theme.Light);
            window.FitTo(new Bounds(-20000, 0, 960 + 2 * WindowFit.Margin, 900 + 2 * WindowFit.Margin));
            window.Show();
            try
            {
                UiHarness.Pump(TimeSpan.FromMilliseconds(600));
                window.ActualWidth.ShouldBe(960);
                var view = UiHarness.Find<DashboardView>(window)!;
                UiHarness.Find<System.Windows.Controls.Primitives.UniformGrid>(view)!.Columns.ShouldBe(1, "one card under another under 1100 wide");
                var table = Card(view, "Where the power went");
                var inside = table.ActualWidth - table.Padding.Right - table.BorderThickness.Right;
                foreach (var mark in UiTree.Descendants<TrendMark>(table))
                    mark.TranslatePoint(new Point(mark.ActualWidth, 0), table).X.ShouldBeLessThanOrEqualTo(inside + 0.5, "the trend column stays inside the card");
                foreach (var name in UiTree.Descendants<TextBlock>(table).Where(text => text.Text is "CPU package" or "Rest of system"))
                    name.DesiredSize.Width.ShouldBeLessThanOrEqualTo(name.ActualWidth + 0.5, $"{name.Text} is whole");
                foreach (var title in new[] { "Power over time", "Where the power went" })
                {
                    var card = Card(view, title);
                    var heading = UiHarness.Find<TextBlock>(card, text => text.Text == title)!;
                    var pills = UiHarness.Find<Border>(card, border => border.Style == window.Resources["M.PillTrack"])!;
                    heading.TranslatePoint(new Point(heading.ActualWidth, 0), card).X
                        .ShouldBeLessThan(pills.TranslatePoint(new Point(0, 0), card).X, $"{title}: the heading and the pills keep apart");
                }
                UiHarness.Render(window, (int)window.ActualWidth, (int)window.ActualHeight, "midnight-dashboard-960-Light.png");
                UiHarness.Find<ScrollViewer>(view)!.ScrollToEnd();
                window.UpdateLayout();
                UiHarness.Render(window, (int)window.ActualWidth, (int)window.ActualHeight, "midnight-dashboard-960-end-Light.png");
            }
            finally
            {
                window.CloseForSwitch();
            }
        });
        new FileInfo(Path.Combine(UiHarness.Folder, "midnight-dashboard-960-Light.png")).Length.ShouldBeGreaterThan(30_000);
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
                var transform = (TranslateTransform)pages.Showing.RenderTransform;
                transform.Y.ShouldBe(0, "nothing rises under reduced motion");
            }
            finally
            {
                window.CloseForSwitch();
            }
        });

    /// <summary>Plan O 0.4, as Classic's window keeps it: placed at the bounds a switch carries rather than fitting itself to
    /// the screen, Classic's Now shown as the Dashboard, and a close for the switch a close.</summary>
    [Fact]
    public void Midnights_window_takes_the_bounds_and_page_a_switch_carries_and_closes_for_it()
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var shell = MidnightFixtures.Shell(saver);
            var midnight = MidnightFixtures.Window(shell);
            IShellWindow window = midnight;
            window.Window.ShouldBeSameAs(midnight);
            var closed = 0;
            window.Closed += (_, _) => closed++;

            window.Bounds = new Rect(-20000, 10, 1000, 700);
            window.Page = Page.Now;
            shell.Page.ShouldBe(Page.Dashboard, "Classic's Now arrives as the Dashboard");
            window.Page = Page.Report;
            window.Page.ShouldBe(Page.Report);
            try
            {
                window.Show();
                midnight.UpdateLayout();
                foreach (var bounds in new[] { new Rect(midnight.Left, midnight.Top, midnight.ActualWidth, midnight.ActualHeight), window.Bounds })
                {
                    bounds.X.ShouldBe(-20000, 1);
                    bounds.Y.ShouldBe(10, 1);
                    bounds.Width.ShouldBe(1000, 1);
                    bounds.Height.ShouldBe(700, 1);
                }
                window.State.ShouldBe(WindowState.Normal);
            }
            finally
            {
                window.CloseForSwitch();
            }
            closed.ShouldBe(1);
        });

    /// <summary>The caption's close is an ordinary close: the App's Closing handler, not the window, turns it into a hide
    /// to the tray while the window is the current one, and lets it through once a switch has moved on.</summary>
    [Fact]
    public void The_close_button_asks_to_close_and_leaves_the_hiding_to_the_app()
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var window = MidnightFixtures.Window(MidnightFixtures.Shell(saver));
            var current = true;
            window.Closing += (_, args) =>
            {
                if (!current) return;
                args.Cancel = true;
                window.Hide();
            };
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Show();
            var close = UiHarness.Find<Button>(window, button => AutomationProperties.GetName(button) == "Close")!;
            close.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            window.IsVisible.ShouldBeFalse("the App hid it");
            closed.ShouldBeFalse("and it lives on, for the tray");
            current = false;
            close.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            closed.ShouldBeTrue("the window itself never holds on");
        });

    /// <summary>The Dashboard's card that <paramref name="title"/> names for a screen reader.</summary>
    private static Border Card(DashboardView view, string title)
        => UiHarness.Find<Border>(view, border => AutomationProperties.GetName(border) == title) ?? throw new InvalidOperationException($"No card {title}.");

    private static string[] Pills(DashboardView view, string card)
        => [.. UiTree.Descendants<RadioButton>(Card(view, card)).Select(pill => (string)pill.Content)];

    private static string? Checked(DashboardView view, string card)
        => UiTree.Descendants<RadioButton>(Card(view, card)).SingleOrDefault(pill => pill.IsChecked == true)?.Content as string;

    /// <summary>
    /// The window with the chart's tooltip laid where it opens over the chart: a popup is a window of its own, which a render
    /// of the main one leaves out. The popup of a window far off screen lands on a screen, so its frosted backdrop, a
    /// snapshot of what lies under it there, is empty and the glass shows its fill alone.
    /// </summary>
    private static void WithTip(MidnightWindow window, AreaChart chart, string name)
    {
        var tip = chart.Tip!;
        var at = chart.TranslatePoint(new Point(tip.HorizontalOffset, tip.VerticalOffset), window);
        static System.Windows.Media.Imaging.BitmapSource Shot(Visual visual, double width, double height)
        {
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            return bitmap;
        }
        var picture = new DrawingVisual();
        using (var dc = picture.RenderOpen())
        {
            dc.DrawImage(Shot(window, window.ActualWidth, window.ActualHeight), new Rect(0, 0, Math.Ceiling(window.ActualWidth), Math.Ceiling(window.ActualHeight)));
            dc.DrawImage(Shot(tip, tip.ActualWidth, tip.ActualHeight), new Rect(at.X, at.Y, Math.Ceiling(tip.ActualWidth), Math.Ceiling(tip.ActualHeight)));
        }
        UiHarness.Render(picture, (int)window.ActualWidth, (int)window.ActualHeight, name);
    }
}
