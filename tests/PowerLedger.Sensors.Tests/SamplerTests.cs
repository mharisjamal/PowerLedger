using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class SamplerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Every_source_contributes_to_one_sample()
    {
        var cpu = new FakeSource("cpu", d => { d.CpuPackageW = 14.6; d.CpuLoad = 0.3; });
        var battery = new FakeSource("battery", d => { d.BatteryRateW = 34.2; d.OnBattery = true; });
        using var sampler = new Sampler([cpu, battery]);

        var sample = sampler.Read(T0, 1.0);

        sample.CpuPackageW.ShouldBe(14.6);
        sample.BatteryRateW.ShouldBe(34.2);
        sample.OnBattery.ShouldBeTrue();
        sample.Timestamp.ShouldBe(T0);
        sample.DeltaSeconds.ShouldBe(1.0);
    }

    [Fact]
    public void An_unsupported_source_is_never_called()
    {
        var dead = new FakeSource("dead", _ => { }) { Supported = false, Unavailable = "no battery" };
        using var sampler = new Sampler([dead]);

        sampler.Read(T0, 1.0);
        sampler.Read(T0.AddSeconds(1), 1.0);

        dead.Calls.ShouldBe(0);
        sampler.Health.Single().Unavailable.ShouldBe("no battery");
    }

    [Fact]
    public void A_throwing_source_does_not_stop_the_others_or_the_tick()
    {
        var bad = new FakeSource("bad", d => d.CpuLoad = 0.9) { Throw = new InvalidOperationException("sensor gone") };
        var good = new FakeSource("good", d => d.Brightness = 0.6);
        using var sampler = new Sampler([bad, good]);

        var sample = sampler.Read(T0, 1.0);

        sample.Brightness.ShouldBe(0.6);
        sample.CpuLoad.ShouldBe(0);
        sampler.Health.Single(h => h.Name == "bad").Failures.ShouldBe(1);
        sampler.Health.Single(h => h.Name == "bad").LastError.ShouldBe("sensor gone");
    }

    [Fact]
    public void A_failing_source_backs_off_and_doubles_the_wait_each_time()
    {
        var bad = new FakeSource("bad", _ => { }) { Throw = new InvalidOperationException("boom") };
        using var sampler = new Sampler([bad]);

        sampler.Read(T0, 1.0);                                  // tick 1: throws, skip 1
        bad.Calls.ShouldBe(1);
        sampler.Read(T0.AddSeconds(1), 1.0);                    // tick 2: skipped
        bad.Calls.ShouldBe(1);
        sampler.Read(T0.AddSeconds(2), 1.0);                    // tick 3: retried, throws, skip 2
        bad.Calls.ShouldBe(2);
        sampler.Read(T0.AddSeconds(3), 1.0);
        sampler.Read(T0.AddSeconds(4), 1.0);
        bad.Calls.ShouldBe(2);
        sampler.Read(T0.AddSeconds(5), 1.0);                    // tick 6: retried
        bad.Calls.ShouldBe(3);
    }

    [Fact]
    public void The_backoff_is_capped_and_a_recovery_clears_it()
    {
        var flaky = new FakeSource("flaky", d => d.CpuLoad = 0.5) { Throw = new InvalidOperationException("boom") };
        using var sampler = new Sampler([flaky], maxBackoffTicks: 4);

        for (var tick = 0; tick < 40; tick++) sampler.Read(T0.AddSeconds(tick), 1.0);
        var beforeRecovery = flaky.Calls;
        sampler.Health.Single().SkippedUntilTick.ShouldBeLessThanOrEqualTo(41 + 4);

        flaky.Throw = null;
        for (var tick = 40; tick < 60; tick++) sampler.Read(T0.AddSeconds(tick), 1.0);

        flaky.Calls.ShouldBeGreaterThan(beforeRecovery);
        sampler.Health.Single().SkippedUntilTick.ShouldBe(0);
        sampler.Read(T0.AddSeconds(60), 1.0).CpuLoad.ShouldBe(0.5);
    }

    [Fact]
    public void Disposing_the_sampler_disposes_every_source()
    {
        var one = new FakeSource("one", _ => { });
        var two = new FakeSource("two", _ => { });
        var sampler = new Sampler([one, two]);

        sampler.Dispose();

        one.Disposed.ShouldBeTrue();
        two.Disposed.ShouldBeTrue();
    }
}
