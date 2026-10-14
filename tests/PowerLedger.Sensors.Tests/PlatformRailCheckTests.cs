using PowerLedger.Core;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class PlatformRailCheckTests
{
    private readonly List<string> _notes = [];
    private DateTimeOffset _at = DateTimeOffset.UnixEpoch;

    [Fact]
    public void Until_the_battery_has_had_its_say_the_rail_is_trusted()
    {
        var check = new PlatformRailCheck();

        check.Apply(OnAc(platform: 12), _notes.Add).PlatformW.ShouldBe(12);
        check.Status.ShouldBeNull();
        _notes.ShouldBeEmpty();
    }

    [Fact]
    public void A_rail_that_agrees_with_the_discharge_stays_trusted_and_says_so_once()
    {
        var check = new PlatformRailCheck();

        Battery(check, minutes: 12, platform: 10, discharge: 10.4);

        check.Trusted.ShouldBeTrue();
        check.Apply(OnAc(platform: 12), _notes.Add).PlatformW.ShouldBe(12);
        _notes.Count.ShouldBe(1);
        _notes[0].ShouldContain("agrees");
    }

    [Fact]
    public void A_rail_that_disagrees_on_battery_is_set_aside_and_the_model_takes_over_with_one_log_line()
    {
        var check = new PlatformRailCheck();

        Battery(check, minutes: 12, platform: 7, discharge: 10);

        check.Trusted.ShouldBeFalse();
        check.Apply(OnAc(platform: 12), _notes.Add).PlatformW.ShouldBeNull();
        _notes.Count.ShouldBe(1);
        _notes[0].ShouldContain("0.70");
        check.Status.ShouldNotBeNull().ShouldContain("0.70");
    }

    [Fact]
    public void Too_high_is_as_untrusted_as_too_low()
    {
        var check = new PlatformRailCheck();

        Battery(check, minutes: 6, platform: 11.5, discharge: 10);

        check.Trusted.ShouldBeFalse();
    }

    [Fact]
    public void Five_battery_minutes_are_needed_before_any_verdict()
    {
        var check = new PlatformRailCheck();

        Battery(check, minutes: 4, platform: 7, discharge: 10);
        check.Trusted.ShouldBeTrue();
        _notes.ShouldBeEmpty();

        Battery(check, minutes: 1, platform: 7, discharge: 10, fresh: false);
        check.Trusted.ShouldBeFalse();
    }

    [Fact]
    public void The_first_minute_on_battery_is_left_out_while_the_battery_s_rate_catches_up()
    {
        var check = new PlatformRailCheck();

        Tick(check, seconds: 60, platform: 3, discharge: 10);                 // a stale rate just after unplugging
        for (var minute = 0; minute < 6; minute++) Tick(check, seconds: 60, platform: 10, discharge: 10);

        check.Trusted.ShouldBeTrue();
        _notes.Single().ShouldContain("agrees");
    }

    [Fact]
    public void A_rail_set_aside_is_trusted_again_once_it_agrees_again()
    {
        var check = new PlatformRailCheck();
        Battery(check, minutes: 7, platform: 7, discharge: 10);
        check.Trusted.ShouldBeFalse();

        Battery(check, minutes: 11, platform: 10, discharge: 10);

        check.Trusted.ShouldBeTrue();
        check.Status.ShouldBeNull();
        _notes.Count.ShouldBe(2);
    }

    [Fact]
    public void Suspect_ticks_and_ticks_without_a_discharge_rate_teach_it_nothing()
    {
        var check = new PlatformRailCheck();
        Tick(check, seconds: 61, platform: 10, discharge: 10);
        for (var tick = 0; tick < 600; tick++)
        {
            check.Apply(Sample(onBattery: true, platform: 3, discharge: 10, suspect: true), _notes.Add);
            check.Apply(Sample(onBattery: true, platform: 3, discharge: null), _notes.Add);
        }

        check.Trusted.ShouldBeTrue();
        _notes.ShouldBeEmpty();
    }

    [Fact]
    public void A_machine_without_a_platform_rail_is_never_checked()
    {
        var check = new PlatformRailCheck();
        for (var tick = 0; tick < 1000; tick++) check.Apply(Sample(onBattery: true, platform: null, discharge: 10), _notes.Add);

        check.Trusted.ShouldBeTrue();
        _notes.ShouldBeEmpty();
    }

    /// <summary>Unplugs, then runs whole minutes on battery after the first minute, one tick a second.</summary>
    private void Battery(PlatformRailCheck check, int minutes, double platform, double discharge, bool fresh = true)
    {
        if (fresh)
        {
            check.Apply(OnAc(platform), _notes.Add);
            Tick(check, seconds: 60, platform, discharge);
        }
        for (var second = 0; second < minutes * 60; second++) check.Apply(Sample(onBattery: true, platform, discharge), _notes.Add);
    }

    private void Tick(PlatformRailCheck check, int seconds, double platform, double discharge)
    {
        for (var second = 0; second < seconds; second++) check.Apply(Sample(onBattery: true, platform, discharge), _notes.Add);
    }

    private Sample OnAc(double platform) => Sample(onBattery: false, platform, discharge: null);

    private Sample Sample(bool onBattery, double? platform, double? discharge, bool suspect = false)
    {
        _at = _at.AddSeconds(1);
        var sample = new SampleDraft { OnBattery = onBattery, PlatformW = platform, BatteryRateW = discharge }.ToSample(_at, 1);
        return suspect ? sample with { Suspect = true } : sample;
    }
}
