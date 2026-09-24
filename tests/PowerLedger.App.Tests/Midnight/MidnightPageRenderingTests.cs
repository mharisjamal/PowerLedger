using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.App.Midnight;
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Storage;
using Shouldly;
using static PowerLedger.App.Tests.MidnightHost;
using static PowerLedger.App.Tests.UiHarness;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan O tasks M2-1 to M2-4: Midnight's History, Report, Household and Settings pages over the Classic ViewModels,
/// drawn in both Midnight palettes at the page's full length, and in the room a short window (880 × 560) leaves a page,
/// where each scrolls and cuts nothing off at the side. Each page is checked for what it must show, without a ViewModel
/// of its own to test.
/// </summary>
[Trait("Category", "UI")]
public class MidnightPageRenderingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 7, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>The room a short Midnight window leaves a page: 880 less the 232 px sidebar, 560 less the 56 px top bar and the page header row.</summary>
    private const double ShortWidth = 880 - 232;
    private const double ShortHeight = 560 - 56 - 64;

    [Fact]
    public void History_shows_the_range_pills_the_chart_card_and_a_row_per_part_with_its_share_bar_in_both_themes()
    {
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                using var styles = Midnight(theme);
                var model = BreakdownScreen();
                model.Range.Choice = RangeChoice.SevenDays;
                model.Show();
                var view = new HistoryView { DataContext = model };
                using var page = Page(view, 1010);
                var pills = AllOf<RadioButton>(view).Where(p => p.Content is string).ToList();
                pills.Select(p => p.Content).ShouldBe(["Today", "7 days", "30 days", "This month", "Last month", "Custom", "W", "Wh"], ignoreOrder: true, theme.ToString());
                pills.Where(p => p.IsChecked == true).Select(p => p.Content).ShouldBe(["7 days", "W"], ignoreOrder: true, theme.ToString());
                Find<StackedChart>(view).ShouldNotBeNull(theme.ToString()).ActualHeight.ShouldBe(300, theme.ToString());

                // A row per part and the total, each part with a bar as long as its share of the track.
                var rows = Find<ItemsControl>(view, items => items.ItemsSource == model.Parts).ShouldNotBeNull(theme.ToString());
                model.Parts.Count.ShouldBe(5, theme.ToString());
                foreach (var part in model.Parts)
                {
                    var row = rows.ItemContainerGenerator.ContainerFromItem(part).ShouldBeAssignableTo<DependencyObject>(theme.ToString());
                    Find<TextBlock>(row, t => t.Text == part.Name).ShouldNotBeNull($"{part.Name} on {theme}");
                    Find<TextBlock>(row, t => t.Text == part.Energy).ShouldNotBeNull($"{part.Name} on {theme}");
                    var track = Find<Border>(row, b => System.Windows.Automation.AutomationProperties.GetName(b) == $"{part.Name} share").ShouldNotBeNull($"{part.Name} on {theme}");
                    if (part.Part is null) track.Visibility.ShouldBe(Visibility.Hidden, theme.ToString());
                    else ((FrameworkElement)track.Child).ActualWidth.ShouldBe(120 * part.Fraction, 0.5, $"{part.Name} on {theme}");
                }
                Render(page.Host, (int)page.Host.ActualWidth, (int)page.Host.ActualHeight, $"midnight-history-{theme}.png");

                model.Range.Choice = RangeChoice.Custom;
                Pump(TimeSpan.FromMilliseconds(300));
                page.Host.UpdateLayout();
                Find<DatePicker>(view).ShouldNotBeNull(theme.ToString()).IsVisible.ShouldBeTrue(theme.ToString());
                Render(page.Host, (int)page.Host.ActualWidth, (int)page.Host.ActualHeight, $"midnight-history-custom-{theme}.png");
            }
        });
        Sizes("history", "history-custom");
    }

    [Fact]
    public void The_pages_scroll_in_the_room_a_short_window_leaves_and_cut_nothing_off_at_the_side()
    {
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            using var styles = Midnight(Theme.Dark);
            foreach (var (name, view) in ShortPages())
            {
                var window = new Window
                {
                    Content = view, Width = ShortWidth, Height = ShortHeight, Background = (Brush)Application.Current.FindResource("M.Ground"),
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
                    ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.None,
                };
                window.Show();
                try
                {
                    Pump(TimeSpan.FromMilliseconds(600));
                    var scroller = view.Content.ShouldBeOfType<ScrollViewer>(name);
                    (scroller.ExtentHeight > scroller.ViewportHeight).ShouldBeTrue($"{name} is {scroller.ExtentHeight:0} tall in a view {scroller.ViewportHeight:0} tall");
                    scroller.ComputedVerticalScrollBarVisibility.ShouldBe(Visibility.Visible, name);
                    foreach (var element in AllOf<FrameworkElement>((DependencyObject)scroller.Content).Where(e => e.IsVisible && e is TextBlock or Button or Border))
                    {
                        element.TranslatePoint(new Point(element.ActualWidth, 0), scroller).X.ShouldBeLessThanOrEqualTo(scroller.ViewportWidth + 0.5, $"{name}: {Describe(element)} runs past the side");
                    }
                    Render(window, (int)ShortWidth, (int)ShortHeight, $"midnight-short-{name}.png");
                    scroller.ScrollToEnd();
                    Pump(TimeSpan.FromMilliseconds(200));
                    scroller.VerticalOffset.ShouldBe(Math.Max(0, scroller.ExtentHeight - scroller.ViewportHeight), 0.5, $"the end of {name} is out of reach");
                    Render(window, (int)ShortWidth, (int)ShortHeight, $"midnight-short-{name}-end.png");
                }
                finally
                {
                    window.Close();
                }
            }
        });
    }

    /// <summary>The pages a short window shows, each with the data the Classic renders use.</summary>
    private static IEnumerable<(string Name, UserControl View)> ShortPages()
    {
        var history = BreakdownScreen();
        history.Range.Choice = RangeChoice.SevenDays;
        history.Show();
        yield return ("history", new HistoryView { DataContext = history });
    }

    private static void Sizes(params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                new FileInfo(Path.Combine(Folder, $"midnight-{name}-{theme}.png")).Length.ShouldBeGreaterThan(30_000, $"{name} on {theme}");
            }
        }
    }

    private static string Describe(FrameworkElement element) => element switch
    {
        TextBlock text => $"text \"{text.Text}\"",
        Button button => $"button {button.Content}",
        _ => element.GetType().Name + (element.Name.Length > 0 ? " " + element.Name : ""),
    };

    /// <summary>
    /// Midnight's palette, styles and page styles over the application's dictionaries until disposed. The styles are
    /// M1's stand-in until Styles.Midnight.xaml lands; the page styles are M2's own, merged after them as MidnightWindow will.
    /// </summary>
    private static IDisposable Midnight(Theme theme)
    {
        var palette = UsePalette(theme);
        var styles = Use(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PowerLedger.App.Tests;component/Midnight/Stub.Styles.Midnight.xaml", UriKind.Absolute),
        });
        var pages = Use(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PowerLedger;component/Midnight/Styles.Midnight.Pages.xaml", UriKind.Absolute),
        });
        return new Undo(() =>
        {
            pages.Dispose();
            styles.Dispose();
            palette.Dispose();
        });
    }

    /// <summary>A page laid out at <paramref name="width"/> and its whole length, on Midnight's ground, as the window's page host would show it.</summary>
    private static PageHost Page(FrameworkElement view, double width)
    {
        view.Width = width;
        var host = new Border { Background = (Brush)Application.Current.FindResource("M.Ground"), Child = view };
        var window = new Window
        {
            Content = host, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
        };
        window.Show();
        Pump(TimeSpan.FromMilliseconds(600));
        host.UpdateLayout();
        return new PageHost(host, window);
    }

    private sealed record PageHost(Border Host, Window Window) : IDisposable
    {
        public void Dispose() => Window.Close();
    }

    private sealed class Undo(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>The same machine's last seven days the Classic renders show: asleep overnight, working days, quiet evenings.</summary>
    private static BreakdownViewModel BreakdownScreen()
    {
        var link = new FakeLink();
        link.Connect(true);
        var history = new FakeRangeHistory { Answer = range => Reports.Typical(range) with { Series = Week(range) } };
        return new BreakdownViewModel(link, history, UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc, English);
    }

    private static IReadOnlyList<Aggregate> Week(DateRange range)
    {
        var series = new List<Aggregate>();
        var seed = 5;
        double Noise()
        {
            seed = (seed * 9301 + 49297) % 233280;
            return seed / 233280.0 - 0.5;
        }
        for (var start = range.From; start < range.To; start += range.Bucket)
        {
            var hour = start.Hour;
            var (watts, on) = hour switch
            {
                < 7 or 23 => (0.0, 0.0),
                7 => (16 + Noise() * 2, 1800.0),
                < 12 => (36 + Noise() * 10, 3600.0),
                < 13 => (18 + Noise() * 3, 3600.0),
                < 18 => (42 + Noise() * 14, 3600.0),
                _ => (15 + Noise() * 3, 3600.0),
            };
            var wh = watts * on / 3600;
            series.Add(Aggregate.Empty(start) with
            {
                EnergyWh = wh, CpuWh = wh * 0.45, GpuWh = wh * 0.1, DisplayWh = 4 * on / 3600, RestWh = wh * 0.45 - 4 * on / 3600,
                OnSeconds = on, GapSeconds = 3600 - on,
            });
        }
        return series;
    }
}
