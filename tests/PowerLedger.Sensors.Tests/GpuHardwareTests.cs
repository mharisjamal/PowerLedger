using System.Diagnostics;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

[Trait("Category", "Hardware")]
public class GpuHardwareTests
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
}
