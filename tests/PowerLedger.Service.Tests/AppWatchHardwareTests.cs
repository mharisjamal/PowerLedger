using System.Diagnostics;
using System.Runtime.InteropServices;
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Sensors;
using PowerLedger.Storage;
using Shouldly;
using Xunit.Abstractions;

namespace PowerLedger.Service.Tests;

/// <summary>Plan AE's checks on this PC's own processes and energy meter: what a tick of the energy by app costs, and that
/// a busy loop is given about the extra watts it makes the processor draw.</summary>
[Trait("Category", "Hardware")]
public sealed class AppWatchHardwareTests(ITestOutputHelper output) : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private static Reading Reading(DateTimeOffset at, double seconds, double cpuW, double load)
        => new(at, seconds, cpuW + 10, Quality.Measured, new Components(cpuW, 0, 5, 0, 0, 0, 0, 0, 0, 5),
            OnBattery: false, DisplayOn: true, UserIdle: false, SessionLocked: false, CpuLoad: load, GpuLoad: null, Brightness: null, Suspect: false);

    /// <summary>The processor time a tick costs at the loop's watched pace, one second, where a look is taken every fifth
    /// tick, and at its unwatched pace, five seconds, where every tick takes one; what a look itself costs; and what a tick
    /// allocates.</summary>
    [Fact]
    public void A_second_of_ticks_costs_well_under_a_millisecond_of_processor_time()
    {
        var sample = new Sample(DateTimeOffset.UtcNow, 1, 5, null, 0.2, null, null, false, null, false, null, true, 1, 0, false, false);
        (double Cpu, double Bytes, long Looks) Run(double seconds, int ticks)
        {
            using var watch = new AppWatch(new WindowsProcesses(), new AppHoursRepository(_database.Db));
            watch.Start();
            var at = DateTimeOffset.UtcNow;
            for (var i = 0; i < 20; i++) watch.Observe(Reading(at = at.AddSeconds(seconds), seconds, 5, 0.2), sample, 60);   // warm
            var looks = watch.Looks;
            Reading[] readings = [.. Enumerable.Range(1, ticks).Select(i => Reading(at.AddSeconds(seconds * i), seconds, 5, 0.2))];
            var cpuBefore = ThreadTime();
            var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            foreach (var reading in readings) watch.Observe(reading, sample, 60);
            return ((ThreadTime() - cpuBefore) / 10_000.0 / ticks, (GC.GetAllocatedBytesForCurrentThread() - bytesBefore) / (double)ticks, watch.Looks - looks);
        }

        var watched = Run(1, 500);
        var unwatched = Run(5, 200);
        double look;
        using (var source = new WindowsProcesses())
        {
            List<ProcessCounts> listed = [];
            var before = ThreadTime();
            for (var i = 0; i < 200; i++) source.Read(listed, out _);
            look = (ThreadTime() - before) / 10_000.0 / 200;
        }
        var threads = Process.GetProcesses().Sum(process =>
        {
            using (process) return process.Threads.Count;
        });
        output.WriteLine($"{Process.GetProcesses().Length} processes, {threads} threads: a look {look:F3} ms; one-second ticks {watched.Cpu:F3} ms each " +
                         $"({watched.Looks} looks in 500, {watched.Bytes:F0} bytes); five-second ticks {unwatched.Cpu:F3} ms each ({unwatched.Looks} looks in 200, " +
                         $"{unwatched.Bytes:F0} bytes), {unwatched.Cpu / 5:F3} ms a second");
        watched.Cpu.ShouldBeLessThan(1);
        (unwatched.Cpu / 5).ShouldBeLessThan(1);
    }

    [Fact]
    public void A_busy_loop_is_given_about_the_extra_watts_it_makes_the_processor_draw()
    {
        // A copy of cmd.exe outside the Windows folder spins one thread in a loop: an app of its own. Echo off, or the
        // console host, Windows' own, does half the work of showing each command.
        var folder = Path.Combine(Path.GetTempPath(), $"pl-spin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var spinner = Path.Combine(folder, "spinner.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), spinner);
        using var sensors = MachineSensors.Create(() => true, () => false);
        using var watch = new AppWatch(new WindowsProcesses(), new AppHoursRepository(_database.Db));
        Process? spinning = null;
        try
        {
            watch.Start();
            var last = Stopwatch.GetTimestamp();
            var at = DateTimeOffset.UtcNow;
            double onWattSeconds = 0, onSeconds = 0, offWattSeconds = 0, offSeconds = 0;
            void Tick(bool on)
            {
                var wait = 1000 - (int)Stopwatch.GetElapsedTime(last).TotalMilliseconds;
                if (wait > 0) Thread.Sleep(wait);
                var seconds = Stopwatch.GetElapsedTime(last).TotalSeconds;
                last = Stopwatch.GetTimestamp();
                at = DateTimeOffset.UtcNow;
                var sample = sensors.Read(at, seconds);
                var watts = sample.CpuPackageW ?? throw new InvalidOperationException("no energy meter on this PC");
                watch.Observe(Reading(at, seconds, watts, sample.CpuLoad), sample, 60);
                if (on)
                {
                    onWattSeconds += watts * seconds;
                    onSeconds += seconds;
                }
                else
                {
                    offWattSeconds += watts * seconds;
                    offSeconds += seconds;
                }
            }

            for (var s = 0; s < 75; s++) Tick(on: false);          // a quiet minute or so to learn the resting draw from
            offWattSeconds = offSeconds = 0;
            for (var round = 0; round < 8; round++)
            {
                spinning = Process.Start(new ProcessStartInfo(spinner, "/d /q /c \"for /l %i in (0,0,1) do rem\"") { CreateNoWindow = true, UseShellExecute = false });
                for (var s = 0; s < 15; s++) Tick(on: true);
                spinning!.Kill();
                spinning.WaitForExit();
                spinning.Dispose();
                spinning = null;
                for (var s = 0; s < 15; s++) Tick(on: false);
            }
            watch.Write();

            var key = spinner.ToLowerInvariant();
            var hour = new DateTimeOffset(at.UtcDateTime.Date.AddHours(at.UtcDateTime.Hour), TimeSpan.Zero);
            var repository = new AppHoursRepository(_database.Db);
            var spinnerWh = repository.ReadHour(hour).Concat(repository.ReadHour(hour.AddHours(-1))).Where(row => row.Key == key).Sum(row => row.CpuWh);
            var given = spinnerWh * 3600 / onSeconds;
            var extra = (onWattSeconds / onSeconds) - (offWattSeconds / offSeconds);
            output.WriteLine($"package {offWattSeconds / offSeconds:F2} W without the loop, {onWattSeconds / onSeconds:F2} W with it: {extra:F2} W extra; " +
                             $"the loop was given {given:F2} W ({(given / extra) - 1:P1})");
            given.ShouldBe(extra, extra * 0.15);
        }
        finally
        {
            if (spinning is { } left)
            {
                left.Kill();
                left.WaitForExit();
            }
            Directory.Delete(folder, recursive: true);
        }
    }

    private static long ThreadTime() => GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user) ? kernel + user : 0;

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);
}
