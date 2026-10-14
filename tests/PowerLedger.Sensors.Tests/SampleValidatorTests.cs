using PowerLedger.Core;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class SampleValidatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);

    private static Sample Raw(
        double? cpu = 15, double? gpu = 5, double? battery = null, bool onBattery = false,
        double cpuLoad = 0.3, double? brightness = 0.6, int second = 0, double? ups = null, double? psu = null,
        double? wall = null)
        => new(T0.AddSeconds(second), 1.0, cpu, null, cpuLoad, gpu, 0.2, DGpuPresent: true,
               battery, onBattery, brightness, DisplayOn: true, MonitorCount: 1,
               UserIdleSeconds: 0, SessionLocked: false, Suspect: false,
               UpsOutputW: ups, PsuOutputW: psu, PsuWallW: wall);

    [Fact]
    public void A_plain_reading_passes_through_untouched()
    {
        var validator = new SampleValidator();
        var clean = validator.Validate(Raw());
        clean.CpuPackageW.ShouldBe(15);
        clean.DGpuW.ShouldBe(5);
        clean.Suspect.ShouldBeFalse();
        validator.SuspectCount.ShouldBe(0);
    }

    [Fact]
    public void A_platform_rail_is_range_checked_but_never_spike_filtered()
    {
        var validator = new SampleValidator();
        for (var second = 0; second < 40; second++) validator.Validate(Raw(second: second) with { PlatformW = 5 });

        // A jump from idle to load is energy over the tick, a fact rather than a glitch.
        var jump = validator.Validate(Raw(second: 40) with { PlatformW = 60 });
        jump.PlatformW.ShouldBe(60);
        jump.Suspect.ShouldBeFalse();

        var misread = validator.Validate(Raw(second: 41) with { PlatformW = 3000 });
        misread.PlatformW.ShouldBeNull();
        misread.Suspect.ShouldBeTrue();
    }

    [Theory]
    [InlineData(401.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_cpu_reading_outside_its_range_is_dropped_and_the_tick_marked_suspect(double watts)
    {
        var validator = new SampleValidator();
        var checked_ = validator.Validate(Raw(cpu: watts));
        checked_.CpuPackageW.ShouldBeNull();
        checked_.Suspect.ShouldBeTrue();
        validator.SuspectCount.ShouldBe(1);
    }

    [Fact]
    public void Gpu_and_battery_have_their_own_ranges()
    {
        var validator = new SampleValidator();
        validator.Validate(Raw(gpu: 701)).DGpuW.ShouldBeNull();
        validator.Validate(Raw(gpu: 699)).DGpuW.ShouldBe(699);
        validator.Validate(Raw(battery: 301, onBattery: true)).BatteryRateW.ShouldBeNull();
        validator.Validate(Raw(battery: 299, onBattery: true)).BatteryRateW.ShouldBe(299);
    }

    [Fact]
    public void A_single_gpu_spike_is_replaced_by_the_last_good_value()
    {
        var validator = new SampleValidator();
        for (var i = 0; i < 30; i++) validator.Validate(Raw(gpu: 10, second: i));

        var spike = validator.Validate(Raw(gpu: 200, second: 30));

        spike.DGpuW.ShouldBe(10);
        spike.Suspect.ShouldBeTrue();
    }

    [Fact]
    public void Each_card_is_range_checked_and_spike_filtered_on_its_own_and_the_totals_follow()
    {
        var validator = new SampleValidator();
        Sample Cards(int second, double? geforce, double? quadro, double? load = 0.4)
        {
            IReadOnlyList<GpuCard> cards =
            [
                new GpuCard("NVIDIA GeForce GTX 1080", geforce, load),
                new GpuCard("Quadro 6000", quadro, 0.2, RatedW: 204),
            ];
            var totals = GpuCard.Totals(cards);
            return Raw(gpu: totals.Watts, second: second) with { Gpus = cards, DGpuLoad = totals.Load };
        }

        // The Quadro idles at 30 W the whole time; the GeForce's window fills at 100 W.
        for (var i = 0; i < 30; i++) validator.Validate(Cards(i, 100, 30)).Suspect.ShouldBeFalse();

        // A GeForce spike is the GeForce's alone: it is replaced, and the Quadro's figure stands.
        var spike = validator.Validate(Cards(30, 400, 30));
        spike.Gpus.ShouldNotBeNull().Select(g => g.Watts).ShouldBe([100, 30]);
        spike.DGpuW.ShouldBe(130);
        spike.Suspect.ShouldBeTrue();

        // A figure no card could draw is dropped, which leaves that card to the model, and a load past 1 is held to 1.
        var broken = validator.Validate(Cards(31, 100, 5000, load: 1.7));
        broken.Gpus.ShouldNotBeNull()[1].Watts.ShouldBeNull();
        broken.Gpus[0].Load.ShouldBe(1);
        broken.DGpuW.ShouldBeNull();
        broken.DGpuLoad.ShouldBe(1);
        broken.DGpuPresent.ShouldBeTrue();
        broken.Suspect.ShouldBeTrue();
    }

    [Fact]
    public void At_one_reading_every_five_seconds_the_spike_window_covers_the_same_half_minute()
    {
        var validator = new SampleValidator();
        for (var second = 0; second <= 25; second += 5) validator.Validate(Raw(gpu: 10, second: second));
        validator.Validate(Raw(gpu: 200, second: 29)).DGpuW.ShouldBe(200);     // under half a minute of readings says nothing yet

        validator = new SampleValidator();
        for (var second = 0; second <= 30; second += 5) validator.Validate(Raw(gpu: 10, second: second));
        var spike = validator.Validate(Raw(gpu: 200, second: 35));
        spike.DGpuW.ShouldBe(10);
        spike.Suspect.ShouldBeTrue();
    }

    [Fact]
    public void Readings_from_before_a_sleep_say_nothing_about_after_it()
    {
        var validator = new SampleValidator();
        for (var i = 0; i < 30; i++) validator.Validate(Raw(gpu: 10, second: i));
        var woken = validator.Validate(Raw(gpu: 60, second: 150));
        woken.DGpuW.ShouldBe(60);
        woken.Suspect.ShouldBeFalse();

        for (var second = 151; second < 180; second++) validator.Validate(Raw(gpu: 60, second: second));
        validator.Validate(Raw(gpu: 200, second: 181)).DGpuW.ShouldBe(60);    // the window is the new level's half minute
    }

    [Fact]
    public void A_card_whose_watts_are_exact_over_the_tick_is_range_checked_but_never_spike_filtered()
    {
        // NVML's energy counter on Volta and newer, Level Zero's and IGCL's give the energy over the tick, as the processor's
        // meter does, so a step up from idle is a fact; the first tick of each was replaced with the last (accuracy A11).
        var validator = new SampleValidator();
        Sample Card(int second, double? watts, bool exact = true)
            => Raw(gpu: watts, second: second) with { Gpus = [new GpuCard("NVIDIA GeForce RTX 4070", watts, 0.5, Exact: exact)] };
        for (var i = 0; i < 30; i++) validator.Validate(Card(i, 10)).Suspect.ShouldBeFalse();

        var step = validator.Validate(Card(30, 200));
        step.Gpus.ShouldNotBeNull().Single().Watts.ShouldBe(200);
        step.DGpuW.ShouldBe(200);
        step.Suspect.ShouldBeFalse();
        validator.Validate(Card(31, 10)).DGpuW.ShouldBe(10);
        validator.Validate(Card(32, 200)).DGpuW.ShouldBe(200);              // a burst every other tick is kept too

        var misread = validator.Validate(Card(33, 5000));
        misread.Gpus.ShouldNotBeNull().Single().Watts.ShouldBeNull();
        misread.Suspect.ShouldBeTrue();

        var instant = new SampleValidator();                                 // a figure the card's library samples is filtered still
        for (var i = 0; i < 30; i++) instant.Validate(Card(i, 10, exact: false));
        instant.Validate(Card(30, 200, exact: false)).DGpuW.ShouldBe(10);
    }

    [Fact]
    public void A_sustained_rise_is_accepted_on_its_second_reading_instead_of_being_rejected_forever()
    {
        var validator = new SampleValidator();
        for (var i = 0; i < 30; i++) validator.Validate(Raw(gpu: 10, second: i));

        validator.Validate(Raw(gpu: 200, second: 30)).DGpuW.ShouldBe(10);

        var second = validator.Validate(Raw(gpu: 200, second: 31));
        second.DGpuW.ShouldBe(200);
        second.Suspect.ShouldBeFalse();
        validator.Validate(Raw(gpu: 210, second: 32)).DGpuW.ShouldBe(210);
    }

    [Fact]
    public void Cpu_watts_from_the_energy_meter_are_never_spike_filtered()
    {
        // They are averages of real energy over the tick, so a jump from idle to turbo is a fact, not a glitch.
        var validator = new SampleValidator();
        for (var i = 0; i < 30; i++) validator.Validate(Raw(cpu: 6, second: i));

        var load = validator.Validate(Raw(cpu: 35, second: 30));
        load.CpuPackageW.ShouldBe(35);
        load.Suspect.ShouldBeFalse();
    }

    [Fact]
    public void A_dip_far_below_the_median_is_left_alone_because_idle_is_real()
    {
        var validator = new SampleValidator();
        for (var i = 0; i < 30; i++) validator.Validate(Raw(gpu: 30, second: i));

        var dip = validator.Validate(Raw(gpu: 1, second: 30));

        dip.DGpuW.ShouldBe(1);
        dip.Suspect.ShouldBeFalse();
    }

    [Fact]
    public void A_reading_stays_trusted_until_the_window_has_something_to_say()
    {
        var validator = new SampleValidator();
        validator.Validate(Raw(gpu: 5)).DGpuW.ShouldBe(5);
        validator.Validate(Raw(gpu: 300, second: 1)).DGpuW.ShouldBe(300);
        validator.SuspectCount.ShouldBe(0);
    }

    [Fact]
    public void A_negative_cpu_reading_is_dropped()
    {
        var validator = new SampleValidator();
        validator.Validate(Raw(cpu: 20));
        var negative = validator.Validate(Raw(cpu: -0.5, second: 1));
        negative.CpuPackageW.ShouldBeNull();
        negative.Suspect.ShouldBeTrue();
    }

    [Fact]
    public void The_first_seconds_after_plugging_in_are_marked_but_the_battery_flag_switches_at_once()
    {
        var validator = new SampleValidator();
        validator.Validate(Raw(battery: 30, onBattery: true, second: 0));

        var justAfter = validator.Validate(Raw(battery: null, onBattery: false, second: 1));
        justAfter.OnBattery.ShouldBeFalse();
        justAfter.Suspect.ShouldBeTrue();

        validator.Validate(Raw(second: 2)).Suspect.ShouldBeTrue();
        validator.Validate(Raw(second: 3)).Suspect.ShouldBeTrue();
        validator.Validate(Raw(second: 4)).Suspect.ShouldBeFalse();
    }

    [Fact]
    public void Unplugging_opens_the_same_window()
    {
        var validator = new SampleValidator();
        validator.Validate(Raw(second: 0));
        validator.Validate(Raw(battery: 30, onBattery: true, second: 1)).Suspect.ShouldBeTrue();
        validator.Validate(Raw(battery: 30, onBattery: true, second: 5)).Suspect.ShouldBeFalse();
    }

    [Fact]
    public void Only_the_seconds_after_an_ac_change_are_a_transition_and_other_doubts_are_not()
    {
        // The model drops the battery's rate in a transition alone: a brightness clamped on the same tick says nothing of it.
        var validator = new SampleValidator();
        validator.Validate(Raw(battery: 30, onBattery: true, second: 0)).AcTransition.ShouldBeFalse();
        var unplugged = validator.Validate(Raw(onBattery: false, second: 1));
        (unplugged.Suspect, unplugged.AcTransition).ShouldBe((true, true));
        var back = validator.Validate(Raw(battery: 30, onBattery: true, second: 10));
        (back.Suspect, back.AcTransition).ShouldBe((true, true));
        var clamped = validator.Validate(Raw(battery: 30, onBattery: true, brightness: 1.5, second: 20));
        (clamped.Suspect, clamped.AcTransition).ShouldBe((true, false));
    }

    [Fact]
    public void Coming_off_mains_starts_the_battery_median_afresh()
    {
        var validator = new SampleValidator();
        for (var i = 0; i < 30; i++) validator.Validate(Raw(battery: 10, onBattery: true, second: i));
        validator.Validate(Raw(battery: null, onBattery: false, second: 30));

        validator.Validate(Raw(battery: 40, onBattery: true, second: 40)).BatteryRateW.ShouldBe(40);
    }

    [Fact]
    public void Brightness_and_load_are_clamped_rather_than_dropped()
    {
        var validator = new SampleValidator();
        var wild = validator.Validate(Raw(brightness: 1.4, cpuLoad: 2.5));
        wild.Brightness.ShouldBe(1.0);
        wild.CpuLoad.ShouldBe(1.0);
        wild.Suspect.ShouldBeTrue();

        var nonsense = validator.Validate(Raw(brightness: double.NaN, cpuLoad: double.NaN, second: 1));
        nonsense.Brightness.ShouldBeNull();
        nonsense.CpuLoad.ShouldBe(0);

        validator.Validate(Raw(second: 2) with { DGpuBusyLoad = 1.3 }).DGpuBusyLoad.ShouldBe(1.0);
        validator.Validate(Raw(second: 3) with { DGpuBusyLoad = double.NaN }).DGpuBusyLoad.ShouldBeNull();
        validator.Validate(Raw(second: 4) with { DGpuBusyLoad = 0.6 }).DGpuBusyLoad.ShouldBe(0.6);
    }

    [Theory]
    [InlineData(5001.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_ups_reading_outside_its_range_is_dropped_and_the_tick_marked_suspect(double watts)
    {
        // A unit exponent read one place out turns 250 W into 25 kW, which banks 6.94 Wh in a single second.
        var validator = new SampleValidator();
        var checked_ = validator.Validate(Raw(ups: watts));
        checked_.UpsOutputW.ShouldBeNull();
        checked_.Suspect.ShouldBeTrue();
        validator.SuspectCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(2001.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_power_supply_reading_outside_its_range_is_dropped_and_the_tick_marked_suspect(double watts)
    {
        var validator = new SampleValidator();
        var checked_ = validator.Validate(Raw(psu: watts));
        checked_.PsuOutputW.ShouldBeNull();
        checked_.Suspect.ShouldBeTrue();
        validator.SuspectCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(2001.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_wall_reading_outside_the_same_range_is_dropped_and_the_tick_marked_suspect(double watts)
    {
        // What a supply says it draws from the wall is the total on its own, so it needs the ceiling most of all.
        var validator = new SampleValidator();
        var checked_ = validator.Validate(Raw(wall: watts));
        checked_.PsuWallW.ShouldBeNull();
        checked_.Suspect.ShouldBeTrue();
        validator.SuspectCount.ShouldBe(1);
    }

    [Fact]
    public void A_ups_and_a_power_supply_are_believed_up_to_their_ceilings_and_a_zero_is_left_to_the_model()
    {
        var validator = new SampleValidator();
        var ceiling = validator.Validate(Raw(ups: 5000, psu: 2000));
        ceiling.UpsOutputW.ShouldBe(5000);
        ceiling.PsuOutputW.ShouldBe(2000);
        ceiling.Suspect.ShouldBeFalse();
        validator.Validate(Raw(wall: 2000, second: 1)).PsuWallW.ShouldBe(2000);

        // Nought watts is in range here: refusing to make it the whole total is the model's job, not the validator's.
        var zero = validator.Validate(Raw(ups: 0, psu: 0, wall: 0, second: 2));
        zero.UpsOutputW.ShouldBe(0);
        zero.PsuOutputW.ShouldBe(0);
        zero.PsuWallW.ShouldBe(0);
        zero.Suspect.ShouldBeFalse();
        validator.SuspectCount.ShouldBe(0);
    }

    [Fact]
    public void A_whole_machine_reading_is_never_spike_filtered_because_a_jump_to_load_is_real()
    {
        var validator = new SampleValidator();
        for (var i = 0; i < 30; i++) validator.Validate(Raw(ups: 40, psu: 30, wall: 35, second: i));

        var load = validator.Validate(Raw(ups: 400, psu: 300, wall: 350, second: 30));

        load.UpsOutputW.ShouldBe(400);
        load.PsuOutputW.ShouldBe(300);
        load.PsuWallW.ShouldBe(350);
        load.Suspect.ShouldBeFalse();
    }

    [Fact]
    public void A_non_finite_integrated_graphics_reading_is_dropped()
    {
        var validator = new SampleValidator();
        var checked_ = validator.Validate(Raw() with { IGpuW = double.NaN });
        checked_.IGpuW.ShouldBeNull();
        checked_.Suspect.ShouldBeTrue();
    }

    [Fact]
    public void Options_choose_the_ranges_and_the_windows()
    {
        var strict = new SampleValidator(new ValidatorOptions(CpuMaxW: 20, MedianWindow: 3, OutlierFactor: 2, TransitionSeconds: 1));
        strict.Validate(Raw(cpu: 25)).CpuPackageW.ShouldBeNull();
        for (var i = 0; i < 3; i++) strict.Validate(Raw(gpu: 5, second: i));
        strict.Validate(Raw(gpu: 15, second: 3)).DGpuW.ShouldBe(5);

        var bounded = new SampleValidator(new ValidatorOptions(UpsMaxW: 100, PsuMaxW: 50));
        var over = bounded.Validate(Raw(ups: 101, psu: 51, wall: 51));
        over.UpsOutputW.ShouldBeNull();
        over.PsuOutputW.ShouldBeNull();
        over.PsuWallW.ShouldBeNull();
        over.Suspect.ShouldBeTrue();

        var under = bounded.Validate(Raw(ups: 100, psu: 50, wall: 50, second: 1));
        under.UpsOutputW.ShouldBe(100);
        under.PsuOutputW.ShouldBe(50);
        under.PsuWallW.ShouldBe(50);
        under.Suspect.ShouldBeFalse();
    }
}
