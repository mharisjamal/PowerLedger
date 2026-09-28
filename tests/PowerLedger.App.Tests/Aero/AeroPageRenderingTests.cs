using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.App.Aero;
using Shouldly;
using static PowerLedger.App.Tests.AeroPages;
using static PowerLedger.App.Tests.MidnightHost;
using static PowerLedger.App.Tests.UiHarness;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan S, P1 to P5: Aero's History, Parts, Reports and Household pages over the ViewModels the other looks read, drawn in
/// both Aero palettes at the page widths a 960 and a 1440 px window leave, with their empty and failed states, the
/// automation names a screen reader reads, and nothing cut off.
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]   // its reduced-motion render sets AeroMotion's override, one for the process
public class AeroPageRenderingTests
{
    [Fact]
    public void History_shows_the_ranges_as_sliding_pills_the_chart_and_a_row_per_part_with_search_and_save_csv()
    {
        OnUi(() =>
        {
            foreach (var theme in Themes)
            {
                foreach (var (width, size) in new[] { (Narrow, "960"), (Wide, "1440") })
                {
                    using var saver = new FakeSaver();
                    var model = HistoryScreen(saver);
                    model.Range.Choice = RangeChoice.SevenDays;
                    model.Show();
                    var view = new HistoryView { DataContext = model };
                    using var page = Page(view, width, theme);
                    var at = $"{size} on {theme}";

                    var pills = MidnightHost.AllOf<RadioButton>(view).Where(p => p.Style == view.FindResource("A.SegItem")).ToList();
                    pills.Select(p => p.Content).ShouldBe(["Last hour", "Today", "7 days", "30 days", "Last year", "This month", "Last month"], at);
                    pills.Single(p => p.IsChecked == true).Content.ShouldBe("7 days", at);
                    Find<StackedChart>(view).ShouldNotBeNull(at).ActualHeight.ShouldBe(300, at);

                    // The white pill sits behind the chosen range, and slides to the next one chosen.
                    var tracks = MidnightHost.AllOf<SegTrack>(view).ToList();
                    Behind(tracks[0], pills[2], at);
                    tracks[1].Chosen.ShouldBeNull(at);
                    pills[5].IsChecked = true;
                    model.Range.Choice.ShouldBe(RangeChoice.ThisMonth, at);
                    Pump(TimeSpan.FromMilliseconds(900));
                    Behind(tracks[1], pills[5], at);
                    tracks[0].Chosen.ShouldBeNull(at);
                    model.Range.Choice = RangeChoice.SevenDays;
                    Pump(TimeSpan.FromMilliseconds(900));
                    Behind(tracks[0], pills[2], at);

                    // A row per part and the total, each part with its bar.
                    var rows = Find<ItemsControl>(view, items => items.ItemsSource == model.FoundParts).ShouldNotBeNull(at);
                    rows.Items.Count.ShouldBe(5, at);
                    foreach (var part in model.FoundParts)
                    {
                        var row = rows.ItemContainerGenerator.ContainerFromItem(part).ShouldBeAssignableTo<DependencyObject>(at)!;
                        Find<TextBlock>(row, t => t.Text == part.Name).ShouldNotBeNull($"{part.Name} {at}");
                        Find<TextBlock>(row, t => t.Text == part.Energy).ShouldNotBeNull($"{part.Name} {at}");
                        var bar = Find<ShareBar>(row, b => AutomationProperties.GetName(b) == $"{part.Name} share").ShouldNotBeNull($"{part.Name} {at}");
                        bar.Visibility.ShouldBe(part.Part is null ? Visibility.Hidden : Visibility.Visible, $"{part.Name} {at}");
                    }
                    var save = Find<Button>(view, b => AutomationProperties.GetName(b) == "Save as CSV").ShouldNotBeNull(at);
                    save.IsEnabled.ShouldBeTrue(at);
                    Find<TextBox>(view, b => AutomationProperties.GetName(b) == "Search parts").ShouldNotBeNull(at);
                    page.Render($"history-{size}");

                    // The search finds the GPU alone, and says so when it finds nothing.
                    var search = Find<TextBox>(view, b => AutomationProperties.GetName(b) == "Search parts")!;
                    search.Text = "gpu";
                    Pump(TimeSpan.FromMilliseconds(200));
                    rows.Items.Count.ShouldBe(1, at);
                    search.Text = "fan";
                    Pump(TimeSpan.FromMilliseconds(200));
                    rows.Items.Count.ShouldBe(0, at);
                    Find<TextBlock>(view, t => t.Text == "No part here matches \"fan\".").ShouldNotBeNull(at).IsVisible.ShouldBeTrue(at);
                    page.Render($"history-search-{size}");

                    // Save CSV writes the hour rows and says so under the chart's heading.
                    search.Text = "";
                    save.Command.Execute(null);
                    Pump(TimeSpan.FromMilliseconds(200));
                    Find<TextBlock>(view, t => t.Text == "Saved " + Path.GetFileName(saver.Chosen)).ShouldNotBeNull(at).IsVisible.ShouldBeTrue(at);
                }
            }
        });
        Sizes(30_000, "history-960", "history-1440", "history-search-960");
    }

