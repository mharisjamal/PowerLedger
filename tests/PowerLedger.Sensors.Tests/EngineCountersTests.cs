using System.ComponentModel;
using PowerLedger.Core;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

/// <summary>Plan AE follow-up: where NVIDIA's library reads every card, the load counters are not read, so the Apps page
/// reads the GPU Engine counters itself and tells the adapters apart by Windows' DirectX registry, which wakes no card.</summary>
public class EngineCountersTests
{
    private static readonly GpuAdapter GeForce = new("NVIDIA GeForce MX330", DiscreteGpu.NvidiaVendor, 2UL << 30, 0xDCE0, 0, false, 0x1D16);
    private static readonly GpuAdapter Iris = new("Intel(R) Iris(R) Xe Graphics", DiscreteGpu.IntelVendor, 128UL << 20, 0xD938, 0, false, 0x9A49);
    private static readonly GpuAdapter BasicRender = new("Microsoft Basic Render Driver", DiscreteGpu.MicrosoftVendor, 0, 0xDCAD, 0, true, 0x8C);

    private static EngineSnapshot Snapshot(long at, params (string Instance, long Time)[] times)
        => new(at, times.ToDictionary(t => t.Instance, t => t.Time, StringComparer.OrdinalIgnoreCase));

    private static string On(GpuAdapter adapter, uint pid, string engine = "eng_0_engtype_3D") => $"pid_{pid}_{adapter.CounterLuid}_phys_0_{engine}";

    [Fact]
    public void Each_process_s_time_on_the_card_and_on_the_processor_s_graphics_comes_from_two_reads()
    {
        var reads = new Queue<EngineSnapshot>(
        [
            Snapshot(0, (On(GeForce, 10), 1000), (On(Iris, 20), 500), (On(BasicRender, 30), 7)),
            Snapshot(50_000_000, (On(GeForce, 10), 4000), (On(GeForce, 10, "eng_3_engtype_VideoDecode"), 700), (On(Iris, 20), 800), (On(BasicRender, 30), 99)),
        ]);
        var adapterReads = 0;
        var counters = new EngineCounters(reads.Dequeue, () =>
        {
            adapterReads++;
            return [GeForce, Iris, BasicRender];
        });

        counters.Read().ShouldBeNull();                             // one read is no window yet
        var work = counters.Read().ShouldNotBeNull();
        work.ByProcess.Keys.ShouldBe([10u, 20u], ignoreOrder: true);
        work.ByProcess[10].ShouldBe(new GraphicsTime(CardLoad: 3000, CardAny: 3700, ProcessorLoad: 0, ProcessorAny: 0));
        work.ByProcess[20].ShouldBe(new GraphicsTime(0, 0, ProcessorLoad: 300, ProcessorAny: 300));
        adapterReads.ShouldBe(1);                                   // the registry is asked once, while the adapters stay
    }

    [Fact]
    public void An_adapter_new_to_the_counters_has_the_registry_asked_again_once()
    {
        var restarted = GeForce with { LuidLow = 0xE001 };          // a driver restart gives the card a new LUID
        var reads = new Queue<EngineSnapshot>(
        [
            Snapshot(0, (On(GeForce, 10), 1000)),
            Snapshot(1, (On(restarted, 10), 10)),
            Snapshot(2, (On(restarted, 10), 60)),
            Snapshot(3, (On(restarted, 10), 90), ("pid_11_luid_0x00000000_0x0000FFFF_phys_0_eng_0_engtype_3D", 5)),
            Snapshot(4, (On(restarted, 10), 95), ("pid_11_luid_0x00000000_0x0000FFFF_phys_0_eng_0_engtype_3D", 9)),
        ]);
        List<GpuAdapter> adapters = [GeForce, Iris];
        var adapterReads = 0;
        var counters = new EngineCounters(reads.Dequeue, () =>
        {
            adapterReads++;
            return adapters;
        });

        counters.Read();
        adapters = [restarted, Iris];
        counters.Read().ShouldNotBeNull().ByProcess[10].CardLoad.ShouldBe(10);   // all of it began since
        adapterReads.ShouldBe(2);
        counters.Read().ShouldNotBeNull().ByProcess[10].CardLoad.ShouldBe(50);
        counters.Read();                                            // an adapter the registry does not know: asked once more
        counters.Read().ShouldNotBeNull().ByProcess.Keys.ShouldBe([10u]);
        adapterReads.ShouldBe(3);
    }

