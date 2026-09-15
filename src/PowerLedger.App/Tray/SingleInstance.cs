namespace PowerLedger.App;

/// <summary>One App per session: a second start signals the first to show its window, then exits. The named mutex is only
/// held open, never owned: its existence is the signal, and a mutex owned by one thread cannot be released from another.</summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private RegisteredWaitHandle? _wait;

    public SingleInstance(string name = "PowerLedger.App")
    {
        _mutex = new Mutex(initiallyOwned: false, $@"Local\{name}", out var created);
        IsFirst = created;
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Show");
    }

    public bool IsFirst { get; }

    /// <summary>Asks the first instance to show itself.</summary>
    public void SignalFirst() => _show.Set();

    /// <summary>Runs <paramref name="show"/> on a pool thread each time a later start asks.</summary>
    public void OnShowRequested(Action show)
        => _wait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => show(), null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        _wait?.Unregister(null);
        _show.Dispose();
        _mutex.Dispose();
    }
}
