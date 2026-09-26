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

    /// <summary>Saved with the energy card on Today, the period most of these tests are about; the tests of the period
    /// menu set it as they need.</summary>
    private readonly FakeUiSettings _ui = new() { Current = UiPreferences.Default with { EnergyPeriod = EnergyPeriod.Today } };
    private readonly NowViewModel _now;
    private DashboardViewModel? _dashboard;

    public DashboardViewModelTests()
    {
        _now = new NowViewModel(_link, _summary, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, 0.38, () => { });
        _summary.Snapshot = Snapshots.Typical(Now);   // today 0.284 kWh for $0.05; the month 2.74 kWh, 0.21 kWh of it idle
        _history.Answer = range => range.Title switch
        {
            "Last 31 days" => Reports.Typical(range, 31 * 0.3),   // 0.3 kWh a day, today among them
            "Last month to date" => Reports.Typical(range, 2.74), // 1–8 Aug 14:32; idle: 14 % of it, 0.3836 kWh
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
        return _dashboard = new DashboardViewModel(_now, _history, _summary, _clock, TimeZoneInfo.Utc, English, UiThreads.Inline, _ui);
    }

    [Fact]
    public void Each_part_names_its_model_under_it_once_read_and_none_without()
    {
        _link.Connect(true);
        _link.Push(Frames.At(Now));
        var dashboard = _dashboard = new DashboardViewModel(_now, _history, _summary, _clock, TimeZoneInfo.Utc, English, UiThreads.Inline, _ui, new FakeHardware());
        dashboard.Show();

        dashboard.Parts.Single(p => p.Part == Part.Cpu).Model.ShouldBe("Intel Core i7-8650U");
        dashboard.Parts.Single(p => p.Part == Part.Gpu).Model.ShouldBe("NVIDIA GeForce RTX 3060");
        dashboard.Parts.Single(p => p.Part == Part.Display).Model.ShouldBe("DELL U2720Q");
        dashboard.Parts.Single(p => p.Part == Part.Rest).Model.ShouldBe("16 GB DDR4 RAM, Samsung SSD 980 1TB");
        Dashboard().Parts.ShouldAllBe(p => p.Model == null);
    }

    [Theory]
    [InlineData("Intel(R) Core(TM) i7-8650U CPU @ 1.90GHz", "Intel Core i7-8650U")]
    [InlineData("AMD Ryzen 7 5800H with Radeon Graphics         ", "AMD Ryzen 7 5800H with Radeon Graphics")]
    [InlineData("AMD Ryzen 9 5900X 12-Core Processor", "AMD Ryzen 9 5900X")]
    [InlineData("  ", null)]
    public void A_processor_name_loses_the_trademarks_and_the_clock(string raw, string? shown)
        => HardwareNames.Cpu(raw).ShouldBe(shown);

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3060", "NVIDIA GeForce RTX 3060")]
    [InlineData("Microsoft Basic Display Adapter", null)]
    [InlineData("Microsoft Remote Display Adapter", null)]
    public void Windows_own_display_adapters_are_not_named_as_graphics_cards(string raw, string? shown)
        => HardwareNames.Gpu(raw).ShouldBe(shown);

    [Fact]
    public void A_monitors_name_is_read_from_its_padded_code_units_and_memory_is_counted_in_gigabytes()
    {
        HardwareNames.Monitor([.. "DELL U2720Q".Select(c => (ushort)c), 0, 0, 0]).ShouldBe("DELL U2720Q");
        HardwareNames.Monitor([0, 0]).ShouldBeNull();
        HardwareNames.Memory(2 * 8589934592d, 26).ShouldBe("16 GB DDR4 RAM");
        HardwareNames.Memory(34359738368d, 0).ShouldBe("32 GB RAM");
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

        today.Label.ShouldBe("Energy used");
        today.Period.ShouldBe("Today");
        today.Big.ShouldBe("0.284 kWh");
        today.Small.ShouldBe("$0.05");
        today.Against.ShouldBe("vs your average day");
        today.Trend.ShouldBe("56%");                          // 284 Wh against 300 Wh × 0.606 of the day = 182 Wh expected
        today.Kind.ShouldBe(TrendKind.Up);
        today.HasBar.ShouldBeTrue();
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
    public void Without_the_service_the_live_card_shows_no_reading_and_the_last_known_quality()
    {
        var dashboard = Dashboard();
        dashboard.Show();

        _link.Connect(false);

        var power = dashboard.Kpis[0];
        power.Big.ShouldBe("No reading");
        power.Small.ShouldBe("Service not running");
        power.Trend.ShouldBe("Measured");
        power.Kind.ShouldBe(TrendKind.Quality);
        power.Fill.ShouldBe(0);
        dashboard.Kpis[1].Big.ShouldBe("0.284 kWh");         // the rest of the page stays
        dashboard.Parts.Count.ShouldBe(4);
        dashboard.Parts[0].NowW.ShouldBe("N/A");
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
        dashboard.Parts[0].Energy.ShouldBe("N/A");

        _history.Answer = Reports.Empty;
        _summary.Snapshot = null;
        dashboard.Range = RangePill.Week;
        dashboard.ChartMessage.ShouldBe("No history yet");
        dashboard.Kpis[1].Big.ShouldBe("No reading");
        dashboard.Kpis[1].Small.ShouldBe("History can't be read right now");
        dashboard.Kpis[2].Big.ShouldBe("No reading");
    }

    [Fact]
    public void The_first_month_has_no_last_month_to_compare_with()
    {
        var answer = _history.Answer;
        _history.Answer = range => range.Title == "Last month to date" ? Reports.Empty(range) : answer(range);
        _summary.First = new DateTimeOffset(2026, 9, 3, 18, 0, 0, TimeSpan.Zero);   // the history starts this month
        var dashboard = Dashboard();
        dashboard.Show();

        dashboard.Kpis[2].Trend.ShouldBe("first month");
        dashboard.Kpis[2].Kind.ShouldBe(TrendKind.Text);
        dashboard.Kpis[2].Big.ShouldBe("0.210 kWh");
    }

    /// <summary>Review 9: an empty stretch of last month is not a first month. The morning of the 1st on a PC that was
    /// off overnight has nothing yet from the 1st of last month, and a PC installed late last month has nothing from its
    /// start; both have history from before this month, so neither is told this is its first.</summary>
    [Theory]
    [InlineData("2026-10-01T08:00:00Z", "2026-07-20T08:00:00Z")]   // off overnight: last month to date is 1 Sep, midnight to 08:00
    [InlineData("2026-09-08T14:32:07Z", "2026-08-15T10:00:00Z")]   // installed on 15 Aug: nothing in 1 to 8 Aug
    public void An_empty_start_of_last_month_is_no_first_month_when_the_history_began_before_this_one(string now, string first)
    {
        _clock.SetUtcNow(DateTimeOffset.Parse(now, CultureInfo.InvariantCulture));
        _summary.First = DateTimeOffset.Parse(first, CultureInfo.InvariantCulture);
        var answer = _history.Answer;
        _history.Answer = range => range.Title == "Last month to date" ? Reports.Empty(range) : answer(range);
        var dashboard = Dashboard();
        dashboard.Show();

        dashboard.Kpis[2].Trend.ShouldBeNull("nothing to compare with, but not a first month");
        dashboard.Kpis[2].Kind.ShouldBe(TrendKind.Text);
        dashboard.Kpis[2].Big.ShouldBe("0.210 kWh");
    }

    /// <summary>Review 7: a chart that couldn't be read is read again the next minute on every range, not only on the
    /// hour and the day, which are read every minute anyway.</summary>
    [Theory]
    [InlineData("Week", "Last 7 days", false)]
    [InlineData("Month", "Last 30 days", true)]
    [InlineData("Year", "Last 365 days", false)]
    [InlineData("All", "Since 20 Jul 2026", true)]
    public void A_chart_that_could_not_be_read_is_read_again_the_next_minute(string pill, string title, bool throws)
    {
        _summary.First = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
        var answer = _history.Answer;
        var failing = true;
        _history.Answer = range => !failing || range.Title != title ? answer(range)
            : throws ? throw new System.IO.IOException("The history file is locked.") : null;
        var dashboard = Dashboard();
        dashboard.Range = Enum.Parse<RangePill>(pill);
        dashboard.Show();
        dashboard.ChartMessage.ShouldBe("Couldn't read the history");

        failing = false;
        _clock.Advance(DashboardViewModel.RefreshEvery);

        dashboard.ChartMessage.ShouldBeNull();
        dashboard.ChartTitle.ShouldBe(title);
        dashboard.Chart.Buckets.ShouldNotBeEmpty();
        _history.Reads.Clear();
        _clock.Advance(DashboardViewModel.RefreshEvery);
        _history.Reads.Count(range => range.Title == title).ShouldBe(0, "a chart read well is kept for the rest of the day");
    }

    /// <summary>Review 8: the history answers every range with its buckets, empty or not, so an empty chart is one whose
    /// every bucket is empty, whatever the range's totals say.</summary>
    [Fact]
    public void A_chart_whose_every_bucket_is_empty_says_there_is_no_history_yet()
    {
        _history.Answer = range => Reports.Typical(range) with
        {
            Series = [.. Enumerable.Range(0, 12).Select(i => Core.Aggregate.Empty(range.From + i * range.Bucket))],
        };
        var dashboard = Dashboard();
        dashboard.Show();

        dashboard.Chart.Buckets.ShouldNotBeEmpty();
        dashboard.ChartMessage.ShouldBe("No history yet");

        _history.Answer = Reports.Typical;
        dashboard.Range = RangePill.Hour;
        dashboard.ChartMessage.ShouldBeNull();
    }

    /// <summary>Review 7: a kept week is kept only for the day it was cut on; the first minute after midnight cuts it again.</summary>
    [Fact]
    public void A_kept_chart_is_cut_again_after_midnight()
    {
        _clock.SetUtcNow(new DateTimeOffset(2026, 9, 8, 23, 59, 30, TimeSpan.Zero));
        var dashboard = Dashboard();
        dashboard.Range = RangePill.Week;
        dashboard.Show();
        dashboard.ChartFrom.ShouldBe(new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero));

        _clock.Advance(DashboardViewModel.RefreshEvery);   // 00:00:30 on the 9th

        dashboard.ChartFrom.ShouldBe(new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero));
        dashboard.ChartTitle.ShouldBe("Last 7 days");
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
        _history.Reads[^1].From.ShouldBe(_history.Reads[^2].From.AddDays(-7), "the week before, from the same hour");
        _history.Reads[^1].To.ShouldBe(Now.AddDays(-7), "to the same moment a week ago");
    }

    /// <summary>Review 2: today so far against yesterday up to the same time, not against the hours just before midnight.</summary>
    [Fact]
    public void The_parts_compare_with_the_same_hours_a_period_before()
    {
        var dashboard = Dashboard();
        dashboard.Show();
        var before = _history.Reads.Last(range => range.Title == "Before");
        before.From.ShouldBe(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        before.To.ShouldBe(Now.AddDays(-1));

        dashboard.PartsRange = PartsRange.ThirtyDays;
        before = _history.Reads.Last(range => range.Title == "Before");
        before.From.ShouldBe(new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero));   // 10 Aug, the 30 days' first, less 30
        before.To.ShouldBe(Now.AddDays(-30));
    }

    /// <summary>Calendar days, not 24-hour blocks: the Monday after the clocks go back compares with the Sunday from its
    /// midnight, an hour longer on the clock's face.</summary>
    [Fact]
    public void Across_a_clock_change_the_period_before_keeps_to_the_clock()
    {
        var london = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
        var monday = new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);   // the day after British Summer Time ends
        var before = DashboardViewModel.Before(Ranges.Today(monday, london, English), 1, london);
        before.From.ShouldBe(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.FromHours(1)));
        before.To.ShouldBe(new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero));
    }

    /// <summary>Review 3: the month so far against last month up to the same day and time, not all of last month.</summary>
    [Fact]
    public void Idle_waste_compares_with_last_month_up_to_the_same_day_and_time()
    {
        var dashboard = Dashboard();
        dashboard.Show();
        var last = _history.Reads.Last(range => range.Title == "Last month to date");
        last.From.ShouldBe(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        last.To.ShouldBe(new DateTimeOffset(2026, 8, 8, 14, 32, 7, TimeSpan.Zero));
        _history.Reads.ShouldNotContain(range => range.Title == "August 2026", "not the whole of last month");

        var endOfMarch = DashboardViewModel.LastMonthSoFar(new DateTimeOffset(2026, 3, 31, 10, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);
        endOfMarch.From.ShouldBe(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        endOfMarch.To.ShouldBe(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), "a shorter month ends where it ends");
    }

    /// <summary>Review 1: a live reading comes every second; it redraws the cards and the parts but leaves the chart, the
    /// same instance, so its crosshair and tooltip stay where the user put them and nothing is rebuilt each second.</summary>
    [Fact]
    public void A_new_reading_redraws_the_cards_but_leaves_the_chart_alone()
    {
        var dashboard = Dashboard();
        dashboard.Show();
        var chart = dashboard.Chart;
        var raised = new List<string?>();
        dashboard.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        _link.Push(Frames.At(Now.AddSeconds(1), totalW: 51.3));

        raised.ShouldContain(nameof(DashboardViewModel.Kpis), "the cards follow the reading");
        dashboard.Chart.ShouldBeSameAs(chart);
        raised.ShouldNotContain(nameof(DashboardViewModel.Chart));
        raised.ShouldNotContain(nameof(DashboardViewModel.ChartTitle));
    }

    /// <summary>Review 4: the minute's tick may start while a new range is being read and finish after it; it must read
    /// the chart for the range now chosen, not keep the old one under the new pill.</summary>
    [Fact]
    public void A_minute_tick_during_a_range_change_reads_the_new_range()
    {
        var queue = new Queue<Action>();
        var dashboard = Queued(queue);
        void Drain()
        {
            while (queue.TryDequeue(out var work)) work();
        }
        dashboard.Show();
        Drain();
        dashboard.ChartTitle.ShouldBe("Today");

        dashboard.Range = RangePill.Year;                    // its read waits…
        _clock.Advance(DashboardViewModel.RefreshEvery);      // …and the minute's tick is read after it
        Drain();

        dashboard.ChartTitle.ShouldBe("Last 365 days");
        dashboard.Chart.Bucket.ShouldBe(TimeSpan.FromDays(1));
    }

    /// <summary>Review 5: a read that throws off the UI thread would be lost with its task; the page says it couldn't read.</summary>
    [Fact]
    public void A_history_read_that_throws_says_so_on_the_page()
    {
        _history.Answer = _ => throw new InvalidOperationException("The history file is locked.");
        var dashboard = Dashboard();

        Should.NotThrow(dashboard.Show);

        dashboard.ChartMessage.ShouldBe("Couldn't read the history");
        dashboard.Kpis[1].Small.ShouldBe("History can't be read right now");
        dashboard.Kpis[1].Big.ShouldBe(Format.NoReading);
    }

    /// <summary>With nothing chosen, the energy card covers everything this PC has recorded: from the first row, 50 days and
    /// 6½ hours before now, with its cost, and the day's average over that time in place of a bar and a trend.</summary>
    [Fact]
    public void Energy_used_covers_everything_since_the_start_until_another_period_is_chosen()
    {
        _ui.Current = UiPreferences.Default;
        _summary.First = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
        var answer = _history.Answer;
        _history.Answer = range => range.Title == "Since start" ? Reports.Typical(range, 15) : answer(range);
        var dashboard = Dashboard();
        dashboard.EnergyPeriod.ShouldBe(EnergyPeriod.SinceStart);
        dashboard.Show();

        var since = _history.Reads.Last(range => range.Title == "Since start");
        since.From.ShouldBe(new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero));
        since.To.ShouldBe(Now);
        var energy = dashboard.Kpis[1];
        energy.Label.ShouldBe("Energy used");
        energy.Period.ShouldBe("Since start");
        energy.Big.ShouldBe("15.0 kWh");
        energy.Small.ShouldBe("$2.55");
        energy.Against.ShouldBe("since 20 July, 0.30 kWh a day on average");   // 15 kWh over 50.27 days
        energy.HasBar.ShouldBeFalse();
        energy.Trend.ShouldBeNull();
        energy.Kind.ShouldBe(TrendKind.Text);
        energy.LowerIsBetter.ShouldBeTrue();
    }

    /// <summary>The average is over whole days at the least: an hour's history is not 24 hours' worth a day.</summary>
    [Fact]
    public void A_history_that_began_today_averages_over_a_whole_day()
    {
        _ui.Current = UiPreferences.Default;
        _summary.First = Now.AddHours(-1);
        var answer = _history.Answer;
        _history.Answer = range => range.Title == "Since start" ? Reports.Typical(range, 0.04) : answer(range);
        var dashboard = Dashboard();
        dashboard.Show();

        dashboard.Kpis[1].Against.ShouldBe("since 8 September, 0.04 kWh a day on average");
    }

    [Fact]
    public void Since_the_start_without_history_says_what_it_can()
    {
        _ui.Current = UiPreferences.Default;
        _history.Answer = range => range.Title == "Since start" ? null : Reports.Typical(range);
        var dashboard = Dashboard();
        dashboard.Show();

        dashboard.Kpis[1].Big.ShouldBe(Format.NoReading);
        dashboard.Kpis[1].Small.ShouldBe("History can't be read right now");
        dashboard.Kpis[1].Against.ShouldBeNull();
        dashboard.Kpis[1].HasBar.ShouldBeFalse();
    }

    /// <summary>This week, Monday to now as the app's weeks run, against last week from its Monday to the same day and
    /// time; the bar is this week so far over the whole of last week.</summary>
    [Fact]
    public void This_week_compares_with_last_week_at_the_same_point()
    {
        _ui.Current = UiPreferences.Default with { EnergyPeriod = EnergyPeriod.ThisWeek };
        var answer = _history.Answer;
        _history.Answer = range => range.Title switch
        {
            "This week" => Reports.Typical(range, 1.2),
            "Last week to date" => Reports.Typical(range, 1.0),
            "All of last week" => Reports.Typical(range, 3.0),
            _ => answer(range),
        };
        var dashboard = Dashboard();
        dashboard.Show();

        var week = _history.Reads.Last(range => range.Title == "This week");
        week.From.ShouldBe(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));   // the Monday
        week.To.ShouldBe(Now);
        var before = _history.Reads.Last(range => range.Title == "Last week to date");
        before.From.ShouldBe(new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero));
        before.To.ShouldBe(Now.AddDays(-7));
        var whole = _history.Reads.Last(range => range.Title == "All of last week");
        whole.From.ShouldBe(new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero));
        whole.To.ShouldBe(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        var energy = dashboard.Kpis[1];
        energy.Period.ShouldBe("This week");
        energy.Big.ShouldBe("1.20 kWh");
        energy.Small.ShouldBe("$0.20");
        energy.Against.ShouldBe("vs last week");
        energy.Trend.ShouldBe("20%");
        energy.Kind.ShouldBe(TrendKind.Up);
        energy.HasBar.ShouldBeTrue();
        energy.Fill.ShouldBe(1.2 / 3.0, 1e-9);
        energy.LowerIsBetter.ShouldBeTrue();
    }

    /// <summary>This month so far, from the same read as the idle waste's, against last month to the same day and time;
    /// the bar is this month so far over the whole of last month.</summary>
    [Fact]
    public void This_month_compares_with_last_month_at_the_same_point()
    {
        _ui.Current = UiPreferences.Default with { EnergyPeriod = EnergyPeriod.ThisMonth };
        var answer = _history.Answer;
        _history.Answer = range => range.Title switch
        {
            "Last month to date" => Reports.Typical(range, 3.0),
            "All of last month" => Reports.Typical(range, 8.0),
            _ => answer(range),
        };
        var dashboard = Dashboard();
        dashboard.Show();

        var whole = _history.Reads.Last(range => range.Title == "All of last month");
        whole.From.ShouldBe(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        whole.To.ShouldBe(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var energy = dashboard.Kpis[1];
        energy.Period.ShouldBe("This month");
        energy.Big.ShouldBe("2.74 kWh");
        energy.Small.ShouldBe("$0.47");
        energy.Against.ShouldBe("vs last month");
        energy.Trend.ShouldBe("9%");                          // 2.74 against 3.0 to the same point
        energy.Kind.ShouldBe(TrendKind.Down);
        energy.Fill.ShouldBe(2.74 / 8.0, 1e-9);
    }

    [Fact]
    public void With_nothing_last_week_there_is_no_trend_but_still_the_week()
    {
        _ui.Current = UiPreferences.Default with { EnergyPeriod = EnergyPeriod.ThisWeek };
        var answer = _history.Answer;
        _history.Answer = range => range.Title is "Last week to date" or "All of last week" ? Reports.Empty(range) : answer(range);
        var dashboard = Dashboard();
        dashboard.Show();

        dashboard.Kpis[1].Big.ShouldBe("2.74 kWh");
        dashboard.Kpis[1].Trend.ShouldBeNull();
        dashboard.Kpis[1].Kind.ShouldBe(TrendKind.Text);
        dashboard.Kpis[1].Fill.ShouldBe(0);
    }

    /// <summary>The menu's choice switches the card where it stands, is saved for the next start, and reads only what the
    /// card needs: the chart is left as it was.</summary>
    [Fact]
    public void A_chosen_period_switches_the_card_in_place_and_is_remembered()
    {
        _ui.Current = UiPreferences.Default with { EnergyPeriod = EnergyPeriod.ThisMonth };
        var dashboard = Dashboard();
        dashboard.EnergyPeriod.ShouldBe(EnergyPeriod.ThisMonth, "the saved choice");
        dashboard.Show();
        dashboard.Kpis[1].Period.ShouldBe("This month");
        var chart = dashboard.Chart;
        _history.Reads.Clear();

        dashboard.ChooseEnergyPeriod.Execute(EnergyPeriod.ThisWeek);

        dashboard.EnergyPeriod.ShouldBe(EnergyPeriod.ThisWeek);
        dashboard.Kpis[1].Period.ShouldBe("This week");
        dashboard.Kpis[1].Against.ShouldBe("vs last week");
        _ui.Current.EnergyPeriod.ShouldBe(EnergyPeriod.ThisWeek);
        _ui.Changes.ShouldBe(["energy period ThisWeek"]);
        _history.Reads.Count(range => range.Title == "Today").ShouldBe(1, "the parts' today, and not the chart's, which stays");
        dashboard.Chart.ShouldBeSameAs(chart);

        dashboard.EnergyPeriod = EnergyPeriod.SinceStart;
        dashboard.Kpis[1].Period.ShouldBe("Since start");
        dashboard.EnergyPeriod = EnergyPeriod.Today;
        dashboard.Kpis[1].Period.ShouldBe("Today");
        dashboard.Kpis[1].Against.ShouldBe("vs your average day");
        _ui.Current.EnergyPeriod.ShouldBe(EnergyPeriod.Today);
        _ui.Changes.Count.ShouldBe(3);
    }

    /// <summary>Every period is read again with the rest each minute, so the card keeps up as the others do.</summary>
    [Theory]
    [InlineData("SinceStart", "Since start")]
    [InlineData("ThisWeek", "This week")]
    [InlineData("ThisMonth", "All of last month")]
    public void The_chosen_period_is_read_again_every_minute(string period, string title)
    {
        _ui.Current = UiPreferences.Default with { EnergyPeriod = Enum.Parse<EnergyPeriod>(period) };
        var dashboard = Dashboard();
        dashboard.Show();
        _history.Reads.Clear();

        _clock.Advance(DashboardViewModel.RefreshEvery);

        _history.Reads.Count(range => range.Title == title).ShouldBe(1);
    }

    /// <summary>The Dashboard with its background reads held in <paramref name="queue"/> until a test runs them.</summary>
    private DashboardViewModel Queued(Queue<Action> queue)
    {
        _link.Connect(true);
        _link.Push(Frames.At(Now));
        return _dashboard = new DashboardViewModel(_now, _history, _summary, _clock, TimeZoneInfo.Utc, English, new UiThreads(action => action(), queue.Enqueue), _ui);
    }
}
