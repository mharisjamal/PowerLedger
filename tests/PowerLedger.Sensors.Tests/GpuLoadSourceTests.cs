using System.Runtime.InteropServices;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class GpuLoadSourceTests
{
    private static readonly GpuAdapter Radeon = new("AMD Radeon RX 7800 XT", DiscreteGpu.AmdVendor, 16UL << 30, 0xD1A4, 0, false);

    private static readonly GpuAdapter[] DevelopmentLaptop =
    [
        new("Intel(R) Iris(R) Xe Graphics", DiscreteGpu.IntelVendor, 128UL << 20, 0xD935, 0, false),
        new("NVIDIA GeForce MX330", 0x10DE, 1968UL << 20, 0xDCDD, 0, false),
        new("Microsoft Basic Render Driver", 0x1414, 0, 0xDCAA, 0, true),
    ];

    private TimeSpan _now;

    private SampleDraft Tick(GpuLoadSource source, double atSeconds)
    {
        _now = TimeSpan.FromSeconds(atSeconds);
        var draft = new SampleDraft();
        source.Contribute(draft);
        return draft;
    }

    [Fact]
    public void Without_an_amd_or_intel_card_the_source_says_so_and_claims_nothing()
    {
        var engines = new Engines(() => _now);
        var source = new GpuLoadSource(() => DevelopmentLaptop, engines.Read, () => _now);

        source.Supported.ShouldBeFalse();
        source.Unavailable.ShouldBe("no AMD or Intel discrete GPU");
        var draft = Tick(source, 0);
        draft.DGpuPresent.ShouldBeFalse();
        draft.DGpuLoad.ShouldBeNull();
        engines.Reads.ShouldBe(0);
    }

    [Fact]
    public void Windows_declining_to_answer_makes_the_source_unsupported_instead_of_throwing()
    {
        // A constructor that threw would stop the whole sensor set from being built, tick after tick.
        var refused = Should.NotThrow(() => new GpuLoadSource(
            () => throw new COMException("DXGI_ERROR_NOT_CURRENTLY_AVAILABLE", unchecked((int)0x887A0022)),
            () => throw new InvalidOperationException("never read"), () => _now));
        refused.Supported.ShouldBeFalse();
        refused.Unavailable.ShouldBe("Windows would not list the graphics adapters");

        var unpublished = new GpuLoadSource(() => [Radeon], () => throw new InvalidOperationException("never read"), () => _now, published: () => false);
        unpublished.Supported.ShouldBeFalse();
        unpublished.Unavailable.ShouldBe("Windows publishes no GPU load counters");
        Should.NotThrow(() => Tick(unpublished, 0)).DGpuPresent.ShouldBeFalse();
    }

    [Fact]
    public void The_first_tick_claims_the_card_before_its_load_is_known()
    {
        var source = new GpuLoadSource(() => [Radeon], new Engines(() => _now) { Busy = 0.4 }.Read, () => _now);

        source.Supported.ShouldBeTrue();
        source.Unavailable.ShouldBeNull();
        var draft = Tick(source, 0);
        draft.DGpuPresent.ShouldBeTrue();
        draft.DGpuLoad.ShouldBeNull();       // one snapshot has no interval; the model counts the card as idle
        draft.DGpuW.ShouldBeNull();
    }

    [Fact]
    public void Windows_is_read_every_five_seconds_and_the_load_held_in_between()
    {
        var engines = new Engines(() => _now) { Busy = 0.4 };
        var source = new GpuLoadSource(() => [Radeon], engines.Read, () => _now);

        Tick(source, 0);
        for (var second = 1; second < 5; second++) Tick(source, second).DGpuLoad.ShouldBeNull();
        engines.Reads.ShouldBe(1);

        var measured = Tick(source, 5);
        measured.DGpuPresent.ShouldBeTrue();
        measured.DGpuLoad.ShouldNotBeNull().ShouldBe(0.4, 1e-9);
        measured.DGpuW.ShouldBeNull();      // load only: the power model turns it into watts
        engines.Reads.ShouldBe(2);

        engines.Busy = 0.9;
        for (var second = 6; second < 10; second++) Tick(source, second).DGpuLoad.ShouldNotBeNull().ShouldBe(0.4, 1e-9);
        engines.Reads.ShouldBe(2);

        Tick(source, 10).DGpuLoad.ShouldNotBeNull().ShouldBe(0.9, 1e-9);
        engines.Reads.ShouldBe(3);
    }

    [Fact]
    public void A_driver_that_restarted_renames_the_card_and_the_load_starts_over()
    {
        var engines = new Engines(() => _now) { Busy = 0.4 };
        GpuAdapter[] adapters = [Radeon];
        var source = new GpuLoadSource(() => adapters, engines.Read, () => _now);
        Tick(source, 0);
        Tick(source, 5).DGpuLoad.ShouldNotBeNull().ShouldBe(0.4, 1e-9);

        // After a driver update the same card comes back under a new LUID, and its processes' counts start again.
        adapters = [Radeon with { LuidLow = 0xE001 }];
        engines.Rename(adapters[0].CounterLuid);

        var restarted = Tick(source, 10);
        restarted.DGpuPresent.ShouldBeTrue();
        restarted.DGpuLoad.ShouldBeNull();
        Tick(source, 15).DGpuLoad.ShouldNotBeNull().ShouldBe(0.4, 1e-9);
    }

    [Fact]
    public void A_card_that_went_away_is_no_longer_claimed()
    {
        var engines = new Engines(() => _now) { Busy = 0.4 };
        GpuAdapter[] adapters = [Radeon];
        var source = new GpuLoadSource(() => adapters, engines.Read, () => _now);
        Tick(source, 0);

        adapters = DevelopmentLaptop;
        var gone = Tick(source, 5);
        gone.DGpuPresent.ShouldBeFalse();
        gone.DGpuLoad.ShouldBeNull();
        engines.Reads.ShouldBe(1);
    }

    [Fact]
    public void Odd_counter_data_never_throws()
    {
        const string instance = "pid_9_luid_0x00000000_0x0000D1A4_phys_0_eng_0_engtype_3D";
        var snapshots = new Queue<EngineSnapshot>(
        [
            new(0, new Dictionary<string, long>()),                                        // no instances at all
            new(50_000_000, new Dictionary<string, long> { [instance] = 20_000_000 }),
            new(50_000_000, new Dictionary<string, long> { [instance] = 20_000_000 }),     // no time passed
            new(100_000_000, new Dictionary<string, long> { [instance] = 1_000 }),         // the count went backwards
            new(150_000_000, new Dictionary<string, long> { [instance] = long.MaxValue }), // far more time than passed
            new(200_000_000, new Dictionary<string, long> { ["_Total"] = 7 }),            // the process ended
        ]);
        var source = new GpuLoadSource(() => [Radeon], snapshots.Dequeue, () => _now);

        for (var read = 0; read < 6; read++)
        {
            var draft = Should.NotThrow(() => Tick(source, read * 5));
            draft.DGpuPresent.ShouldBeTrue();
            if (draft.DGpuLoad is { } load) load.ShouldBeInRange(0, 1);
        }
    }

    [Fact]
    public void A_read_windows_refuses_is_left_to_the_samplers_backoff_and_claims_nothing()
    {
        var engines = new Engines(() => _now) { Busy = 0.4 };
        var refuse = true;
        var source = new GpuLoadSource(
            () => [Radeon], () => refuse ? throw new InvalidOperationException("Category does not exist.") : engines.Read(), () => _now);
        using var sampler = new Sampler([source]);

        var refused = sampler.Read(DateTimeOffset.UnixEpoch, 1);
        refused.DGpuPresent.ShouldBeFalse();
        refused.DGpuLoad.ShouldBeNull();
        sampler.Health.Single().Failures.ShouldBe(1);
        sampler.Health.Single().LastError.ShouldBe("Category does not exist.");

        refuse = false;
        sampler.Read(DateTimeOffset.UnixEpoch.AddSeconds(1), 1).DGpuPresent.ShouldBeFalse();   // the one tick it backs off
        sampler.Read(DateTimeOffset.UnixEpoch.AddSeconds(2), 1).DGpuPresent.ShouldBeTrue();
        engines.Reads.ShouldBe(1);
    }

    [Fact]
    public void The_source_is_named_for_the_status_screen()
        => new GpuLoadSource(() => [Radeon], new Engines(() => _now).Read, () => _now).Name.ShouldBe("gpu-load");

    /// <summary>One process keeping the card's 3D engine busy for a set share of the time.</summary>
    private sealed class Engines(Func<TimeSpan> clock)
    {
        private string _luid = Radeon.CounterLuid;
        private TimeSpan _last;
        private double _busySeconds;

        /// <summary>The share of the time since the last read the engine was busy.</summary>
        public double Busy { get; set; }

        public int Reads { get; private set; }

        /// <summary>The adapter's new LUID after a driver restart; the process's count starts again.</summary>
        public void Rename(string luid)
        {
            _luid = luid;
            _busySeconds = 0;
        }

        public EngineSnapshot Read()
        {
            Reads++;
            var now = clock();
            _busySeconds += (now - _last).TotalSeconds * Busy;
            _last = now;
            return new EngineSnapshot(now.Ticks, new Dictionary<string, long>
            {
                [$"pid_4242_{_luid}_phys_0_eng_0_engtype_3D"] = (long)Math.Round(_busySeconds * TimeSpan.TicksPerSecond),
            });
        }
    }
}
