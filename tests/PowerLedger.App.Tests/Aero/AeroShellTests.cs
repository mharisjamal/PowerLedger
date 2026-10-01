using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PowerLedger.App.Aero;
using Shouldly;
using AeroDashboard = PowerLedger.App.Aero.DashboardView;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan S D1, D2, D4: Aero's shell as a person uses it. The sidebar's pill follows the page, every page has its view
/// (the Dashboard and Parts by page over one ViewModel), the banner's Switch back goes to the look the move left, Switch
/// look offers the other two looks and says why one didn't open, the bell holds approvals and today's alerts, a dialog
/// opens and Esc closes it, the keyboard walks the sidebar, the top bar and the page in order, and the Dashboard shows
/// real figures in one column or two.
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]
public class AeroShellTests
{
    /// <summary>Runs <paramref name="test"/> on a shown Aero window over <paramref name="shell"/>, reduced motion so every
    /// movement has landed, and closes it after.</summary>
    private static void OnWindow(ShellViewModel shell, Action<AeroWindow> test, double width = 1440, double height = 900, Theme theme = Theme.Dark)
        => UiHarness.OnUi(() =>
        {
            var window = AeroHost.Window(shell, theme);
            using var reduced = AeroMotion.Force(true);   // after the window: its glass sets the override from Settings
            window.Width = width;
            window.Height = height;
            window.Show();
            try
            {
                UiHarness.Pump(TimeSpan.FromMilliseconds(400));
                test(window);
            }
            finally
            {
                window.OpenMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
                window.CloseForSwitch();
            }
        });

    private static RadioButton NavItem(AeroWindow window, string name)
        => UiHarness.Find<RadioButton>(window, r => AutomationProperties.GetName(r) == name)!;

    /// <summary>A press as a person makes it: the button's command when it has one, else its Click.</summary>
    private static void Press(ButtonBase button)
    {
        if (button.Command is { } command) command.Execute(button.CommandParameter);
        else button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
    }

    private static void Key(Window window, Key key)
    {
        var source = PresentationSource.FromVisual(window)!;
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }

    [Fact]
    public void The_sidebar_lists_the_seven_pages_and_the_pill_follows_the_page()
    {
        using var saver = new FakeSaver();
        var shell = AeroFixtures.Shell(saver);
        OnWindow(shell, window =>
        {
            UiHarness.Find<StackPanel>(window, p => p.Name == "NavItems")!.Children.OfType<RadioButton>().Select(AutomationProperties.GetName)
                .ShouldBe(["Dashboard", "History", "Parts", "Insights", "Reports", "Household", "Settings"]);
            foreach (var (name, page) in new[] { ("Insights", Page.Insights), ("Parts", Page.Parts), ("History", Page.Breakdown), ("Settings", Page.Settings), ("Dashboard", Page.Dashboard) })
            {
                var item = NavItem(window, name);
                item.IsChecked = true;
                UiHarness.Pump(TimeSpan.FromMilliseconds(100));
                shell.Page.ShouldBe(page);
                var pillY = window.Pill.TranslatePoint(new Point(0, 0), item).Y;
                pillY.ShouldBe(0, 0.5, $"the pill sits on {name}");
                window.Pill.Opacity.ShouldBe(1);
            }
        });
    }

    [Fact]
    public void Each_page_shows_its_own_view_the_dashboard_and_parts_by_page()
    {
        using var saver = new FakeSaver();
        var shell = AeroFixtures.Shell(saver);
        OnWindow(shell, window =>
        {
            window.PageHost.Showing.ShouldBeOfType<AeroDashboard>();
            shell.Page = Page.Parts;
            UiHarness.Pump(TimeSpan.FromMilliseconds(300));
            window.PageHost.Showing.ShouldNotBeOfType<AeroDashboard>("Parts is its own view over the same ViewModel");
            window.PageHost.Showing!.DataContext.ShouldBeSameAs(shell.Dashboard);
            shell.Page = Page.Insights;
            UiHarness.Pump(TimeSpan.FromMilliseconds(300));
            window.PageHost.Showing!.DataContext.ShouldBeSameAs(shell.Insights);
            shell.Page = Page.Dashboard;
            UiHarness.Pump(TimeSpan.FromMilliseconds(300));
            window.PageHost.Showing.ShouldBeOfType<AeroDashboard>();
            UiHarness.PumpUntil(() => window.PageHost.Children.Count == 1, TimeSpan.FromSeconds(10), "the pages left to go once faded");
        });
    }

