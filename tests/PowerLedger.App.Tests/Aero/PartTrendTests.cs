using System.Globalization;
using System.Windows;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Plan S, P2: the Parts page's 7-day trend, a small line drawn from <see cref="DashboardPart.Last7DaysWh"/>.</summary>
public class PartTrendTests
{
    [Fact]
    public void The_days_spread_evenly_from_side_to_side_with_nothing_at_the_foot_and_the_most_at_the_top()
    {
        var points = PartTrend.Points([0, 50, 100, 25, 75, 100, 0], 100, 20, 2);

        points.Count.ShouldBe(7);
        points.Select(p => p.X).ShouldBe(new[] { 2.0, 18, 34, 50, 66, 82, 98 }, 1e-9);
        points[0].Y.ShouldBe(18, 1e-9);                               // nothing: the foot, less the pad
        points[2].Y.ShouldBe(2, 1e-9);                                // the most: the top, less the pad
        points[1].Y.ShouldBe(10, 1e-9);                               // half: halfway, so a small change looks small
    }

    [Fact]
    public void A_week_of_nothing_lies_along_the_foot_and_what_isnt_a_figure_counts_as_nothing()
    {
        PartTrend.Points([0, 0, 0], 60, 20, 2).ShouldAllBe(p => Math.Abs(p.Y - 18) < 1e-9);
        var points = PartTrend.Points([double.NaN, -5, 40, double.PositiveInfinity], 60, 20, 2);
        points.Select(p => p.Y).ShouldBe(new[] { 18.0, 18, 2, 18 }, 1e-9);
    }

    [Fact]
    public void Fewer_than_two_days_draw_no_line()
    {
        PartTrend.Points([], 60, 20, 2).ShouldBeEmpty();
        PartTrend.Points([12], 60, 20, 2).ShouldBeEmpty();
        PartTrend.Points([12, 14], 0, 20, 2).ShouldBeEmpty();         // not laid out yet
    }

    [Fact]
    public void It_describes_itself_for_a_screen_reader_in_words()
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        PartTrend.Describe([120, 340, 280], culture).ShouldBe("Each day over the last 3 days: 0.120 to 0.340 kWh, 0.280 kWh on the last.");
        PartTrend.Describe([], culture).ShouldBe("No days recorded yet.");
        PartTrend.Describe([double.NaN], culture).ShouldBe("One day so far: 0.000 kWh.");
    }
}