    [Fact]
    public void History_with_the_service_away_says_so_over_an_empty_chart()
    {
        OnUi(() =>
        {
            foreach (var theme in Themes)
            {
                var link = new FakeLink();
                link.Connect(true);
                var model = new BreakdownViewModel(link, new FakeRangeHistory { Answer = _ => null }, UiThreads.Inline, new FakeTimeProvider(MidnightFixtures.Now),
                    TimeZoneInfo.Utc, MidnightFixtures.English, new FakeSaver());
                model.Show();
                var view = new HistoryView { DataContext = model };
                using var page = Page(view, Narrow, theme);
                Find<TextBlock>(view, t => t.Text == "History can't be read right now. It comes back when the service is running.").ShouldNotBeNull(theme.ToString())
                    .IsVisible.ShouldBeTrue(theme.ToString());
                page.Render("history-failed");
            }
        });
        Sizes(10_000, "history-failed");
    }

    [Fact]
    public void Parts_shows_each_parts_model_watts_energy_share_quality_and_a_seven_day_line()
    {
        OnUi(() =>
        {
            foreach (var theme in Themes)
            {
                // The Dashboard's own parts, as D fills them: a model under each and a week of days.
                var dashboard = MidnightFixtures.DashboardScreen(MidnightFixtures.NowScreen());
                dashboard.Show();
                dashboard.Parts.Count.ShouldBe(4, theme.ToString());
                double[][] weeks = [[210, 260, 180, 300, 280, 120, 240], [60, 90, 40, 120, 80, 20, 70], [48, 50, 47, 52, 49, 20, 48], [150, 170, 140, 190, 160, 90, 150]];
                string?[] models = ["AMD Ryzen 7 7840U with Radeon 780M Graphics", "AMD Radeon 780M", "Built-in display, 14 inches", null];
                var screen = new PartsScreen
                {
                    Parts = [.. dashboard.Parts.Select((p, i) => p with { Last7DaysWh = weeks[i], Model = models[i] })],
                };
                foreach (var (width, size) in new[] { (Narrow, "960"), (Wide, "1440") })
                {
                    var at = $"{size} on {theme}";
                    var view = new PartsView { DataContext = screen };
                    using var page = Page(view, width, theme);
                    var rows = Find<ItemsControl>(view, items => items.ItemsSource == screen.Parts).ShouldNotBeNull(at);
                    foreach (var part in screen.Parts)
                    {
                        var row = rows.ItemContainerGenerator.ContainerFromItem(part).ShouldBeAssignableTo<DependencyObject>(at)!;
                        Find<TextBlock>(row, t => t.Text == part.Name).ShouldNotBeNull($"{part.Name} {at}");
                        Find<TextBlock>(row, t => t.Text == part.NowW).ShouldNotBeNull($"{part.Name} {at}");
                        Find<TextBlock>(row, t => t.Text == part.Energy).ShouldNotBeNull($"{part.Name} {at}");
                        if (part.Model is { } model) Find<TextBlock>(row, t => t.Text == model).ShouldNotBeNull($"{part.Name} {at}").IsVisible.ShouldBeTrue($"{part.Name} {at}");
                        var line = Find<PartTrend>(row).ShouldNotBeNull($"{part.Name} {at}");
                        line.Values.ShouldBe(part.Last7DaysWh, $"{part.Name} {at}");
                        AutomationProperties.GetName(line).ShouldBe($"{part.Name}, last 7 days", $"{part.Name} {at}");
                        var chips = MidnightHost.AllOf<ContentControl>(row).Where(c => c.IsVisible).ToList();
                        if (part.Quality is { } quality) chips.Single().Content.ShouldBe(quality.ToString(), $"{part.Name} {at}");
                        else Find<TextBlock>(row, t => t.Text == "No reading").ShouldNotBeNull($"{part.Name} {at}").IsVisible.ShouldBeTrue($"{part.Name} {at}");
                    }
                    MidnightHost.AllOf<RadioButton>(view).Single(p => p.IsChecked == true).Content.ShouldBe("7 days", at);
                    page.Render($"parts-{size}");
                }

                // Nothing read yet: the words, not an empty table.
                var empty = new PartsView { DataContext = new PartsScreen() };
                using (var page = Page(empty, Narrow, theme))
                {
                    Find<TextBlock>(empty, t => t.Text.StartsWith("No readings yet.", StringComparison.Ordinal)).ShouldNotBeNull(theme.ToString()).IsVisible.ShouldBeTrue(theme.ToString());
                    page.Render("parts-empty");
                }
                var some = new PartsView { DataContext = screen };
                using (Page(some, Narrow, theme))
                {
                    Find<TextBlock>(some, t => t.Text.StartsWith("No readings yet.", StringComparison.Ordinal))!.IsVisible.ShouldBeFalse(theme.ToString());
                }

                // Over the real Dashboard, before D fills the week: no line, and the page still whole.
                var real = new PartsView { DataContext = dashboard };
                using (var page = Page(real, Narrow, theme))
                {
                    MidnightHost.AllOf<PartTrend>(real).Count().ShouldBe(4, theme.ToString());
                    page.Render("parts-dashboard");
                }
            }
        });
        Sizes(20_000, "parts-960", "parts-1440", "parts-dashboard");
        Sizes(8_000, "parts-empty");
    }

