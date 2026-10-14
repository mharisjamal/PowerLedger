using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

[Collection(AeroMotionScope.Name)]
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

    /// <summary>Perf review P10: the glass is lighter under Windows' Energy Saver, and on battery when the owner's switch
    /// (Lighter glass on battery) is on; never otherwise.</summary>
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, true)]
    public void The_glass_is_lighter_under_energy_saver_or_on_battery_when_chosen(bool energySaver, bool onBattery, bool lighterOnBattery, bool lighter)
        => GlassPower.Lighten(energySaver, onBattery, lighterOnBattery).ShouldBe(lighter);

    /// <summary>Lighter, the glass's background refresh is at most 15 frames a second and its budget 2 % of a core: the pace
    /// never comes faster than a fifteenth of a second, and over 2 % it slows. Back to full, the display's pace comes back
    /// at once.</summary>
    [Fact]
    public void Lighter_the_glass_takes_at_most_fifteen_frames_a_second_within_two_percent_of_a_core()
    {
        LiquidGlassGovernor.Reset();
        try
        {
            GlassPower.EnergySaver = true;
            LiquidGlassGovernor.Interval.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1.0 / 15));
            LiquidGlassGovernor.Pace.ShouldBe(LiquidGlassGovernor.Fastest > LiquidGlassGovernor.LighterFastest ? LiquidGlassGovernor.Fastest : LiquidGlassGovernor.LighterFastest);
            LiquidGlassGovernor.CurrentBudget.ShouldBe(0.02);
            LiquidGlassGovernor.Next(LiquidGlassGovernor.Pace, 0.03, LiquidGlassGovernor.CurrentBudget, tookFrames: true, LiquidGlassGovernor.Pace)
                .ShouldBe(LiquidGlassGovernor.Pace * 1.5, "3 % is over the lighter budget");
            LiquidGlassGovernor.Next(LiquidGlassGovernor.Pace, 0.0, LiquidGlassGovernor.CurrentBudget, tookFrames: true, LiquidGlassGovernor.Pace)
                .ShouldBe(LiquidGlassGovernor.Pace, "never faster than a fifteenth of a second");

            GlassPower.EnergySaver = false;
            LiquidGlassGovernor.Interval.ShouldBe(LiquidGlassGovernor.Fastest, "the display's pace back at once");
            LiquidGlassGovernor.CurrentBudget.ShouldBe(LiquidGlassGovernor.Budget);

            GlassPower.OnBattery = true;
            LiquidGlassGovernor.Interval.ShouldBe(LiquidGlassGovernor.Fastest, "on battery, only when chosen");
            GlassPower.LighterOnBattery = true;
            LiquidGlassGovernor.Interval.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1.0 / 15));
        }
        finally
        {
            (GlassPower.EnergySaver, GlassPower.OnBattery, GlassPower.LighterOnBattery) = (false, false, false);
            GlassPower.Read();
            LiquidGlassGovernor.Reset();
        }
    }
}
