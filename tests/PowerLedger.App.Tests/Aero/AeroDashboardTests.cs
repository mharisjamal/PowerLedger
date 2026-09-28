using System.Globalization;
using System.IO;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan S D3: what Aero's Dashboard reads beyond Midnight's (Aero look design §1), only while <see
/// cref="DashboardViewModel.Detailed"/>: yesterday to the same time, the days of a chosen month and the month before, the
/// history table's span, and each part's last seven days. Midnight never asks, so it reads nothing more.
/// </summary>
public sealed class AeroDashboardTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 7, TimeSpan.Zero);   // a Tuesday
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeLink _link = new();
    private readonly FakeHistory _summary = new() { Snapshot = Snapshots.Typical(Now), First = Now.AddDays(-40) };
    private readonly FakeRangeHistory _history = new();
    private readonly FakeUiSettings _ui = new();
    private readonly FakeSaver _saver = new();
    private readonly NowViewModel _now;
    private DashboardViewModel? _dashboard;

    public AeroDashboardTests()
    {
        _now = new NowViewModel(_link, _summary, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, 0.38, () => { });
        _history.Answer = range => range.Title switch
        {
            DashboardViewModel.YesterdayTitle => Reports.Typical(range, 0.2),
            _ => Reports.Typical(range),
        };
    }

    public void Dispose()
    {
        _dashboard?.Dispose();
        _now.Dispose();
        _saver.Dispose();
    }

    private DashboardViewModel Dashboard(bool detailed = true)
    {
        _link.Connect(true);
        _link.Push(Frames.At(Now));
        _dashboard = new DashboardViewModel(_now, _history, _summary, _clock, TimeZoneInfo.Utc, English, UiThreads.Inline, _ui, saver: _saver);
        _dashboard.Detailed = detailed;
        _dashboard.Show();
        return _dashboard;
    }

    private static readonly string[] AeroTitles =
        [DashboardViewModel.YesterdayTitle, DashboardViewModel.WeekByDayTitle, DashboardViewModel.TableTitle, "September 2026", "August 2026"];

    [Fact]
    public void Midnight_reads_nothing_more_and_has_no_detail()
    {
        var dashboard = Dashboard(detailed: false);

        dashboard.Detail.ShouldBeNull();
        _history.Reads.ShouldNotContain(range => AeroTitles.Contains(range.Title));
        dashboard.Parts.ShouldAllBe(part => part.Last7DaysWh.Count == 0);
    }

    [Fact]
    public void Asked_for_detail_it_reads_at_once_and_with_every_minute_after()
    {
        var dashboard = Dashboard(detailed: false);
        _history.Reads.Clear();

        dashboard.Detailed = true;

        dashboard.Detail.ShouldNotBeNull();
        _history.Reads.Count(range => range.Title == DashboardViewModel.YesterdayTitle).ShouldBe(1);
        _clock.Advance(DashboardViewModel.RefreshEvery);
        _history.Reads.Count(range => range.Title == DashboardViewModel.YesterdayTitle).ShouldBe(2);
    }

    /// <summary>Power now: today so far against yesterday from midnight to the same time, and the tariff for the cost
    /// toggle; This month's "Day N of M".</summary>
    [Fact]
    public void Today_is_measured_against_yesterday_to_the_same_time()
    {
        var detail = Dashboard().Detail!;

        var yesterday = _history.Reads.Single(range => range.Title == DashboardViewModel.YesterdayTitle);
        yesterday.From.ShouldBe(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        yesterday.To.ShouldBe(Now.AddDays(-1));
        detail.TodayKwh.ShouldBe(0.284);
        detail.ChangeVsYesterday!.Value.ShouldBe(0.42, 1e-9);   // 0.284 kWh against 0.2
        detail.PricePerKwh.ShouldBe(0.17m);
        detail.Currency.ShouldBe("USD");
        detail.MonthDay.ShouldBe(8);
        detail.MonthDays.ShouldBe(30);
    }

    [Fact]
    public void Without_yesterday_there_is_no_change()
    {
        _history.Answer = range => range.Title == DashboardViewModel.YesterdayTitle ? Reports.Empty(range) : Reports.Typical(range);

        Dashboard().Detail!.ChangeVsYesterday.ShouldBeNull();
    }

    /// <summary>Energy each day: this month's days so far and the whole of the month before, a day a point; the picker
    /// offers every month from the first row's to this one, newest first.</summary>
    [Fact]
    public void Energy_each_day_draws_the_chosen_month_against_the_one_before()
    {
        var dashboard = Dashboard();
        var detail = dashboard.Detail!;

        detail.DailyMonth.ShouldBe(new DateOnly(2026, 9, 1));
        detail.Daily.Count.ShouldBe(8, "the 1st to today");
        detail.Daily[0].Day.ShouldBe(new DateOnly(2026, 9, 1));
        detail.Daily.Sum(day => day.Kwh).ShouldBe(2.74, 1e-9);
        detail.DailyKwh.ShouldBe(2.74, 1e-9);
        detail.DailyPrevious.Count.ShouldBe(31, "the whole of August");
        detail.DailyPreviousKwh.ShouldBe(2.74, 1e-9);
        _history.Reads.Single(range => range.Title == "September 2026").Bucket.ShouldBe(TimeSpan.FromDays(1));
        dashboard.DailyMonths.ShouldBe([new DateOnly(2026, 9, 1), new DateOnly(2026, 8, 1), new DateOnly(2026, 7, 1)]);

        dashboard.DailyMonth = new DateOnly(2026, 8, 1);

        dashboard.Detail!.DailyMonth.ShouldBe(new DateOnly(2026, 8, 1));
        dashboard.Detail.Daily.Count.ShouldBe(31);
        dashboard.Detail.DailyPrevious.Count.ShouldBe(31, "July");
        _history.Reads.ShouldContain(range => range.Title == "July 2026");
    }

    [Fact]
    public void A_day_without_history_counts_as_nothing_used()
    {
        _history.Answer = range => range.Title == "September 2026"
            ? Reports.Typical(range) with { Days = Reports.Typical(range).Days.Where(day => day.Day.Day != 3).ToList() }
            : Reports.Typical(range);

        var detail = Dashboard().Detail!;

        detail.Daily.Count.ShouldBe(8);
        detail.Daily[2].Kwh.ShouldBe(0);
    }

    /// <summary>The history table: Day, Week, Month and Year, newest first, each period with its energy, cost, average
    /// and peak and how its figures were got.</summary>
    [Fact]
    public void The_history_table_lists_the_days_newest_first()
    {
        var detail = Dashboard().Detail!;

        detail.History.Select(row => row.Period).ShouldBe(
            ["Today, so far", "Yesterday", "Sunday 6", "Saturday 5", "Friday 4", "Thursday 3", "Wednesday 2"]);
        var today = detail.History[0];
        today.Energy.ShouldBe("0.391 kWh");                   // 2.74 kWh over seven days
        today.Cost.ShouldBe("$0.05");
        today.Average.ShouldBe("49 W");                        // 391 Wh over 8 hours on
        today.Peak.ShouldBe("60 W");
        today.Source.ShouldBe(Quality.Estimated);
        _history.Reads.ShouldContain(range => range.Title == DashboardViewModel.WeekByDayTitle && range.Bucket == TimeSpan.FromDays(1));
    }

    [Theory]
    [InlineData("Week", "2026-08-17", "This week, so far|31 August to 6 September|24 to 30 August|17 to 23 August")]
    [InlineData("Month", "2026-06-01", "September, so far|August|July|June")]
    [InlineData("Year", "2025-01-01", "2026, so far|2025")]
    public void Each_span_reads_its_periods_and_names_them(string span, string from, string periods)
    {
        var dashboard = Dashboard();
        _history.Reads.Clear();

        dashboard.HistorySpan = Enum.Parse<HistorySpan>(span);

        var read = _history.Reads.Single(range => range.Title == DashboardViewModel.TableTitle);
        read.From.ShouldBe(new DateTimeOffset(DateOnly.Parse(from, CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        read.Bucket.ShouldBe(TimeSpan.FromDays(1));
        dashboard.Detail!.History.Select(row => row.Period).ShouldBe(periods.Split('|'));
    }

    [Fact]
    public void A_period_before_the_history_began_is_left_out()
    {
        _history.Answer = range => range.Title == DashboardViewModel.TableTitle
            ? Reports.Typical(range) with { Days = Reports.Typical(range).Days.Where(day => day.Day >= new DateOnly(2026, 8, 20)).ToList() }
            : Reports.Typical(range);
        var dashboard = Dashboard();

        dashboard.HistorySpan = HistorySpan.Month;

        dashboard.Detail!.History.Select(row => row.Period).ShouldBe(["September, so far", "August"]);
    }

    /// <summary>The top bar's search (Ctrl K) narrows the table to the rows that mention it, in any case.</summary>
    [Fact]
    public void The_search_narrows_the_table()
    {
        var dashboard = Dashboard();

        dashboard.HistoryQuery = "YESTER";
        dashboard.HistoryShown.Select(row => row.Period).ShouldBe(["Yesterday"]);

        dashboard.HistoryQuery = "60 w";
        dashboard.HistoryShown.Count.ShouldBe(7, "the peak column is searched too");

        dashboard.HistoryQuery = "nowhere";
        dashboard.HistoryShown.ShouldBeEmpty();

        dashboard.HistoryQuery = "  ";
        dashboard.HistoryShown.Count.ShouldBe(7);
    }

    /// <summary>Plan S D3: Aero's Parts page draws each part's last seven days, oldest first, from the week's daily read.</summary>
    [Fact]
    public void Each_part_carries_its_last_seven_days()
    {
        var dashboard = Dashboard();

        var cpu = dashboard.Parts.Single(part => part.Part == Part.Cpu).Last7DaysWh;
        cpu.Count.ShouldBe(7);
        cpu.ShouldAllBe(wh => Math.Abs(wh - 2740 / 7.0 * 0.43) < 1e-6);
        dashboard.Parts.Single(part => part.Part == Part.Rest).Last7DaysWh[6].ShouldBe(2740 / 7.0 * 0.36, 1e-6);
    }

    [Fact]
    public void A_day_missing_from_the_week_is_a_zero_in_its_place()
    {
        _history.Answer = range => range.Title == DashboardViewModel.WeekByDayTitle
            ? Reports.Typical(range) with { Series = Reports.Typical(range).Series.Where(row => row.Start.Day != 4).ToList() }
            : Reports.Typical(range);

        var cpu = Dashboard().Parts.Single(part => part.Part == Part.Cpu).Last7DaysWh;

        cpu.Count.ShouldBe(7);
        cpu[2].ShouldBe(0, "the 4th, third of the seven");
    }

    [Fact]
    public void Save_csv_writes_the_rows_shown_and_says_so()
    {
        var dashboard = Dashboard();
        dashboard.HistoryQuery = "day";   // Today, Yesterday, Sunday, Saturday, Friday, Thursday, Wednesday

        dashboard.SaveHistory.Execute(null);

        _saver.Suggested.ShouldBe("PowerLedger history by day.csv");
        var lines = File.ReadAllLines(_saver.Chosen);
        lines[0].ShouldBe("Day,Energy (kWh),Cost,Average (W),Peak (W),Source");
        lines[1].ShouldBe("\"Today, so far\",0.391,$0.05,49,60,Estimated");
        lines.Length.ShouldBe(8);
        dashboard.Saved.ShouldBe("Saved PowerLedger history by day.csv");
    }

    [Fact]
    public void A_cancelled_save_writes_and_says_nothing()
    {
        var dashboard = Dashboard();
        _saver.Cancel = true;

        dashboard.SaveHistory.Execute(null);

        dashboard.Saved.ShouldBeNull();
    }

    [Fact]
    public void A_save_that_fails_says_why()
    {
        var dashboard = Dashboard();
        _saver.MissingFolder = true;

        dashboard.SaveHistory.Execute(null);

        dashboard.Saved!.ShouldStartWith("Couldn't save: ");
    }

    /// <summary>A pass that throws leaves the detail at the last good one's, never a half-read one, and the next minute
    /// tries again.</summary>
    [Fact]
    public void A_read_that_throws_keeps_the_last_detail()
    {
        var dashboard = Dashboard();
        var before = dashboard.Detail;
        _history.Answer = _ => throw new IOException("locked");

        _clock.Advance(DashboardViewModel.RefreshEvery);

        dashboard.Detail.ShouldBeSameAs(before);
    }
}