    [Fact]
    public void Counters_that_fail_or_a_registry_that_names_no_card_give_nothing_so_the_processor_s_split_is_followed()
    {
        var failing = true;
        var at = 0L;
        var counters = new EngineCounters(() => failing ? throw new Win32Exception(5) : Snapshot(at++, (On(GeForce, 10), at * 100)), () => [GeForce]);
        counters.Read().ShouldBeNull();
        failing = false;
        counters.Read().ShouldBeNull();                             // starts over
        counters.Read().ShouldNotBeNull();

        var noCard = new EngineCounters(() => Snapshot(at++, (On(Iris, 20), at * 100)), () => [Iris]);
        noCard.Read();
        noCard.Read().ShouldBeNull();

        new EngineCounters(() => throw new InvalidOperationException("not published"), () => [GeForce], published: () => false).Read().ShouldBeNull();
    }

    /// <summary>Review item 1: the counters are a sensor source, read under the sensor watchdog every five seconds and held
    /// between, and read no more once the registry names no card to split.</summary>
    [Fact]
    public void As_a_sensor_source_the_counters_are_read_every_five_seconds_and_no_more_once_there_is_no_card()
    {
        var now = TimeSpan.Zero;
        var reads = 0;
        var counters = new EngineCounters(() => Snapshot(++reads, (On(GeForce, 10), reads * 100L)), () => [GeForce, Iris]);
        var source = new EngineCountersSource(counters, () => now);

        source.Name.ShouldBe("gpu-engines");
        source.Supported.ShouldBeTrue();
        var first = new SampleDraft();
        source.Contribute(first);
        first.Graphics.ShouldBeNull();                              // one read is no window yet
        now = TimeSpan.FromSeconds(1);
        source.Contribute(new SampleDraft());
        reads.ShouldBe(1);
        now = TimeSpan.FromSeconds(5);
        var second = new SampleDraft();
        source.Contribute(second);
        reads.ShouldBe(2);
        second.Graphics.ShouldNotBeNull().ByProcess[10].CardLoad.ShouldBe(100);
        now = TimeSpan.FromSeconds(6);
        var held = new SampleDraft();
        source.Contribute(held);
        held.Graphics.ShouldBeSameAs(second.Graphics);

        var noCardReads = 0;
        var noCard = new EngineCountersSource(new EngineCounters(() => Snapshot(++noCardReads, (On(Iris, 20), 1)), () => [Iris]), () => now);
        noCard.Contribute(new SampleDraft());
        noCard.Supported.ShouldBeFalse();
        noCard.Unavailable.ShouldNotBeNull();
        noCardReads.ShouldBe(1);
    }

    [Fact]
    public void The_registry_s_last_entry_for_a_LUID_wins_and_its_LUID_reads_as_the_counters_name_it()
    {
        var adapters = DirectXAdapters.From(
        [
            new DirectXAdapters.Entry("Old card", DiscreteGpu.AmdVendor, 0x73BF, 8UL << 30, Luid: 0xDCE0, LastSeen: 100),
            new DirectXAdapters.Entry("NVIDIA GeForce MX330", DiscreteGpu.NvidiaVendor, 0x1D16, 2UL << 30, Luid: 0xDCE0, LastSeen: 200),
            new DirectXAdapters.Entry("Microsoft Basic Render Driver", DiscreteGpu.MicrosoftVendor, 0x8C, 0, Luid: 0x1_0000_DCAD, LastSeen: 200),
        ]);
        adapters.Count.ShouldBe(2);
        adapters[0].ShouldBe(GeForce);
        adapters[0].CounterLuid.ShouldBe("luid_0x00000000_0x0000DCE0");
        adapters[1].CounterLuid.ShouldBe("luid_0x00000001_0x0000DCAD");
        adapters[1].Software.ShouldBeTrue();
    }
}
