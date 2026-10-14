using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

/// <summary>Accuracy A10: what a sleep costs, learned from the battery's charge before and after each sleep on battery.</summary>
public class SleepDrainTests
{
    private static BatteryCharge Charge(double wh, bool onAc = false, double full = 50) => new(onAc, wh, full);

    [Fact]
    public void Eight_hours_asleep_on_battery_that_cost_four_watt_hours_teach_half_a_watt()
    {
        var learned = SleepDrain.None.Learn(Charge(40), Charge(36), TimeSpan.FromHours(8));
        learned.Watts.ShouldNotBeNull().ShouldBe(0.5, 1e-9);
    }

    [Fact]
    public void Nothing_is_known_before_two_hours_of_sleep_have_been_seen()
    {
        var learned = SleepDrain.None.Learn(Charge(40), Charge(39.9), TimeSpan.FromMinutes(90));
        learned.Watts.ShouldBeNull();
        learned.Learn(Charge(39.9), Charge(39.8), TimeSpan.FromMinutes(40)).Watts.ShouldNotBeNull().ShouldBe(0.2 / (130 / 60.0), 0.001);
    }

    [Theory]
    [InlineData(true, false, 40, 36, 50, 50, 480)]      // on AC as it went to sleep
    [InlineData(false, true, 40, 36, 50, 50, 480)]      // on AC as it woke
    [InlineData(false, false, 40, 36, 50, 50, 9)]       // under ten minutes
    [InlineData(false, false, 36, 40, 50, 50, 480)]     // the charge rose
    [InlineData(false, false, 40, 36, 50, 60, 480)]     // a different battery
    [InlineData(false, false, 40, 0, 50, 50, 120)]      // 20 W asleep: no sleep at all
    public void Readings_that_say_nothing_of_a_sleep_are_dropped(bool acBefore, bool acAfter, double before, double after, double fullBefore, double fullAfter, int minutes)
    {
        var known = SleepDrain.None.Learn(Charge(40), Charge(36), TimeSpan.FromHours(8));
        known.Learn(Charge(before, acBefore, fullBefore), Charge(after, acAfter, fullAfter), TimeSpan.FromMinutes(minutes)).ShouldBe(known);
    }

    [Fact]
    public void A_sleep_that_cost_nothing_the_battery_could_show_still_counts_its_hours()
    {
        var learned = SleepDrain.None.Learn(Charge(40), Charge(36), TimeSpan.FromHours(8)).Learn(Charge(36), Charge(36), TimeSpan.FromHours(8));
        learned.Watts.ShouldNotBeNull().ShouldBeInRange(0.2, 0.3);
    }

    [Fact]
    public void Older_sleeps_weigh_less_as_newer_ones_come()
    {
        var learned = SleepDrain.None.Learn(Charge(40), Charge(32), TimeSpan.FromHours(8));    // 1 W
        for (var night = 0; night < 30; night++) learned = learned.Learn(Charge(40), Charge(37.6), TimeSpan.FromHours(8));   // 0.3 W
        learned.Watts.ShouldNotBeNull().ShouldBe(0.3, 0.01);
    }

    [Fact]
    public void What_was_learned_is_kept_as_text_and_read_back()
    {
        var learned = SleepDrain.None.Learn(Charge(40), Charge(36.3), TimeSpan.FromHours(7.5));
        SleepDrain.Parse(learned.Save()).ShouldBe(learned);
        SleepDrain.Parse(null).ShouldBe(SleepDrain.None);
        SleepDrain.Parse("not a drain").ShouldBe(SleepDrain.None);
        SleepDrain.Parse("-1 3").ShouldBe(SleepDrain.None);
        SleepDrain.Parse("NaN 3").ShouldBe(SleepDrain.None);
    }

    [Fact]
    public void A_laptop_draws_half_a_watt_asleep_until_it_has_learned_its_own()
    {
        // On battery, at the wall it was charged from (accuracy A9).
        SleepPower.Watts(laptop: true, onBattery: true, SleepDrain.None).ShouldBe(SleepPower.LaptopDrainW * BatteryRoundTrip.Factor, 1e-12);
        SleepPower.Watts(laptop: true, onBattery: true, learned: null).ShouldBe(SleepPower.LaptopDrainW * BatteryRoundTrip.Factor, 1e-12);
        var learned = SleepDrain.None.Learn(Charge(40), Charge(37.6), TimeSpan.FromHours(8));
        SleepPower.Watts(laptop: true, onBattery: true, learned).ShouldBe(0.3 * BatteryRoundTrip.Factor, 1e-9);
    }

    [Fact]
    public void On_ac_a_laptop_asleep_draws_its_drain_through_the_adapter()
    {
        SleepPower.Watts(laptop: true, onBattery: false, SleepDrain.None).ShouldBe(AdapterEfficiency.Wall(SleepPower.LaptopDrainW), 1e-9);
        SleepPower.Watts(laptop: true, onBattery: false, SleepDrain.None).ShouldBeInRange(0.8, 0.9);
    }

    [Fact]
    public void A_desktop_asleep_draws_two_watts_at_the_wall()
    {
        var learned = SleepDrain.None.Learn(Charge(40), Charge(37.6), TimeSpan.FromHours(8));
        SleepPower.Watts(laptop: false, onBattery: false, learned).ShouldBe(SleepPower.DesktopW);
        SleepPower.Watts(laptop: false, onBattery: true, SleepDrain.None).ShouldBe(SleepPower.DesktopW);
    }
}
