using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class SimpleSourcesTests
{
    private static Win32.BatteryState Battery(bool ac, bool present = true, int rate = -34_200)
        => new(ac, present, Charging: ac, Discharging: !ac, rate);

    [Fact]
    public void On_battery_the_discharge_rate_becomes_positive_watts()
    {
        var draft = new SampleDraft();
        new BatterySource(() => Battery(ac: false)).Contribute(draft);

        draft.OnBattery.ShouldBeTrue();
        draft.BatteryRateW.ShouldNotBeNull().ShouldBe(34.2, 1e-9);
    }

    [Fact]
    public void On_mains_the_rate_is_not_reported_even_while_charging()
    {
        var draft = new SampleDraft();
        new BatterySource(() => Battery(ac: true, rate: 25_000)).Contribute(draft);

        draft.OnBattery.ShouldBeFalse();
        draft.BatteryRateW.ShouldBeNull();
    }

    [Fact]
    public void An_unknown_or_absent_rate_is_no_reading_at_all()
    {
        var unknown = new SampleDraft();
        new BatterySource(() => Battery(ac: false, rate: Win32.UnknownRate)).Contribute(unknown);
        unknown.BatteryRateW.ShouldBeNull();
        unknown.OnBattery.ShouldBeTrue();

        var idle = new SampleDraft();
        new BatterySource(() => Battery(ac: false, rate: 0)).Contribute(idle);
        idle.BatteryRateW.ShouldBeNull();
    }

    [Fact]
    public void A_desktop_with_no_battery_is_never_on_battery_and_the_source_says_it_is_unsupported()
    {
        var source = new BatterySource(() => Battery(ac: true, present: false, rate: 0));
        source.Supported.ShouldBeFalse();
        source.Unavailable.ShouldBe("no battery fitted");

        var draft = new SampleDraft();
        source.Contribute(draft);
        draft.OnBattery.ShouldBeFalse();
    }

    [Fact]
    public void Windows_refusing_to_answer_leaves_the_draft_alone()
    {
        var draft = new SampleDraft { OnBattery = true };
        new BatterySource(() => null).Contribute(draft);
        draft.OnBattery.ShouldBeTrue();
        draft.BatteryRateW.ShouldBeNull();
    }

    [Fact]
    public void Cpu_load_is_the_busy_share_of_the_time_that_passed()
    {
        var times = new Queue<Win32.SystemTimes?>([
            new Win32.SystemTimes(Idle: 1000, Kernel: 1000, User: 0),
            new Win32.SystemTimes(Idle: 1100, Kernel: 1200, User: 100),   // busy 300, idle 100 -> 0.667
        ]);
        var source = new CpuLoadSource(() => times.Dequeue());

        var first = new SampleDraft();
        source.Contribute(first);
        first.CpuLoad.ShouldBe(0);                                        // the first tick only primes the counters

        var second = new SampleDraft();
        source.Contribute(second);
        second.CpuLoad.ShouldBe(1 - 100 / 300.0, 1e-9);
    }

    [Fact]
    public void A_load_reading_is_clamped_and_a_still_clock_reads_zero()
    {
        var times = new Queue<Win32.SystemTimes?>([
            new Win32.SystemTimes(1000, 1000, 0),
            new Win32.SystemTimes(1000, 1000, 0),
        ]);
        var source = new CpuLoadSource(() => times.Dequeue());
        source.Contribute(new SampleDraft());

        var draft = new SampleDraft();
        source.Contribute(draft);
        draft.CpuLoad.ShouldBe(0);
    }

    [Fact]
    public void Activity_reports_idle_seconds_and_the_lock_state()
    {
        var draft = new SampleDraft();
        new ActivitySource(() => 42.5, () => true).Contribute(draft);

        draft.UserIdleSeconds.ShouldBe(42.5);
        draft.SessionLocked.ShouldBeTrue();
    }

    [Fact]
    public void Activity_with_no_console_session_reports_no_idle_time_rather_than_guessing()
    {
        var draft = new SampleDraft { UserIdleSeconds = 99 };
        new ActivitySource(() => null, () => false).Contribute(draft);

        draft.UserIdleSeconds.ShouldBe(0);
        draft.SessionLocked.ShouldBeFalse();
    }
}