    [Fact]
    public void Under_reduced_motion_the_range_pill_jumps_to_the_choice_without_travel()
        => OnUi(() =>
        {
            using var reduced = AeroMotion.Force(true);
            var model = HistoryScreen(new FakeSaver());
            model.Show();
            var view = new HistoryView { DataContext = model };
            using var page = Page(view, Narrow, Theme.Dark);
            var track = MidnightHost.AllOf<SegTrack>(view).First();
            var pills = MidnightHost.AllOf<RadioButton>(track).ToList();
            pills[3].IsChecked = true;
            Pump(TimeSpan.FromMilliseconds(50));   // the layout pass the pill waits for, far short of the spring's 500 ms
            Behind(track, pills[3], "reduced");
            model.Range.Choice.ShouldBe(RangeChoice.ThirtyDays);
        });

    [Fact]
    public void Reports_shows_the_range_the_exports_and_the_sheet_of_figures_and_its_png_draws_the_sheet()
    {
        OnUi(() =>
        {
            foreach (var theme in Themes)
            {
                foreach (var (width, size) in new[] { (Narrow, "960"), (Wide, "1440") })
                {
                    var at = $"{size} on {theme}";
                    using var saver = new FakeSaver();
                    var model = MidnightFixtures.ReportScreen(saver);
                    model.Show();
                    var view = new ReportsView { DataContext = model };
                    using var page = Page(view, width, theme);
                    MidnightHost.AllOf<Button>(view).Where(b => b.TemplatedParent is null).Select(b => b.Content)
                        .ShouldBe(["Save as PDF", "PNG", "CSV · 1 h", "CSV · 1 min", "CSV · raw"], ignoreOrder: true, at);
                    var ranges = MidnightHost.AllOf<RadioButton>(view).ToList();
                    ranges.Select(p => p.Content).ShouldBe(["Today", "7 days", "30 days", "This month", "Last month"], at);
                    ranges.Single(p => p.IsChecked == true).Content.ShouldBe("This month", at);
                    Find<CheckBox>(view, c => Equals(c.Content, "Include my household")).ShouldNotBeNull(at).Visibility.ShouldBe(Visibility.Collapsed, at);
                    foreach (var title in new[] { "Bill", "Time", "By part", "Everyday equivalents", "Idle waste", "Data quality", "Energy each day" })
                    {
                        Find<TextBlock>(view, t => t.Text == title).ShouldNotBeNull($"{title} {at}").IsVisible.ShouldBeTrue($"{title} {at}");
                    }
                    Find<TextBlock>(view, t => t.Text == model.Data.Title).ShouldNotBeNull(at);
                    var energy = MidnightHost.AllOf<KeyValue>(view).Single(k => k.Key == "Energy used");
                    energy.Value.ShouldBe(model.Data.Energy, at);
                    new KeyValueAutomation(energy).Name.ShouldBe($"Energy used: {model.Data.Energy}, kWh", at);
                    MidnightHost.AllOf<KeyValue>(view).Count(k => k.IsVisible).ShouldBeGreaterThan(10, at);
                    page.Render($"reports-{size}");

                    // PNG draws the sheet, the element named Sheet, into the file the saver names.
                    view.FindName("Sheet").ShouldBeOfType<Grid>(at);
                    Find<Button>(view, b => Equals(b.Content, "PNG"))!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    new FileInfo(saver.Chosen).Length.ShouldBeGreaterThan(30_000, at);
                    File.Copy(saver.Chosen, Path.Combine(Folder, $"aero-reports-picture-{size}-{theme}.png"), overwrite: true);
                }

                // The service away: the message, and the figures as words, not zeros.
                var failed = new ReportViewModel(new FakeLink(), new FakeRangeHistory { Answer = _ => null }, new FakeHouseholdHistory(), new FakeSleep(), new FakeSaver(),
                    _ => [], UiThreads.Inline, new FakeTimeProvider(MidnightFixtures.Now), TimeZoneInfo.Utc, MidnightFixtures.English, 0.38);
                failed.Show();
                var none = new ReportsView { DataContext = failed };
                using (var page = Page(none, Narrow, theme))
                {
                    Find<TextBlock>(none, t => t.Text == "History can't be read right now. It comes back when the service is running.").ShouldNotBeNull(theme.ToString())
                        .IsVisible.ShouldBeTrue(theme.ToString());
                    MidnightHost.AllOf<KeyValue>(none).Single(k => k.Key == "Energy used").Value.ShouldBe(Format.Missing, theme.ToString());
                    page.Render("reports-failed");
                }
            }
        });
        Sizes(30_000, "reports-960", "reports-1440", "reports-failed");
    }

