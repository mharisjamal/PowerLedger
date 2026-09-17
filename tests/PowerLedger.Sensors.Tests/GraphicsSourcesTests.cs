using PowerLedger.Contracts;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class GraphicsSourcesTests
{
    [Fact]
    public void A_working_nvidia_source_is_the_only_graphics_source_and_the_others_are_never_built()
    {
        var nvidia = new FakeSource("nvidia-gpu", _ => { });
        var built = 0;

        var sources = MachineSensors.Graphics(nvidia, Build, Build);

        sources.ShouldHaveSingleItem().ShouldBeSameAs(nvidia);
        built.ShouldBe(0);     // so neither Level Zero nor DXGI is asked, and neither wakes a switched-off card

        ISensorSource Build()
        {
            built++;
            return new FakeSource("built", _ => { });
        }
    }

    [Fact]
    public void Without_nvidia_level_zero_measures_an_arc_card_and_the_gpu_load_counters_stand_in()
    {
        var nvidia = new FakeSource("nvidia-gpu", _ => { }) { Supported = false, Unavailable = "no NVIDIA driver installed" };

        var sources = MachineSensors.Graphics(nvidia, () => new FakeSource("arc-gpu", _ => { }), () => new FakeSource("gpu-load", _ => { }));

        sources.Select(s => s.Name).ShouldBe(["nvidia-gpu", "arc-gpu", "gpu-load"]);
    }

    [Fact]
    public void The_gpu_load_counters_leave_the_watts_level_zero_measured()
    {
        // An Arc A770 both sources can see: Level Zero measures its watts, and Windows' counters give only its load.
        var sysman = new FakeSysman();
        var arc = new SysmanDevice(SysmanDevice.GpuType, DiscreteGpu.IntelVendor, 0x56A0, CoreFlags: 0, ExtendedFlags: 0);
        var card = sysman.Add(arc, PowerDomain.Card)[0];
        GpuAdapter[] adapters = [new("Intel(R) Arc(TM) A770 Graphics", DiscreteGpu.IntelVendor, 16UL << 30, 0xA770, 0, false)];
        var nvidia = new FakeSource("nvidia-gpu", _ => { }) { Supported = false, Unavailable = "no NVIDIA driver installed" };
        using var sampler = new Sampler(MachineSensors.Graphics(
            nvidia,
            () => new ArcSource(sysman, _ => static () => false),
            () => new GpuLoadSource(() => adapters, () => new EngineSnapshot(0, new Dictionary<string, long>()), () => TimeSpan.Zero)));

        sampler.Read(DateTimeOffset.UnixEpoch, 1);
        card.Draw(watts: 190, seconds: 1);
        var sample = sampler.Read(DateTimeOffset.UnixEpoch.AddSeconds(1), 1);

        sample.DGpuPresent.ShouldBeTrue();
        sample.DGpuW.ShouldNotBeNull().ShouldBe(190, 1e-9);
        sample.DGpuScope.ShouldBe(GpuPowerScope.Board);
    }

    [Fact]
    public void The_assembled_set_never_has_both_graphics_sources_working()
    {
        using var sensors = MachineSensors.Create(() => true, () => false);
        var health = sensors.Sampler.Health;

        health.Count(h => h.Name is "nvidia-gpu" or "gpu-load" && h.Supported).ShouldBeLessThanOrEqualTo(1);
        if (health.Single(h => h.Name == "nvidia-gpu").Supported)
        {
            health.ShouldNotContain(h => h.Name == "gpu-load" || h.Name == "arc-gpu");
        }
        else
        {
            health.ShouldContain(h => h.Name == "arc-gpu");
            health.ShouldContain(h => h.Name == "gpu-load");
        }
    }
}
