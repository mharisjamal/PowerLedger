using System.Diagnostics;
using System.Runtime.InteropServices;
using PowerLedger.Sensors;
using Shouldly;
using Xunit.Abstractions;

namespace PowerLedger.Sensors.Tests;

/// <summary>Plan AE follow-up, on this PC: the GPU Engine counters read for the Apps page, their adapters told apart by the
/// DirectX registry, a switched-off GeForce left off, and what a read costs.</summary>
[Trait("Category", "Hardware")]
public class EngineCountersHardwareTests(ITestOutputHelper output)
{
    [Fact]
    public void The_registry_names_every_adapter_the_counters_show_and_a_switched_off_card_stays_off()
    {
        if (!GpuEngines.Published) return;
        var nvidia = DevicePowerState.FindNvidiaGpu();
        var offBefore = nvidia is not null && DevicePowerState.IsPoweredOff(nvidia);
        var adapters = DirectXAdapters.Read();
        var shown = new GpuEngines().Read().RunningTime.Keys.Select(GpuEngines.AdapterOf).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in adapters) output.WriteLine($"{adapter.CounterLuid} {adapter.Description} ({adapter.VendorId:X4}:{adapter.DeviceId:X4})");
        shown.ShouldBeSubsetOf(adapters.Select(adapter => adapter.CounterLuid));

        var counters = new EngineCounters();
        counters.Read();
        const int Reads = 200;
        var before = ThreadTime();
        var walls = new double[Reads];
        Core.GraphicsWork? work = null;
        for (var i = 0; i < Reads; i++)
        {
            var started = Stopwatch.GetTimestamp();
            work = counters.Read();
            walls[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        var cpu = (ThreadTime() - before) / 10_000.0 / Reads;
        Array.Sort(walls);
        output.WriteLine($"{shown.Count} adapters shown, the GeForce {(nvidia is null ? "absent" : offBefore ? "switched off" : "on")}; " +
                         $"{new GpuEngines().Read().RunningTime.Count} instances; a read costs {cpu:F2} ms of processor time, {walls[Reads / 2]:F2} ms median wall; " +
                         $"{work?.ByProcess.Count ?? 0} processes worked on graphics in the last window");
        if (DiscreteGpu.Candidates(adapters).Any(card => shown.Contains(card.CounterLuid))) work.ShouldNotBeNull();
        if (offBefore)
        {
            Thread.Sleep(2000);
            DevicePowerState.IsPoweredOff(nvidia!).ShouldBeTrue("the counters and the registry must leave a switched-off card off");
        }
    }

    private static long ThreadTime() => GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user) ? kernel + user : 0;

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);
}
