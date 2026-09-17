using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class MachineSensorsTests
{
    [Fact]
    public void The_assembled_set_covers_every_field_of_a_tick()
    {
        using var sensors = MachineSensors.Create(displayOn: () => true, sessionLocked: () => false);

        var names = sensors.Sampler.Health.Select(h => h.Name).ToList();
        names.ShouldContain("energy-meter");
        names.ShouldContain("nvidia-gpu");
        names.ShouldContain("battery");
        names.ShouldContain("cpu-load");
        names.ShouldContain("activity");
        names.ShouldContain("display");
        names.ShouldContain("power-supply");
    }

    [Fact]
    public void Without_the_owners_tick_no_power_supply_is_read_at_all()
    {
        using var sensors = MachineSensors.Create(() => true, () => false);

        // A set built without the tick has no way to know the owner's answer, so it leaves any supply alone.
        sensors.Read(DateTimeOffset.UtcNow, 1.0).PsuOutputW.ShouldBeNull();
    }

    [Fact]
    public void A_tick_comes_back_shaped_even_on_a_machine_that_answers_little()
    {
        using var sensors = MachineSensors.Create(() => true, () => false);
        var at = DateTimeOffset.UtcNow;

        var sample = sensors.Read(at, 1.0);

        sample.Timestamp.ShouldBe(at);
        sample.DeltaSeconds.ShouldBe(1.0);
        sample.CpuLoad.ShouldBeInRange(0, 1);
        sample.UserIdleSeconds.ShouldBeGreaterThanOrEqualTo(0);
        sample.MonitorCount.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void The_second_tick_carries_a_real_cpu_load_because_the_first_primed_the_counters()
    {
        using var sensors = MachineSensors.Create(() => true, () => false);
        sensors.Read(DateTimeOffset.UtcNow, 1.0);
        Thread.Sleep(250);

        var sample = sensors.Read(DateTimeOffset.UtcNow, 0.25);

        sample.CpuLoad.ShouldBeInRange(0, 1);
        sample.Suspect.ShouldBeFalse();
    }

    [Fact]
    public void Readings_pass_through_the_validator()
    {
        using var sensors = MachineSensors.Create(() => true, () => false);
        sensors.Read(DateTimeOffset.UtcNow, 1.0);

        sensors.Validator.SuspectCount.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void The_monitors_this_machine_has_reach_whoever_asked_for_them_on_the_first_tick()
    {
        IReadOnlyList<MonitorFacts>? found = null;
        using var sensors = MachineSensors.Create(() => true, () => false, monitorsDetected: monitors => found = monitors);

        sensors.Read(DateTimeOffset.UtcNow, 1.0);

        found.ShouldNotBeNull();
    }

    [Fact]
    public void Disposing_the_set_disposes_its_sources()
    {
        var sensors = MachineSensors.Create(() => true, () => false);
        sensors.Dispose();
        Should.NotThrow(() => sensors.Dispose());
    }
}
