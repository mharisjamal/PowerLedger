namespace PowerLedger.Service;

/// <summary>
/// Facts that reach the service from outside the sampling loop: whether the console display is lit and the session
/// locked, from Windows notifications, and how long the user has been idle and whether anyone looks at the live figures,
/// reported by App clients over the pipe. Written from notification and pipe threads, read from the sensor thread.
/// </summary>
internal sealed class ServiceSignals(TimeProvider clock)
{
    /// <summary>A report older than this comes from an App that has gone quiet, and is ignored.</summary>
    public static readonly TimeSpan ReportLifetime = TimeSpan.FromSeconds(30);

    /// <summary>How long a subscriber that has said nothing of its windows counts as watching: long enough for an App to ask
    /// whether the service takes its window state and say it, short enough that one which never does (an App from before
    /// Plan Q, a console client) leaves the service at its unwatched pace.</summary>
    public static readonly TimeSpan SilentGrace = TimeSpan.FromSeconds(15);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (double IdleSeconds, long At)> _reports = new();
    private readonly Dictionary<string, (uint? Session, bool Visible, bool Overlay)> _windows = new();
    private readonly Dictionary<string, Grace> _subscribed = new();
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

    /// <summary>Raised, on the reporting thread, each time <see cref="Watched"/> turns.</summary>
    public event Action? WatchersChanged;

    /// <summary>
    /// True while someone may be looking at the live figures (perf review §4): an App says its main window or its overlay
    /// shows, or an App subscribed to the readings less than <see cref="SilentGrace"/> ago and hasn't said anything of its
    /// windows yet. Nobody at all, every App hidden in the tray, or a subscriber silent past the grace is nobody watching.
    /// </summary>
    public bool Watched
    {
        get
        {
            lock (_gate) return WatchedNow();
        }
    }

    /// <summary>A client subscribed to the readings: it counts as watching until it says otherwise, or for
    /// <see cref="SilentGrace"/> if it says nothing.</summary>
    public void Subscribed(string client) => Change(() =>
    {
        if (_subscribed.ContainsKey(client)) return;
        var grace = new Grace();
        _subscribed[client] = grace;
        grace.Timer = clock.CreateTimer(_ => Change(() => grace.Over = true), null, SilentGrace, Timeout.InfiniteTimeSpan);
    });

    /// <summary>One App client's idle time, keyed by its connection so each session's App keeps its own report.</summary>
    public void ReportIdle(string client, double idleSeconds)
    {
        if (!double.IsFinite(idleSeconds) || idleSeconds < 0) return;
        lock (_gate) _reports[client] = (idleSeconds, clock.GetTimestamp());
    }

    /// <summary>Plan Q §4: whether one App client's main window is showing, and the session it is in; and whether its
    /// watts overlay shows, which counts as someone watching but is no window an update waits for.</summary>
    public void ReportWindow(string client, uint? session, bool visible, bool overlay = false)
        => Change(() => _windows[client] = (session, visible, overlay));

    /// <summary>True while an App in <paramref name="session"/> says its main window is showing: the update worker waits
    /// for it to go, or for the user to leave it. An App that hasn't said, or has gone, shows nothing.</summary>
    public bool WindowShowingIn(uint session)
    {
        lock (_gate) return _windows.Values.Any(window => window.Visible && window.Session == session);
    }

    /// <summary>A connection closed: its idle report, its windows and its subscription go with it.</summary>
    public void ForgetClient(string client) => Change(() =>
    {
        _reports.Remove(client);
        _windows.Remove(client);
        if (_subscribed.Remove(client, out var grace)) grace.Timer?.Dispose();
    });

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

    private bool WatchedNow()
        => _windows.Values.Any(window => window.Visible || window.Overlay)
           || _subscribed.Any(subscriber => !subscriber.Value.Over && !_windows.ContainsKey(subscriber.Key));

    /// <summary>Makes a change under the lock, and tells whoever follows the watchers when it turned them.</summary>
    private void Change(Action change)
    {
        bool turned;
        lock (_gate)
        {
            var before = WatchedNow();
            change();
            turned = WatchedNow() != before;
        }
        if (turned) WatchersChanged?.Invoke();
    }

    /// <summary>A subscriber's grace: its timer, and whether it has run out.</summary>
    private sealed class Grace
    {
        public ITimer? Timer;
        public bool Over;
    }
}
