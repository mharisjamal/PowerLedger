using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

public class LiquidGlassGovernorTests
{
    private static readonly TimeSpan Thirtieth = LiquidGlassGovernor.Fastest;

    [Fact]
    public void Over_the_budget_while_taking_frames_the_glass_slows_by_half_down_to_one_a_second()
    {
        LiquidGlassGovernor.Next(Thirtieth, 0.12, 0.08, tookFrames: true).ShouldBe(Thirtieth * 1.5);
        LiquidGlassGovernor.Next(TimeSpan.FromMilliseconds(900), 0.5, 0.08, tookFrames: true).ShouldBe(LiquidGlassGovernor.Slowest);
    }

    [Fact]
    public void Over_the_budget_without_frames_the_glass_is_not_to_blame_and_keeps_its_pace()
        => LiquidGlassGovernor.Next(Thirtieth * 2, 0.5, 0.08, tookFrames: false).ShouldBe(Thirtieth * 2);

    [Fact]
    public void Well_under_the_budget_it_speeds_up_by_a_quarter_up_to_thirty_a_second()
    {
        LiquidGlassGovernor.Next(TimeSpan.FromMilliseconds(500), 0.02, 0.08, tookFrames: true).ShouldBe(TimeSpan.FromMilliseconds(400));
        LiquidGlassGovernor.Next(Thirtieth, 0.0, 0.08, tookFrames: true).ShouldBe(Thirtieth);
    }

    [Fact]
    public void Just_under_the_budget_it_holds()
        => LiquidGlassGovernor.Next(TimeSpan.FromMilliseconds(100), 0.07, 0.08, tookFrames: true).ShouldBe(TimeSpan.FromMilliseconds(100));
}
