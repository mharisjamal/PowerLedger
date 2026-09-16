using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class MonitorPowerTests
{
    [Fact]
    public void A_monitor_whose_brightness_is_unknown_draws_its_listed_figure()
    {
        MonitorPower.At(28.3, null).ShouldBe(28.3, 1e-9);
        MonitorPower.At(28.3, double.NaN).ShouldBe(28.3, 1e-9);
        MonitorPower.At(28.3, double.PositiveInfinity).ShouldBe(28.3, 1e-9);
    }

    [Fact]
    public void The_listed_figure_is_the_draw_at_three_quarters_brightness()
        => MonitorPower.At(28.3, 0.75).ShouldBe(28.3, 1e-9);

    [Fact]
    public void Full_brightness_draws_more_and_none_still_draws_the_fixed_share()
    {
        MonitorPower.At(28.3, 1).ShouldBe(28.3 * 1.0 / 0.8625, 1e-9);
        MonitorPower.At(28.3, 1).ShouldBe(32.81, 0.005);
        MonitorPower.At(28.3, 0).ShouldBe(28.3 * 0.45 / 0.8625, 1e-9);
        MonitorPower.At(28.3, 0).ShouldBe(14.77, 0.005);
    }

    [Fact]
    public void The_draw_rises_in_a_straight_line_with_brightness()
        => MonitorPower.At(20, 0.5).ShouldBe((MonitorPower.At(20, 0) + MonitorPower.At(20, 1)) / 2, 1e-9);

    [Fact]
    public void Brightness_outside_zero_to_one_is_clamped()
    {
        MonitorPower.At(28.3, 1.4).ShouldBe(MonitorPower.At(28.3, 1));
        MonitorPower.At(28.3, -0.2).ShouldBe(MonitorPower.At(28.3, 0));
    }

    [Fact]
    public void No_monitors_draw_nothing_with_the_display_on_or_off()
    {
        NoMonitors.Instance.Watts(displayOn: true).ShouldBe(0);
        NoMonitors.Instance.Watts(displayOn: false).ShouldBe(0);
    }
}
