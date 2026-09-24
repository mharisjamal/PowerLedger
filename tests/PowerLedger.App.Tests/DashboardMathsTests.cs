using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.App.Tests;

public class DashboardMathsTests
{
    private static DayTotals Day(int day, double kwh) => new(new DateOnly(2026, 8, day), kwh, 0.05m, "USD", false, 8, 60, 0.01, 0);

    [Fact]
    public void The_average_day_is_the_mean_of_the_days_given_and_none_is_nothing()
    {
        DashboardMaths.AverageDayWh([Day(1, 0.2), Day(2, 0.4), Day(4, 0.3)]).ShouldNotBeNull().ShouldBe(300, 1e-9);   // the 3rd, with no rows, is not a zero
        DashboardMaths.AverageDayWh([]).ShouldBeNull();
    }

    [Fact]
    public void Todays_trend_compares_so_far_with_the_average_day_up_to_the_same_time()
    {
        DashboardMaths.TodayTrend(180, 300, 0.5).ShouldNotBeNull().ShouldBe(0.2, 1e-9);      // 180 Wh by noon against 150 expected
        DashboardMaths.TodayTrend(120, 300, 0.5).ShouldNotBeNull().ShouldBe(-0.2, 1e-9);
        DashboardMaths.TodayTrend(300, 300, 1.5).ShouldNotBeNull().ShouldBe(0, 1e-9);        // a fraction past one is the whole day
        DashboardMaths.TodayTrend(180, null, 0.5).ShouldBeNull();
        DashboardMaths.TodayTrend(180, 0, 0.5).ShouldBeNull();
        DashboardMaths.TodayTrend(0, 300, 0).ShouldBeNull();
    }

    [Fact]
    public void A_bar_is_the_value_over_its_maximum_and_never_past_full()
    {
        DashboardMaths.Fill(30, 120).ShouldBe(0.25, 1e-9);
        DashboardMaths.Fill(150, 120).ShouldBe(1);
        DashboardMaths.Fill(-5, 120).ShouldBe(0);
        DashboardMaths.Fill(30, 0).ShouldBe(0);
        DashboardMaths.Fill(double.NaN, 120).ShouldBe(0);
    }

    [Fact]
    public void The_months_trend_is_against_last_month_and_none_without_one()
    {
        DashboardMaths.MonthTrend(210, 380).ShouldNotBeNull().ShouldBe(-170 / 380.0, 1e-9);
        DashboardMaths.MonthTrend(400, 200).ShouldNotBeNull().ShouldBe(1, 1e-9);
        DashboardMaths.MonthTrend(210, null).ShouldBeNull();
        DashboardMaths.MonthTrend(210, 0).ShouldBeNull();
    }

    [Theory]
    [InlineData(0.12, "Up")]
    [InlineData(-0.12, "Down")]
    [InlineData(0.004, "Flat")]
    [InlineData(-0.004, "Flat")]
    public void A_change_points_up_down_or_lies_flat(double change, string kind)
        => DashboardMaths.Kind(change).ToString().ShouldBe(kind);

    /// <summary>Energy is a cost: where lower is better a fall is the good news and a rise the bad, the arrow still
    /// pointing the way the figure went.</summary>
    [Theory]
    [InlineData("Up", false, "Good")]
    [InlineData("Down", false, "Bad")]
    [InlineData("Up", true, "Bad")]
    [InlineData("Down", true, "Good")]
    [InlineData("Flat", true, "Neutral")]
    [InlineData("Text", true, "Neutral")]
    [InlineData("Quality", false, "Neutral")]
    public void Whether_a_change_is_good_news_depends_on_whether_lower_is_better(string kind, bool lowerIsBetter, string sense)
        => DashboardMaths.Sense(Enum.Parse<TrendKind>(kind), lowerIsBetter).ToString().ShouldBe(sense);
}
