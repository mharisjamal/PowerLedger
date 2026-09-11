namespace PowerLedger.Service;

/// <summary>
/// Facts that reach the service from outside the sampling loop: whether the console display is lit and the session
/// locked, from Windows notifications, and how long the user has been idle, reported by App clients over the pipe.
/// Written from notification and pipe threads, read from the sensor thread.
/// </summary>
internal sealed class ServiceSignals(TimeProvider clock)
{
    /// <summary>A report older than this comes from an App that has gone quiet, and is ignored.</summary>
    public static readonly TimeSpan ReportLifetime = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (double IdleSeconds, long At)> _reports = new();
    private volatile bool _displayOn = true;
    private volatile bool _sessionLocked;

    public bool DisplayOn
    {
        get => _displayOn;
        set => _displayOn = value;
    }

    public bool SessionLocked
    {
        get => _sessionLocked;
        set => _sessionLocked = value;
    }

    /// <summary>One App client's idle time, keyed by its connection so each session's App keeps its own report.</summary>
    public void ReportIdle(string client, double idleSeconds)
    {
        if (!double.IsFinite(idleSeconds) || idleSeconds < 0) return;
        lock (_gate) _reports[client] = (idleSeconds, clock.GetTimestamp());
    }

    public void ForgetClient(string client)
    {
        lock (_gate) _reports.Remove(client);
    }

    /// <summary>
    /// Seconds since the most recently active user's last input: each fresh report aged by the time since it arrived,
    /// the smallest winning. Null when no App has reported lately.
    /// </summary>
    public double? UserIdleSeconds()
    {
        lock (_gate)
        {
            double? idle = null;
            foreach (var (seconds, at) in _reports.Values)
            {
                var age = clock.GetElapsedTime(at);
                if (age > ReportLifetime) continue;
                var now = seconds + age.TotalSeconds;
                idle = idle is { } best ? Math.Min(best, now) : now;
            }
            return idle;
        }
    }
}