    [Fact]
    public void Shown_it_asks_the_dashboard_for_aeros_figures_and_closed_it_stops()
    {
        using var saver = new FakeSaver();
        var shell = AeroFixtures.Shell(saver);
        UiHarness.OnUi(() =>
        {
            var window = AeroHost.Window(shell);
            shell.Dashboard!.Detailed.ShouldBeFalse();
            window.Show();
            shell.Dashboard.Detailed.ShouldBeTrue();
            shell.Dashboard.Detail.ShouldNotBeNull();
            window.CloseForSwitch();
            shell.Dashboard.Detailed.ShouldBeFalse("Midnight, which may open next, reads nothing more");
        });
    }

    [Theory]
    [InlineData("Classic", "Classic")]
    [InlineData(null, "Midnight")]
    public void The_banner_shows_once_and_switch_back_goes_to_the_look_the_move_left(string? before, string back)
    {
        using var saver = new FakeSaver();
        var ui = AeroFixtures.Moved(before is null ? null : Enum.Parse<Look>(before));
        var shell = AeroFixtures.Shell(saver, ui);
        OnWindow(shell, window =>
        {
            var banner = UiHarness.Find<GlassPanel>(window, p => p.Name == "LookIntro")!;
            banner.IsVisible.ShouldBeTrue();
            UiHarness.Find<TextBlock>(banner, t => t.Name == "LookIntroLine")!.Text.ShouldContain($"Prefer {back}?");

            Press(UiHarness.Find<Button>(banner, b => b.Name == "SwitchBackButton")!);

            ui.Changes.ShouldBe(["look introduced", $"look {back}"]);
            banner.IsVisible.ShouldBeFalse();
        });
    }

    [Fact]
    public void Got_it_retires_the_banner_and_keeps_aero()
    {
        using var saver = new FakeSaver();
        var ui = AeroFixtures.Moved();
        var shell = AeroFixtures.Shell(saver, ui);
        OnWindow(shell, window =>
        {
            Press(UiHarness.Find<Button>(window, b => b.Name == "GotItButton")!);

            ui.Changes.ShouldBe(["look introduced"]);
            ui.Current.Look.ShouldBe(Look.Aero);
            UiHarness.Find<GlassPanel>(window, p => p.Name == "LookIntro")!.IsVisible.ShouldBeFalse();
        });
    }

    /// <summary>0.10.9: the mockup's sidebar has no Switch look; Settings' Look row chooses, and a look that doesn't open
    /// says why on the banner.</summary>
    [Fact]
    public void A_look_chosen_that_didnt_open_says_why()
    {
        using var saver = new FakeSaver();
        var ui = AeroFixtures.Moved(introduced: true);
        var shell = AeroFixtures.Shell(saver, ui);
        OnWindow(shell, window =>
        {
            UiHarness.Find<Button>(window, b => b.Name == "SwitchLookButton").ShouldBeNull("the mockup's sidebar has none");
            window.SwitchTo(Look.Midnight);
            ui.Changes.ShouldBe(["look Midnight"]);

            ui.LookProblem = "Classic couldn't open: its window failed to show.";
            window.SwitchTo(Look.Classic);
            var problem = UiHarness.Find<GlassPanel>(window, p => p.Name == "SwitchProblem")!;
            problem.IsVisible.ShouldBeTrue();
            ((TextBlock)window.FindName("SwitchProblemText")).Text.ShouldBe(ui.LookProblem);
        });
    }

    [Fact]
    public void The_bell_holds_the_approvals_and_todays_unusual_hours_with_its_dot_on()
    {
        using var saver = new FakeSaver();
        var hour = new DateTimeOffset(2026, 9, 8, 13, 0, 0, TimeSpan.Zero);   // an hour of today on the Insights' clock (MidnightFixtures.Now, UTC)
        var insights = new FakeInsights { Answer = FakeInsights.Empty with { Anomalies = [new UsageAnomaly(hour, 0.42, 0.12, 3.5)] } };
        var shell = AeroFixtures.Shell(saver, insights: insights, pendingApprovals: 2);
        OnWindow(shell, window =>
        {
            UiHarness.Find<System.Windows.Shapes.Ellipse>(window, e => e.Name == "BellDot")!.IsVisible.ShouldBeTrue();
            Press(UiHarness.Find<Button>(window, b => b.Name == "BellButton")!);
            UiHarness.Pump(TimeSpan.FromMilliseconds(100));
            var items = window.OpenMenu!.Items.OfType<MenuItem>().ToList();
            items.Where(i => i.IsHitTestVisible).Select(AutomationProperties.GetName).ShouldBe(
                ["2 PCs are waiting to join", Bell.Line(insights.Answer.Anomalies[0], TimeZoneInfo.Local, System.Globalization.CultureInfo.CurrentCulture)]);

            items.First(i => AutomationProperties.GetName(i) == "2 PCs are waiting to join").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            shell.Page.ShouldBe(Page.Household);
        });
    }

