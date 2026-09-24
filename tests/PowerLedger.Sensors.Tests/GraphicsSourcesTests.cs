using PowerLedger.Contracts;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class GraphicsSourcesTests
{
    private static readonly GpuAdapter Radeon = new("AMD Radeon RX 7800 XT", DiscreteGpu.AmdVendor, 16UL << 30, 0xD1A4, 0, false, DeviceId: 0x747E);

    [Fact]
    public void A_working_nvidia_source_is_the_only_graphics_source_and_the_others_are_never_built()
    {
        var nvidia = new FakeSource("nvidia-gpu", _ => { });
        var built = 0;

        var sources = MachineSensors.Graphics(nvidia, Build, Build, Build, otherCards: () => false, nvidiaLoadsEveryCard: () => true);

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
        var asked = false;

        var sources = MachineSensors.Graphics(
            nvidia,
            () => new FakeSource("amd-gpu", _ => { }),
            () => new FakeSource("arc-gpu", _ => { }),
            () => new FakeSource("gpu-load", _ => { }),
            otherCards: () => asked = true);

        // The vendors' libraries measure the watts and the counters fill the load, so all are wanted, and in that order.
        sources.Select(s => s.Name).ShouldBe(["nvidia-gpu", "amd-gpu", "arc-gpu", "gpu-load"]);
        asked.ShouldBeFalse();  // WMI is not asked when there is no NVIDIA library to spare the others
    }

    [Fact]
    public void Beside_an_nvidia_card_another_makers_card_brings_in_every_other_source()
    {
        var nvidia = new FakeSource("nvidia-gpu", _ => { });

        var sources = MachineSensors.Graphics(
            nvidia,
            () => new FakeSource("amd-gpu", _ => { }),
            () => new FakeSource("arc-gpu", _ => { }),
            () => new FakeSource("gpu-load", _ => { }),
            otherCards: () => true);

        sources.Select(s => s.Name).ShouldBe(["nvidia-gpu", "amd-gpu", "arc-gpu", "gpu-load"]);
    }

    [Fact]
    public void An_nvidia_card_nvml_gives_no_utilisation_for_brings_in_the_load_counters_alone()
    {
        // A Fermi GeForce: NVML answers, but with neither power nor utilisation.
        var nvidia = new FakeSource("nvidia-gpu", _ => { });

        var sources = MachineSensors.Graphics(
            nvidia,
            () => throw new InvalidOperationException("AMD's library is not wanted"),
            () => throw new InvalidOperationException("Level Zero is not wanted"),
            () => new FakeSource("gpu-load", _ => { }),
            otherCards: () => false, nvidiaLoadsEveryCard: () => false);

        sources.Select(s => s.Name).ShouldBe(["nvidia-gpu", "gpu-load"]);
    }

    [Fact]
    public void The_gpu_load_counters_leave_the_watts_level_zero_measured()
    {
        // An Arc A770 both sources can see: Level Zero measures its watts, and Windows' counters give only its load.
        var sysman = new FakeSysman();
        var arc = new SysmanDevice(SysmanDevice.GpuType, DiscreteGpu.IntelVendor, 0x56A0, CoreFlags: 0, ExtendedFlags: 0);
        var card = sysman.Add(arc, PowerDomain.Card)[0];
        GpuAdapter[] adapters = [new("Intel(R) Arc(TM) A770 Graphics", DiscreteGpu.IntelVendor, 16UL << 30, 0xA770, 0, false, DeviceId: 0x56A0)];
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
        var only = sample.Gpus.ShouldNotBeNull().ShouldHaveSingleItem();       // one card, not the library's and the counters' both
        only.Name.ShouldBe("Intel(R) Arc(TM) A770 Graphics");
        only.RatedW.ShouldBe(225);
    }

    [Fact]
    public void A_radeon_and_an_arc_card_both_count()
    {
        // A machine with both a discrete Radeon and a discrete Arc: each is a card of its own, and a sleeping Arc's nought
        // is its own, not the Radeon's.
        var sysman = new FakeSysman();
        var arc = new SysmanDevice(SysmanDevice.GpuType, DiscreteGpu.IntelVendor, 0x56A0, CoreFlags: 0, ExtendedFlags: 0);
        var card = sysman.Add(arc, PowerDomain.Card)[0];
        using var asleep = new ArcSource(sysman, _ => static () => true);
        using var awake = new ArcSource(sysman, _ => static () => false);

        var sleeping = Radeons(214.5);
        asleep.Contribute(sleeping);
        sleeping.Gpus.Select(g => g.Watts).ShouldBe([214.5, 0]);
        sleeping.DGpuW.ShouldBe(214.5);
        sleeping.DGpuScope.ShouldBe(GpuPowerScope.ChipOnly);

        awake.Contribute(Radeons(214.5));
        card.Draw(watts: 190, seconds: 1);
        var reading = Radeons(214.5);
        awake.Contribute(reading);

        reading.Gpus.Count.ShouldBe(2);
        reading.DGpuW.ShouldNotBeNull().ShouldBe(214.5 + 190, 1e-9);
        reading.Gpus[1].VendorId.ShouldBe(DiscreteGpu.IntelVendor);
        reading.Gpus[1].DeviceId.ShouldBe(0x56A0u);
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
    public void The_assembled_set_reads_nvidia_first_and_builds_no_source_twice()
    {
        using var sensors = MachineSensors.Create(() => true, () => false);
        var graphics = sensors.Sampler.Health.Where(h => h.Name is "nvidia-gpu" or "amd-gpu" or "arc-gpu" or "gpu-load").Select(h => h.Name).ToList();

        graphics[0].ShouldBe("nvidia-gpu");
        graphics.ShouldBeUnique();
        if (!sensors.Sampler.Health.Single(h => h.Name == "nvidia-gpu").Supported)
        {
            graphics.ShouldBe(["nvidia-gpu", "amd-gpu", "arc-gpu", "gpu-load"]);
        }
        else if (graphics.Contains("amd-gpu"))
        {
            graphics.ShouldBe(["nvidia-gpu", "amd-gpu", "arc-gpu", "gpu-load"]);
        }
    }

    [Fact]
    public void The_load_source_leaves_the_watts_the_amd_library_measured_and_fills_in_the_rest()
    {
        // The load source runs after the AMD source and fills the load, name and rating beside its watts, never over them.
        var draft = Radeons(214.5);
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
        draft = Radeons(214.5);
        load.Contribute(draft);

        var card = draft.Gpus.ShouldHaveSingleItem();
        card.Watts.ShouldBe(214.5);
        card.Scope.ShouldBe(GpuPowerScope.ChipOnly);
        card.Load.ShouldBe(0.5);
        card.Name.ShouldBe("AMD Radeon RX 7800 XT");
        card.Rating.ShouldBe(new GpuRating(263, Rough: false));
    }

    /// <summary>A tick in which AMD's library has already measured a discrete Radeon's chip.</summary>
    private static SampleDraft Radeons(double watts)
    {
        var draft = new SampleDraft();
        var card = draft.AddGpu(DiscreteGpu.AmdVendor, 0x747E);
        card.Watts = watts;
        card.Scope = GpuPowerScope.ChipOnly;
        return draft;
    }
}
