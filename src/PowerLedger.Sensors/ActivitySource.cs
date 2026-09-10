namespace PowerLedger.Sensors;

/// <summary>
/// How long the person at the keyboard has been away, and whether they locked the screen. The service runs in
/// session 0, so it asks about the console session rather than its own.
/// </summary>
public sealed class ActivitySource : ISensorSource
{
    private readonly Func<double?> _idleSeconds;
    private readonly Func<bool> _locked;

    /// <param name="sessionLocked">The service supplies this from its session-change notifications; the preview passes false.</param>
    public ActivitySource(Func<bool> sessionLocked)
        : this(() => Win32.ReadConsoleSessionIdleSeconds() ?? Win32.ReadIdleSeconds(), sessionLocked) { }

    /// <summary>Test seam: any source of idle time and lock state.</summary>
    internal ActivitySource(Func<double?> idleSeconds, Func<bool> locked)
    {
        _idleSeconds = idleSeconds;
        _locked = locked;
    }

    public string Name => "activity";

    public bool Supported => true;

    public string? Unavailable => null;

    public void Contribute(SampleDraft draft)
    {
        draft.UserIdleSeconds = _idleSeconds() ?? 0;
        draft.SessionLocked = _locked();
    }

    public void Dispose() { }
}
