namespace PowerLedger.Sensors;

/// <summary>
/// How long the person at the keyboard has been away, and whether they locked the screen.
/// Idle time is only meaningful from the user's own session: the default reads it there with GetLastInputInfo,
/// which is right for the App and the preview. The service runs in session 0, where that call describes the
/// service itself, so it must pass in idle time reported from the user's session.
/// </summary>
public sealed class ActivitySource : ISensorSource
{
    private readonly Func<double?> _idleSeconds;
    private readonly Func<bool> _locked;

    /// <param name="sessionLocked">The service supplies this from its session-change notifications; the preview passes false.</param>
    /// <param name="idleSeconds">Idle time from the user's session, or null for this process's own session.</param>
    public ActivitySource(Func<bool> sessionLocked, Func<double?>? idleSeconds = null)
        : this(idleSeconds ?? OwnSessionIdle, sessionLocked) { }

    /// <summary>Test seam: any source of idle time and lock state.</summary>
    internal ActivitySource(Func<double?> idleSeconds, Func<bool> locked)
    {
        _idleSeconds = idleSeconds;
        _locked = locked;
    }

    public string Name => "activity";

    public bool Supported => true;

    public string? Unavailable => null;

    /// <summary>Unknown idle time counts as active, which never overstates idle waste.</summary>
    public void Contribute(SampleDraft draft)
    {
        draft.UserIdleSeconds = _idleSeconds() ?? 0;
        draft.SessionLocked = _locked();
    }

    public void Dispose() { }

    private static double? OwnSessionIdle() => Win32.ReadIdleSeconds();
}
