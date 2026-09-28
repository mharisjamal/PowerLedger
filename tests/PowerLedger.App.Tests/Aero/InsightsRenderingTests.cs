using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.App.Aero;
using Shouldly;
using static PowerLedger.App.Tests.UiHarness;

namespace PowerLedger.App.Tests;

/// <summary>
/// Aero look design §4 and §7: the Insights page drawn in both Aero palettes, with a week and more of data and before
/// it, at the room a 1440 px and a 960 px window leave a page, over a backdrop for the glass to show. PNGs go to
/// %TEMP%\powerledger-renders\aero-insights-*.png; each is checked for what it must show and for text cut off.
/// </summary>
[Trait("Category", "UI")]
public class InsightsRenderingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 18, 30, 0, TimeSpan.Zero);
    private static readonly CultureInfo British = CultureInfo.GetCultureInfo("en-GB");

    /// <summary>A wide and a narrow window's page: less the sidebar and the page's margins.</summary>
    public static TheoryData<string, int> Sizes => new() { { "Dark", 1100 }, { "Light", 1100 }, { "Dark", 640 }, { "Light", 640 } };

    private static InsightsReport Ready()
    {
        var heatmap = new double[7, 24];
        for (var day = 0; day < 7; day++)
        {
            for (var hour = 0; hour < 24; hour++) heatmap[day, hour] = hour >= 22 ? 90 + 10 * day : hour is >= 12 and < 14 ? 25 : hour < 6 ? 4 : 0;
        }
        return new InsightsReport(
            new BillForecast(18.00m, 16.40m, 19.75m, "GBP", 56, true),
            [
                new UsageAnomaly(new DateTimeOffset(2026, 9, 15, 14, 0, 0, TimeSpan.Zero), 0.42, 0.12, 3.5),
                new UsageAnomaly(new DateTimeOffset(2026, 9, 13, 3, 0, 0, TimeSpan.Zero), 0.2, 0, double.PositiveInfinity),
            ],
            new IdleHabits(heatmap, 22, 2, 1.12m, 5.58, "GBP"),
            new CarbonEstimate(7.08, 94.4, 236, "The United Kingdom's grid in 2023, from Ember"));
    }

    private static InsightsReport NotReady() => new(
        BillForecast.NotReady(3, "GBP"), [], null, new CarbonEstimate(0.62, 0.62, 400, "The default in Settings, a world average"));

    [Theory]
    [MemberData(nameof(Sizes))]
    public void With_data_the_page_shows_the_forecast_band_the_unusual_hours_the_heatmap_and_carbon(string look, int width)
    {
        var theme = Enum.Parse<Theme>(look);
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            var model = Model(Ready());
            var (window, view) = Page(model, theme, width);
            try
            {
                Text(view, "ForecastCost").ShouldBe("£18.00");
                Named<FrameworkElement>(view, "ForecastWaiting").Visibility.ShouldBe(Visibility.Collapsed);
                var band = Named<ForecastBand>(view, "Band");
                (band.IsVisible, band.Low, band.Value, band.High).ShouldBe((true, 16.40, 18.00, 19.75));
                Named<ItemsControl>(view, "UnusualRows").Items.Count.ShouldBe(2);
                AllOf<NormalChart>(view).Count().ShouldBe(2);
                var heat = Named<Heatmap>(view, "Heat");
                (heat.IsVisible, heat.WorstStart, heat.WorstHours).ShouldBe((true, 22, 2));
                heat.ActualHeight.ShouldBe(7 * Heatmap.CellHeight + 6 * Heatmap.Gap + Heatmap.AxisHeight);
                Text(view, "CarbonMonth").ShouldBe("7.08 kg");
                Text(view, "HabitsWorst").ShouldBe("Idle time costs most from 22:00 to 00:00");
                NothingCut(view, $"{theme} {width}");
                window.Render($"aero-insights-{theme}-{width}.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Before_a_week_of_data_each_card_says_what_it_needs(string look, int width)
    {
        var theme = Enum.Parse<Theme>(look);
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            var model = Model(NotReady());
            var (window, view) = Page(model, theme, width);
            try
            {
                Text(view, "ForecastWaiting").ShouldBe("Needs a week of data");
                Named<FrameworkElement>(view, "ForecastCost").Visibility.ShouldBe(Visibility.Collapsed);
                Named<ForecastBand>(view, "Band").IsVisible.ShouldBeFalse();
                Text(view, "UnusualNote").ShouldBe("Nothing unusual this week");
                Named<ItemsControl>(view, "UnusualRows").Items.Count.ShouldBe(0);
                Text(view, "HabitsWorst").ShouldBe("Needs a week of data");
                Named<Heatmap>(view, "Heat").IsVisible.ShouldBeFalse();
                Text(view, "CarbonMonth").ShouldBe("0.62 kg");
                NothingCut(view, $"{theme} {width}");
                window.Render($"aero-insights-waiting-{theme}-{width}.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>The heatmap tells a screen reader what it shows, and lights more idle more brightly.</summary>
    [Fact]
    public void The_heatmap_describes_itself_and_shades_by_the_square_root()
        => OnUi(() =>
        {
            var heat = new Heatmap { Values = new double[7, 24], WorstStart = 22, WorstHours = 2 };
            heat.Values![1, 22] = 100;
            UIElementAutomationPeer.CreatePeerForElement(heat).GetName().ShouldBe("Idle energy by weekday and hour, most from 22:00 to 00:00");
            Heatmap.Shade(0, 100).ShouldBe(0);
            Heatmap.Shade(100, 100).ShouldBe(1);
            Heatmap.Shade(25, 100).ShouldBe(0.14 + 0.86 * 0.5, 1e-9);
            Heatmap.Rows(CultureInfo.GetCultureInfo("en-GB"))[0].ShouldBe(DayOfWeek.Monday);
            Heatmap.Rows(CultureInfo.GetCultureInfo("en-US"))[0].ShouldBe(DayOfWeek.Sunday);
        });

    private static InsightsViewModel Model(InsightsReport report)
    {
        var model = new InsightsViewModel(new FakeInsights { Answer = report }, UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc, British);
        model.Refresh();
        return model;
    }

    /// <summary>The page at <paramref name="width"/> on a window dressed in Aero's palette and styles, over a backdrop.</summary>
    private static (PageWindow Window, InsightsView View) Page(InsightsViewModel model, Theme theme, int width)
    {
        var view = new InsightsView { DataContext = model, Width = width };
        var backdrop = new Border
        {
            Padding = new Thickness(24),
            Child = view,
            Background = theme == Theme.Dark
                ? new LinearGradientBrush(Color.FromRgb(0x23, 0x2A, 0x4A), Color.FromRgb(0x4A, 0x2E, 0x3A), 45)
                : new LinearGradientBrush(Color.FromRgb(0xC9, 0xD6, 0xF0), Color.FromRgb(0xF0, 0xD9, 0xC4), 45),
        };
        var window = AeroHost.Dressed(new Window
        {
            Content = backdrop, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
        }, theme);
        window.Show();
        Pump(TimeSpan.FromMilliseconds(300));
        backdrop.UpdateLayout();
        return (new PageWindow(window, backdrop), view);
    }

    private static T Named<T>(FrameworkElement root, string name)
        where T : class => root.FindName(name).ShouldBeAssignableTo<T>(name)!;

    private static string Text(FrameworkElement root, string name) => Named<TextBlock>(root, name).Text;

    private static IEnumerable<T> AllOf<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in AllOf<T>(child)) yield return deeper;
        }
    }

    /// <summary>No shown text is narrower than it needs to be unless it wraps or ends in an ellipsis by design.</summary>
    private static void NothingCut(FrameworkElement view, string where)
    {
        foreach (var text in AllOf<TextBlock>(view).Where(t => t.IsVisible && t.TextWrapping == TextWrapping.NoWrap && t.TextTrimming == TextTrimming.None))
        {
            var natural = new TextBlock { Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize, FontWeight = text.FontWeight };
            System.Windows.Documents.Typography.SetNumeralAlignment(natural, System.Windows.Documents.Typography.GetNumeralAlignment(text));   // Geist's tabular figures are narrower than its proportional ones
            natural.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            text.ActualWidth.ShouldBeGreaterThanOrEqualTo(natural.DesiredSize.Width - 0.5, $"\"{text.Text}\" is cut off at {where}");
        }
    }

    private sealed record PageWindow(Window Window, FrameworkElement Content)
    {
        public void Render(string name)
        {
            Content.UpdateLayout();
            UiHarness.Render(Content, (int)Math.Ceiling(Content.ActualWidth), (int)Math.Ceiling(Content.ActualHeight), name);
        }

        public void Close() => Window.Close();
    }
}
