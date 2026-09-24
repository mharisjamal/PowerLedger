using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Plan O M1-2: the Dashboard's drawn controls, their words for a screen reader, and the pure rules behind them.</summary>
public class MidnightControlsTests
{
    [Theory]
    [InlineData("Haris Jamal", "HJ")]
    [InlineData("study pc", "SP")]
    [InlineData("Anna Maria Lopez", "AL")]
    [InlineData("desktop", "DE")]
    [InlineData("x", "X")]
    [InlineData("", "?")]
    [InlineData(null, "?")]
    public void Initials_are_the_first_letters_of_the_first_and_last_words(string? name, string expected) => Initials.Letters(name).ShouldBe(expected);

    [Fact]
    public void A_name_keeps_its_place_in_the_ring_and_names_spread_over_it()
    {
        Initials.Slot("Haris Jamal").ShouldBe(Initials.Slot("Haris Jamal"));
        Initials.Slot(null).ShouldBe(Initials.Slot(""));
        var names = new[] { "Study PC", "Laptop", "Kitchen", "Anna's PC", "Server", "Old Dell", "Work", "Gaming rig" };
        names.Select(Initials.Slot).ShouldAllBe(slot => slot >= 0 && slot < Initials.RingSize);
        names.Select(Initials.Slot).Distinct().Count().ShouldBeGreaterThan(2);
    }

    [Fact]
    public void Letters_go_light_on_a_deep_disc_and_dark_on_a_pale_one()
    {
        Initials.WantsLightLetters(Color.FromRgb(0x3A, 0x5B, 0xFF)).ShouldBeTrue();    // the light theme's accent
        Initials.WantsLightLetters(Color.FromRgb(0x1E, 0x9E, 0x6C)).ShouldBeTrue();    // its green
        Initials.WantsLightLetters(Color.FromRgb(0xF5, 0xB9, 0x42)).ShouldBeFalse();   // the dark theme's amber
        Initials.WantsLightLetters(Color.FromRgb(0x38, 0xBD, 0xF8)).ShouldBeFalse();   // its cyan
    }

    [Fact]
    public void The_status_pill_reads_the_service_state_the_way_the_classic_status_bar_does()
    {
        StatusPill.For(false, Quality.Measured).ShouldBe((StatusKind.Bad, "Service not running"));
        StatusPill.For(true, Quality.Estimated).ShouldBe((StatusKind.Warn, "Estimating"));
        StatusPill.For(true, Quality.Calibrated).ShouldBe((StatusKind.Good, "Recording"));
        StatusPill.For(true, Quality.Measured).ShouldBe((StatusKind.Good, "Recording"));
        StatusPill.For(true, null).ShouldBe((StatusKind.Good, "Recording"));
    }

    [Fact]
    public void A_trend_mark_says_its_direction()
    {
        TrendMark.MarkFor(TrendKind.Up).ShouldBe("▲");
        TrendMark.MarkFor(TrendKind.Down).ShouldBe("▼");
        TrendMark.MarkFor(TrendKind.Flat).ShouldBe("—");
        TrendMark.MarkFor(TrendKind.Text).ShouldBe("");
        TrendMark.MarkFor(TrendKind.Quality).ShouldBe("");
        Sta.Run(() =>
        {
            new TrendMark { Kind = TrendKind.Up, Text = "+12% vs avg" }.Describe().ShouldBe("Up +12% vs avg");
            new TrendMark { Kind = TrendKind.Down, Text = "8%" }.Describe().ShouldBe("Down 8%");
            new TrendMark { Kind = TrendKind.Flat }.Describe().ShouldBe("Unchanged");
            new TrendMark { Kind = TrendKind.Text, Text = "first month" }.Describe().ShouldBe("first month");
            new TrendMark { Kind = TrendKind.Quality, Text = "x" }.Describe().ShouldBe("");
            return true;
        });
    }

    [Fact]
    public void The_bars_and_the_disc_describe_themselves()
        => Sta.Run(() =>
        {
            new HatchBar { Value = 0.43 }.Describe().ShouldBe("43% of the way.");
            new ShareBar { Value = 0.615 }.Describe().ShouldBe("62%.");
            new Initials { Member = "Study PC" }.Describe().ShouldBe("Study PC");
            new Initials().Describe().ShouldBe("Unnamed PC");
            new StatusPill { Running = true, Quality = Quality.Estimated }.Content.ShouldBe("Estimating");
            return true;
        });

