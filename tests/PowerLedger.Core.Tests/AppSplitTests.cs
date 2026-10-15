using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class AppSplitTests
{
    /// <summary>An hour-long tick, so watts and watt-hours read alike.</summary>
    private static TickEnergy Hour(double cpuW, double gpuW = 0, double? igpuW = null)
        => new(CpuWh: cpuW, GpuWh: gpuW, Seconds: 3600, IGpuW: igpuW);

    /// <summary>Two live processes, A with 300 cycles and B with 100, whose time explains 80 of 100 units of busy time; 5 more
    /// went to drivers' interrupts and deferred calls, and 15 nobody running explains.</summary>
    private static readonly ProcessorWork Busy = new(Cycles: 400, Busy: 100, Explained: 80, Kernel: 5);

    [Fact]
    public void The_processor_at_rest_goes_to_idle_and_the_rest_by_busy_time_then_by_cycles()
    {
        var split = AppSplit.Of(Hour(cpuW: 10), new RestWatts(Cpu: 2, Gpu: 0, IGpu: 0), Busy, card: null, processorGraphics: null);
        split.IdleCpuWh.ShouldBe(2, 1e-12);
        split.KernelCpuWh.ShouldBe(8 * 0.05, 1e-12);                 // the drivers' time is Windows' own
        split.ClosedCpuWh.ShouldBe(8 * 0.15, 1e-12);
        (300 * split.CpuWhPerCycle).ShouldBe(8 * 0.8 * 0.75, 1e-12);
        (100 * split.CpuWhPerCycle).ShouldBe(8 * 0.8 * 0.25, 1e-12);
    }

    [Fact]
    public void Resting_watts_above_the_draw_give_it_all_to_idle()
    {
        var split = AppSplit.Of(Hour(cpuW: 1.5), new RestWatts(Cpu: 2, Gpu: 0, IGpu: 0), Busy, null, null);
        split.IdleCpuWh.ShouldBe(1.5, 1e-12);
        split.CpuWhPerCycle.ShouldBe(0);
        split.ClosedCpuWh.ShouldBe(0);
    }

    [Fact]
    public void Busy_time_with_nothing_running_to_explain_it_is_split_between_drivers_and_closed_programs()
    {
        var split = AppSplit.Of(Hour(cpuW: 10), new RestWatts(0, 0, 0), new ProcessorWork(Cycles: 0, Busy: 40, Explained: 0, Kernel: 10), null, null);
        split.KernelCpuWh.ShouldBe(10 * 0.25, 1e-12);
        split.ClosedCpuWh.ShouldBe(10 * 0.75, 1e-12);
        split.IdleCpuWh.ShouldBe(0);
    }

    [Fact]
    public void A_processor_that_did_nothing_anyone_can_see_gives_everything_to_idle()
    {
        var split = AppSplit.Of(Hour(cpuW: 10, gpuW: 4), new RestWatts(1, 1, 0), new ProcessorWork(0, 0, 0, 0), null, null);
        split.IdleCpuWh.ShouldBe(10, 1e-12);
        split.IdleGpuWh.ShouldBe(4, 1e-12);
    }

    [Fact]
    public void Live_cycles_with_no_busy_time_counted_yet_take_the_whole_working_part()
    {
        var split = AppSplit.Of(Hour(cpuW: 10), new RestWatts(2, 0, 0), new ProcessorWork(Cycles: 50, Busy: 0, Explained: 0, Kernel: 0), null, null);
        (50 * split.CpuWhPerCycle).ShouldBe(8, 1e-12);
    }

    [Fact]
    public void The_processors_graphics_working_watts_go_by_time_on_its_engines_before_the_rest_goes_by_cycles()
    {
        // 3 W on the graphics rail, 0.5 of it at rest: 2.5 Wh by time on the processor's graphics, a quarter of it by a
        // program that has since closed; the other 5.5 Wh of the working part by busy time and cycles.
        var gfx = new EngineWork(LiveLoad: 600, DeadLoad: 200, LiveAny: 900, DeadAny: 200);
        var split = AppSplit.Of(Hour(cpuW: 10, igpuW: 3), new RestWatts(2, 0, IGpu: 0.5), Busy, null, gfx);
        split.GraphicsByLoadEngines.ShouldBeTrue();
        (600 * split.CpuWhPerGraphicsTime).ShouldBe(2.5 * 0.75, 1e-12);
        split.ClosedCpuWh.ShouldBe(2.5 * 0.25 + 5.5 * 0.15, 1e-12);
        split.KernelCpuWh.ShouldBe(5.5 * 0.05, 1e-12);
        (400 * split.CpuWhPerCycle).ShouldBe(5.5 * 0.8, 1e-12);
    }

    [Fact]
    public void Without_a_graphics_rail_the_processors_graphics_add_no_weight()
    {
        var gfx = new EngineWork(LiveLoad: 600, DeadLoad: 0, LiveAny: 600, DeadAny: 0);
        var split = AppSplit.Of(Hour(cpuW: 10, igpuW: null), new RestWatts(2, 0, 0.5), Busy, null, gfx);
        split.CpuWhPerGraphicsTime.ShouldBe(0);
        (400 * split.CpuWhPerCycle).ShouldBe(8 * 0.8, 1e-12);
    }

    [Fact]
    public void A_graphics_rail_drawing_more_than_the_working_part_is_held_to_it()
    {
        var gfx = new EngineWork(LiveLoad: 100, DeadLoad: 0, LiveAny: 100, DeadAny: 0);
        var split = AppSplit.Of(Hour(cpuW: 4, igpuW: 9), new RestWatts(1, 0, 0), Busy, null, gfx);
        (100 * split.CpuWhPerGraphicsTime).ShouldBe(3, 1e-12);
        split.CpuWhPerCycle.ShouldBe(0);
        split.IdleCpuWh.ShouldBe(1, 1e-12);
    }

    [Fact]
    public void The_cards_working_watts_go_by_time_on_its_shader_engines_or_on_any_engine_when_none_ran()
    {
        var shaders = AppSplit.Of(Hour(cpuW: 0, gpuW: 20), new RestWatts(0, Gpu: 5, IGpu: 0), Busy,
            new EngineWork(LiveLoad: 300, DeadLoad: 100, LiveAny: 1000, DeadAny: 100), null);
        shaders.IdleGpuWh.ShouldBe(5, 1e-12);
        shaders.CardByLoadEngines.ShouldBeTrue();
        (300 * shaders.GpuWhPerCardTime).ShouldBe(15 * 0.75, 1e-12);
        shaders.ClosedGpuWh.ShouldBe(15 * 0.25, 1e-12);

        var video = AppSplit.Of(Hour(cpuW: 0, gpuW: 20), new RestWatts(0, 5, 0), Busy, new EngineWork(0, 0, LiveAny: 300, DeadAny: 100), null);
        video.CardByLoadEngines.ShouldBeFalse();
        (300 * video.GpuWhPerCardTime).ShouldBe(15 * 0.75, 1e-12);
    }

    [Fact]
    public void A_card_awake_that_nobody_used_draws_for_itself()
    {
        var split = AppSplit.Of(Hour(cpuW: 0, gpuW: 20), new RestWatts(0, 5, 0), Busy, new EngineWork(0, 0, 0, 0), null);
        split.IdleGpuWh.ShouldBe(20, 1e-12);
        split.GpuWhPerCardTime.ShouldBe(0);
    }

    [Fact]
    public void A_card_whose_engines_nobody_reads_follows_the_processors_split()
    {
        var split = AppSplit.Of(Hour(cpuW: 10, gpuW: 20), new RestWatts(2, 5, 0), Busy, card: null, null);
        split.IdleGpuWh.ShouldBe(5, 1e-12);
        split.KernelGpuWh.ShouldBe(15 * 0.05, 1e-12);
        split.ClosedGpuWh.ShouldBe(15 * 0.15, 1e-12);
        (400 * split.GpuWhPerCycle).ShouldBe(15 * 0.8, 1e-12);
    }

    [Fact]
    public void A_gap_tick_has_nothing_to_split()
    {
        var split = AppSplit.Of(new TickEnergy(0, 0, 0, null), new RestWatts(2, 5, 0.5), Busy, new EngineWork(1, 1, 1, 1), new EngineWork(1, 1, 1, 1));
        split.ShouldBe(AppSplit.Nothing);
    }

    [Fact]
    public void Every_tick_hands_out_exactly_its_processor_and_graphics_energy()
    {
        var random = new Random(20261016);
        long Time() => random.Next(0, 3) * (long)random.Next(0, 10_000);
        for (var run = 0; run < 2000; run++)
        {
            var count = random.Next(0, 6);
            ulong[] cycles = [.. Enumerable.Range(0, count).Select(_ => (ulong)random.Next(0, 5_000_000))];
            long[] cardLoad = [.. cycles.Select(_ => Time())], cardAny = [.. cardLoad.Select(t => t + Time())];
            long[] gfxLoad = [.. cycles.Select(_ => Time())], gfxAny = [.. gfxLoad.Select(t => t + Time())];
            var work = new ProcessorWork((ulong)cycles.Sum(c => (long)c), random.Next(0, 20_000), random.Next(0, 25_000), random.Next(0, 3_000));
            var deadLoad = Time();
            EngineWork? card = random.Next(4) == 0 ? null : new EngineWork(cardLoad.Sum(), deadLoad, cardAny.Sum(), deadLoad + Time());
            EngineWork? gfx = random.Next(4) == 0 ? null : new EngineWork(gfxLoad.Sum(), deadLoad, gfxAny.Sum(), deadLoad + Time());
            var seconds = random.NextDouble() * 5;
            var cpuW = random.NextDouble() * 40;
            var gpuW = random.Next(3) == 0 ? 0 : random.NextDouble() * 200;
            var energy = new TickEnergy(cpuW * seconds / 3600, gpuW * seconds / 3600, seconds, random.Next(3) == 0 ? null : random.NextDouble() * 15);
            var rest = new RestWatts(random.NextDouble() * 8, random.NextDouble() * 30, random.NextDouble() * 2);

            var split = AppSplit.Of(energy, rest, work, card, gfx);
            var cpu = split.IdleCpuWh + split.KernelCpuWh + split.ClosedCpuWh;
            var gpu = split.IdleGpuWh + split.KernelGpuWh + split.ClosedGpuWh;
            for (var i = 0; i < count; i++)
            {
                // Each process's figures as the service books them.
                cpu += cycles[i] * split.CpuWhPerCycle + (split.GraphicsByLoadEngines ? gfxLoad[i] : gfxAny[i]) * split.CpuWhPerGraphicsTime;
                gpu += cycles[i] * split.GpuWhPerCycle + (split.CardByLoadEngines ? cardLoad[i] : cardAny[i]) * split.GpuWhPerCardTime;
            }
            cpu.ShouldBe(energy.CpuWh, 1e-12 + 1e-9 * energy.CpuWh, $"run {run}");
            gpu.ShouldBe(energy.GpuWh, 1e-12 + 1e-9 * energy.GpuWh, $"run {run}");
            new[] { split.IdleCpuWh, split.KernelCpuWh, split.ClosedCpuWh, split.IdleGpuWh, split.KernelGpuWh, split.ClosedGpuWh, split.CpuWhPerCycle,
                    split.GpuWhPerCycle, split.CpuWhPerGraphicsTime, split.GpuWhPerCardTime }.ShouldAllBe(x => double.IsFinite(x) && x >= 0);
        }
    }
}