    [Fact]
    public void Household_shows_its_figures_a_row_per_pc_the_pcs_waiting_manage_and_sign_in()
    {
        OnUi(() =>
        {
            foreach (var theme in Themes)
            {
                foreach (var (width, size) in new[] { (Narrow, "960"), (Wide, "1440") })
                {
                    var at = $"{size} on {theme}";
                    var model = MidnightFixtures.HouseholdScreen(pendingApprovals: 2);
                    model.Show();
                    var view = new Aero.HouseholdView { DataContext = model };
                    using var page = Page(view, width, theme);
                    foreach (var title in new[] { "Household", "Today", "This week", "This month", "Your PCs", "Manage this household", "Sign in" })
                    {
                        Find<TextBlock>(view, t => t.Text == title && t.IsVisible).ShouldNotBeNull($"{title} {at}");
                    }
                    Find<TextBlock>(view, t => t.Text == "46.8").ShouldNotBeNull(at).IsVisible.ShouldBeTrue(at);
                    Find<TextBlock>(view, t => t.Text == HouseholdViewModel.Explanation).ShouldBeNull(at);
                    var waiting = Find<TextBlock>(view, t => AutomationProperties.GetName(t) == "Waiting for approval").ShouldNotBeNull(at);
                    waiting.IsVisible.ShouldBeTrue(at);
                    waiting.Inlines.OfType<System.Windows.Documents.Run>().Any(r => r.Text == "2").ShouldBeTrue(at);

                    var rows = Find<ItemsControl>(view, items => items.ItemsSource == model.Members).ShouldNotBeNull(at);
                    model.Members.Count.ShouldBe(2, at);
                    foreach (var member in model.Members)
                    {
                        var row = rows.ItemContainerGenerator.ContainerFromItem(member).ShouldBeAssignableTo<DependencyObject>(at)!;
                        Find<TextBlock>(row, t => t.Text == member.Name).ShouldNotBeNull($"{member.Name} {at}");
                        Find<TextBlock>(row, t => t.Text == member.Energy).ShouldNotBeNull($"{member.Name} {at}");
                        Find<TextBlock>(row, t => t.Text == "this PC")!.IsVisible.ShouldBe(member.IsThisPc, $"{member.Name} {at}");
                        Find<Button>(row, b => Equals(b.Content, "Remove"))!.IsVisible.ShouldBe(!member.IsThisPc, $"{member.Name} {at}");
                        Find<ShareBar>(row, b => AutomationProperties.GetName(b) == $"{member.Name} share")!.Value.ShouldBe(member.Share, $"{member.Name} {at}");
                        Find<Initials>(row)!.Member.ShouldBe(member.Name, $"{member.Name} {at}");
                    }
                    MidnightHost.AllOf<Button>(view).Count(b => Equals(b.Content, "Add a PC") && b.IsVisible).ShouldBe(1, at);
                    Find<TextBox>(view, b => AutomationProperties.GetName(b) == "This PC's name").ShouldNotBeNull(at).IsVisible.ShouldBeTrue(at);
                    page.Render($"household-{size}");
                }

                // Before a household: the explanation and Add a PC; nobody waits, so no approvals panel.
                var link = new FakeLink();
                link.Connect(true);
                var none = new HouseholdViewModel(link, new FakeHouseholdHistory(), UiThreads.Inline, new FakeTimeProvider(MidnightFixtures.Now), TimeZoneInfo.Utc,
                    MidnightFixtures.English, FakeAccount.Model(link));
                none.Show();
                var explainer = new Aero.HouseholdView { DataContext = none };
                using (var page = Page(explainer, Narrow, theme))
                {
                    Find<TextBlock>(explainer, t => t.Text == HouseholdViewModel.Explanation).ShouldNotBeNull(theme.ToString()).IsVisible.ShouldBeTrue(theme.ToString());
                    Find<TextBlock>(explainer, t => t.Text == "Your PCs")!.IsVisible.ShouldBeFalse(theme.ToString());
                    Find<TextBlock>(explainer, t => AutomationProperties.GetName(t) == "Waiting for approval")!.IsVisible.ShouldBeFalse(theme.ToString());
                    var add = MidnightHost.AllOf<Button>(explainer).Single(b => Equals(b.Content, "Add a PC") && b.IsVisible);
                    add.Style.ShouldBe(explainer.FindResource("A.AccentBtn"), theme.ToString());
                    Find<TextBlock>(explainer, t => t.Text == "Sign in")!.IsVisible.ShouldBeTrue(theme.ToString());
                    page.Render("household-none");
                }

                // Removing a PC asks first, in the Manage panel.
                var asking = MidnightFixtures.HouseholdScreen();
                asking.Show();
                asking.AskRemove.Execute(asking.Members.Single(m => !m.IsThisPc));
                var confirm = new Aero.HouseholdView { DataContext = asking };
                using (var page = Page(confirm, Narrow, theme))
                {
                    Find<TextBlock>(confirm, t => t.Text == asking.ConfirmText && t.IsVisible).ShouldNotBeNull(theme.ToString());
                    Find<Button>(confirm, b => Equals(b.Content, "Confirm") && b.IsVisible).ShouldNotBeNull(theme.ToString());
                    page.Render("household-confirm");
                }
            }
        });
        Sizes(20_000, "household-960", "household-1440", "household-none", "household-confirm");
    }

