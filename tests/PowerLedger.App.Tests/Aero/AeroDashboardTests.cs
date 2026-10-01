using System.Globalization;
using System.IO;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan S D3 and 0.10.9: what Aero's Dashboard reads beyond Midnight's (Aero look design §1), only while <see
/// cref="DashboardViewModel.Detailed"/>: yesterday to the same time, Energy each day's fourteen days, weeks or months,
/// and each part's last seven days. Midnight never asks, so it reads nothing more.
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
    }

    private DashboardViewModel Dashboard(bool detailed = true)
    {
        _link.Connect(true);
        _link.Push(Frames.At(Now));
        _dashboard = new DashboardViewModel(_now, _history, _summary, _clock, TimeZoneInfo.Utc, English, UiThreads.Inline, _ui);
        _dashboard.Detailed = detailed;
        _dashboard.Show();
        return _dashboard;
    }

    private static readonly string[] AeroTitles =
        [DashboardViewModel.YesterdayTitle, DashboardViewModel.WeekByDayTitle, DashboardViewModel.BarsTitle];

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

    /// <summary>Energy each day (the mockup's fourteen bars): the last fourteen days by default, the latest today, each
    /// with its energy and its tip, from one read by the day.</summary>
    [Fact]
    public void Energy_each_day_is_the_last_fourteen_days()
    {
        var dashboard = Dashboard();
        var detail = dashboard.Detail!;

        detail.Span.ShouldBe(HistorySpan.Day);
        detail.Bars.Count.ShouldBe(14);
        detail.Bars[0].From.ShouldBe(new DateOnly(2026, 8, 26));
        detail.Bars[13].From.ShouldBe(new DateOnly(2026, 9, 8), "today, the latest");
        detail.Bars.Select(b => b.Label).ShouldBe(["26", "27", "28", "29", "30", "31", "1", "2", "3", "4", "5", "6", "7", "8"]);
        detail.Bars[13].Tip.ShouldBe("$0.05 on the 8th");
        var read = _history.Reads.Single(range => range.Title == DashboardViewModel.BarsTitle);
        read.From.ShouldBe(new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero));
        read.Bucket.ShouldBe(TimeSpan.FromDays(1));
    }

    [Theory]
    [InlineData("Week", "2026-06-08", "2026-09-07", "7")]
    [InlineData("Month", "2025-08-01", "2026-09-01", "Sep")]
    public void Week_and_month_are_the_last_fourteen_of_them(string span, string from, string latest, string label)
    {
        var dashboard = Dashboard();
        _history.Reads.Clear();

        dashboard.HistorySpan = Enum.Parse<HistorySpan>(span);

        var read = _history.Reads.Single(range => range.Title == DashboardViewModel.BarsTitle);
        read.From.ShouldBe(new DateTimeOffset(DateOnly.Parse(from, CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        var bars = dashboard.Detail!.Bars;
        bars.Count.ShouldBe(14);
        bars[13].From.ShouldBe(DateOnly.Parse(latest, CultureInfo.InvariantCulture));
        bars[13].Label.ShouldBe(label);
    }

    [Fact]
    public void A_period_without_history_is_nothing_used()
    {
        _history.Answer = range => range.Title == DashboardViewModel.BarsTitle
            ? Reports.Typical(range) with { Days = Reports.Typical(range).Days.Where(day => day.Day != new DateOnly(2026, 9, 3)).ToList() }
            : Reports.Typical(range);

        var bars = Dashboard().Detail!.Bars;

        bars.Single(b => b.From == new DateOnly(2026, 9, 3)).Kwh.ShouldBe(0);
        bars.Single(b => b.From == new DateOnly(2026, 9, 4)).Kwh.ShouldBeGreaterThan(0);
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
