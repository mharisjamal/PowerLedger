using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>The tray App (spec §9): one per session, living in the tray, with a window on demand.</summary>
public partial class App : Application
{
    private SingleInstance? _instance;
    private ThemeManager? _theme;
    private SqliteDatabase? _database;
    private PipeServiceLink? _link;
    private NowViewModel? _now;
    private BreakdownViewModel? _breakdown;
    private ShellViewModel? _shell;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new SingleInstance();
        if (!_instance.IsFirst)
        {
            _instance.SignalFirst();
            Shutdown();
            return;
        }

        var options = AppOptions.Parse(e.Args);
        var preferences = new UiPreferencesStore(UiPreferencesStore.DefaultPath).Load();
        _theme = new ThemeManager(this, preferences.Theme);
        _database = new SqliteDatabase(options.DatabasePath, readOnly: true);
        _link = new PipeServiceLink(options.PipeName, new LastInputIdleSource(), TimeProvider.System);
        var threads = new UiThreads(action => Dispatcher.InvokeAsync(action), action => Task.Run(action));
        var history = new HistoryReader(_database);
        _now = new NowViewModel(
            _link, history, threads, TimeProvider.System, TimeZoneInfo.Local, CultureInfo.CurrentCulture,
            preferences.Co2KgPerKwh, ServiceStarter.Start);
        _breakdown = new BreakdownViewModel(history, threads, TimeProvider.System, TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        _shell = new ShellViewModel(_now, _breakdown, Version());
        _tray = new TrayIcon(ShowWindow, ExitUi, new StartWithWindows(Environment.ProcessPath!));
        _now.PropertyChanged += OnNowChanged;
        _instance.OnShowRequested(() => Dispatcher.InvokeAsync(ShowWindow));

        _link.Start();
        _now.Start();
        if (!options.StartInTray) ShowWindow();
    }

    private void OnNowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NowViewModel.TrayTooltip)) _tray?.Show(_now?.Last?.TotalW, _now?.TrayTooltip ?? "PowerLedger");
    }

    private void ShowWindow()
    {
        if (_exiting || _shell is null) return;
        if (_window is null)
        {
            _window = new MainWindow { DataContext = _shell };
            _window.Closing += (_, args) =>
            {
                if (_exiting) return;
                args.Cancel = true;      // closing hides to the tray; the service keeps logging either way
                _window.Hide();
            };
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>Windows is signing out or shutting down: let the window close instead of hiding it.</summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _exiting = true;
        base.OnSessionEnding(e);
    }

    /// <summary>However the App ends, the tray icon goes with it rather than lingering until the mouse passes over it.</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }

    /// <summary>"Exit UI" in the tray menu. It is an event handler, so nothing may escape it: the App ends either way.</summary>
    private async void ExitUi()
    {
        _exiting = true;
        try
        {
            _window?.Close();
            _tray?.Dispose();
            if (_now is not null)
            {
                _now.PropertyChanged -= OnNowChanged;
                _now.Dispose();
            }
            _breakdown?.Dispose();
            if (_link is not null) await _link.DisposeAsync();
            _theme?.Dispose();
            _database?.Dispose();
            _instance?.Dispose();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Ending anyway; nothing left to tell.
        }
        finally
        {
            Shutdown();
        }
    }

    private static string Version()
        => (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];
}
