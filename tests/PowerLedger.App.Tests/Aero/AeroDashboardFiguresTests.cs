using System.Globalization;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Plan S D3: how Aero's Dashboard words its figures (Aero look design §1), pure: Power now in watts or cost an
/// hour, the change against yesterday, This month in cost or energy, its day of the month and the forecast's range.</summary>
public class AeroDashboardFiguresTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    [Fact]
    public void Power_now_is_whole_watts_or_the_cost_of_an_hour_at_the_tariff()
    {
        DashboardFigures.PowerNow(34.4, cost: false, 0.17m, "USD", English).ShouldBe(("34", "W"));
        DashboardFigures.PowerNow(34.4, cost: true, 0.17m, "USD", English).ShouldBe(("$0.006", "an hour"));
        DashboardFigures.PowerNow(double.NaN, cost: false, 0.17m, "USD", English).ShouldBe((Format.NoReading, ""));
        DashboardFigures.PowerNow(34.4, cost: true, null, null, English).ShouldBe(("34", "W"), "no tariff, no cost");
    }

    [Theory]
    [InlineData(0.42, "+42%")]
    [InlineData(-0.081, "-8%")]
    [InlineData(0.001, "0%")]
    [InlineData(null, "N/A")]
    public void The_change_against_yesterday_carries_its_sign(double? change, string text)
        => DashboardFigures.Change(change, English).ShouldBe(text);

    [Fact]
    public void This_month_reads_as_cost_or_as_energy_with_the_other_under_it()
    {
        var month = MonthLedger.Empty with { Energy = "2.74", Cost = "$0.47", Projected = "$1.60", ProjectedEnergy = "9.31 kWh" };

        DashboardFigures.MonthBig(month, energy: false).ShouldBe("$0.47");
        DashboardFigures.MonthSub(month, energy: false).ShouldBe("2.74 kWh so far, on track for $1.60");
        DashboardFigures.MonthBig(month, energy: true).ShouldBe("2.74 kWh");
        DashboardFigures.MonthSub(month, energy: true).ShouldBe("$0.47 so far, on track for 9.31 kWh");
        DashboardFigures.MonthSub(MonthLedger.Empty with { Energy = "0.10", Cost = "N/A", Projected = "N/A" }, energy: false)
            .ShouldBe("0.10 kWh so far");
    }

    [Fact]
    public void The_month_bar_says_which_day_it_is() => DashboardFigures.DayOf(8, 30).ShouldBe("Day 8 of 30");

    [Fact]
    public void The_forecast_gives_its_range_once_it_has_a_week()
    {
        DashboardFigures.Forecast(new BillForecast(6.8m, 6.1m, 7.4m, "USD", 20, true), English).ShouldBe("Likely $6.10 to $7.40 by the month's end");
        DashboardFigures.Forecast(BillForecast.NotReady(3, "USD"), English).ShouldBe("Needs a week of data");
        DashboardFigures.Forecast(new BillForecast(0, 0, 0, null, 20, true, 41.2, 38.5, 44.9), English).ShouldBe("Likely 38.5 to 44.9 kWh by the month's end");
        DashboardFigures.Forecast(null, English).ShouldBeNull();
    }
}
