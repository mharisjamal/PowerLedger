using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>Plan AC review: what the System event log says of a sleep that was a hibernate, modelled on the owner's
/// laptop's own events, and the live log and sessions on this machine.</summary>
public class SleepKindsTests
{
    private const string Power = "Microsoft-Windows-Kernel-Power";
    private const string Boot = "Microsoft-Windows-Kernel-Boot";
    private const string Troubleshooter = "Microsoft-Windows-Power-Troubleshooter";

    private static readonly DateTimeOffset Suspended = new(2026, 10, 14, 13, 14, 42, TimeSpan.Zero);

    private static PowerEvent Event(string provider, int id, DateTimeOffset at, params (string Name, string Value)[] data)
        => new(provider, id, at, data.ToDictionary(d => d.Name, d => d.Value));

    /// <summary>The owner's hibernate of 14 October: entering S4 three seconds after the suspend, waking from it, a resume
    /// from hibernation at boot, and the troubleshooter's summary with the time it went.</summary>
    [Fact]
    public void A_hibernate_is_found_from_the_moment_it_went()
    {
        PowerEvent[] events =
        [
            Event(Power, 42, Suspended.AddSeconds(3.7), ("TargetState", "5"), ("EffectiveState", "5"), ("Reason", "0")),
            Event(Power, 107, Suspended.AddSeconds(4.6), ("TargetState", "5"), ("EffectiveState", "5"), ("WakeFromState", "5")),
            Event(Boot, 27, Suspended.AddSeconds(76.7), ("BootType", "2")),
            Event(Troubleshooter, 1, Suspended.AddSeconds(76.8), ("SleepTime", "2026-10-14T13:14:42.6993358Z"), ("TargetState", "5"), ("EffectiveState", "5")),
        ];

        SleepKinds.HibernatedAt(events, Suspended).ShouldBe(new DateTimeOffset(2026, 10, 14, 13, 14, 42, 699, TimeSpan.Zero).AddTicks(3358));
    }

    /// <summary>A Modern Standby PC hibernates hours into its sleep; the standby before it drew all the same.</summary>
    [Fact]
    public void A_standby_that_went_on_into_a_hibernate_is_found_from_when_it_did()
    {
        PowerEvent[] events =
        [
            Event(Power, 42, Suspended.AddHours(3), ("TargetState", "5"), ("EffectiveState", "5")),
            Event(Boot, 27, Suspended.AddHours(9), ("BootType", "2")),
        ];

        SleepKinds.HibernatedAt(events, Suspended).ShouldBe(Suspended.AddHours(3));
    }

    [Fact]
    public void A_sleep_in_memory_is_no_hibernate()
    {
        PowerEvent[] events =
        [
            Event(Power, 42, Suspended.AddSeconds(2), ("TargetState", "4"), ("EffectiveState", "4")),
            Event(Power, 107, Suspended.AddSeconds(3), ("TargetState", "4"), ("EffectiveState", "4"), ("WakeFromState", "4")),
            Event(Troubleshooter, 1, Suspended.AddHours(8), ("SleepTime", "2026-10-14T13:14:44Z"), ("TargetState", "4"), ("EffectiveState", "4")),
        ];

        SleepKinds.HibernatedAt(events, Suspended).ShouldBeNull();
        SleepKinds.HibernatedAt([], Suspended).ShouldBeNull();   // Modern Standby logs none of these
    }

    /// <summary>A hybrid sleep writes the hibernation file and sleeps in memory: woken from memory it drew a sleep's
    /// power; woken from the file, after the power went, it lay hibernated, since some time nobody logged.</summary>
    [Fact]
    public void A_hybrid_sleep_is_a_hibernate_only_when_it_woke_from_the_file()
    {
        var entered = Event(Power, 42, Suspended.AddSeconds(2), ("TargetState", "4"), ("EffectiveState", "5"));

        SleepKinds.HibernatedAt([entered, Event(Power, 107, Suspended.AddSeconds(3), ("WakeFromState", "4"))], Suspended).ShouldBeNull();
        SleepKinds.HibernatedAt([entered, Event(Power, 107, Suspended.AddSeconds(3), ("WakeFromState", "5"))], Suspended).ShouldBe(Suspended);
    }

    /// <summary>A Fast Startup boot or a resume from hibernation says the machine hibernated, though not when.</summary>
    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    public void A_boot_from_the_hibernation_file_is_a_hibernate(string bootType)
        => SleepKinds.HibernatedAt([Event(Boot, 27, Suspended.AddHours(8), ("BootType", bootType))], Suspended).ShouldBe(Suspended);

    [Fact]
    public void Events_from_before_the_suspend_and_others_say_nothing()
    {
        PowerEvent[] events =
        [
            Event(Boot, 27, Suspended.AddDays(-1), ("BootType", "2")),
            Event(Power, 42, Suspended.AddMinutes(-5), ("TargetState", "5")),
            Event(Boot, 27, Suspended.AddHours(8), ("BootType", "0")),
            Event("Microsoft-Windows-Kernel-General", 1, Suspended.AddHours(8), ("Reason", "2")),
            Event(Power, 42, Suspended.AddSeconds(2), ("TargetState", "nonsense")),
        ];

        SleepKinds.HibernatedAt(events, Suspended).ShouldBeNull();
    }

    /// <summary>The live log answers, and fast enough to ask as the machine wakes.</summary>
    [Fact]
    [Trait("Category", "Hardware")]
    public void The_live_log_answers_within_its_time()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _ = SleepKinds.Hibernated(DateTimeOffset.UtcNow.AddDays(-7));
        watch.Elapsed.ShouldBeLessThan(SleepKinds.LogWait + TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public void Someone_is_signed_in_while_these_tests_run() => SleepKinds.SomeoneSignedIn().ShouldBeTrue();
}
