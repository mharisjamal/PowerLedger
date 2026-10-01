using System.Diagnostics;

namespace PowerLedger.App.Aero;

/// <summary>
/// The safety net that holds the live glass within a CPU budget, on either path: 8 % of one core for the whole process,
/// the owner's ceiling for the main window over a playing video. Every frame the glass shows makes WPF compose what lies
/// over it again, which for a window of many panes over a video can cost more than the glass itself. Once a second, while
/// the glass is taking frames, the governor reads this process's CPU: over the budget, the next frame waits half as long
/// again (down to one a second); under seven tenths of it, it comes a quarter sooner again (up to 30 a second). A window
/// at rest takes no frames and is never slowed.
/// </summary>
internal static class LiquidGlassGovernor
{
    public static readonly TimeSpan Fastest = TimeSpan.FromSeconds(1.0 / 30);
    public static readonly TimeSpan Slowest = TimeSpan.FromSeconds(1);

    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static TimeSpan _interval = Fastest;
    private static TimeSpan _sampledAt;
    private static TimeSpan _cpuAt;
    private static long _frames;
    private static long _framesAt;
    private static bool _started;

    /// <summary>The share of one core the whole process aims to stay under while the glass is live.</summary>
    public static double Budget { get; set; } = 0.08;

    /// <summary>The time between two frames the glass takes now.</summary>
    public static TimeSpan Interval
    {
        get
        {
            lock (Gate) return _interval;
        }
    }

    /// <summary>A frame was handed to a window.</summary>
    public static void Delivered() => Interlocked.Increment(ref _frames);

    /// <summary>Called by the capture threads as they run: at most once a second, reads the CPU and adjusts.</summary>
    public static void Tick()
    {
        lock (Gate)
        {
            var now = Clock.Elapsed;
            if (_started && now - _sampledAt < TimeSpan.FromSeconds(1)) return;
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var frames = Interlocked.Read(ref _frames);
            if (_started) _interval = Next(_interval, (cpu - _cpuAt) / (now - _sampledAt), Budget, frames > _framesAt);
            (_started, _sampledAt, _cpuAt, _framesAt) = (true, now, cpu, frames);
        }
    }

    /// <summary>The next interval, pure: slower by half when <paramref name="load"/> (the share of one core used) is over
    /// <paramref name="budget"/> and the glass took frames; a quarter faster when under seven tenths of it.</summary>
    public static TimeSpan Next(TimeSpan interval, double load, double budget, bool tookFrames)
    {
        if (load > budget && tookFrames) return Min(Slowest, interval * 1.5);
        if (load < budget * 0.7) return Max(Fastest, interval / 1.25);
        return interval;
    }

    /// <summary>Back to the start, for a test.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            _interval = Fastest;
            _started = false;
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
