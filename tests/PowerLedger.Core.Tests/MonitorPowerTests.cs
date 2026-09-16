using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class MonitorPowerTests
{
    [Fact]
    public void A_monitor_whose_brightness_is_unknown_is_assumed_at_three_quarters_where_a_figure_taken_there_is_drawn_as_listed()
    {
        MonitorPower.At(28.3, null, 0.75).ShouldBe(28.3, 1e-9);
        MonitorPower.At(28.3, double.NaN, 0.75).ShouldBe(28.3, 1e-9);
        MonitorPower.At(28.3, double.PositiveInfinity, 0.75).ShouldBe(28.3, 1e-9);
        MonitorPower.At(28.3, null, 0.5).ShouldBe(MonitorPower.At(28.3, 0.75, 0.5), 1e-9);
    }

    [Fact]
    public void The_listed_figure_is_the_draw_at_the_brightness_it_was_taken_at()
    {
        MonitorPower.At(28.3, 0.75, 0.75).ShouldBe(28.3, 1e-9);
        MonitorPower.At(28.3, 0.5, 0.5).ShouldBe(28.3, 1e-9);
        MonitorPower.At(28.3, 1, 1).ShouldBe(28.3, 1e-9);
    }

    [Fact]
    public void Full_brightness_draws_more_and_none_still_draws_the_fixed_share()
    {
        MonitorPower.At(28.3, 1, 0.75).ShouldBe(28.3 * 1.0 / 0.8625, 1e-9);
        MonitorPower.At(28.3, 1, 0.75).ShouldBe(32.81, 0.005);
        MonitorPower.At(28.3, 0, 0.75).ShouldBe(28.3 * 0.45 / 0.8625, 1e-9);
        MonitorPower.At(28.3, 0, 0.75).ShouldBe(14.77, 0.005);
    }

    [Fact]
    public void The_draw_rises_in_a_straight_line_with_brightness()
        => MonitorPower.At(20, 0.5, 0.75).ShouldBe((MonitorPower.At(20, 0, 0.75) + MonitorPower.At(20, 1, 0.75)) / 2, 1e-9);

    [Fact]
    public void Brightness_outside_zero_to_one_is_clamped()
    {
        MonitorPower.At(28.3, 1.4, 0.75).ShouldBe(MonitorPower.At(28.3, 1, 0.75));
        MonitorPower.At(28.3, -0.2, 0.75).ShouldBe(MonitorPower.At(28.3, 0, 0.75));
    }

    [Theory]
    [InlineData(200.0, 1.0)]
    [InlineData(400.0, 0.5)]
    [InlineData(250.0, 0.8)]
    [InlineData(150.0, 1.0)]        // it can't reach 200 nits, so it was tested at its brightest
    [InlineData(8000.0, 0.05)]
    [InlineData(null, 0.75)]
    [InlineData(0.0, 0.75)]
    [InlineData(-300.0, 0.75)]
    [InlineData(double.NaN, 0.75)]
    [InlineData(double.PositiveInfinity, 0.75)]
    public void The_list_s_figure_was_taken_where_200_nits_sits_on_a_monitor_s_brightness_scale(double? maxNits, double anchor)
        => MonitorPower.Anchor(maxNits).ShouldBe(anchor, 1e-12);

    [Fact]
    public void A_figure_taken_at_200_nits_draws_more_at_the_assumed_brightness_on_a_400_nit_monitor_and_less_on_a_200_nit_one()
    {
        // 20 W at 200 cd/m²: half way up a 400-nit monitor's scale, and at the top of a 200-nit one's.
        MonitorPower.At(20, 0.5, MonitorPower.Anchor(400)).ShouldBe(20, 1e-9);
        MonitorPower.At(20, null, MonitorPower.Anchor(400)).ShouldBe(20 * 0.8625 / 0.725, 1e-9);
        MonitorPower.At(20, null, MonitorPower.Anchor(400)).ShouldBe(23.79, 0.005);
        MonitorPower.At(20, null, MonitorPower.Anchor(200)).ShouldBe(17.25, 1e-9);
        MonitorPower.At(20, null, MonitorPower.Anchor(null)).ShouldBe(20, 1e-9);
    }

    [Fact]
    public void No_monitors_draw_nothing_with_the_display_on_or_off()
    {
        NoMonitors.Instance.Watts(displayOn: true).ShouldBe(new MonitorWatts(OwnPlug: 0, FromPc: 0));
        NoMonitors.Instance.Watts(displayOn: false).ShouldBe(new MonitorWatts(OwnPlug: 0, FromPc: 0));
    }

    [Fact]
    public void What_monitors_draw_in_all_is_what_they_draw_from_their_own_plugs_and_from_the_pc()
        => new MonitorWatts(OwnPlug: 25, FromPc: 6.2).Total.ShouldBe(31.2, 1e-9);
}