    [Fact]
    public void Restart_opens_a_dialog_from_the_sidebars_foot_which_esc_closes_and_restart_goes_ahead()
    {
        using var saver = new FakeSaver();
        var shell = AeroFixtures.Shell(saver);
        var restarted = 0;
        var before = ServiceRestart.Run;
        ServiceRestart.Run = () => restarted++;
        try
        {
            OnWindow(shell, window =>
            {
                window.OpenModal(UiHarness.Find<Button>(window, b => b.Name == "ServiceButton"), new TextBlock { Text = "A dialog" });
                window.Modal.ShouldNotBeNull();
                UiHarness.Pump(TimeSpan.FromMilliseconds(200));
                var frost = UiHarness.Find<System.Windows.Shapes.Rectangle>(window.Modal!, r => r.Fill is System.Windows.Media.VisualBrush { Visual: Grid { Name: "Stage" } });
                frost.ShouldNotBeNull("the dialog is frosted with what is under it");
                ((System.Windows.Media.VisualBrush)frost.Fill).Viewbox.Width.ShouldBeGreaterThan(window.Modal!.ActualWidth);
                UiHarness.Find<Border>(window, b => b.Name == "Scrim")!.IsVisible.ShouldBeTrue();
                Key(window, System.Windows.Input.Key.Escape);
                window.Modal.ShouldBeNull("Esc closes it");

                Press(UiHarness.Find<Button>(window, b => b.Name == "ServiceButton")!);
                UiHarness.Pump(TimeSpan.FromMilliseconds(100));
                var restart = window.OpenMenu!.Items.OfType<MenuItem>().Single(i => AutomationProperties.GetName(i) == "Restart the service");
                restart.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                window.Modal.ShouldNotBeNull();
                UiHarness.Pump(TimeSpan.FromMilliseconds(100));
                Press(UiHarness.Find<Button>(window.Modal!, b => (string)b.Content == "Restart")!);
                restarted.ShouldBe(1);
                window.Modal.ShouldBeNull();
                window.ToastText.ShouldBe("Restarting the service");
            });
        }
        finally
        {
            ServiceRestart.Run = before;
        }
    }

    [Fact]
    public void The_keyboard_walks_the_sidebar_then_the_top_bar_then_the_page()
    {
        using var saver = new FakeSaver();
        var shell = AeroFixtures.Shell(saver);
        OnWindow(shell, window =>
        {
            NavItem(window, "Dashboard").Focus().ShouldBeTrue();
            var names = new List<string>();
            for (var i = 0; i < 14; i++)
            {
                var element = (UIElement)Keyboard.FocusedElement;
                element.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                names.Add(AutomationProperties.GetName((DependencyObject)Keyboard.FocusedElement));
            }
            names.Take(11).ShouldBe(["This PC", "Laptop-2", "Service", "Search history", "Approvals and alerts", "Watts overlay", "Household", "Minimize", "Maximize", "Close", "Open report"],
                "a group of pages is one stop, as a radio group is; then the PCs, the service, the top bar with the window's buttons, and the page");
        });
    }

    /// <summary>0.10.9: the Dashboard has no history table (History keeps its own page), so the top bar's Search history
    /// searches the History page and brings it up.</summary>
    [Fact]
    public void Ctrl_k_finds_the_search_and_a_search_searches_the_history_page()
    {
        using var saver = new FakeSaver();
        var shell = AeroFixtures.Shell(saver);
        shell.Page = Page.Settings;
        OnWindow(shell, window =>
        {
            Keyboard.Modifiers.ShouldBe(ModifierKeys.None);
            var search = UiHarness.Find<TextBox>(window, t => t.Name == "Search")!;
            search.Focus();
            search.Text = "cpu";
            UiHarness.Pump(TimeSpan.FromMilliseconds(300));
            shell.Page.ShouldBe(Page.Breakdown, "the search is of the History page");
            shell.Breakdown.Search.ShouldBe("cpu");
            UiHarness.HasFocus(search).ShouldBeTrue("the search keeps the focus as the page changes");
            search.Focus();   // back from wherever another process's foreground took it; Esc clears only a focused search
            Key(window, System.Windows.Input.Key.Escape);
            search.Text.ShouldBeEmpty();
            shell.Breakdown.Search.ShouldBeEmpty();
        });
    }

