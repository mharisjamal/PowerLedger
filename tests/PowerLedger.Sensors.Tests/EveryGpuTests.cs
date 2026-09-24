using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

/// <summary>
/// Feedback issue #4 and its kin: every graphics card is found and its power measured or estimated. A Quadro 6000 on an
/// R390 driver, a Fermi GeForce whose NVML says nothing, a workstation with two cards, another maker's card beside an
/// NVIDIA one, and a card no table knows.
/// </summary>
public class EveryGpuTests
{
    private const ulong GiB = 1UL << 30;
    private const double Idle = PowerModel.GpuIdleW;

    private static readonly GpuAdapter Quadro6000 = new("NVIDIA Quadro 6000", DiscreteGpu.NvidiaVendor, 6 * GiB, 0x1000, 0, false, DeviceId: 0x06D8);
    private static readonly GpuAdapter Gtx1080 = new("NVIDIA GeForce GTX 1080", DiscreteGpu.NvidiaVendor, 8 * GiB, 0x2000, 0, false, DeviceId: 0x1B80);
    private static readonly GpuAdapter Matrox = new("Matrox C900", 0x102B, 2 * GiB, 0x3000, 0, false, DeviceId: 0x0540);

    private TimeSpan _now;

    [Fact]
    public void A_quadro_6000_nvml_cannot_open_is_estimated_from_windows_load_and_its_own_rating()
    {
        // The report: no NVML in System32, so the App said "0.0 W, no discrete GPU the service can read".
        var engines = new Engines(() => _now) { [Quadro6000] = 0.5 };
        var sample = TwoReads(NoNvml(), Counters(engines, Quadro6000));

        var card = sample.Gpus.ShouldNotBeNull().ShouldHaveSingleItem();
        card.ShouldBe(new GpuCard("NVIDIA Quadro 6000", Watts: null, Load: 0.5, GpuPowerScope.Board, RatedW: 204));
        Watts(sample).ShouldBe(Idle + (204 - Idle) * 0.5, 1e-9);
        sample.DGpuW.ShouldBeNull();         // so the frame says it was not measured
    }

