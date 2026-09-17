using System.Diagnostics;
using PowerLedger.Contracts;
using PowerLedger.Sensors;
using Shouldly;
using Xunit.Abstractions;

namespace PowerLedger.Sensors.Tests;

[Trait("Category", "Hardware")]
public class GpuHardwareTests(ITestOutputHelper output)
{
    [Fact]
    public void Dxgi_lists_the_adapters_and_finds_no_amd_or_intel_card_on_the_development_laptop()
    {
        using var dxgi = new DxgiAdapters();
        var adapters = dxgi.Current();

        adapters.ShouldNotBeEmpty();
        adapters.ShouldAllBe(a => a.Description.Length > 0);
        adapters.ShouldContain(a => !a.Software && a.VendorId != 0);

        // Intel Iris Xe graphics in the processor and a GeForce MX330: neither is an AMD or Intel card.
        DiscreteGpu.Choose(adapters).ShouldBeNull();
        var source = new GpuLoadSource();
        source.Supported.ShouldBeFalse();
        source.Unavailable.ShouldBe("no AMD or Intel discrete GPU");
        source.Dispose();

        // Nothing changed, so the kept list answers without waking a switched-off card again.
        dxgi.Current().ShouldBeSameAs(adapters);
    }

    [Fact]
    public void The_gpu_engine_counters_read_without_throwing_and_name_the_adapters_as_dxgi_does()
    {
        GpuEngines.Published.ShouldBeTrue();
        var engines = new GpuEngines();
        var before = engines.Read();
        Thread.Sleep(500);
        var after = engines.Read();

        before.RunningTime.ShouldNotBeEmpty();
        after.At.ShouldBeGreaterThan(before.At);

        // The desktop is drawn on at least one adapter, so the counters name it exactly as DXGI's LUID does.
        using var dxgi = new DxgiAdapters();
        var hardware = dxgi.Current().Where(a => !a.Software).ToList();
        hardware.ShouldContain(a => after.RunningTime.Keys.Any(instance => GpuEngines.EngineOf(instance, a.CounterLuid) != null));
        foreach (var adapter in hardware)
        {
            GpuEngines.Busiest(before, after, adapter.CounterLuid).ShouldNotBeNull().ShouldBeInRange(0, 1);
        }

        // Well inside the budget of a read every five seconds; about a millisecond on the development laptop.
        var timer = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) engines.Read();
        (timer.Elapsed.TotalMilliseconds / 20).ShouldBeLessThan(25);
    }

    [Fact]
    public void Level_zero_reads_the_processor_graphics_package_domain_as_the_energy_meter_reads_the_package()
    {
        // The development laptop has an Iris Xe in the processor and no Arc card, so the source is honestly unsupported.
        using var arc = new ArcSource();
        arc.Supported.ShouldBeFalse();
        arc.Unavailable.ShouldBe("no Intel Arc discrete GPU");

        // Told to include integrated graphics, it reads the Iris Xe's one power domain, which is the processor's package.
        using var integrated = new ArcSource(new LevelZeroSysman(), ArcSource.WindowsPowerState, includeIntegrated: true);
        integrated.Supported.ShouldBeTrue();
        using var meter = new EnergyMeter();
        meter.Available.ShouldBeTrue();

        var first = new SampleDraft();
        meter.Read();                          // both counters start their interval here
        integrated.Contribute(first);
        first.DGpuPresent.ShouldBeTrue();
        first.DGpuScope.ShouldBe(GpuPowerScope.Package);
        first.DGpuW.ShouldBeNull();

        Thread.Sleep(3000);
        var second = new SampleDraft();
        integrated.Contribute(second);
        var levelZero = second.DGpuW.ShouldNotBeNull();
        var package = meter.Read().PackageW.ShouldNotBeNull();
        output.WriteLine($"Level Zero {levelZero:0.000} W, energy meter package {package:0.000} W, {(levelZero - package) / package:P1}");

        // Both read the same processor package meter, which is why integrated graphics are skipped in the first place.
        levelZero.ShouldBe(package, package * 0.2);

        // Windows can say whether that device is switched off, found by the PCI ids Level Zero reported.
        DevicePowerState.FindDisplayAdapter(@"PCI\VEN_8086&DEV_9A49").ShouldNotBeNull();
    }
}