    [Fact]
    public void The_dashboard_shows_real_figures_in_two_columns_wide_and_one_narrow()
    {
        using var saver = new FakeSaver();
        var shell = AeroFixtures.Shell(saver);
        OnWindow(shell, window =>
        {
            var view = (AeroDashboard)window.PageHost.Showing!;
            view.OneColumn.ShouldBeFalse();
            UiHarness.Find<TextBlock>(view, t => t.Name == "TodayKwh")!.Text.ShouldBe("0.284");
            UiHarness.Find<TextBlock>(view, t => t.Name == "ChangeText")!.Text.ShouldNotBe("N/A");
            AutomationProperties.GetName(UiHarness.Find<Grid>(view, g => g.Name == "MonthBar")!).ShouldBe("Day 8 of 30");
            UiHarness.Find<ItemsControl>(view, i => i.Name == "HistRows").ShouldBeNull("History keeps its own page");
            UiHarness.Find<DonutChart>(view)!.Arcs.Count.ShouldBe(4);
            UiHarness.Find<LiveChart>(view)!.Samples!.Count.ShouldBeGreaterThan(30);
            UiHarness.Find<EnergyBarChart>(view)!.BarPieces.Count.ShouldBe(14, "the mockup's fourteen bars");
            UiHarness.Find<TextBlock>(view, t => t.Name == "NowSource")!.Text.ShouldNotBeNullOrWhiteSpace();
            UiHarness.Find<ItemsControl>(view, i => i.Name == "MonthSplit")!.Items.Count.ShouldBe(2, "this desktop and the laptop");
            window.Width = 960;
            window.UpdateLayout();
            view.OneColumn.ShouldBeTrue();
            window.SideColumn().ShouldBe(76);
        });
    }

    /// <summary>0.10.9: the bottom row is the rest of the page (494 at 1440 by 900), so it follows the window's height at
    /// once; it once kept its old height, measured from the scroller's viewport, which catches up a layout pass late.</summary>
    [Fact]
    public void The_bottom_row_follows_the_windows_height_at_once()
    {
        using var saver = new FakeSaver();
        var shell = AeroFixtures.Shell(saver);
        OnWindow(shell, window =>
        {
            var view = (AeroDashboard)window.PageHost.Showing!;
            var daily = (GlassPanel)view.FindName("PDaily");
            var before = daily.ActualHeight;
            window.Height -= 40;
            window.UpdateLayout();
            UiHarness.Pump(TimeSpan.FromMilliseconds(100));
            daily.ActualHeight.ShouldBe(before - 40, 1, "the bottom row is the page's rest");
        });
    }

    [Fact]
    public void The_camera_pushes_in_on_a_pane_and_esc_comes_back()
    {
        using var saver = new FakeSaver();
        var shell = AeroFixtures.Shell(saver);
        OnWindow(shell, window =>
        {
            var view = (AeroDashboard)window.PageHost.Showing!;
            view.FocusPane((GlassPanel)view.FindName("PDaily"));   // the tour's step; the mockup's panes have no expand button
            // The fades land with the frames, which come late under load: seen still at full opacity 311 ms into a 200 ms fade.
            UiHarness.PumpUntil(() => view.Panes.Where(p => p.Name != "PDaily").All(p => p.Opacity < 0.5), TimeSpan.FromSeconds(10), "the others to dim");
            view.Focused!.Name.ShouldBe("PDaily");
            Key(window, System.Windows.Input.Key.Escape);
            view.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, System.Windows.Input.Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            UiHarness.PumpUntil(() => view.Panes.All(p => p.Opacity > 0.95), TimeSpan.FromSeconds(10), "every pane back in full");
            view.Focused.ShouldBeNull();
        });
    }

    [Fact]
    public void The_overlay_button_turns_the_overlay_on_and_off_through_settings()
    {
        using var saver = new FakeSaver();
        var ui = AeroFixtures.Moved(introduced: true);
        var shell = AeroFixtures.Shell(saver, ui);
        OnWindow(shell, window =>
        {
            var button = UiHarness.Find<ToggleButton>(window, b => b.Name == "OverlayButton")!;
            button.Command.ShouldBeSameAs(shell.Settings.ToggleOverlay, "the one toggle the tray uses too");
            button.IsChecked.ShouldBe(false);
            Press(button);
            shell.Settings.Overlay.Enabled.ShouldBeTrue();
            button.IsChecked.ShouldBe(true);
            Press(button);
            shell.Settings.Overlay.Enabled.ShouldBeFalse();
            shell.Settings.OverlaySection.Enabled = true;   // Settings' Overlay section
            button.IsChecked.ShouldBe(true);
        });
    }
}

internal static class AeroWindowProbe
{
    /// <summary>The sidebar column's width.</summary>
    public static double SideColumn(this AeroWindow window) => ((Grid)window.FindName("Stage")).ColumnDefinitions[0].ActualWidth;
}

/// <summary>
/// The tests that set <see cref="AeroMotion"/>'s override, which is one for the whole process: they run on their own, with
/// nothing else in parallel, so a test that counts the override's changes (AeroMotionTests) never hears theirs.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AeroMotionScope
{
    public const string Name = "Aero motion";
}