    [Fact]
    [Trait("Category", "UI")]
    public void The_controls_draw_in_both_themes_and_the_chart_answers_the_pointer_and_the_keys()
    {
        Directory.CreateDirectory(UiHarness.Folder);
        foreach (var theme in new[] { Theme.Dark, Theme.Light })
        {
            UiHarness.OnUi(() =>
            {
                var host = new Border { Width = 760, Padding = new Thickness(24) };
                host.Resources.MergedDictionaries.Add(MidnightStylesTests.Load());
                host.Resources.MergedDictionaries.Add(ThemeManager.Palette(Look.Midnight, theme));   // in the host, not the application: no other test can swap it
                host.SetResourceReference(Border.BackgroundProperty, "M.Ground");
                var chart = new AreaChart { Model = MidnightFixtures.DayChart(), From = MidnightFixtures.Today().From, Zone = TimeZoneInfo.Utc };
                var column = new StackPanel();
                column.Children.Add(new HatchBar { Value = 0.43, Margin = new Thickness(0, 0, 0, 12) });
                column.Children.Add(new ShareBar { Value = 0.61, Margin = new Thickness(0, 0, 0, 12) });
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
                foreach (var name in new[] { "Haris Jamal", "Study PC", "Kitchen", "Laptop", "Old Dell", "Work", "" })
                    row.Children.Add(new Initials { Member = name, Margin = new Thickness(0, 0, 8, 0) });
                row.Children.Add(new StatusPill { Running = true, Quality = Quality.Measured, Margin = new Thickness(8, 0, 0, 0) });
                row.Children.Add(new StatusPill { Running = true, Quality = Quality.Estimated, Margin = new Thickness(8, 0, 0, 0) });
                row.Children.Add(new StatusPill { Running = false, Margin = new Thickness(8, 0, 0, 0) });
                column.Children.Add(row);
                var trends = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
                trends.Children.Add(new TrendMark { Kind = TrendKind.Up, Text = "12% vs avg day", Margin = new Thickness(0, 0, 16, 0) });
                trends.Children.Add(new TrendMark { Kind = TrendKind.Down, Text = "8% vs last month", Margin = new Thickness(0, 0, 16, 0) });
                trends.Children.Add(new TrendMark { Kind = TrendKind.Flat, Text = "same as avg", Margin = new Thickness(0, 0, 16, 0) });
                trends.Children.Add(new TrendMark { Kind = TrendKind.Text, Text = "first month" });
                column.Children.Add(trends);
                column.Children.Add(chart);
                column.Children.Add(new AreaChart { Margin = new Thickness(0, 12, 0, 0) });
                host.Child = column;
                var window = new Window
                {
                    Content = host, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
                };
                window.Show();
                try
                {
                    UiHarness.Pump(TimeSpan.FromMilliseconds(300));
                    chart.Describe().ShouldStartWith("Today: power by component in watts");
                    chart.Hovered.ShouldBe(-1);
                    chart.Tip.ShouldBeNull();

                    chart.Hover(144);
                    UiHarness.Pump(TimeSpan.FromMilliseconds(250));
                    chart.Hovered.ShouldBe(144);
                    chart.Tip.ShouldNotBeNull();
                    chart.Tip.IsOpen.ShouldBeTrue();
                    chart.Tip.Content.ShouldBeOfType<string>().ShouldMatch(@"^12:00 PM · \d+ W$");
                    chart.Describe().ShouldMatch(@"At 12:00 PM · \d+ W\.$");
                    UiHarness.Find<Border>(chart.Tip, border => border.Style == host.Resources["M.Glass"]).ShouldNotBeNull("the tooltip wears the glass");
                    UiHarness.Render(host, (int)host.ActualWidth, (int)host.ActualHeight, $"midnight-controls-{theme}.png");
                    UiHarness.Render(chart.Tip, (int)Math.Ceiling(chart.Tip.ActualWidth), (int)Math.Ceiling(chart.Tip.ActualHeight), $"midnight-tooltip-{theme}.png");

                    chart.Focus();
                    chart.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(chart)!, 0, System.Windows.Input.Key.Right) { RoutedEvent = UIElement.KeyDownEvent });
                    chart.Hovered.ShouldBe(145);
                    chart.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(chart)!, 0, System.Windows.Input.Key.Escape) { RoutedEvent = UIElement.KeyDownEvent });
                    chart.Hovered.ShouldBe(-1);
                    chart.Tip.IsOpen.ShouldBeFalse();

                    chart.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(chart)!, 0, System.Windows.Input.Key.Left) { RoutedEvent = UIElement.KeyDownEvent });
                    chart.Hovered.ShouldBe(174, "from nowhere, the keys start at now");
                    chart.Model = ChartModel.Empty;
                    chart.Hovered.ShouldBe(-1, "a new model takes the crosshair away");
                }
                finally
                {
                    window.Close();
                }
            });
            new FileInfo(Path.Combine(UiHarness.Folder, $"midnight-controls-{theme}.png")).Length.ShouldBeGreaterThan(20_000);
        }
    }
}
