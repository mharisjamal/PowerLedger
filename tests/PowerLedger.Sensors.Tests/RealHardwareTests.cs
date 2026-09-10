using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

[Trait("Category", "Hardware")]
public class RealHardwareTests
{
    [Fact]
    public void The_machine_answers_the_basic_power_questions()
    {
        var battery = Win32.ReadBatteryState();
        battery.ShouldNotBeNull();

        var times = Win32.ReadSystemTimes();
        times.ShouldNotBeNull();
        times!.Value.Kernel.ShouldBeGreaterThan(0UL);
        times.Value.User.ShouldBeGreaterThan(0UL);
        times.Value.Idle.ShouldBeGreaterThan(0UL);

        Win32.ReadIdleSeconds().ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void The_energy_meter_reports_a_believable_package_wattage()
    {
        using var meter = new EnergyMeter();
        if (!meter.Available)
        {
            // Windows 10 without metering hardware. Nothing to assert; the source falls back to the load model.
            meter.Unavailable.ShouldNotBeNull();
            return;
        }

        var reading = meter.Read();
        reading.PackageW.ShouldNotBeNull();
        reading.PackageW!.Value.ShouldBeInRange(0.1, 200);

        // Cross-check the undocumented Power unit against the monotonic energy counter.
        var first = meter.ReadRails().Single(r => EnergyMeter.Classify(r.Name) == RailKind.Package);
        Thread.Sleep(3000);
        var second = meter.ReadRails().Single(r => r.Name == first.Name);
        second.EnergyPicowattHours.ShouldBeGreaterThan(first.EnergyPicowattHours);

        var joules = (second.EnergyPicowattHours - first.EnergyPicowattHours) * 3.6e-9;   // 1 pWh = 1e-12 Wh = 3.6e-9 J
        var derivedWatts = joules / 3.0;
        derivedWatts.ShouldBeInRange(0.1, 200);
        derivedWatts.ShouldBe(reading.PackageW.Value, tolerance: reading.PackageW.Value * 0.9 + 2);
    }

    [Fact]
    public void An_nvidia_card_answers_or_is_honestly_absent()
    {
        using var nvml = new Nvml();
        if (!nvml.Available)
        {
            nvml.Unavailable.ShouldNotBeNull();
            return;
        }

        var reading = nvml.Read();
        reading.Present.ShouldBeTrue();
        reading.LoadFraction.ShouldNotBeNull();
        reading.LoadFraction!.Value.ShouldBeInRange(0, 1);

        // Power is null on cards with no measurement hardware, which is most low-end laptop GPUs.
        if (reading.PowerWatts is { } watts) watts.ShouldBeInRange(0.1, 700);
    }

    [Fact]
    public void The_machine_describes_itself()
    {
        var facts = HardwareInventory.Detect();

        facts.CpuName.ShouldNotBeNullOrWhiteSpace();
        facts.RamSticks.ShouldBeGreaterThan(0);
        (facts.SsdCount + facts.HddCount).ShouldBeGreaterThan(0);
        facts.MonitorCount.ShouldBeGreaterThan(0);
        facts.Hash.Length.ShouldBe(16);

        // Detection must be repeatable: the same machine, the same hash.
        HardwareInventory.Detect().Hash.ShouldBe(facts.Hash);
    }
}
