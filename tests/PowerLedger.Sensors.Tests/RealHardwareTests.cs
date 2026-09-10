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
    public void The_energy_meter_reports_a_believable_package_wattage_well_inside_the_tick_budget()
    {
        using var meter = new EnergyMeter();
        if (!meter.Available)
        {
            // Windows 10 without metering hardware. Nothing to assert; the source falls back to the load model.
            meter.Unavailable.ShouldNotBeNull();
            return;
        }

        // A thousandfold unit error would put this at a few milliwatts or several kilowatts, so the band pins the unit.
        Thread.Sleep(1000);
        meter.Read().PackageW.ShouldNotBeNull().ShouldBeInRange(0.1, 200);

        // Spec §4 budgets about 2 ms a tick for the fast sources; the old WMI query took about 270 ms.
        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) meter.Read();
        (timer.Elapsed.TotalMilliseconds / 20).ShouldBeLessThan(5);
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

        // Windows can say whether the card is switched off without waking it.
        var device = DevicePowerState.FindNvidiaGpu();
        device.ShouldNotBeNull().ShouldStartWith(@"PCI\VEN_10DE");
        Should.NotThrow(() => DevicePowerState.IsPoweredOff(device));
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
