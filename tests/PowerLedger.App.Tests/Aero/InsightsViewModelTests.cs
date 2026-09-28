using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Aero look design §4 and §6: what the Insights page binds to, from the report, with each card's empty state,
/// and the bell's alerts.</summary>
public class InsightsViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 18, 30, 0, TimeSpan.Zero);
    private static readonly CultureInfo British = CultureInfo.GetCultureInfo("en-GB");

    private readonly FakeInsights _insights = new();
    private readonly FakeTimeProvider _clock = new(Now);

    private InsightsViewModel Model(UiThreads? threads = null) => new(_insights, threads ?? UiThreads.Inline, _clock, TimeZoneInfo.Utc, British);

    private static InsightsReport Report(
        BillForecast? forecast = null, IReadOnlyList<UsageAnomaly>? anomalies = null, IdleHabits? habits = null, CarbonEstimate? carbon = null)
        => new(forecast ?? BillForecast.NotReady(3, "GBP"), anomalies ?? [], habits, carbon ?? new CarbonEstimate(7.08, 94.4, 236, "The United Kingdom's grid in 2023, from Ember"));

    private static IdleHabits Habits(double worstWh = 100, decimal cost = 1.12m, double kwh = 5.58)
    {
        var heatmap = new double[7, 24];
        for (var day = 0; day < 7; day++) heatmap[day, 22] = heatmap[day, 23] = worstWh;
        return new IdleHabits(heatmap, 22, 2, cost, kwh, "GBP");
    }

    [Fact]
    public void Before_the_first_read_every_card_says_it_is_reading()
    {
        var model = Model();

        model.Forecast.Ready.ShouldBeFalse();
        model.Forecast.Cost.ShouldBe("Reading the history");
        model.Unusual.ShouldBeEmpty();
        model.UnusualNote.ShouldBe("Reading the history");
        model.Habits.Ready.ShouldBeFalse();
        model.Carbon.Month.ShouldBe(Format.Missing);
        model.Alerts.ShouldBeEmpty();
    }

    [Fact]
    public void A_ready_forecast_shows_the_likely_bill_and_its_range()
    {
        _insights.Answer = Report(new BillForecast(18.00m, 16.40m, 19.75m, "GBP", 56, true));
        var model = Model();

        model.Show();

        var card = model.Forecast;
        (card.Ready, card.Cost, card.Range).ShouldBe((true, "£18.00", "Likely £16.40 to £19.75"));
        card.Note.ShouldBe("By the end of September, from 56 days of history");
        (card.Low, card.Projected, card.High).ShouldBe((16.40, 18.00, 19.75));
        card.Maximum.ShouldBeGreaterThan(19.75);
    }

    [Fact]
    public void A_forecast_before_a_week_of_data_says_so()
    {
        _insights.Answer = Report(BillForecast.NotReady(3, "GBP"));
        var model = Model();

        model.Show();

        (model.Forecast.Ready, model.Forecast.Cost, model.Forecast.Note).ShouldBe((false, "Needs a week of data", "3 of 7 days so far"));
        model.Forecast.Range.ShouldBe("");
    }

    [Fact]
    public void A_forecast_without_a_tariff_asks_for_one()
    {
        _insights.Answer = Report(new BillForecast(0, 0, 0, null, 30, true));
        var model = Model();

        model.Show();

        (model.Forecast.Ready, model.Forecast.Cost, model.Forecast.Note).ShouldBe((false, "No tariff set", "Set a tariff in Settings to see the likely bill"));
    }

    [Fact]
    public void Unusual_hours_are_listed_with_their_normal()
    {
        _insights.Answer = Report(anomalies:
        [
            new UsageAnomaly(new DateTimeOffset(2026, 9, 15, 14, 0, 0, TimeSpan.Zero), 0.42, 0.12, 3.5),
            new UsageAnomaly(new DateTimeOffset(2026, 9, 12, 3, 0, 0, TimeSpan.Zero), 0.2, 0, double.PositiveInfinity),
        ]);
        var model = Model();

        model.Show();

        model.UnusualNote.ShouldBe("2 hours this week used far more than usual");
        var first = model.Unusual[0];
        (first.When, first.Used, first.Normal, first.Times).ShouldBe(($"{new DateTime(2026, 9, 15).ToString("ddd d MMM", British)}, 14:00 to 15:00", "0.420 kWh", "Normally 0.120 kWh", "3.5 times normal"));
        (first.Kwh, first.NormalKwh).ShouldBe((0.42, 0.12));
        first.Maximum.ShouldBe(0.42);
        model.Unusual[1].Times.ShouldBe("Normally off");
        model.Unusual[1].Maximum.ShouldBe(0.42, "every row's chart is drawn to the same scale");
    }

    [Fact]
    public void Nothing_unusual_says_so()
    {
        _insights.Answer = Report();
        var model = Model();

        model.Show();

        model.UnusualNote.ShouldBe("Nothing unusual this week");
    }

    [Fact]
    public void The_bell_gets_todays_alerts_newest_first()
    {
        _insights.Answer = Report(anomalies:
        [
            new UsageAnomaly(new DateTimeOffset(2026, 9, 15, 16, 0, 0, TimeSpan.Zero), 0.5, 0.1, 5),
            new UsageAnomaly(new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero), 0.5, 0.1, 5),
            new UsageAnomaly(new DateTimeOffset(2026, 9, 14, 22, 0, 0, TimeSpan.Zero), 0.5, 0.1, 5),
        ]);
        var model = Model();
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.Show();

        model.Alerts.Select(a => a.Hour.Hour).ShouldBe([16, 9]);
        raised.ShouldContain(nameof(InsightsViewModel.Alerts));
    }

    [Fact]
    public void Habits_name_the_worst_window_and_the_saving()
    {
        _insights.Answer = Report(habits: Habits());
        var model = Model();

        model.Show();

        var card = model.Habits;
        card.Ready.ShouldBeTrue();
        card.Heatmap.ShouldNotBeNull()[1, 22].ShouldBe(100);
        (card.WorstStart, card.WorstHours).ShouldBe((22, 2));
        card.Worst.ShouldBe("Idle time costs most from 22:00 to 00:00");
        card.Saving.ShouldBe("Sleeping after 10 min idle would save about 5.58 kWh (£1.12) a month.");
    }

    [Fact]
    public void Habits_without_a_tariff_give_the_energy_alone()
    {
        _insights.Answer = Report(habits: Habits() with { Currency = null, SavingPerMonthCost = 0 });
        var model = Model();

        model.Show();

        model.Habits.Saving.ShouldBe("Sleeping after 10 min idle would save about 5.58 kWh a month.");
    }

    [Fact]
    public void Habits_with_no_idle_time_say_so()
    {
        _insights.Answer = Report(habits: Habits(worstWh: 0, cost: 0, kwh: 0));
        var model = Model();

        model.Show();

        (model.Habits.Ready, model.Habits.Worst, model.Habits.Saving).ShouldBe((true, "No idle time yet", ""));
    }

    [Fact]
    public void Habits_before_a_week_of_data_say_so()
    {
        _insights.Answer = Report();
        var model = Model();

        model.Show();

        (model.Habits.Ready, model.Habits.Worst).ShouldBe((false, "Needs a week of data"));
    }

    [Fact]
    public void Carbon_shows_the_kilograms_and_where_the_factor_came_from()
    {
        _insights.Answer = Report();
        var model = Model();

        model.Show();

        model.Carbon.ShouldBe(new CarbonCard("7.08 kg", "94.40 kg", "236 g CO₂ per kWh", "The United Kingdom's grid in 2023, from Ember"));
    }

    [Fact]
    public void Each_card_is_raised_when_a_read_comes_back()
    {
        _insights.Answer = Report();
        var model = Model();
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.Show();

        raised.ShouldBe(
            [nameof(InsightsViewModel.Report), nameof(InsightsViewModel.Forecast), nameof(InsightsViewModel.Unusual), nameof(InsightsViewModel.UnusualNote),
             nameof(InsightsViewModel.Alerts), nameof(InsightsViewModel.Habits), nameof(InsightsViewModel.Carbon)],
            ignoreOrder: true);
    }

    /// <summary>The read runs on the background thread, and one a newer read overtook is dropped.</summary>
    [Fact]
    public void Reads_run_off_the_ui_thread_and_an_overtaken_one_is_dropped()
    {
        var background = new Queue<Action>();
        var model = Model(new UiThreads(action => action(), background.Enqueue));
        _insights.Answer = Report(BillForecast.NotReady(1, "GBP"));

        model.Show();
        _insights.Answer = Report(BillForecast.NotReady(2, "GBP"));
        model.Refresh();
        _insights.Reads.ShouldBeEmpty();

        var (first, second) = (background.Dequeue(), background.Dequeue());
        second();
        _insights.Answer = Report(BillForecast.NotReady(1, "GBP"));
        first();

        model.Forecast.Note.ShouldBe("2 of 7 days so far");
    }
}
