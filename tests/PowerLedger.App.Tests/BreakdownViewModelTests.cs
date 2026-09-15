using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.App.Tests;

public class BreakdownViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeRangeHistory _history = new();

    private BreakdownViewModel Model(UiThreads? threads = null) => new(_history, threads ?? UiThreads.Inline, _clock, TimeZoneInfo.Utc, English);

    [Fact]
    public void It_shows_today_in_watts_when_the_page_opens()
    {
        var model = Model();
        _history.Reads.ShouldBeEmpty();                               // nothing is read until the page shows
        model.Show();

        _history.Reads.Single().Title.ShouldBe("Today");
        model.Heading.ShouldBe("Today · 5-min · stacked by component");
        model.UnitLabel.ShouldBe("Watts");
        model.Chart.Capacity.ShouldBe(288);
        model.Chart.Buckets.Count.ShouldBe(175);
        model.Message.ShouldBeNull();
    }

    [Fact]
    public void Watt_hours_redraw_the_last_read_without_reading_again()
    {
        var model = Model();
        model.Show();
        var watts = model.Chart.Buckets[0].Total;

        model.Unit = ChartUnit.WattHours;

        _history.Reads.Count.ShouldBe(1);
        model.UnitLabel.ShouldBe("Wh per 5 min");
        model.Chart.Unit.ShouldBe(ChartUnit.WattHours);
        model.Chart.Buckets[0].Total.ShouldBe(watts / 12, 1e-9);        // five minutes on: Wh = W × 300 / 3600
    }

    [Fact]
    public void Choosing_a_range_reads_it()
    {
        var model = Model();
        model.Show();
        model.Range.Choice = RangeChoice.SevenDays;

        _history.Reads[^1].Title.ShouldBe("Last 7 days");
        model.Heading.ShouldBe("Last 7 days · hourly · stacked by component");
        model.Chart.Capacity.ShouldBe(168);
    }

    [Fact]
    public void The_table_gives_each_band_its_energy_and_share()
    {
        var model = Model();
        model.Show();

        model.Parts.Select(p => p.Name).ShouldBe(new[] { "CPU package", "GPU", "Display", "Rest of system", "Total" });
        model.Parts[0].Energy.ShouldBe("1.18");                       // 2.74 kWh × 43%
        model.Parts[0].Share.ShouldBe("43%");
        model.Parts[^1].Energy.ShouldBe("2.74");
        model.Parts[^1].Share.ShouldBe("100%");
        model.HasNegativeRest.ShouldBeFalse();
    }

    [Fact]
    public void A_negative_rest_counts_as_zero_and_is_called_out()
    {
        _history.Answer = range =>
        {
            var report = Reports.Typical(range);
            return report with { Totals = report.Totals with { RestKwh = -0.1 } };
        };
        var model = Model();
        model.Show();

        model.HasNegativeRest.ShouldBeTrue();
        model.Parts[3].Energy.ShouldBe("0.000");
        model.Parts[3].Share.ShouldBe("0%");
        model.Parts[0].Share.ShouldBe("67%");                         // 43 of the 64 left
    }

    [Fact]
    public void Nothing_to_show_says_why()
    {
        _history.Answer = _ => null;
        var model = Model();
        model.Show();
        model.Message.ShouldBe("History can't be read right now. It comes back when the service is running.");
        model.HasMessage.ShouldBeTrue();
        model.Heading.ShouldBe("Today · 5-min · stacked by component");

        _history.Answer = Reports.Empty;
        model.Refresh();
        model.Message.ShouldBe("No readings in this range.");
    }

    [Fact]
    public void A_read_that_a_newer_one_overtook_is_dropped()
    {
        var held = new Queue<Action>();
        var model = Model(new UiThreads(action => action(), held.Enqueue));
        model.Show();                                                 // today, held back
        model.Range.Choice = RangeChoice.SevenDays;                   // seven days, held back

        var today = held.Dequeue();
        held.Dequeue()();                                             // seven days lands first
        today();                                                      // then today, too late

        model.Heading.ShouldStartWith("Last 7 days");
    }

    [Fact]
    public void It_reads_every_minute_while_shown_and_stops_when_hidden()
    {
        var model = Model();
        model.Show();
        _clock.Advance(BreakdownViewModel.RefreshEvery);
        _history.Reads.Count.ShouldBe(2);

        model.Hide();
        _clock.Advance(BreakdownViewModel.RefreshEvery * 3);
        _history.Reads.Count.ShouldBe(2);
    }
}
