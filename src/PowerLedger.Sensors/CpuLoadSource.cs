namespace PowerLedger.Sensors;

/// <summary>
/// CPU load from the deltas of the system time counters. Available on every machine, including those where
/// the energy meter reports nothing, which is what makes the load fallback in the power model possible.
/// </summary>
public sealed class CpuLoadSource : ISensorSource
{
    private readonly Func<Win32.SystemTimes?> _read;
    private Win32.SystemTimes? _previous;

    public CpuLoadSource() : this(Win32.ReadSystemTimes) { }

    /// <summary>Test seam: any source of system times.</summary>
    internal CpuLoadSource(Func<Win32.SystemTimes?> read)
    {
        _read = read;
        Supported = true;
    }

    public string Name => "cpu-load";

    public bool Supported { get; }

    public string? Unavailable => null;

    public void Contribute(SampleDraft draft)
    {
        if (_read() is not { } now) return;
        if (_previous is not { } before)
        {
            _previous = now;      // the first tick has no delta to work with
            return;
        }
        _previous = now;

        // Kernel time already includes idle time, so busy plus idle is kernel plus user.
        var total = now.Kernel - before.Kernel + (now.User - before.User);
        var idle = now.Idle - before.Idle;
        draft.CpuLoad = total == 0 ? 0 : Math.Clamp(1.0 - (double)idle / total, 0, 1);
    }

    public void Dispose() { }
}
