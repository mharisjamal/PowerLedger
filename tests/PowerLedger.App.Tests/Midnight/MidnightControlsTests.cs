using System.Globalization;
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
        TrendMark.MarkFor(TrendKind.Flat).ShouldBe("●");
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

    /// <summary>The mark's colour is its sense, not its direction: where lower is better a rise is red and a fall green.</summary>
    [Fact]
    [Trait("Category", "UI")]
    public void A_trend_marks_colour_follows_whether_lower_is_better()
        => UiHarness.OnUi(() =>
        {
            var host = new StackPanel();
            host.Resources.MergedDictionaries.Add(MidnightStylesTests.Load());
            host.Resources.MergedDictionaries.Add(ThemeManager.Palette(Look.Midnight, Theme.Dark));
            var marks = new[]
            {
                new TrendMark { Kind = TrendKind.Up, Text = "12%" },
                new TrendMark { Kind = TrendKind.Down, Text = "12%" },
                new TrendMark { Kind = TrendKind.Up, Text = "12%", LowerIsBetter = true },
                new TrendMark { Kind = TrendKind.Down, Text = "12%", LowerIsBetter = true },
                new TrendMark { Kind = TrendKind.Flat, Text = "0%", LowerIsBetter = true },
            };
            foreach (var mark in marks) host.Children.Add(mark);
            host.Measure(new Size(400, 400));
            host.Arrange(new Rect(0, 0, 400, 400));
            Color Ink(string key) => ((SolidColorBrush)host.FindResource(key)).Color;
            Color Of(TrendMark mark) => ((SolidColorBrush)mark.Foreground).Color;
            marks.Select(mark => mark.Sense.ToString()).ShouldBe(["Good", "Bad", "Bad", "Good", "Neutral"]);
            Of(marks[0]).ShouldBe(Ink("M.Good"));
            Of(marks[1]).ShouldBe(Ink("M.Bad"));
            Of(marks[2]).ShouldBe(Ink("M.Bad"), "more energy is the bad news");
            Of(marks[3]).ShouldBe(Ink("M.Good"), "less energy is the good news");
            Of(marks[4]).ShouldBe(Ink("M.Ink3"));
            marks[2].Describe().ShouldBe("Up 12%", "the arrow and its words still say which way the figure went");
            // The template draws the mark MarkFor gives, a dot for no change rather than a dash.
            marks.ShouldAllBe(mark => ((TextBlock)mark.Template.FindName("Mark", mark)).Text == TrendMark.MarkFor(mark.Kind));
        });

    /// <summary>
    /// No one accent passes 4.5:1 both under white text and as small text on the dark panel, so a drawn control's small
    /// accent-coloured words (the chart's "now") take M.AccentText under Midnight's styles, and Classic's controls, which
    /// set no such brush, keep their accent.
    /// </summary>
    [Fact]
    [Trait("Category", "UI")]
    public void Small_accent_words_in_a_drawn_control_take_the_accent_text_tint()
        => UiHarness.OnUi(() =>
        {
            var host = new StackPanel();
            host.Resources.MergedDictionaries.Add(MidnightStylesTests.Load());
            host.Resources.MergedDictionaries.Add(ThemeManager.Palette(Look.Midnight, Theme.Dark));
            var chart = new AreaChart();
            var history = new StackedChart { Style = (Style)host.FindResource("M.StackedChart") };
            host.Children.Add(chart);
            host.Children.Add(history);
            host.Measure(new Size(600, 800));
            Color Ink(string key) => ((SolidColorBrush)host.FindResource(key)).Color;
            ((SolidColorBrush)chart.AccentTextOrAccent).Color.ShouldBe(Ink("M.AccentText"));
            ((SolidColorBrush)history.AccentTextOrAccent).Color.ShouldBe(Ink("M.AccentText"));
            ((SolidColorBrush)chart.AccentBrush).Color.ShouldBe(Ink("M.Accent"), "the line and the dashes keep the accent");

            var classic = new StackedChart { AccentBrush = Brushes.Orange };
            classic.AccentTextOrAccent.ShouldBeSameAs(Brushes.Orange, "without a text tint, the accent itself");
        });

    /// <summary>0.8.1, after the reference: a section is flat, framed by a hairline with a small corner; nothing lifts or
    /// shades it, and nothing about it moves under the pointer.</summary>
    [Fact]
    [Trait("Category", "UI")]
    public void A_section_is_flat_and_framed_by_a_hairline()
        => UiHarness.OnUi(() =>
        {
            var styles = MidnightStylesTests.Load();
            var host = new Grid { Width = 360, Height = 200 };
            host.Resources.MergedDictionaries.Add(styles);
            host.Resources.MergedDictionaries.Add(ThemeManager.Palette(Look.Midnight, Theme.Dark));
            var section = new Border { Style = (Style)styles["M.Card"], Width = 200, Height = 100 };
            host.Children.Add(section);
            host.Measure(new Size(360, 200));
            host.Arrange(new Rect(0, 0, 360, 200));

            section.Effect.ShouldBeNull();
            ((Style)styles["M.Card"]).Triggers.ShouldBeEmpty("no hover lift");
            section.CornerRadius.ShouldBe(new CornerRadius(10));
            section.BorderThickness.ShouldBe(new Thickness(1));
            ((SolidColorBrush)section.BorderBrush).Color.ShouldBe(((SolidColorBrush)host.FindResource("M.Line")).Color, "a hairline, not the strong line");
        });

    /// <summary>A KPI's bar: the stripes run to the value, and the marker stands at it, kept whole inside the bar at either end.</summary>
    [Fact]
    public void The_bars_marker_stands_at_the_value_inside_the_bar()
    {
        HatchBar.FillWidth(0.43, 200).ShouldBe(86, 1e-9);
        HatchBar.FillWidth(1.7, 200).ShouldBe(200);
        HatchBar.FillWidth(double.NaN, 200).ShouldBe(0);
        HatchBar.MarkerAt(86, 200).ShouldBe(new Rect(85, 0, HatchBar.MarkerWidth, HatchBar.BarHeight + 2 * HatchBar.MarkerReach));
        HatchBar.MarkerAt(0, 200).X.ShouldBe(0, "at nothing, flush with the start");
        HatchBar.MarkerAt(200, 200).X.ShouldBe(200 - HatchBar.MarkerWidth, "at the whole, flush with the end");
    }

    /// <summary>The Report's daily bars in Midnight run from the brighter indigo at the top to the deeper at the foot;
    /// Classic, which gives no ends, keeps its accent.</summary>
    [Fact]
    public void Daily_bars_run_from_a_bright_top_to_a_deep_foot_where_the_look_gives_both()
        => Sta.Run(() =>
        {
            var (top, bottom, accent) = (new SolidColorBrush(Color.FromRgb(0x81, 0x8C, 0xF8)), new SolidColorBrush(Color.FromRgb(0x4F, 0x46, 0xE5)), Brushes.Orange);
            var bar = DailyBars.BarBrush(top, bottom, accent).ShouldBeOfType<LinearGradientBrush>();
            bar.GradientStops.Select(stop => stop.Color).ShouldBe([top.Color, bottom.Color]);
            (bar.StartPoint, bar.EndPoint).ShouldBe((new Point(0, 0), new Point(0, 1)), "down each bar");
            DailyBars.BarBrush(null, bottom, accent).ShouldBeSameAs(accent);
            DailyBars.BarBrush(null, null, accent).ShouldBeSameAs(accent, "Classic's bars stay the accent");
            return true;
        });

    /// <summary>The chart's fill stays rich under the line, three quarters as strong past half way, and is gone at the axis.</summary>
    [Fact]
    public void The_charts_fill_holds_under_the_line_and_fades_out_at_the_axis()
    {
        var top = Color.FromArgb(0x99, 0x3F, 0x3F, 0xC8);
        var stops = AreaChart.FillStops(top, Color.FromArgb(0, 0x3F, 0x3F, 0xC8));
        stops.Select(stop => stop.Offset).ShouldBe([0, 0.55, 1]);
        stops[0].Color.ShouldBe(top);
        stops[1].Color.A.ShouldBe((byte)Math.Round(0x99 * 0.75));
        stops[2].Color.A.ShouldBe((byte)0, "transparent at the axis");
    }

    /// <summary>The sidebar's current row runs from the accent at its left into the violet and out to nothing at its right.</summary>
    [Fact]
    public void The_current_rows_wash_runs_from_the_accent_into_the_violet_and_out()
    {
        var accent = Color.FromRgb(0x3D, 0x5C, 0xFF);
        var violet = Color.FromRgb(0x8B, 0x5C, 0xF6);
        var stops = NavGlow.Stops(accent, violet);
        stops.Select(stop => stop.Offset).ShouldBe([0, 0.6, 1]);
        stops[0].Color.ShouldBe(Color.FromArgb((byte)Math.Round(255 * NavGlow.Strength), 0x3D, 0x5C, 0xFF));
        stops[1].Color.ShouldBe(Color.FromArgb((byte)Math.Round(255 * NavGlow.SecondStrength), 0x8B, 0x5C, 0xF6));
        stops[2].Color.A.ShouldBe((byte)0);
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
                    chart.TipText.ShouldMatch(@"^12:00 Power: \d+ W$");
                    chart.Describe().ShouldMatch(@"At 12:00, \d+ W\.$");
                    var bubble = UiHarness.Find<Border>(chart.Tip, border => border.Name == "Bubble")!;
                    ((SolidColorBrush)bubble.Background).Color.ShouldBe(((SolidColorBrush)host.FindResource("M.Tip")).Color, "a solid bubble, no glass");
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
                    chart.Model = MidnightFixtures.DayChart();
                    chart.Hovered.ShouldBe(174, "the same range read again, a minute on, keeps the crosshair where it was");
                    chart.Tip.IsOpen.ShouldBeTrue();
                    chart.Model = ChartModel.Empty;
                    chart.Hovered.ShouldBe(-1, "another range's model takes the crosshair away");

                    var german = CultureInfo.GetCultureInfo("de-DE");
                    var week = Ranges.LastDays(7, MidnightFixtures.Now, TimeZoneInfo.Utc, MidnightFixtures.English);
                    chart.Culture = german;
                    chart.From = week.From;
                    chart.Model = Charts.Build(week, MidnightFixtures.WeekSeries(week), ChartUnit.Watts, TimeZoneInfo.Utc, MidnightFixtures.English);
                    chart.Hover(30);
                    chart.TipText.ShouldStartWith((week.From + TimeSpan.FromHours(30)).ToString("ddd d MMM", german), Case.Sensitive,
                        "the tooltip speaks the chart's culture, the page's, not the thread's");
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