    [Fact]
    public void A_quadro_6000_whose_nvml_gives_no_power_is_estimated_from_its_utilisation()
    {
        var nvidia = Nvidia(Card("Quadro 6000", 0x06D8, 6 * GiB, watts: null, load: 0.5));

        var sample = TwoReads(nvidia);

        sample.Gpus.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new GpuCard("Quadro 6000", null, 0.5, GpuPowerScope.Board, 204));
        Watts(sample).ShouldBe(Idle + (204 - Idle) * 0.5, 1e-9);
    }

    [Fact]
    public void A_card_whose_nvml_gives_neither_power_nor_utilisation_takes_its_load_from_windows()
    {
        // A Fermi GeForce: NVML answers, but "not supported" for both.
        var nvidia = Nvidia(Card("Quadro 6000", 0x06D8, 6 * GiB, watts: null, load: null));
        var engines = new Engines(() => _now) { [Quadro6000] = 0.3 };

        var sample = TwoReads(nvidia, Counters(engines, Quadro6000));

        var card = sample.Gpus.ShouldNotBeNull().ShouldHaveSingleItem();     // one card, not NVML's and the counters' both
        card.Name.ShouldBe("Quadro 6000");
        card.Load.ShouldNotBeNull().ShouldBe(0.3, 1e-9);
        card.RatedW.ShouldBe(204);
        Watts(sample).ShouldBe(Idle + (204 - Idle) * 0.3, 1e-9);
    }

    [Fact]
    public void Two_cards_one_measured_and_one_estimated_are_summed_and_neither_is_counted_twice()
    {
        var nvidia = Nvidia(
            Card("NVIDIA GeForce GTX 1080", 0x1B80, 8 * GiB, watts: 150, load: 0.6),
            Card("Quadro 6000", 0x06D8, 6 * GiB, watts: null, load: 0.25));
        var engines = new Engines(() => _now) { [Quadro6000] = 0.9, [Gtx1080] = 0.9 };

        var sample = TwoReads(nvidia, Counters(engines, Quadro6000, Gtx1080));

        sample.Gpus.ShouldNotBeNull().Count.ShouldBe(2);
        sample.Gpus.Select(g => g.Load).ShouldBe([0.6, 0.25]);                // NVML's own loads stand
        Watts(sample).ShouldBe(150 + Idle + (204 - Idle) * 0.25, 1e-9);
        sample.DGpuW.ShouldBeNull();
        sample.DGpuLoad.ShouldBe(0.6);
    }

    [Fact]
    public void Two_measured_cards_are_measured_together()
    {
        var sample = TwoReads(Nvidia(
            Card("NVIDIA GeForce GTX 1080", 0x1B80, 8 * GiB, watts: 150, load: 0.6),
            Card("NVIDIA Quadro RTX 4000", 0x1EB1, 8 * GiB, watts: 60, load: 0.1)));

        Watts(sample).ShouldBe(210, 1e-9);
        sample.DGpuW.ShouldBe(210);
    }

    [Fact]
    public void Another_makers_card_beside_an_nvidia_one_is_added_by_windows_load_counters_with_a_rough_rating()
    {
        var nvidia = Nvidia(Card("Quadro 6000", 0x06D8, 6 * GiB, watts: 120, load: 0.4));
        var engines = new Engines(() => _now) { [Quadro6000] = 0.4, [Matrox] = 1.0 };

        var sample = TwoReads(nvidia, Counters(engines, Quadro6000, Matrox));

        sample.Gpus.ShouldNotBeNull().Count.ShouldBe(2);
        sample.Gpus[1].ShouldBe(new GpuCard("Matrox C900", null, 1.0, GpuPowerScope.Board, 75));
        Watts(sample).ShouldBe(120 + 75, 1e-9);
    }

    [Fact]
    public void Two_identical_cards_are_two_cards_not_four()
    {
        var twin = Quadro6000 with { LuidLow = 0x1001 };
        var nvidia = Nvidia(
            Card("Quadro 6000", 0x06D8, 6 * GiB, watts: 100, load: 0.2),
            Card("Quadro 6000", 0x06D8, 6 * GiB, watts: 110, load: 0.3));
        var engines = new Engines(() => _now) { [Quadro6000] = 0.2, [twin] = 0.3 };

        var sample = TwoReads(nvidia, Counters(engines, Quadro6000, twin));

        sample.Gpus.ShouldNotBeNull().Count.ShouldBe(2);
        Watts(sample).ShouldBe(210, 1e-9);
    }

    [Fact]
    public void A_card_whose_pci_device_nvml_would_not_give_is_still_recognised_by_its_maker()
    {
        var nvidia = Nvidia(Card("Quadro 6000", 0, 6 * GiB, watts: 120, load: 0.4));
        var engines = new Engines(() => _now) { [Quadro6000] = 0.9 };

        var sample = TwoReads(nvidia, Counters(engines, Quadro6000));

        sample.Gpus.ShouldNotBeNull().ShouldHaveSingleItem().Watts.ShouldBe(120);
    }

    [Fact]
    public void A_card_only_windows_counters_read_and_windows_has_switched_off_draws_nothing()
    {
        // The development laptop without NVML: its MX330 sleeps almost all the time.
        var mx330 = new GpuAdapter("NVIDIA GeForce MX330", DiscreteGpu.NvidiaVendor, 1968UL << 20, 0xDCDD, 0, false, DeviceId: 0x1D16);
        var engines = new Engines(() => _now) { [mx330] = 0 };
        var counters = new GpuLoadSource(() => [mx330], engines.Read, () => _now, powerState: adapter =>
        {
            adapter.DeviceId.ShouldBe(0x1D16u);
            return static () => true;
        });

        var sample = TwoReads(NoNvml(), counters);

        sample.Gpus.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new GpuCard("NVIDIA GeForce MX330", 0, 0, GpuPowerScope.Board, 10));
        Watts(sample).ShouldBe(0);
    }

    [Fact]
    public void A_card_no_table_knows_is_never_nothing()
    {
        var unknown = new GpuAdapter("NVIDIA Imaginary 9000", DiscreteGpu.NvidiaVendor, 12 * GiB, 0x4000, 0, false, DeviceId: 0x9999);
        var engines = new Engines(() => _now) { [unknown] = 1.0 };

        var sample = TwoReads(NoNvml(), Counters(engines, unknown));

        sample.Gpus.ShouldNotBeNull().ShouldHaveSingleItem().RatedW.ShouldBe(250);   // rough, from its 12 GB
        Watts(sample).ShouldBe(250, 1e-9);
    }

    /// <summary>Two ticks five seconds apart, so the counters have an interval; the second one, validated.</summary>
    private Sample TwoReads(params ISensorSource[] sources)
    {
        using var sampler = new Sampler(sources);
        var validator = new SampleValidator();
        _now = TimeSpan.Zero;
        validator.Validate(sampler.Read(DateTimeOffset.UnixEpoch, 1));
        _now = GpuLoadSource.ReadEvery;
        return validator.Validate(sampler.Read(DateTimeOffset.UnixEpoch.AddSeconds(5), 1));
    }

    /// <summary>The GPU watts a desktop's model makes of the sample; the desktop's own GPU figure is 75 W.</summary>
    private static double Watts(Sample sample)
        => new PowerModel(MachineProfile.DefaultDesktop, HardwareFacts.DesktopDefaults, new PowerModelOptions(), new NoBaseline())
            .Evaluate(sample).Components.Gpu;

    private static NvidiaSource NoNvml() => new(() => new GpuReading(false, null, null), present: false, "no NVIDIA driver installed");

    private static NvidiaSource Nvidia(params NvidiaCard[] cards) => new(cards, present: true, unavailable: null, loadsEveryCard: true);

    private static NvidiaCard Card(string name, uint deviceId, ulong memory, double? watts, double? load)
        => new(name, deviceId, TdpTable.Bundled.GpuOrGuess(name, memory), () => new GpuReading(true, watts, load), static () => false);

    private GpuLoadSource Counters(Engines engines, params GpuAdapter[] adapters) => new(() => adapters, engines.Read, () => _now);

    private sealed class NoBaseline : IBaselineProvider
    {
        public double? GetBaseline(int bucket) => null;
    }

    /// <summary>One process keeping each card's 3D engine busy for a set share of the time.</summary>
    private sealed class Engines(Func<TimeSpan> clock)
    {
        private readonly Dictionary<string, double> _busy = [];
        private readonly Dictionary<string, double> _busySeconds = [];
        private TimeSpan _last;

        public double this[GpuAdapter adapter]
        {
            set => _busy[adapter.CounterLuid] = value;
        }

        public EngineSnapshot Read()
        {
            var now = clock();
            var times = new Dictionary<string, long>();
            foreach (var (luid, busy) in _busy)
            {
                _busySeconds[luid] = _busySeconds.GetValueOrDefault(luid) + (now - _last).TotalSeconds * busy;
                times[$"pid_4242_{luid}_phys_0_eng_0_engtype_3D"] = (long)Math.Round(_busySeconds[luid] * TimeSpan.TicksPerSecond);
            }
            _last = now;
            return new EngineSnapshot(now.Ticks, times);
        }
    }
}
