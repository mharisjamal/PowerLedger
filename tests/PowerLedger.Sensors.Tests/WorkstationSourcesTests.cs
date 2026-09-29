using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

/// <summary>The workstation sources of "measure more" part B, in the assembled set and on this machine.</summary>
public class WorkstationSourcesTests
{
    [Fact]
    public void The_assembled_set_has_the_workstation_sources_and_reads_no_network_ups_unless_one_is_set_up()
    {
        using var sensors = MachineSensors.Create(() => true, () => false);

        var health = sensors.Sampler.Health;
        health.Select(h => h.Name).ShouldContain("nut");
        health.Select(h => h.Name).ShouldContain("power-meter");
        health.Select(h => h.Name).ShouldContain("ipmi");
        health.Single(h => h.Name == "nut").Unavailable.ShouldBe("no UPS on another computer is set up");
    }

    [Fact]
    public void The_real_power_meters_never_throw_and_a_battery_s_is_never_read_as_the_machine_s()
    {
        using var source = new PowerMeterSource();
        var draft = new SampleDraft();

        Should.NotThrow(() => source.Contribute(draft));
        if (!source.Supported) draft.SystemMeterW.ShouldBeNull();
    }
}

/// <summary>What only real hardware can say: the layouts of Intel's control library against the driver installed here.</summary>
[Trait("Category", "Hardware")]
public class IgclHardwareTests
{
    /// <summary>CTL_RESULT_ERROR_PLATFORM_NOT_SUPPORTED.</summary>
    private const int PlatformNotSupported = 0x40000020;

    [Fact]
    public void The_control_library_starts_and_names_the_adapters_it_finds()
    {
        if (!File.Exists(Path.Combine(Environment.SystemDirectory, "ControlLib.dll"))) return;
        using var igcl = new ControlLibrary();

        // The owner's i7-1165G7 (Tiger Lake) is older than the library supports, and it says so: platform not supported.
        // Nothing past start-up can be checked on such a machine.
        var started = igcl.Init();
        if (started == PlatformNotSupported) return;
        started.ShouldBe(0);
        igcl.Adapters(out var adapters).ShouldBe(0);

        adapters.ShouldNotBeEmpty();
        foreach (var adapter in adapters)
        {
            adapter.VendorId.ShouldBe(DiscreteGpu.IntelVendor);
            adapter.Name.ShouldContain("Intel");
            var result = igcl.Telemetry(adapter.Handle, out var telemetry);
            if (result != 0) continue;
            // An integrated GPU keeps a chip energy counter and a timestamp; a discrete card also a card-wide counter.
            telemetry.Seconds.ShouldNotBeNull().ShouldBeGreaterThan(0);
            if (telemetry.GpuJoules is { } joules) joules.ShouldBeGreaterThanOrEqualTo(0);
        }
    }
}
