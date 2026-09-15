using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class GpuEngineLoadTests
{
    private const string Card = "luid_0x00000000_0x0000D1A4";
    private const string ProcessorGraphics = "luid_0x00000000_0x0000D935";
    private const long Second = 10_000_000;

    private static string Instance(int pid, int engine, string type = "3D", string luid = Card)
        => $"pid_{pid}_{luid}_phys_0_eng_{engine}_engtype_{type}";

    /// <summary>A snapshot taken <paramref name="seconds"/> in, with each instance's engine time so far.</summary>
    private static EngineSnapshot At(double seconds, params (string Instance, double BusySeconds)[] instances)
        => new((long)(seconds * Second), instances.ToDictionary(i => i.Instance, i => (long)(i.BusySeconds * Second)));

    [Fact]
    public void An_engines_processes_add_up()
    {
        var before = At(0, (Instance(100, 0), 0), (Instance(200, 0), 0));
        var after = At(1, (Instance(100, 0), 0.3), (Instance(200, 0), 0.25));

        GpuEngines.Busiest(before, after, Card).ShouldNotBeNull().ShouldBe(0.55, 1e-9);
    }

    [Fact]
    public void The_busiest_engine_is_the_cards_load()
    {
        var before = At(10, (Instance(100, 0), 4), (Instance(100, 2, "Copy"), 1), (Instance(300, 1, "VideoDecode"), 0));
        var after = At(15, (Instance(100, 0), 5), (Instance(100, 2, "Copy"), 1.5), (Instance(300, 1, "VideoDecode"), 3.5));

        // 3D 20 %, copy 10 %, video decode 70 %.
        GpuEngines.Busiest(before, after, Card).ShouldNotBeNull().ShouldBe(0.7, 1e-9);
    }

    [Fact]
    public void An_engine_is_never_busier_than_all_the_time_there_was()
    {
        var before = At(0, (Instance(100, 0), 0), (Instance(200, 0), 0));
        var after = At(1, (Instance(100, 0), 0.8), (Instance(200, 0), 0.5));

        GpuEngines.Busiest(before, after, Card).ShouldBe(1);
    }

    [Fact]
    public void Only_the_chosen_adapters_engines_count()
    {
        var before = At(0, (Instance(100, 0, luid: ProcessorGraphics), 0), (Instance(100, 0), 0));
        var after = At(1, (Instance(100, 0, luid: ProcessorGraphics), 0.9), (Instance(100, 0), 0.1));

        GpuEngines.Busiest(before, after, Card).ShouldNotBeNull().ShouldBe(0.1, 1e-9);
        GpuEngines.Busiest(before, after, ProcessorGraphics).ShouldNotBeNull().ShouldBe(0.9, 1e-9);
    }

    [Fact]
    public void The_adapter_is_matched_whatever_case_windows_writes_it_in()
    {
        var before = At(0, (Instance(100, 0, luid: Card.ToLowerInvariant()), 0));
        var after = At(1, (Instance(100, 0, luid: Card.ToLowerInvariant()), 0.4));

        GpuEngines.Busiest(before, after, Card).ShouldNotBeNull().ShouldBe(0.4, 1e-9);
    }

    [Fact]
    public void A_process_that_started_or_ended_between_snapshots_is_left_out()
    {
        var before = At(0, (Instance(100, 0), 0), (Instance(200, 0), 7));
        var after = At(1, (Instance(100, 0), 0.2), (Instance(300, 0), 50));

        GpuEngines.Busiest(before, after, Card).ShouldNotBeNull().ShouldBe(0.2, 1e-9);
    }

    [Fact]
    public void A_count_that_went_backwards_counts_as_idle()
    {
        // Windows reused a process id, so the new process's count starts again from nothing.
        var before = At(0, (Instance(100, 0), 9), (Instance(200, 0), 0));
        var after = At(1, (Instance(100, 0), 0.1), (Instance(200, 0), 0.3));

        GpuEngines.Busiest(before, after, Card).ShouldNotBeNull().ShouldBe(0.3, 1e-9);
    }

    [Fact]
    public void An_adapter_no_process_is_using_is_idle()
    {
        var before = At(0, (Instance(100, 0, luid: ProcessorGraphics), 0));
        var after = At(1, (Instance(100, 0, luid: ProcessorGraphics), 0.5));

        GpuEngines.Busiest(before, after, Card).ShouldBe(0);
        GpuEngines.Busiest(At(0), At(1), Card).ShouldBe(0);
    }

    [Fact]
    public void Snapshots_with_no_time_between_them_say_nothing()
    {
        var before = At(3, (Instance(100, 0), 1));
        var after = At(3, (Instance(100, 0), 1));

        GpuEngines.Busiest(before, after, Card).ShouldBeNull();
    }

    [Theory]
    [InlineData("pid_1234_luid_0x00000000_0x0000D1A4_phys_0_eng_3_engtype_VideoDecode", "phys_0_eng_3")]
    [InlineData("pid_1234_luid_0x00000000_0x0000D1A4_phys_1_eng_11_engtype_", "phys_1_eng_11")]
    [InlineData("pid_1234_luid_0x00000000_0x0000d1a4_phys_0_eng_0_engtype_3d", "phys_0_eng_0")]
    [InlineData("pid_1234_luid_0x00000000_0x0000D1A4_phys_0_eng_0", "phys_0_eng_0")]
    [InlineData("pid_1234_luid_0x00000000_0x0000D935_phys_0_eng_0_engtype_3D", null)]
    [InlineData("pid_1234_luid_0x00000000_0x0000D1A40_phys_0_eng_0_engtype_3D", null)]
    [InlineData("_Total", null)]
    public void An_instance_names_its_engine_on_the_adapter(string instance, string? engine)
        => GpuEngines.EngineOf(instance, Card).ShouldBe(engine);

    [Fact]
    public void An_adapter_is_named_the_way_the_counters_name_it()
    {
        new GpuAdapter("AMD Radeon RX 7800 XT", DiscreteGpu.AmdVendor, 16UL << 30, 0xD1A4, 0, false).CounterLuid.ShouldBe(Card);
        new GpuAdapter("AMD Radeon RX 7800 XT", DiscreteGpu.AmdVendor, 16UL << 30, 0x0001ABCD, 1, false).CounterLuid
            .ShouldBe("luid_0x00000001_0x0001ABCD");
    }
}
