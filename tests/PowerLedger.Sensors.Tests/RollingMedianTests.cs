using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class RollingMedianTests
{
    [Fact]
    public void An_empty_window_has_no_median()
        => new RollingMedian(5).Median.ShouldBeNull();

    [Fact]
    public void An_odd_count_takes_the_middle_value_and_an_even_count_averages_the_pair()
    {
        var window = new RollingMedian(5);
        window.Add(10);
        window.Add(30);
        window.Add(20);
        window.Median.ShouldBe(20);
        window.Add(40);
        window.Median.ShouldBe(25);
    }

    [Fact]
    public void The_window_forgets_the_oldest_value_once_it_is_full()
    {
        var window = new RollingMedian(3);
        window.Add(1);
        window.Add(2);
        window.Add(3);
        window.Median.ShouldBe(2);
        window.Add(100);                 // drops the 1
        window.Median.ShouldBe(3);
        window.Count.ShouldBe(3);
    }

    [Fact]
    public void Adding_does_not_disturb_the_order_of_later_reads()
    {
        var window = new RollingMedian(4);
        foreach (var value in new double[] { 5, 1, 4, 2 }) window.Add(value);
        window.Median.ShouldBe(3);
        window.Median.ShouldBe(3);
    }

    [Fact]
    public void A_window_must_hold_at_least_one_value()
        => Should.Throw<ArgumentOutOfRangeException>(() => new RollingMedian(0));
}
