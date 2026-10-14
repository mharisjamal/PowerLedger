using System.Windows;

namespace PowerLedger.App;

/// <summary>
/// Whether any of the App's windows shows (perf review P1): the shell's window in any look, the overlay, a dialog. A
/// window hidden to the tray or minimised shows nothing. The live panel, the polling and the App's power throttling
/// follow <see cref="Shows"/>; the service is told <see cref="Visible"/>. A window is watched from when it is handed in, or
/// first laid out, until it closes. Only the windows of the thread that made the watch count, the App's UI thread: a
/// window of another can't be read from it. Call on that thread.
/// </summary>
/// <param name="aside">Windows that show but are no shell window, such as the watts overlay: they count in
/// <see cref="Shows"/> but not in <see cref="Visible"/>, since no update need wait for them.</param>
internal sealed class WindowWatch(Func<Window, bool>? aside = null)
{
    private readonly List<Window> _windows = [];
    private readonly System.Windows.Threading.Dispatcher _thread = System.Windows.Threading.Dispatcher.CurrentDispatcher;

    /// <summary>Whether any window is on screen: visible and not minimised.</summary>
    public bool Shows { get; private set; }

    /// <summary>Whether any shell window is visible, minimised or not: Plan Q §4's "the window shows", which keeps the
    /// service from installing an update under it. A window set aside never counts.</summary>
    public bool Visible { get; private set; }

    /// <summary>Raised when <see cref="Shows"/> or <see cref="Visible"/> changes.</summary>
    public event Action? Changed;

    /// <summary>A watch on every window the process lays out, from now on: once, at start. A window's first layout sizes
    /// it, so it is watched from its first showing; Loaded would not do, since WPF raises it only on a window that has a
    /// handler of its own for it.</summary>
    public static WindowWatch EveryWindow(Func<Window, bool>? aside = null)
    {
        var watch = new WindowWatch(aside);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.SizeChangedEvent, new SizeChangedEventHandler((sender, _) => watch.Watch((Window)sender)));
        return watch;
    }

    /// <summary>Watches <paramref name="window"/> until it closes; one already watched, or another thread's, is left as it is.</summary>
    public void Watch(Window window)
    {
        if (window.Dispatcher != _thread || _windows.Contains(window)) return;
        _windows.Add(window);
        window.IsVisibleChanged += OnVisibleChanged;
        window.StateChanged += OnStateChanged;
        window.Closed += OnClosed;
        Update();
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => Update();

    private void OnStateChanged(object? sender, EventArgs e) => Update();

    private void OnClosed(object? sender, EventArgs e)
    {
        var window = (Window)sender!;
        window.IsVisibleChanged -= OnVisibleChanged;
        window.StateChanged -= OnStateChanged;
        window.Closed -= OnClosed;
        _windows.Remove(window);
        Update();
    }

    private void Update()
    {
        var visible = _windows.Exists(window => window.IsVisible && aside?.Invoke(window) != true);
        var shows = _windows.Exists(window => window.IsVisible && window.WindowState != WindowState.Minimized);
        if (visible == Visible && shows == Shows) return;
        Visible = visible;
        Shows = shows;
        Changed?.Invoke();
    }
}
