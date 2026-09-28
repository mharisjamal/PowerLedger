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