    /// <summary>What a screen reader hears from a figure.</summary>
    private sealed class KeyValueAutomation(KeyValue figure)
    {
        public string Name { get; } = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(figure).GetName();
    }

    /// <summary>What the Parts page binds on the Dashboard, with parts a test chooses.</summary>
    private sealed class PartsScreen
    {
        public IReadOnlyList<DashboardPart> Parts { get; init; } = [];

        public PartsRange PartsRange { get; set; } = PartsRange.SevenDays;
    }

    /// <summary>The pill is where <paramref name="chosen"/> is, and as wide.</summary>
    private static void Behind(SegTrack track, RadioButton chosen, string at)
    {
        track.Chosen.ShouldBe(chosen, at);
        var host = (FrameworkElement)track.Template.FindName("PART_Host", track);
        track.Indicator.X.ShouldBe(chosen.TranslatePoint(default, host).X, 0.5, at);
        track.Indicator.Width.ShouldBe(chosen.ActualWidth, 0.5, at);
    }

    /// <summary>The same machine's last seven days the Midnight renders show, with a saver for Save CSV.</summary>
    private static BreakdownViewModel HistoryScreen(FakeSaver saver)
    {
        var link = new FakeLink();
        link.Connect(true);
        var history = new FakeRangeHistory { Answer = range => Reports.Typical(range) with { Series = MidnightFixtures.WeekSeries(range) } };
        return new BreakdownViewModel(link, history, UiThreads.Inline, new FakeTimeProvider(MidnightFixtures.Now), TimeZoneInfo.Utc, MidnightFixtures.English, saver);
    }
}
