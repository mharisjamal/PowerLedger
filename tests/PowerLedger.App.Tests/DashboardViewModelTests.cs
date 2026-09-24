using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>The Dashboard over a live reading and a history that answers by range title, everything on the caller's thread.</summary>
public sealed class DashboardViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 7, TimeSpan.Zero);   // 60.6 % of the day gone
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeLink _link = new();
    private readonly FakeHistory _summary = new();
    private readonly FakeRangeHistory _history = new();
    private readonly NowViewModel _now;
    private DashboardViewModel? _dashboard;

    public DashboardViewModelTests()
    {
        _now = new NowViewModel(_link, _summary, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, 0.38, () => { });
        _summary.Snapshot = Snapshots.Typical(Now);   // today 0.284 kWh for $0.05; the month 2.74 kWh, 0.21 kWh of it idle
        _history.Answer = range => range.Title switch
        {
            "Last 31 days" => Reports.Typical(range, 31 * 0.3),   // 0.3 kWh a day, today among them
            "August 2026" => Reports.Typical(range, 2.74),        // idle: 14 % of it, 0.3836 kWh
            "Before" => Reports.Typical(range, 0.2),
            _ => Reports.Typical(range),
        };
    }

    public void Dispose()
    {
        _dashboard?.Dispose();
        _now.Dispose();
    }

    /// <summary>The service is up and has pushed a battery-measured 34.2 W reading: CPU 14.6, GPU 4.1, display 4.0.</summary>
    private DashboardViewModel Dashboard()
    {
        _link.Connect(true);
        _link.Push(Frames.At(Now));
        return _dashboard = new DashboardViewModel(_now, _history, _summary, _clock, TimeZoneInfo.Utc, English, UiThreads.Inline);
    }

    [Fact]
    public void The_cards_come_from_the_live_reading_and_the_history()
    {
        var dashboard = Dashboard();
        dashboard.Show();

        var (power, today, idle) = (dashboard.Kpis[0], dashboard.Kpis[1], dashboard.Kpis[2]);
        power.Label.ShouldBe("Power now");
        power.Big.ShouldBe("34.2 W");
        power.Small.ShouldBe("Live · battery discharge · 14:32:07");
        power.Trend.ShouldBe("Measured");
        power.Kind.ShouldBe(TrendKind.Quality);
        power.Fill.ShouldBe(34.2 / 75, 1e-9);                  // over the meter's 75 W scale, sized for today's 68 W peak

        today.Label.ShouldBe("Today");
        today.Big.ShouldBe("0.284 kWh");
        today.Small.ShouldBe("$0.05");
        today.Trend.ShouldBe("56%");                          // 284 Wh against 300 Wh × 0.606 of the day = 182 Wh expected
        today.Kind.ShouldBe(TrendKind.Up);
        today.Fill.ShouldBe(284 / 300.0, 1e-9);

        idle.Label.ShouldBe("Idle waste this month");
        idle.Big.ShouldBe("0.210 kWh");
        idle.Small.ShouldBe("$0.04");                         // at the month's average price, 17.2¢
        idle.Trend.ShouldBe("45%");                           // against last month's 0.3836 kWh
        idle.Kind.ShouldBe(TrendKind.Down);
        idle.Fill.ShouldBe(0.21 / 2.74, 1e-9);

        // Energy is a cost: less of it is the good news, so today's rise reads as bad and the idle waste's fall as good.
        power.LowerIsBetter.ShouldBeFalse("its slot holds the quality, not a trend");
        today.LowerIsBetter.ShouldBeTrue();
        idle.LowerIsBetter.ShouldBeTrue();
        DashboardMaths.Sense(today.Kind, today.LowerIsBetter).ShouldBe(TrendSense.Bad);
        DashboardMaths.Sense(idle.Kind, idle.LowerIsBetter).ShouldBe(TrendSense.Good);
    }

    [Fact]
    public void The_live_figures_and_the_status_are_the_now_pages_and_a_new_reading_redraws_the_first_card()
    {
        var dashboard = Dashboard();
        var raised = new List<string?>();
        dashboard.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        _link.Push(Frames.At(Now.AddSeconds(1), totalW: 40.4));

        raised.ShouldContain(nameof(DashboardViewModel.Live));
        raised.ShouldContain(nameof(DashboardViewModel.Kpis));
        dashboard.Live.ShouldBeSameAs(_now.Live);
        dashboard.Status.ShouldBeSameAs(_now.Status);
        dashboard.Today.ShouldBeSameAs(_now.Today);
        dashboard.Kpis[0].Big.ShouldBe("40.4 W");
    }

    [Fact]
    public void The_pills_read_their_ranges_and_all_starts_where_the_history_does()
    {
        _summary.First = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
        var dashboard = Dashboard();
        dashboard.Range.ShouldBe(RangePill.Day);
        dashboard.Show();
        dashboard.ChartTitle.ShouldBe("Today");
        dashboard.Chart.Capacity.ShouldBe(288);
        dashboard.ChartMessage.ShouldBeNull();

        dashboard.Range = RangePill.Hour;
        dashboard.ChartTitle.ShouldBe("Last hour");
        dashboard.Chart.Capacity.ShouldBe(60);
        dashboard.Chart.Bucket.ShouldBe(TimeSpan.FromMinutes(1));

        dashboard.Range = RangePill.Week;
        dashboard.ChartTitle.ShouldBe("Last 7 days");
        dashboard.Range = RangePill.Month;
        dashboard.ChartTitle.ShouldBe("Last 30 days");
        dashboard.Range = RangePill.Year;
        dashboard.ChartTitle.ShouldBe("Last 365 days");
        dashboard.Chart.Bucket.ShouldBe(TimeSpan.FromDays(1));
        dashboard.Range = RangePill.All;
        dashboard.ChartTitle.ShouldBe("Since 20 Jul 2026");
        dashboard.Chart.Capacity.ShouldBe(51);
    }

    [Fact]
    public void The_chart_says_when_it_starts_and_in_which_zone_so_its_tooltip_can_tell_the_time()
    {
        var dashboard = Dashboard();
        dashboard.ChartFrom.ShouldBeNull("nothing is read until the page shows");
        dashboard.Show();
        dashboard.ChartFrom.ShouldBe(new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero));
        dashboard.Zone.ShouldBe(TimeZoneInfo.Utc);

        dashboard.Range = RangePill.Hour;
        dashboard.ChartFrom.ShouldBe(Ranges.LastHour(Now, TimeZoneInfo.Utc, English).From);
    }

    [Fact]
    public void Every_minute_the_page_reads_again_but_the_chart_only_while_it_moves_by_the_minute()
    {
        var dashboard = Dashboard();
        dashboard.PartsRange = PartsRange.ThirtyDays;
        dashboard.Show();
        _history.Reads.Clear();
        int Reads(string title) => _history.Reads.Count(range => range.Title == title);

        _clock.Advance(DashboardViewModel.RefreshEvery);
        Reads("Today").ShouldBe(1);                           // the day's chart moves
        Reads("Last 30 days").ShouldBe(1);

        dashboard.Range = RangePill.Week;
        Reads("Last 7 days").ShouldBe(1);
        _clock.Advance(DashboardViewModel.RefreshEvery);
        Reads("Last 7 days").ShouldBe(1);                     // a week's chart is left as it was
        Reads("Last 30 days").ShouldBe(3);                    // the parts and the cards are read again
        dashboard.ChartTitle.ShouldBe("Last 7 days");

        dashboard.Hide();
        _clock.Advance(DashboardViewModel.RefreshEvery * 3);
        Reads("Last 30 days").ShouldBe(3);
    }

    [Fact]
    public void Without_the_service_the_live_card_shows_a_dash_and_the_last_known_quality()
    {
        var dashboard = Dashboard();
        dashboard.Show();

        _link.Connect(false);

        var power = dashboard.Kpis[0];
        power.Big.ShouldBe("–");
        power.Small.ShouldBe("Service not running");
        power.Trend.ShouldBe("Measured");
        power.Kind.ShouldBe(TrendKind.Quality);
        power.Fill.ShouldBe(0);
        dashboard.Kpis[1].Big.ShouldBe("0.284 kWh");         // the rest of the page stays
        dashboard.Parts.Count.ShouldBe(4);
        dashboard.Parts[0].NowW.ShouldBe("–");
        dashboard.Parts[0].Quality.ShouldBeNull();
    }

    [Fact]
    public void Without_history_the_chart_is_empty_and_says_why_and_the_cards_say_what_they_can()
    {
        _history.Answer = _ => null;
        var dashboard = Dashboard();
        dashboard.Show();
        dashboard.Chart.Buckets.ShouldBeEmpty();
        dashboard.ChartMessage.ShouldBe("Couldn't read the history");
        dashboard.Kpis[1].Big.ShouldBe("0.284 kWh");
        dashboard.Kpis[1].Trend.ShouldBeNull();               // no average day to compare
        dashboard.Kpis[1].Fill.ShouldBe(0);
        dashboard.Parts[0].Energy.ShouldBe("–");

        _history.Answer = Reports.Empty;
        _summary.Snapshot = null;
        dashboard.Range = RangePill.Week;
        dashboard.ChartMessage.ShouldBe("No history yet");
        dashboard.Kpis[1].Big.ShouldBe("–");
        dashboard.Kpis[1].Small.ShouldBe("History can't be read right now");
        dashboard.Kpis[2].Big.ShouldBe("–");
    }

    [Fact]
    public void The_first_month_has_no_last_month_to_compare_with()
    {
        var answer = _history.Answer;
        _history.Answer = range => range.Title == "August 2026" ? Reports.Empty(range) : answer(range);
        var dashboard = Dashboard();
        dashboard.Show();

        dashboard.Kpis[2].Trend.ShouldBe("first month");
        dashboard.Kpis[2].Kind.ShouldBe(TrendKind.Text);
        dashboard.Kpis[2].Big.ShouldBe("0.210 kWh");
    }

    [Fact]
    public void The_parts_table_lists_the_four_parts_with_watts_now_energy_share_quality_and_trend()
    {
        var dashboard = Dashboard();
        dashboard.Show();

        var parts = dashboard.Parts;
        parts.Select(p => p.Part).ShouldBe([Part.Cpu, Part.Gpu, Part.Display, Part.Rest]);
        parts.Select(p => p.Name).ShouldBe(["CPU package", "GPU", "Display", "Rest of system"]);
        parts.Select(p => p.NowW).ShouldBe(["14.6 W", "4.1 W", "4.0 W", "11.5 W"]);
        parts.Select(p => p.Energy).ShouldBe(["1.18 kWh", "301 Wh", "274 Wh", "986 Wh"]);   // 43, 11, 10 and 36 % of 2.74 kWh
        parts.Sum(p => p.Share).ShouldBe(1, 1e-9);
        parts[0].Share.ShouldBe(0.43, 1e-9);
        parts.Select(p => p.Quality).ShouldBe([Quality.Measured, Quality.Estimated, Quality.Estimated, Quality.Measured]);
        parts.All(p => p.Glyph.Length == 1).ShouldBeTrue();
        parts[0].Trend.ShouldBe("1270%");                     // 1.18 kWh against 86 Wh the same length of time before
        parts[0].Kind.ShouldBe(TrendKind.Up);
        parts.ShouldAllBe(p => p.LowerIsBetter, "a part's energy is a cost too");

        dashboard.PartsRange = PartsRange.SevenDays;
        _history.Reads[^2].Title.ShouldBe("Last 7 days");
        _history.Reads[^1].Title.ShouldBe("Before");
        (_history.Reads[^1].To - _history.Reads[^1].From).ShouldBe(_history.Reads[^2].To - _history.Reads[^2].From);
        _history.Reads[^1].To.ShouldBe(_history.Reads[^2].From);
    }
}
