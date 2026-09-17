using PowerLedger.Contracts;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class GraphicsSourcesTests
{
    private static readonly GpuAdapter Radeon = new("AMD Radeon RX 7800 XT", DiscreteGpu.AmdVendor, 16UL << 30, 0xD1A4, 0, false);

    [Fact]
    public void A_working_nvidia_source_is_the_only_graphics_source_and_the_others_are_never_built()
    {
        var nvidia = new FakeSource("nvidia-gpu", _ => { });
        var built = 0;

        var sources = MachineSensors.Graphics(nvidia, Build, Build, Build);

        sources.ShouldHaveSingleItem().ShouldBeSameAs(nvidia);
        built.ShouldBe(0);     // so neither AMD's libraries nor Level Zero nor DXGI is asked, and none wakes a switched-off card

        ISensorSource Build()
        {
            built++;
            return new FakeSource("built", _ => { });
        }
    }

    [Fact]
    public void Without_nvidia_the_amd_library_level_zero_and_the_load_counters_all_stand_in()
    {
        var nvidia = new FakeSource("nvidia-gpu", _ => { }) { Supported = false, Unavailable = "no NVIDIA driver installed" };

        var sources = MachineSensors.Graphics(
            nvidia,
            () => new FakeSource("amd-gpu", _ => { }),
            () => new FakeSource("arc-gpu", _ => { }),
            () => new FakeSource("gpu-load", _ => { }));

        // The vendors' libraries measure the watts and the counters fill the load, so all are wanted, and in that order.
        sources.Select(s => s.Name).ShouldBe(["nvidia-gpu", "amd-gpu", "arc-gpu", "gpu-load"]);
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
            () => new FakeSource("amd-gpu", _ => { }) { Supported = false, Unavailable = "no AMD driver installed" },
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
    public void An_arc_card_leaves_the_watts_amds_library_already_measured()
    {
        // A machine with both a discrete Radeon and a discrete Arc has one field between them for the card's watts.
        // AMD's library runs first, so Level Zero must not write over what it measured — and a sleeping Arc, which
        // answers a flat nought, must not turn the Radeon's draw into nothing at all.
        var sysman = new FakeSysman();
        var arc = new SysmanDevice(SysmanDevice.GpuType, DiscreteGpu.IntelVendor, 0x56A0, CoreFlags: 0, ExtendedFlags: 0);
        var card = sysman.Add(arc, PowerDomain.Card)[0];
        using var asleep = new ArcSource(sysman, _ => static () => true);
        using var awake = new ArcSource(sysman, _ => static () => false);

        var sleeping = Radeons(214.5);
        asleep.Contribute(sleeping);
        sleeping.DGpuW.ShouldBe(214.5);
        sleeping.DGpuScope.ShouldBe(GpuPowerScope.ChipOnly);
        sleeping.DGpuPresent.ShouldBeTrue();

        // Nor does an Arc that is awake and measuring watts of its own.
        awake.Contribute(Radeons(214.5));
        card.Draw(watts: 190, seconds: 1);
        var reading = Radeons(214.5);
        awake.Contribute(reading);

        reading.DGpuW.ShouldBe(214.5);
        reading.DGpuScope.ShouldBe(GpuPowerScope.ChipOnly);
    }

    [Fact]
    public void An_arc_card_on_its_own_still_fills_the_watts()
    {
        var sysman = new FakeSysman();
        var arc = new SysmanDevice(SysmanDevice.GpuType, DiscreteGpu.IntelVendor, 0x56A0, CoreFlags: 0, ExtendedFlags: 0);
        var card = sysman.Add(arc, PowerDomain.Card)[0];
        using var source = new ArcSource(sysman, _ => static () => false);

        source.Contribute(new SampleDraft());
        card.Draw(watts: 190, seconds: 1);
        var draft = new SampleDraft();
        source.Contribute(draft);

        draft.DGpuW.ShouldNotBeNull().ShouldBe(190, 1e-9);
        draft.DGpuScope.ShouldBe(GpuPowerScope.Board);
    }

    [Fact]
    public void A_graphics_source_that_will_not_be_built_does_not_leave_the_ones_before_it_open()
    {
        // Building the set is the one place these sources are held before anybody can close them: a factory that
        // throws halfway through would otherwise leave AMD's library open and its card's interfaces taken.
        var nvidia = new FakeSource("nvidia-gpu", _ => { }) { Supported = false, Unavailable = "no NVIDIA driver installed" };
        var amd = new FakeSource("amd-gpu", _ => { });
        var arc = new FakeSource("arc-gpu", _ => { });

        Should.Throw<InvalidOperationException>(() => MachineSensors.Graphics(
            nvidia, () => amd, () => arc, () => throw new InvalidOperationException("the counters would not open")));

        nvidia.Disposed.ShouldBeTrue();
        amd.Disposed.ShouldBeTrue();
        arc.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void The_assembled_set_never_has_two_sources_filling_the_discrete_gpu_watts()
    {
        using var sensors = MachineSensors.Create(() => true, () => false);
        var health = sensors.Sampler.Health;

        health.Count(h => h.Name is "nvidia-gpu" or "amd-gpu" or "arc-gpu" && h.Supported).ShouldBeLessThanOrEqualTo(1);
        if (health.Single(h => h.Name == "nvidia-gpu").Supported)
        {
            health.ShouldNotContain(h => h.Name == "amd-gpu" || h.Name == "arc-gpu" || h.Name == "gpu-load");
        }
        else
        {
            health.ShouldContain(h => h.Name == "amd-gpu");
            health.ShouldContain(h => h.Name == "arc-gpu");
            health.ShouldContain(h => h.Name == "gpu-load");
        }
    }

    [Fact]
    public void The_load_source_leaves_the_watts_the_amd_library_measured()
    {
        // The load source runs after the AMD source and fills the load beside its watts, never over them.
        var draft = new SampleDraft { DGpuPresent = true, DGpuW = 214.5, DGpuScope = GpuPowerScope.ChipOnly };
        const string Busy3D = "pid_4_luid_0x00000000_0x0000D1A4_phys_0_eng_0_engtype_3D";
        var counters = new Queue<EngineSnapshot>(
        [
            new EngineSnapshot(0, new Dictionary<string, long> { [Busy3D] = 0 }),
            new EngineSnapshot(10_000_000, new Dictionary<string, long> { [Busy3D] = 5_000_000 }),
        ]);
        var now = TimeSpan.Zero;
        var load = new GpuLoadSource(() => [Radeon], counters.Dequeue, () => now);

        load.Contribute(draft);
        now = GpuLoadSource.ReadEvery;
        load.Contribute(draft);

        draft.DGpuW.ShouldBe(214.5);
        draft.DGpuScope.ShouldBe(GpuPowerScope.ChipOnly);
        draft.DGpuLoad.ShouldBe(0.5);
    }

    /// <summary>A tick in which AMD's library has already measured a discrete Radeon's chip.</summary>
    private static SampleDraft Radeons(double watts)
        => new() { DGpuPresent = true, DGpuW = watts, DGpuScope = GpuPowerScope.ChipOnly };
}
