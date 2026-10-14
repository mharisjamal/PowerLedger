using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PowerLedger.Service;

/// <summary>The sampling loop's beat: a handle signalled once a period, which stays signalled until a wait takes it, so
/// beats missed while the loop was busy come as one, as a periodic timer's do. Used from the loop's thread alone.</summary>
internal interface IMetronome : IDisposable
{
    WaitHandle Beat { get; }

    /// <summary>Beats every <paramref name="period"/>, the first one period from now; a beat given before and not yet taken is dropped.</summary>
    void Start(TimeSpan period);

    /// <summary>No more beats until the next <see cref="Start"/>; a beat given and not yet taken is dropped.</summary>
    void Stop();
}

internal static class Metronome
{
    /// <summary>Windows' own timer for the system clock; any other clock, as a test's, beats through its own timers.</summary>
    public static IMetronome For(TimeProvider clock) => clock == TimeProvider.System ? new WaitableTimerMetronome() : new ClockMetronome(clock);
}

/// <summary>
/// A Windows waitable timer (perf review §7, research §3): the kernel signals it and the loop's thread wakes straight from
/// its wait, with no timer queue or thread pool in between. Each beat may come up to a tenth of a period late, so that
/// Windows can fold it into another wake-up (SetWaitableTimerEx's tolerable delay); a reading is stamped when it is taken
/// and its interval measured, so a late beat costs nothing.
/// </summary>
internal sealed class WaitableTimerMetronome : IMetronome
{
    private const uint TimerModifyState = 0x0002;
    private const uint Synchronize = 0x00100000;

    private readonly TimerHandle _beat;

    public WaitableTimerMetronome()
    {
        // No name and no manual reset: a synchronisation timer, which a completed wait resets.
        var timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerModifyState | Synchronize);
        if (timer.IsInvalid) throw new Win32Exception();
        _beat = new TimerHandle(timer);
    }

    public WaitHandle Beat => _beat;

    public void Start(TimeSpan period)
    {
        var due = -period.Ticks;                                   // relative, in 100 ns units
        var milliseconds = (int)Math.Clamp(period.TotalMilliseconds, 1, int.MaxValue);
        if (!SetWaitableTimerEx(_beat.SafeWaitHandle, ref due, milliseconds, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, (uint)(milliseconds / 10)))
        {
            throw new Win32Exception();
        }
        _beat.WaitOne(0);                                          // setting the timer leaves a beat it gave before standing
    }

    public void Stop()
    {
        CancelWaitableTimer(_beat.SafeWaitHandle);
        _beat.WaitOne(0);
    }

    public void Dispose() => _beat.Dispose();

    private sealed class TimerHandle : WaitHandle
    {
        public TimerHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(
        SafeWaitHandle timer, ref long dueTime, int period, IntPtr completion, IntPtr argument, IntPtr wakeContext, uint tolerableDelay);

    [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelWaitableTimer(SafeWaitHandle timer);
}

/// <summary>A metronome that beats through its clock's timers, so a test's fake clock moves the loop.</summary>
internal sealed class ClockMetronome(TimeProvider clock) : IMetronome
{
    private readonly AutoResetEvent _beat = new(false);
    private Run? _run;

    public WaitHandle Beat => _beat;

    public void Start(TimeSpan period)
    {
        Stop();
        var run = new Run(_beat);
        run.Timer = clock.CreateTimer(static state => ((Run)state!).Fire(), run, period, period);
        _run = run;
    }

    public void Stop()
    {
        if (_run is { } run)
        {
            run.Live = false;                                      // a callback already on its way beats for nothing
            run.Timer?.Dispose();
            _run = null;
        }
        _beat.Reset();
    }

    public void Dispose()
    {
        Stop();
        _beat.Dispose();
    }

    private sealed class Run(AutoResetEvent beat)
    {
        public volatile bool Live = true;
        public ITimer? Timer;

        public void Fire()
        {
            if (!Live) return;
            try
            {
                beat.Set();
            }
            catch (ObjectDisposedException)
            {
                // The loop has finished.
            }
        }
    }
}
