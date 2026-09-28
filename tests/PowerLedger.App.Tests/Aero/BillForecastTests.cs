using Shouldly;
using static PowerLedger.App.Tests.InsightsSeries;

namespace PowerLedger.App.Tests;

/// <summary>Aero look design §4, Bill forecast: the rest of the month as the median of the same weekday, priced at the
/// month's average price, with a 10th to 90th percentile band from backtested errors.</summary>
public class BillForecastTests
{
    private static readonly DateOnly Today = new(2026, 9, 15);   // a Tuesday; September has 30 days

    [Fact]
    public void A_flat_history_forecasts_the_same_every_day_with_no_band()
    {
        var days = Days(Today.AddDays(-30), 30, _ => 2);

        var forecast = BillForecasts.From(days, Today, todayLeft: 1, monthKwh: 28, price: 0.20m, currency: "GBP");

        // 14 days used 28 kWh; today and the 15 after it at 2 kWh each: 60 kWh at 0.20.
        forecast.ShouldBe(new BillForecast(12.00m, 12.00m, 12.00m, "GBP", 30, true));
    }

    [Fact]
    public void Weekdays_and_weekends_are_each_forecast_from_their_own_weekday()
    {
        var days = Days(Today.AddDays(-56), 56, d => Weekend(d.DayOfWeek) ? 1 : 3);

        var forecast = BillForecasts.From(days, Today, 1, 14 * 2.4, 1m, "USD");

        // 15 Sep to 30 Sep: 12 weekdays and 4 weekend days.
        var rest = 12 * 3 + 4 * 1;
        forecast.ProjectedCost.ShouldBe(decimal.Round((decimal)(14 * 2.4 + rest), 2));
        (forecast.Low, forecast.High).ShouldBe((forecast.ProjectedCost, forecast.ProjectedCost));
        forecast.DaysOfData.ShouldBe(56);
    }

    [Fact]
    public void Only_the_last_eight_weeks_count()
    {
        var days = Days(Today.AddDays(-84), 84, d => d < Today.AddDays(-56) ? 50 : 2);

        var forecast = BillForecasts.From(days, Today, 1, 28, 1m, "USD");

        forecast.ProjectedCost.ShouldBe(60m);
        forecast.DaysOfData.ShouldBe(56);
    }

    [Fact]
    public void The_rest_of_today_counts_for_the_part_of_it_left()
    {
        var days = Days(Today.AddDays(-30), 30, _ => 2);

        var forecast = BillForecasts.From(days, Today, todayLeft: 0.25, monthKwh: 29.5, price: 1m, currency: "USD");

        // 29.5 so far, a quarter of today's 2, then 15 more days of 2.
        forecast.ProjectedCost.ShouldBe(60m);
    }

    [Fact]
    public void A_spike_day_barely_moves_the_forecast_but_widens_the_band_upwards()
    {
        var days = Days(Today.AddDays(-56), 56, d => d == Today.AddDays(-9) ? 20 : 2);

        var forecast = BillForecasts.From(days, Today, 1, 28, 1m, "USD");

        forecast.ProjectedCost.ShouldBe(60m);
        forecast.Low.ShouldBe(60m);
        forecast.High.ShouldBeGreaterThanOrEqualTo(60m);
    }

    [Fact]
    public void Fewer_than_seven_days_is_not_ready()
    {
        var forecast = BillForecasts.From(Days(Today.AddDays(-6), 6, _ => 2), Today, 1, 12, 1m, "EUR");

        forecast.ShouldBe(BillForecast.NotReady(6, "EUR"));
    }

    [Fact]
    public void Seven_days_is_ready()
        => BillForecasts.From(Days(Today.AddDays(-7), 7, _ => 2), Today, 1, 14, 1m, "EUR").Ready.ShouldBeTrue();

    [Fact]
    public void A_weekday_never_seen_takes_the_month_to_date_daily_mean()
    {
        // Three weeks with no Sundays (the PC was away on Sundays): August's days at 6 kWh, September's at 4. Every other
        // weekday's median is 4 (6, 4, 4); a Sunday takes September's mean, 4, not the mean of all 18 days, 4.67.
        var days = Days(Today.AddDays(-21), 21, d => d.Month == Today.Month ? 4 : 6).Where(d => d.Day.DayOfWeek != DayOfWeek.Sunday).ToList();

        var forecast = BillForecasts.From(days, Today, 1, 48, 1m, "USD");

        forecast.ProjectedCost.ShouldBe(48m + 16 * 4);
    }

    [Fact]
    public void With_no_tariff_the_figures_are_zero_and_the_forecast_still_ready()
    {
        var forecast = BillForecasts.From(Days(Today.AddDays(-30), 30, _ => 2), Today, 1, 28, 0m, null);

        forecast.ShouldBe(new BillForecast(0, 0, 0, null, 30, true));
    }

    [Fact]
    public void The_same_data_always_gives_the_same_band()
    {
        var random = new Random(3);
        var days = Days(Today.AddDays(-56), 56, _ => 2 + random.NextDouble());

        BillForecasts.From(days, Today, 1, 30, 1m, "USD").ShouldBe(BillForecasts.From(days, Today, 1, 30, 1m, "USD"));
    }

    [Fact]
    public void The_band_holds_the_forecast_and_never_falls_under_what_the_month_has_used()
    {
        var random = new Random(5);
        var days = Days(Today.AddDays(-56), 56, _ => Math.Max(0, 2 + random.Gaussian()));

        var forecast = BillForecasts.From(days, Today, 1, 30, 1m, "USD");

        forecast.Low.ShouldBeLessThanOrEqualTo(forecast.ProjectedCost);
        forecast.High.ShouldBeGreaterThanOrEqualTo(forecast.ProjectedCost);
        forecast.Low.ShouldBeGreaterThanOrEqualTo(30m);
        (forecast.High - forecast.Low).ShouldBeGreaterThan(0m);
    }

    /// <summary>Design §7: on synthetic histories (a weekday pattern, a slow drift and day-to-day noise), the band holds
    /// the month's actual cost in about 80 % of backtests.</summary>
    [Fact]
    public void The_band_covers_the_actual_month_in_about_eighty_percent_of_backtests()
    {
        var random = new Random(20260928);
        const int trials = 400;
        var covered = 0;
        for (var trial = 0; trial < trials; trial++)
        {
            var month = new DateOnly(2025, 1, 1).AddMonths(trial % 24);
            var today = month.AddDays(random.Next(0, 26));
            var level = 1 + random.NextDouble() * 4;
            var weekend = 0.5 + random.NextDouble();
            var noise = 0.1 + random.NextDouble() * 0.2;
            var first = today.AddDays(-56);
            var end = month.AddMonths(1);
            var all = Days(first, end.DayNumber - first.DayNumber, d =>
                Math.Max(0, level * (Weekend(d.DayOfWeek) ? weekend : 1) * (1 + noise * random.Gaussian())));
            var history = all.Where(d => d.Day < today).ToList();
            var monthSoFar = history.Where(d => d.Day >= month).Sum(d => d.Kwh);
            var actual = (decimal)all.Where(d => d.Day >= month).Sum(d => d.Kwh);

            var forecast = BillForecasts.From(history, today, 1, monthSoFar, 1m, "USD");

            if (actual >= forecast.Low - 0.005m && actual <= forecast.High + 0.005m) covered++;
        }

        ((double)covered / trials).ShouldBeInRange(0.75, 0.88);
    }
}
