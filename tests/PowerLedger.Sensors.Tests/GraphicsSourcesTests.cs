using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class GraphicsSourcesTests
{
    [Fact]
    public void A_working_nvidia_source_is_the_only_graphics_source_and_the_other_is_never_built()
    {
        var nvidia = new FakeSource("nvidia-gpu", _ => { });
        var built = 0;

        var sources = MachineSensors.Graphics(nvidia, () =>
        {
            built++;
            return new FakeSource("gpu-load", _ => { });
        });

        sources.ShouldHaveSingleItem().ShouldBeSameAs(nvidia);
        built.ShouldBe(0);     // so DXGI is never asked, and never wakes a switched-off card
    }

    [Fact]
    public void Without_nvidia_the_gpu_load_counters_stand_in()
    {
        var nvidia = new FakeSource("nvidia-gpu", _ => { }) { Supported = false, Unavailable = "no NVIDIA driver installed" };

        var sources = MachineSensors.Graphics(nvidia, () => new FakeSource("gpu-load", _ => { }));

        sources.Select(s => s.Name).ShouldBe(["nvidia-gpu", "gpu-load"]);
    }

    [Fact]
    public void The_assembled_set_never_has_both_graphics_sources_working()
    {
        using var sensors = MachineSensors.Create(() => true, () => false);
        var health = sensors.Sampler.Health;

        health.Count(h => h.Name is "nvidia-gpu" or "gpu-load" && h.Supported).ShouldBeLessThanOrEqualTo(1);
        if (health.Single(h => h.Name == "nvidia-gpu").Supported) health.ShouldNotContain(h => h.Name == "gpu-load");
        else health.ShouldContain(h => h.Name == "gpu-load");
    }
}
