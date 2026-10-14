using PowerLedger.Core;
using PowerLedger.Storage;
using Shouldly;
using static PowerLedger.App.Tests.InsightsSeries;

namespace PowerLedger.App.Tests;

/// <summary>Aero look design §4: the <see cref="Insights"/> over the history, one read of 8 weeks of hour rows giving all
/// four findings, and nothing thrown for want of data.</summary>
public class InsightsTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 15);

    private readonly FakeRangeHistory _history = new();

    private Insights Make(double kgPerKwh = 0.236, string? region = "GB") => new(_history, () => kgPerKwh, region);

    /// <summary>The history answering from <paramref name="hours"/>: hour buckets for the hour read, totals for the rest,
    /// priced at <paramref name="price"/>, with <paramref name="tariff"/> as the one in force at the range's end.</summary>
    private void Holds(IReadOnlyList<HourUse> hours, TimeZoneInfo zone, decimal price = 0.25m, string? currency = "GBP", Tariff? tariff = null)
    {
        _history.First = hours.FirstOrDefault(h => h.Seen) is { Seen: true } first ? DateOnly.FromDateTime(first.Local) : null;
        _history.Answer = range =>
        {
            var inRange = hours.Where(h => h.Start >= range.From && h.Start < range.To).ToList();
            var kwh = inRange.Sum(h => h.Kwh);
            var series = new List<Aggregate>();
            if (range.Bucket == TimeSpan.FromHours(1))
            {
                var rows = Rows(inRange).ToDictionary(r => r.Start);
                for (var start = range.From; start < range.To; start += range.Bucket)
                    series.Add(rows.TryGetValue(start, out var row) ? row : Aggregate.Empty(start));
            }
            var days = inRange.Where(h => h.Seen).GroupBy(h => DateOnly.FromDateTime(h.Local)).Select(day => new DayTotals(
                day.Key, day.Sum(h => h.Kwh), (decimal)day.Sum(h => h.Kwh) * price, currency, false, 24, 100, 0, 0)).ToList();
            return new RangeReport(range, Reports.Totals(range, kwh) with { Cost = (decimal)kwh * price, Currency = currency }, days, series, tariff);
        };
    }

    /// <summary>Report finding 4: a PC shut down at weekends has no rows then, and those days used nothing; skipping them
    /// forecast the month's weekends at the weekdays' use.</summary>
    [Fact]
    public void Days_after_the_first_with_no_rows_count_as_days_that_used_nothing()
    {
        Holds(Hours(Today.AddDays(-56), 57, Utc, local => Weekend(local.DayOfWeek) ? null : 0.1), Utc);

        var report = Make().Read(Now, Utc);

        // 1 to 14 September: 10 weekdays at 2.4 kWh, and 12 hours of today (25.2 kWh). Then half of today and the 11 weekdays
        // left at 2.4 (27.6), and the 4 weekend days at nothing: 52.8 kWh at 0.25.
        report.Forecast.ShouldBe(new BillForecast(13.20m, 13.20m, 13.20m, "GBP", 56, true, 52.8, 52.8, 52.8));
    }

    /// <summary>Report finding 19: the rest of today is what recent days used after this time of day. A PC used only in the
    /// evening has all of its day still to come at noon, not half of it.</summary>
    [Fact]
    public void The_rest_of_today_follows_the_shape_of_recent_days()
    {
        Holds(Hours(Today.AddDays(-56), 57, Utc, local => local.Hour >= 18 ? 0.4 : 0), Utc);

        var report = Make().Read(Now, Utc);

        // 14 days of 2.4 kWh, nothing yet today, then all of today and the 15 days after it: 72 kWh at 0.25.
        report.Forecast.ShouldBe(new BillForecast(18.00m, 18.00m, 18.00m, "GBP", 56, true, 72, 72, 72));
    }

    /// <summary>Report finding 4: after a tariff change the days left cost today's price, and the month so far what it did.</summary>
    [Fact]
    public void The_days_left_are_priced_at_today_s_tariff()
    {
        Holds(Hours(Today.AddDays(-56), 57, Utc, _ => 0.1), Utc, tariff: new Tariff(Now.AddDays(-2), 0.50m, "GBP"));

        var report = Make().Read(Now, Utc);

        // 34.8 kWh so far at 0.25 (8.70), then 37.2 kWh at 0.50 (18.60).
        report.Forecast.ShouldBe(new BillForecast(27.30m, 27.30m, 27.30m, "GBP", 56, true, 72, 72, 72));
    }

    /// <summary>Plan AC review: the days the forecast and its band are worked from count as the month so far does, energy
    /// from the battery at the wall equivalent and the energy drawn asleep included; at the pack they forecast a laptop's
    /// days left about a fifth low against a month so far counted in full.</summary>
    [Fact]
    public void The_days_count_as_the_month_so_far_does()
    {
        var perDay = (Aggregate.Empty(Now) with { EnergyWh = 100, BatteryWh = 100, AsleepWh = 5 }).CountedWh * 24 / 1000;
        var hours = Hours(Today.AddDays(-56), 57, Utc, _ => 0.1);
        _history.First = Today.AddDays(-56);
        _history.Answer = range =>
        {
            var rows = Rows(hours.Where(h => h.Start >= range.From && h.Start < range.To))
                .Select(r => r with { BatteryWh = r.EnergyWh, AsleepWh = 5 }).ToList();
            var kwh = rows.Sum(r => r.CountedWh) / 1000;
            var days = rows.GroupBy(r => DateOnly.FromDateTime(r.Start.UtcDateTime)).Select(day => new DayTotals(
                day.Key, day.Sum(r => r.CountedWh) / 1000, (decimal)(day.Sum(r => r.CountedWh) / 1000) * 0.25m, "GBP", false, 24, 100, 0, 0)).ToList();
            var series = range.Bucket == TimeSpan.FromHours(1) ? rows : [];
            return new RangeReport(range, Reports.Totals(range, kwh) with { Cost = (decimal)kwh * 0.25m, Currency = "GBP" }, days, series, null);
        };

        var forecast = Make().Read(Now, Utc).Forecast;

        // 14 and a half days so far, then the other half of today and 15 more days, each at the same counted energy.
        forecast.ProjectedKwh.ShouldBe(30 * perDay, 1e-3);
        (forecast.LowKwh, forecast.HighKwh).ShouldBe((forecast.ProjectedKwh, forecast.ProjectedKwh));
    }

    /// <summary>Plan AC review: an hour's idle energy is on the scale of its energy, idle on battery at its wall equivalent,
    /// since the habits saving prices it at the same tariff.</summary>
    [Fact]
    public void An_hour_s_idle_energy_counts_as_its_energy_does()
    {
        var row = Aggregate.Empty(Now) with { EnergyWh = 100, BatteryWh = 100, IdleOnWh = 40, IdleOffWh = 10 };

        HourUse.From(row, Utc).IdleKwh.ShouldBe(50 * BatteryRoundTrip.Factor / 1000, 1e-12);
        HourUse.From(row with { BatteryWh = 0 }, Utc).IdleKwh.ShouldBe(0.05, 1e-12);
    }

    [Fact]
    public void A_history_that_cannot_be_read_says_nothing_yet_and_does_not_throw()
    {
        _history.Answer = _ => null;

        var report = Make().Read(Now, Utc);

        report.Forecast.ShouldBe(BillForecast.NotReady(0, null));
        report.Anomalies.ShouldBeEmpty();
        report.Habits.ShouldBeNull();
        (report.Carbon.MonthKg, report.Carbon.SinceStartKg, report.Carbon.GramsPerKwh).ShouldBe((0.0, 0.0, 236.0));
    }

    [Fact]
    public void An_empty_history_says_nothing_yet()
    {
        Holds(Hours(Today.AddDays(-56), 57, Utc, _ => null), Utc);

        var report = Make().Read(Now, Utc);

        report.Forecast.ShouldBe(BillForecast.NotReady(0, "GBP"));
        report.Anomalies.ShouldBeEmpty();
        report.Habits.ShouldBeNull();
    }

    [Fact]
    public void One_read_of_eight_weeks_of_hour_rows_to_now()
    {
        Holds(Hours(Today.AddDays(-56), 57, Utc, _ => 0.1), Utc);

        Make().Read(Now, Utc);

        var read = _history.Reads.ShouldHaveSingleItem();
        (read.From, read.To, read.Bucket).ShouldBe((new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero), Now, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Three_days_of_history_is_not_a_forecast_yet()
    {
        Holds(Hours(Today.AddDays(-3), 4, Utc, _ => 0.1), Utc);

        var report = Make().Read(Now, Utc);

        report.Forecast.ShouldBe(BillForecast.NotReady(3, "GBP"));
        report.Habits.ShouldBeNull();
    }

    [Fact]
    public void A_first_day_joined_late_is_not_counted_as_a_whole_day()
    {
        // Installed at 10:00 on 8 September: the 8th is short, so there are 6 whole days, not 7.
        var installed = new DateTime(2026, 9, 8, 10, 0, 0);
        Holds(Hours(Today.AddDays(-56), 57, Utc, local => local >= installed ? 0.1 : null), Utc);

        Make().Read(Now, Utc).Forecast.ShouldBe(BillForecast.NotReady(6, "GBP"));
    }

    [Fact]
    public void Eight_steady_weeks_give_all_four_findings()
    {
        Holds(Hours(Today.AddDays(-56), 57, Utc, _ => 0.1, local => local.Hour >= 22 ? (0.05, 3600) : (0, 0)), Utc);

        var report = Make().Read(Now, Utc);

        // 2.4 kWh a day at 0.25: 14 days and 12 hours so far (34.8 kWh), then half of today and 15 more days (37.2): 72 kWh.
        report.Forecast.ShouldBe(new BillForecast(18.00m, 18.00m, 18.00m, "GBP", 56, true, 72, 72, 72));
        report.Anomalies.ShouldBeEmpty();
        var habits = report.Habits.ShouldNotBeNull();
        (habits.WorstWindowStart, habits.Currency).ShouldBe((22, "GBP"));
        habits.Heatmap[(int)DayOfWeek.Monday, 23].ShouldBe(50, 1e-9);
        report.Carbon.MonthKg.ShouldBe(34.8 * 0.236, 1e-6);
        report.Carbon.SinceStartKg.ShouldBe((56 * 2.4 + 1.2) * 0.236, 1e-6);
        report.Carbon.Source.ShouldBe("The United Kingdom's grid in 2023, from Ember");
    }

    /// <summary>Report finding 5: the saving follows the service's idle threshold; at 30 minutes, a PC that slept after 10
    /// would be asleep before any idle time counted, so all of it is saved.</summary>
    [Fact]
    public void The_habits_saving_follows_the_service_s_idle_setup()
    {
        Holds(Hours(Today.AddDays(-56), 57, Utc, _ => 0.1, local => local.Hour >= 22 ? (0.05, 3600) : (0, 0)), Utc);

        var report = new Insights(_history, () => 0.236, "GB", () => new IdleSetup(TimeSpan.FromMinutes(30), AsleepW: 0)).Read(Now, Utc);

        report.Habits.ShouldNotBeNull().SavingPerMonthKwh.ShouldBe(0.1 * 365.25 / 12, 1e-9);
    }

    [Fact]
    public void A_spike_yesterday_is_found()
    {
        var spike = new DateTime(2026, 9, 14, 20, 0, 0);
        Holds(Hours(Today.AddDays(-56), 57, Utc, local => local == spike ? 0.5 : 0.1), Utc);

        var found = Make().Read(Now, Utc).Anomalies.ShouldHaveSingleItem();

        (found.Hour, found.Kwh, found.NormalKwh).ShouldBe((new DateTimeOffset(spike, TimeSpan.Zero), 0.5, 0.1));
    }

    [Fact]
    public void History_before_the_eight_weeks_counts_toward_carbon_since_the_start()
    {
        Holds(Hours(Today.AddDays(-100), 101, Utc, _ => 0.1), Utc);

        var report = Make(kgPerKwh: 0.5).Read(Now, Utc);

        report.Carbon.SinceStartKg.ShouldBe((100 * 2.4 + 1.2) * 0.5, 1e-6);
        _history.Reads.Count.ShouldBe(2);
        (_history.Reads[1].From, _history.Reads[1].To).ShouldBe((new DateTimeOffset(2026, 6, 7, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void The_factor_is_read_again_at_every_read()
    {
        Holds(Hours(Today.AddDays(-56), 57, Utc, _ => 0.1), Utc);
        var factor = 0.2;
        var insights = new Insights(_history, () => factor, "GB");

        insights.Read(Now, Utc).Carbon.GramsPerKwh.ShouldBe(200, 1e-9);
        factor = 0.4;
        insights.Read(Now, Utc).Carbon.GramsPerKwh.ShouldBe(400, 1e-9);
    }

    [Fact]
    public void Without_a_tariff_the_costs_are_zero()
    {
        Holds(Hours(Today.AddDays(-56), 57, Utc, _ => 0.1), Utc, price: 0, currency: null);

        var report = Make().Read(Now, Utc);

        report.Forecast.ShouldBe(new BillForecast(0, 0, 0, null, 56, true, 72, 72, 72), "the energy is forecast all the same");
    }

    /// <summary>London, the day after the clocks went back (25 October 2026, a 25-hour day): the days still count whole,
    /// and the 25-hour day's extra hour counts in its energy.</summary>
    [Fact]
    public void Clock_changes_leave_the_days_whole()
    {
        var now = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        Holds(Hours(new DateOnly(2026, 8, 31), 57, London, _ => 0.1), London);

        var report = Make().Read(now, London);

        report.Forecast.DaysOfData.ShouldBe(56);
        report.Forecast.Ready.ShouldBeTrue();
        report.Anomalies.ShouldBeEmpty();
        report.Habits.ShouldNotBeNull();
        // October so far: 24 days of 2.4 kWh, the 25th's 2.5, and 12 hours of the 26th.
        report.Carbon.MonthKg.ShouldBe((24 * 2.4 + 2.5 + 1.2) * 0.236, 1e-6);
    }
}
