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
                    UiTree.Descendants<ShareBar>(CardNamed(view, "Where the power went")).Count().ShouldBe(4, "a row a part");
                    UiTree.Descendants<TrendMark>(view).Where(mark => mark.Kind is TrendKind.Up or TrendKind.Down)
                        .ShouldAllBe(mark => mark.LowerIsBetter, "every trend on the page is of energy, where less is better");
                    UiTree.Descendants<TrendMark>(CardNamed(view, "Today")).Single().Sense.ShouldBe(TrendSense.Bad, "today runs above the average day");
                    // 0.8.1, after the reference: two framed sections, the KPIs and the chart in one and the table in the other, flat.
                    UiTree.Descendants<Border>(view).Count(border => border.Style == window.Resources["M.Card"]).ShouldBe(2, "the overview and the table");
                    UiTree.Descendants<Border>(view).ShouldAllBe(border => border.Effect == null, "no shadow under a section");
                    UiHarness.Find<TextBlock>(view, text => text.Text == "Energy used").ShouldNotBeNull("a KPI's name in sentence case");
                    UiHarness.Find<TextBlock>(view, text => text.Text == "This month").ShouldNotBeNull("and the time it covers in its tag");
                    // No Effect over a card's text or the chart: text keeps ClearType, and the crosshair redraws the chart alone.
                    foreach (var element in UiTree.Descendants<TextBlock>(view).Cast<DependencyObject>().Append(UiHarness.Find<AreaChart>(view)!))
                    {
                        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
                            (node as UIElement)?.Effect.ShouldBeNull($"{node.GetType().Name} over {element}");
                    }
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
                var lefts = new Dictionary<string, double>();
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
                    var shown = UiHarness.Find<FrameworkElement>(window, element => element.GetType() == view).ShouldNotBeNull($"the {label} page");
                    // Every page's content starts where the page header's title does.
                    lefts[label] = ((FrameworkElement)UiHarness.Find<ScrollViewer>(shown)!.Content).TranslatePoint(default, window).X;
                    if (page != Page.Dashboard) UiHarness.Render(window, (int)window.ActualWidth, (int)window.ActualHeight, $"midnight-window-{label.ToLowerInvariant()}-Dark.png");
                }
                lefts.ShouldAllBe(left => Math.Abs(left.Value - lefts["Dashboard"]) < 0.5, "each page lines up with the Dashboard and the page header");
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
                // Review 10: short as it is, every page in the sidebar shows beside the update card without scrolling it.
                var nav = UiHarness.Find<ScrollViewer>(sidebar)!;
                foreach (var item in new FrameworkElement[]
                {
                    UiHarness.Find<RadioButton>(window, button => Equals(button.Content, "Settings"))!,
                    UiHarness.Find<Button>(window, button => AutomationProperties.GetName(button) == "Support")!,
                })
                {
                    item.TranslatePoint(new Point(0, item.ActualHeight), nav).Y.ShouldBeLessThanOrEqualTo(nav.ViewportHeight + 0.5, $"{item} in view");
                }
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
    public void The_chart_tooltip_opens_at_the_middle_point_in_its_bubble_and_the_kpis_stand_three_across()
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
                chart.Culture.Name.ShouldBe("en-US", "the page's culture, the ViewModel's");
                chart.Hover(chart.Model.Capacity / 2);   // the middle of the day's width: noon
                UiHarness.Pump(TimeSpan.FromMilliseconds(300));
                chart.Tip.ShouldNotBeNull();
                chart.Tip.IsOpen.ShouldBeTrue();
                chart.TipText.ShouldMatch(@"^12:00 Power: \d+ W$", "the time as the axis writes it, over the figure named");
                UiHarness.Find<Border>(chart.Tip, border => border.Name == "Bubble").ShouldNotBeNull("the tooltip is a solid bubble");
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
                var table = CardNamed(view, "Where the power went");
                var inside = table.ActualWidth - table.Padding.Right - table.BorderThickness.Right;
                foreach (var mark in UiTree.Descendants<TrendMark>(table))
                    mark.TranslatePoint(new Point(mark.ActualWidth, 0), table).X.ShouldBeLessThanOrEqualTo(inside + 0.5, "the trend column stays inside the card");
                foreach (var name in UiTree.Descendants<TextBlock>(table).Where(text => text.Text is "CPU package" or "Rest of system"))
                    name.DesiredSize.Width.ShouldBeLessThanOrEqualTo(name.ActualWidth + 0.5, $"{name.Text} is whole");
                foreach (var bar in UiTree.Descendants<ShareBar>(table))
                    bar.ActualWidth.ShouldBeGreaterThanOrEqualTo(96, "a share bar long enough to read at the narrowest");
                foreach (var title in new[] { "Power over time", "Where the power went" })
                {
                    var card = CardNamed(view, title);
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

    /// <summary>The top bar's Switch look chooses the other look as Settings does, so the choice is saved and a failure lands
    /// on Settings' message line, rather than asking the switcher behind Settings' back.</summary>
    [Fact]
    public void Switch_look_in_the_top_bar_is_the_shells_own()
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var shell = MidnightFixtures.Shell(saver);
            var window = MidnightFixtures.Window(shell);
            window.Show();
            try
            {
                var button = UiHarness.Find<Button>(window, button => AutomationProperties.GetName(button) == "Switch look")!;
                button.Command.ShouldBeSameAs(shell.SwitchLook);
                button.ToolTip.ShouldBe(shell.SwitchLookTip);
            }
            finally
            {
                window.CloseForSwitch();
            }
        });

    /// <summary>The sun and moon choose the theme through Settings too (design §1: the Theme setting applies to whichever
    /// look is on, and the toggle sets it), so the choice is saved and Settings shows it.</summary>
    [Fact]
    public void The_theme_toggle_chooses_the_other_theme_through_settings()
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var shell = MidnightFixtures.Shell(saver);
            var window = MidnightFixtures.Window(shell, Theme.Dark);
            window.Show();
            try
            {
                var button = UiHarness.Find<Button>(window, button => button.Name == "ThemeButton")!;
                button.ToolTip.ShouldBe("Light theme", "the button says what it will do");
                AutomationProperties.GetName(button).ShouldBe("Light theme", "and a screen reader hears the same");
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                shell.Settings.Theme.ShouldBe(ThemeChoice.Light);
            }
            finally
            {
                window.CloseForSwitch();
            }
        });

    /// <summary>Review 7: an icon is named as its tooltip reads, Maximize becomes Restore while maximised, and the PC's kind
    /// glyph says Laptop or Desktop.</summary>
    [Fact]
    public void The_top_bars_icons_are_named_as_their_tooltips_read()
        => UiHarness.OnUi(() =>
        {
            MidnightWindow.MaximizeFace(WindowState.Normal).ShouldBe(("", "Maximize"));
            MidnightWindow.MaximizeFace(WindowState.Maximized).ShouldBe(("", "Restore"));
            using var saver = new FakeSaver();
            var window = MidnightFixtures.Window(MidnightFixtures.Shell(saver));
            window.Show();
            try
            {
                var maximize = UiHarness.Find<Button>(window, button => button.Name == "MaximizeButton")!;
                AutomationProperties.GetName(maximize).ShouldBe("Maximize");
                maximize.ToolTip.ShouldBe("Maximize");
                var kind = UiHarness.Find<TextBlock>(window, text => text.Name == "KindGlyph")!;
                AutomationProperties.GetName(kind).ShouldBe((string)kind.ToolTip);
                AutomationProperties.GetName(kind).ShouldBeOneOf("Laptop", "Desktop");
            }
            finally
            {
                window.CloseForSwitch();
            }
        });

    /// <summary>Review 4 (0.8.0): the window is built while the shell is on Classic's Now, which has no view here. The
    /// page host shows nothing for it, not the ViewModel's type name, and the Dashboard the window shows for it once
    /// shown is its first page, so it comes in at once, without a cross-fade, at every launch and every switch from Classic.</summary>
    [Fact]
    public void A_window_built_on_classics_now_shows_no_page_for_it_and_opens_on_the_dashboard_without_a_fade()
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var shell = MidnightFixtures.Shell(saver);
            shell.Page = Page.Now;
            var window = MidnightFixtures.Window(shell);
            var pages = (PageHost)window.FindName("Pages");
            var presenters = pages.Children.OfType<ContentPresenter>().ToList();
            UiHarness.Pump(TimeSpan.FromMilliseconds(50));   // the page's binding comes alive on the dispatcher's next turn
            pages.Content.ShouldBeSameAs(shell.Now);
            presenters.ShouldAllBe(presenter => presenter.Content == null, "Now has no view here: nothing, not its type's name");
            window.Show();
            try
            {
                pages.Showing.Content.ShouldBeSameAs(shell.Dashboard);
                pages.Showing.Opacity.ShouldBe(1);
                pages.Showing.HasAnimatedProperties.ShouldBeFalse("the first page comes in at once");
                presenters.Single(presenter => presenter != pages.Showing).Content.ShouldBeNull();
                UiHarness.Pump(TimeSpan.FromMilliseconds(200));
                UiHarness.Find<TextBlock>(window, text => text.Text.Contains("ViewModel", StringComparison.Ordinal)).ShouldBeNull();
                UiHarness.Find<DashboardView>(window).ShouldNotBeNull();
            }
            finally
            {
                window.CloseForSwitch();
            }
        });

    /// <summary>Review 9: building the window changes nothing in the shell, so a switch whose window then fails to show
    /// leaves the page as it was; shown, the window takes Classic's Now as its Dashboard.</summary>
    [Fact]
    public void The_window_leaves_the_page_alone_until_it_shows()
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var shell = MidnightFixtures.Shell(saver);
            shell.Page = Page.Now;
            var window = MidnightFixtures.Window(shell);
            shell.Page.ShouldBe(Page.Now, "a window that may yet fail to show has changed nothing");
            window.Show();
            try
            {
                UiHarness.Pump(TimeSpan.FromMilliseconds(200));
                shell.Page.ShouldBe(Page.Dashboard, "shown, it shows its own landing page for Now");
                UiHarness.Find<DashboardView>(window).ShouldNotBeNull();
                window.Pill.Opacity.ShouldBe(1, "and the pill sits on Dashboard");
            }
            finally
            {
                window.CloseForSwitch();
            }
        });

    /// <summary>A look that won't open from the top bar says why on the page in view, not on Settings' line out of sight:
    /// a banner under the page header until it is dismissed or its time is up. A message from elsewhere raises none.</summary>
    [Fact]
    public void A_look_that_wont_open_from_the_top_bar_says_why_on_the_page_until_dismissed_or_its_time_is_up()
    {
        Directory.CreateDirectory(UiHarness.Folder);
        UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var ui = new FakeUiSettings { Current = UiPreferences.Default with { Look = Look.Midnight }, LookProblem = "Couldn't open the Classic look: its window failed to load." };
            var shell = MidnightFixtures.Shell(saver, ui: ui);
            var window = MidnightFixtures.Window(shell);
            // Long enough that no pump can outlast it: another test's work may run inside this one's pumps on the shared
            // dispatcher, and a banner that timed out meanwhile would read as never shown. Its going by itself is timed below.
            window.ProblemShownFor = TimeSpan.FromMinutes(10);
            window.Show();
            try
            {
                UiHarness.Pump(TimeSpan.FromMilliseconds(300));
                var banner = UiHarness.Find<Border>(window, border => AutomationProperties.GetName(border) == "Look switch problem")!;
                banner.IsVisible.ShouldBeFalse();
                var switchLook = UiHarness.Find<Button>(window, button => AutomationProperties.GetName(button) == "Switch look")!;
                var said = UiHarness.Find<TextBlock>(banner, text => text.Name == "SwitchProblemText")!;

                Press(switchLook);
                shell.Settings.Look.ShouldBe(Look.Midnight, "the window stays");
                banner.IsVisible.ShouldBeTrue();
                said.Text.ShouldBe(ui.LookProblem);
                UiHarness.Render(window, (int)window.ActualWidth, (int)window.ActualHeight, "midnight-switch-problem-Dark.png");
                Press(UiHarness.Find<Button>(banner, button => AutomationProperties.GetName(button) == "Dismiss")!);
                banner.IsVisible.ShouldBeFalse("dismissed");

                ui.LookProblem = "Couldn't open the Classic look: still no window.";
                window.ProblemShownFor = TimeSpan.FromMilliseconds(300);
                Press(switchLook);
                said.Text.ShouldBe(ui.LookProblem, "shown again, with the new reason");
                for (var waited = 0; waited < 5000 && banner.IsVisible; waited += 100) UiHarness.Pump(TimeSpan.FromMilliseconds(100));
                banner.IsVisible.ShouldBeFalse("it goes by itself");

                shell.Settings.StartWithWindows = !shell.Settings.StartWithWindows;
                ui.LookProblem = null;
                shell.Settings.Look = Look.Classic;   // from Settings, where its own line says it
                UiHarness.Pump(TimeSpan.FromMilliseconds(100));
                banner.IsVisible.ShouldBeFalse("only the top bar's switch raises it");
            }
            finally
            {
                window.CloseForSwitch();
            }
        });
    }

    private const string IntroName = "This is PowerLedger's new look";

    /// <summary>A Midnight window over preferences that have not yet introduced the new look.</summary>
    private static (MidnightWindow Window, ShellViewModel Shell, FakeUiSettings Ui) Introducing(FakeSaver saver, Theme theme = Theme.Dark)
    {
        var ui = new FakeUiSettings { Current = UiPreferences.Default };   // Midnight, as every PC from 0.7.x lands
        var shell = MidnightFixtures.Shell(saver, ui: ui);
        return (MidnightFixtures.Window(shell, theme), shell, ui);
    }

    private static Border? Intro(Window window) => UiHarness.Find<Border>(window, border => AutomationProperties.GetName(border) == IntroName);

    private static void Press(Button button)
    {
        ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(button)).Invoke();
        UiHarness.Pump(TimeSpan.FromMilliseconds(100));
    }

    /// <summary>Midnight look design §1: the first time Midnight shows, a banner under the page header says this is the new
    /// look and how to go back, with Switch back and Got it; accent-edged, not a warning, blocking nothing, and staying
    /// until answered. Drawn in both themes and in a short window.</summary>
    [Fact]
    public void The_first_midnight_window_says_this_is_the_new_look_until_got_it()
    {
        Directory.CreateDirectory(UiHarness.Folder);
        foreach (var theme in new[] { Theme.Dark, Theme.Light })
        {
            UiHarness.OnUi(() =>
            {
                using var saver = new FakeSaver();
                var (window, shell, ui) = Introducing(saver, theme);
                window.Show();
                try
                {
                    UiHarness.Pump(TimeSpan.FromMilliseconds(400));
                    var intro = Intro(window)!;
                    intro.IsVisible.ShouldBeTrue();
                    intro.BorderBrush.ShouldBe(window.FindResource("M.Accent"), "an accent edge: news, not a warning");
                    UiHarness.Find<TextBlock>(intro, text => AutomationProperties.GetName(text) == "New")!.Foreground
                        .ShouldBe(window.FindResource("M.AccentText"), "a small accent glyph takes the text tint, which reads on the panel");
                    UiHarness.Find<TextBlock>(intro, text => text.Text == IntroName).ShouldNotBeNull();
                    UiHarness.Find<TextBlock>(intro, text => text.Text == "Prefer the classic one? Switch back any time here, or in Settings → Preferences.").ShouldNotBeNull();
                    UiHarness.Find<Button>(intro, button => Equals(button.Content, "Switch back")).ShouldNotBeNull();
                    var gotIt = UiHarness.Find<Button>(intro, button => Equals(button.Content, "Got it"))!;
                    UiHarness.Find<DashboardView>(window)!.IsEnabled.ShouldBeTrue("nothing is blocked");
                    UiHarness.Render(window, (int)window.ActualWidth, (int)window.ActualHeight, $"midnight-new-look-{theme}.png");

                    Press(gotIt);
                    intro.IsVisible.ShouldBeFalse();
                    ui.Current.LookIntroduced.ShouldBeTrue();
                    ui.Changes.ShouldBe(["look introduced"], "nothing switched");
                }
                finally
                {
                    window.CloseForSwitch();
                }

                var again = MidnightFixtures.Window(shell, theme);
                again.Show();
                try
                {
                    UiHarness.Pump(TimeSpan.FromMilliseconds(200));
                    Intro(again)!.IsVisible.ShouldBeFalse("once is enough");
                }
                finally
                {
                    again.CloseForSwitch();
                }
            });
            new FileInfo(Path.Combine(UiHarness.Folder, $"midnight-new-look-{theme}.png")).Length.ShouldBeGreaterThan(30_000);
        }
    }

    [Fact]
    public void In_a_short_window_the_new_look_banner_still_fits_beside_the_page()
    {
        Directory.CreateDirectory(UiHarness.Folder);
        UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var (window, _, _) = Introducing(saver);
            window.FitTo(new Bounds(-20000, 0, 880 + 2 * WindowFit.Margin, 560 + 2 * WindowFit.Margin));
            window.Show();
            try
            {
                UiHarness.Pump(TimeSpan.FromMilliseconds(400));
                var intro = Intro(window)!;
                intro.IsVisible.ShouldBeTrue();
                foreach (var button in UiTree.Descendants<Button>(intro))
                    button.TranslatePoint(new Point(button.ActualWidth, 0), intro).X.ShouldBeLessThanOrEqualTo(intro.ActualWidth, $"{button.Content} inside the banner");
                UiHarness.Find<ScrollViewer>(UiHarness.Find<DashboardView>(window)!)!.ViewportHeight.ShouldBeGreaterThan(150, "the page still shows under it");
                UiHarness.Render(window, (int)window.ActualWidth, (int)window.ActualHeight, "midnight-short-new-look.png");
            }
            finally
            {
                window.CloseForSwitch();
            }
        });
    }

    /// <summary>Switch back chooses Classic through Settings, as the top bar's Switch look does, and retires the banner.</summary>
    [Fact]
    public void Switch_back_in_the_banner_chooses_classic_through_settings_and_retires_it()
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var (window, shell, ui) = Introducing(saver);
            window.Show();
            try
            {
                UiHarness.Pump(TimeSpan.FromMilliseconds(300));
                Press(UiHarness.Find<Button>(Intro(window)!, button => Equals(button.Content, "Switch back"))!);
                ui.Changes.ShouldContain("look Classic");
                shell.Settings.Look.ShouldBe(Look.Classic);
                ui.Current.LookIntroduced.ShouldBeTrue();
                Intro(window)!.IsVisible.ShouldBeFalse();
            }
            finally
            {
                window.CloseForSwitch();
            }
        });

    /// <summary>Any look switch retires it: back and forth from Settings, and it is not there on the return.</summary>
    [Fact]
    public void Any_look_switch_retires_the_banner_for_good()
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var (window, shell, ui) = Introducing(saver);
            window.Show();
            try
            {
                UiHarness.Pump(TimeSpan.FromMilliseconds(300));
                Intro(window)!.IsVisible.ShouldBeTrue();
                shell.Settings.Look = Look.Classic;
                shell.Settings.Look = Look.Midnight;
                UiHarness.Pump(TimeSpan.FromMilliseconds(100));
                ui.Current.LookIntroduced.ShouldBeTrue();
                Intro(window)!.IsVisible.ShouldBeFalse();
            }
            finally
            {
                window.CloseForSwitch();
            }
        });

    /// <summary>Not while the wizard has the window, and never in Classic, which has no such banner at all.</summary>
    [Fact]
    public void Not_during_setup_and_not_in_classic()
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var (window, shell, _) = Introducing(saver);
            shell.BeginSetup();
            window.Show();
            try
            {
                UiHarness.Pump(TimeSpan.FromMilliseconds(300));
                Intro(window)!.IsVisible.ShouldBeFalse("the wizard has the window");
            }
            finally
            {
                window.CloseForSwitch();
            }

            var classic = new MainWindow
            {
                DataContext = MidnightFixtures.Shell(saver, ui: new FakeUiSettings { Current = UiPreferences.Default }), WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
                ShowInTaskbar = false, ShowActivated = false,
            };
            classic.Resources.MergedDictionaries.Add(ThemeManager.Palette(Look.Classic, Theme.Dark));
            classic.Show();
            try
            {
                UiHarness.Pump(TimeSpan.FromMilliseconds(300));
                Intro(classic).ShouldBeNull();
                UiHarness.Find<TextBlock>(classic, text => text.Text == IntroName).ShouldBeNull();
            }
            finally
            {
                ((IShellWindow)classic).CloseForSwitch();
            }
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
    private static Border CardNamed(DashboardView view, string title)
        => UiHarness.Find<Border>(view, border => AutomationProperties.GetName(border) == title) ?? throw new InvalidOperationException($"No card {title}.");

    private static string[] Pills(DashboardView view, string card)
        => [.. UiTree.Descendants<RadioButton>(CardNamed(view, card)).Select(pill => (string)pill.Content)];

    private static string? Checked(DashboardView view, string card)
        => UiTree.Descendants<RadioButton>(CardNamed(view, card)).SingleOrDefault(pill => pill.IsChecked == true)?.Content as string;

    /// <summary>
    /// The window with the chart's tooltip laid where it opens over the chart: a popup is a window of its own, which a render
    /// of the main one leaves out.
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
