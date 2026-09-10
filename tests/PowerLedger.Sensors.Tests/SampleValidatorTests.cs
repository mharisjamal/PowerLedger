using PowerLedger.Core;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class SampleValidatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);

    private static Sample Raw(
        double? cpu = 15, double? gpu = 5, double? battery = null, bool onBattery = false,
        double cpuLoad = 0.3, double? brightness = 0.6, int second = 0)
        => new(T0.AddSeconds(second), 1.0, cpu, null, cpuLoad, gpu, 0.2, DGpuPresent: true,
               battery, onBattery, brightness, DisplayOn: true, MonitorCount: 1,
               UserIdleSeconds: 0, SessionLocked: false, Suspect: false);

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
    public void A_spike_more_than_three_times_the_median_is_replaced_by_the_last_good_value()
    {
        var validator = new SampleValidator();
        for (var i = 0; i < 30; i++) validator.Validate(Raw(cpu: 10, second: i));

        var spike = validator.Validate(Raw(cpu: 200, second: 30));

        spike.CpuPackageW.ShouldBe(10);
        spike.Suspect.ShouldBeTrue();
    }

    [Fact]
    public void A_dip_far_below_the_median_is_left_alone_because_idle_is_real()
    {
        var validator = new SampleValidator();
        for (var i = 0; i < 30; i++) validator.Validate(Raw(cpu: 30, second: i));

        var dip = validator.Validate(Raw(cpu: 1, second: 30));

        dip.CpuPackageW.ShouldBe(1);
        dip.Suspect.ShouldBeFalse();
    }

    [Fact]
    public void The_median_is_not_poisoned_by_the_spike_it_rejected()
    {
        var validator = new SampleValidator();
        for (var i = 0; i < 30; i++) validator.Validate(Raw(cpu: 10, second: i));
        validator.Validate(Raw(cpu: 200, second: 30));

        validator.Validate(Raw(cpu: 200, second: 31)).CpuPackageW.ShouldBe(10);
    }

    [Fact]
    public void A_reading_stays_trusted_until_the_window_has_something_to_say()
    {
        var validator = new SampleValidator();
        validator.Validate(Raw(cpu: 5)).CpuPackageW.ShouldBe(5);
        validator.Validate(Raw(cpu: 300, second: 1)).CpuPackageW.ShouldBe(300);
        validator.SuspectCount.ShouldBe(0);
    }

    [Fact]
    public void A_cpu_reading_below_the_previous_one_by_more_than_a_hair_is_a_counter_wrap_and_is_dropped()
    {
        var validator = new SampleValidator();
        validator.Validate(Raw(cpu: 20));
        var wrapped = validator.Validate(Raw(cpu: -0.5, second: 1));
        wrapped.CpuPackageW.ShouldBeNull();
        wrapped.Suspect.ShouldBeTrue();
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
    }

    [Fact]
    public void Options_choose_the_ranges_and_the_windows()
    {
        var strict = new SampleValidator(new ValidatorOptions(CpuMaxW: 20, MedianWindow: 3, OutlierFactor: 2, TransitionSeconds: 1));
        strict.Validate(Raw(cpu: 25)).CpuPackageW.ShouldBeNull();
        for (var i = 0; i < 3; i++) strict.Validate(Raw(cpu: 5, second: i));
        strict.Validate(Raw(cpu: 15, second: 3)).CpuPackageW.ShouldBe(5);
    }
}
