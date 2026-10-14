using System.Diagnostics;

namespace PowerLedger.App.Aero;

/// <summary>
/// The safety net that holds the live glass within a CPU budget, on either path: 8 % of one core for the whole process,
/// the owner's ceiling for the main window over a playing video. Every frame the glass shows makes WPF compose what lies
/// over it again, which for a window of many panes over a video can cost more than the glass itself. Once a second, while
/// the glass is taking frames, the governor reads this process's CPU: over the budget, the next frame waits half as long
/// again (down to one a second); under seven tenths of it, it comes a quarter sooner again (up to the display's refresh
/// rate, 60 a second on most). A window at rest takes no frames and is never slowed. While the glass is lighter
/// (<see cref="GlassPower.Lighter"/>: Energy Saver, or on battery when chosen) the pace is never faster than
/// <see cref="LighterFastest"/> and the budget is <see cref="LighterBudget"/>.
/// </summary>
internal static class LiquidGlassGovernor
{
    /// <summary>The display's refresh period (DWM's composition rate): the glass is never drawn more often than Windows
    /// composes; 60 a second where DWM doesn't say.</summary>
    public static readonly TimeSpan Fastest = RefreshPeriod();
    public static readonly TimeSpan Slowest = TimeSpan.FromSeconds(1);

    /// <summary>The fastest pace while the glass is lighter: 15 frames a second (perf review P10).</summary>
    public static readonly TimeSpan LighterFastest = TimeSpan.FromSeconds(1.0 / 15);

    /// <summary>The budget while the glass is lighter: 2 % of one core.</summary>
    public const double LighterBudget = 0.02;

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
            var pace = Pace;
            lock (Gate) return Max(_interval, pace);
        }
    }

    /// <summary>The fastest pace now: the display's, or <see cref="LighterFastest"/> while the glass is lighter.</summary>
    public static TimeSpan Pace => GlassPower.Lighter ? Max(Fastest, LighterFastest) : Fastest;

    /// <summary>The budget in force now: <see cref="Budget"/>, or no more than <see cref="LighterBudget"/> while the glass is
    /// lighter.</summary>
    public static double CurrentBudget => GlassPower.Lighter ? Math.Min(Budget, LighterBudget) : Budget;

    /// <summary>The glass just became lighter, or full again: the pace starts from the new fastest at once, rather than
    /// creeping back a quarter a second.</summary>
    internal static void PowerChanged()
    {
        var pace = Pace;
        lock (Gate) _interval = pace;
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
            if (_started) _interval = Next(_interval, (cpu - _cpuAt) / (now - _sampledAt), CurrentBudget, frames > _framesAt, Pace);
            (_started, _sampledAt, _cpuAt, _framesAt) = (true, now, cpu, frames);
        }
    }

    /// <summary>The next interval, pure: slower by half when <paramref name="load"/> (the share of one core used) is over
    /// <paramref name="budget"/> and the glass took frames; a quarter faster when under seven tenths of it.</summary>
    public static TimeSpan Next(TimeSpan interval, double load, double budget, bool tookFrames) => Next(interval, load, budget, tookFrames, Fastest);

    /// <summary>The same, never faster than <paramref name="fastest"/>.</summary>
    public static TimeSpan Next(TimeSpan interval, double load, double budget, bool tookFrames, TimeSpan fastest)
    {
        interval = Max(interval, fastest);
        if (load > budget && tookFrames) return Min(Slowest, interval * 1.5);
        if (load < budget * 0.7) return Max(fastest, interval / 1.25);
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

    private static TimeSpan RefreshPeriod()
    {
        var info = new DwmTimingInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DwmTimingInfo>() };
        try
        {
            if (DwmGetCompositionTimingInfo(IntPtr.Zero, ref info) >= 0 && info.RateRefreshNumerator > 0 && info.RateRefreshDenominator > 0)
            {
                var hz = (double)info.RateRefreshNumerator / info.RateRefreshDenominator;
                if (hz is >= 24 and <= 500) return TimeSpan.FromSeconds(1 / hz);
            }
        }
        catch (DllNotFoundException)
        {
        }
        return TimeSpan.FromSeconds(1.0 / 60);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
    private struct DwmTimingInfo
    {
        public uint Size;
        public uint RateRefreshNumerator, RateRefreshDenominator;
        public uint RateCompositionNumerator, RateCompositionDenominator;
        public ulong QpcRefreshPeriod, QpcVBlank, CRefresh;
        public uint CDxRefresh;
        public ulong QpcCompose, CFrame;
        public uint CDxPresent;
        public ulong CRefreshFrame, CFrameSubmitted;
        public uint CDxPresentSubmitted;
        public ulong CFrameConfirmed;
        public uint CDxPresentConfirmed;
        public ulong CRefreshConfirmed;
        public uint CDxRefreshConfirmed;
        public ulong CFramesLate;
        public uint CFramesOutstanding;
        public ulong CFrameDisplayed, QpcFrameDisplayed, CRefreshFrameDisplayed, CFrameComplete, QpcFrameComplete, CFramePending, QpcFramePending;
        public ulong CFramesDisplayed, CFramesComplete, CFramesPending, CFramesAvailable, CFramesDropped, CFramesMissed, CRefreshNextDisplayed, CRefreshNextPresented;
        public ulong CRefreshesDisplayed, CRefreshesPresented, CRefreshStarted, CPixelsReceived, CPixelsDrawn, CBuffersEmpty;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DwmTimingInfo info);

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
