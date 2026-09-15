namespace PowerLedger.App;

/// <summary>One App per session: a second start signals the first to show its window, then exits; the installer signals it
/// to exit before it replaces or removes the App. The named mutex is only held open, never owned: its existence is the
/// signal, and a mutex owned by one thread cannot be released from another.</summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private readonly EventWaitHandle _exit;
    private RegisteredWaitHandle? _showWait;
    private RegisteredWaitHandle? _exitWait;

    public SingleInstance(string name = "PowerLedger.App")
    {
        _mutex = new Mutex(initiallyOwned: false, $@"Local\{name}", out var created);
        IsFirst = created;
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Show");
        _exit = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Exit");   // PowerLedger.iss sets it by this name
    }

    public bool IsFirst { get; }

    /// <summary>Asks the first instance to show itself.</summary>
    public void SignalFirst() => _show.Set();

    /// <summary>Runs <paramref name="show"/> on a pool thread each time a later start asks.</summary>
    public void OnShowRequested(Action show)
        => _showWait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => show(), null, Timeout.Infinite, executeOnlyOnce: false);

    /// <summary>Runs <paramref name="exit"/> on a pool thread when the installer asks, even if it asked before this was called.
    /// Only once: a second request finds the App already on its way out.</summary>
    public void OnExitRequested(Action exit)
        => _exitWait = ThreadPool.RegisterWaitForSingleObject(_exit, (_, _) => exit(), null, Timeout.Infinite, executeOnlyOnce: true);

    public void Dispose()
    {
        _showWait?.Unregister(null);
        _exitWait?.Unregister(null);
        _show.Dispose();
        _exit.Dispose();
        _mutex.Dispose();
    }
}
