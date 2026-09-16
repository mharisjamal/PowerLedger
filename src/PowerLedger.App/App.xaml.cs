using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using PowerLedger.Contracts;
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
    private ReportViewModel? _report;
    private AppPreferences? _preferences;
    private SettingsViewModel? _settings;
    private WizardViewModel? _wizard;
    private MonthlyReports? _monthly;
    private DdcBrightness? _brightnessReader;
    private BrightnessReporter? _brightness;
    private Updater? _updates;
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
        var store = new UiPreferencesStore(UiPreferencesStore.DefaultPath);
        var preferences = store.Load();
        var zone = TimeZoneInfo.Local;
        var culture = CultureInfo.CurrentCulture;
        var version = Version();
        _theme = new ThemeManager(this, preferences.Theme);
        _database = new SqliteDatabase(options.DatabasePath, readOnly: true);
        IServerCheck check = options.PipeName == PipeProtocol.PipeName ? InstalledServiceCheck.FromServiceManager() : new TrustAnyServer();
        _link = new PipeServiceLink(options.PipeName, new LastInputIdleSource(), TimeProvider.System, check);
        var threads = new UiThreads(action => Dispatcher.InvokeAsync(action), action => Task.Run(action));
        var history = new HistoryReader(_database);
        var sleep = new SleepSettings();
        byte[] Pdf(ReportData data) => ReportDocument.Generate(data, version, DateTimeOffset.Now, culture);

        _now = new NowViewModel(_link, history, threads, TimeProvider.System, zone, culture, preferences.Co2KgPerKwh, ServiceStarter.Start);
        _breakdown = new BreakdownViewModel(_link, history, threads, TimeProvider.System, zone, culture);
        _report = new ReportViewModel(history, sleep, new FileSaver(), Pdf, threads, TimeProvider.System, zone, culture, preferences.Co2KgPerKwh);
        var autostart = new StartWithWindows(Environment.ProcessPath!);
        _preferences = new AppPreferences(store, preferences, choice => _theme.Choose(choice), UseCo2, autostart);
        _preferences.ApplyFirstRunDefaults();
        var http = UpdateHttp.Create(version);
        _updates = new Updater(
            GitHubReleaseFeed.For(http, options.UpdateFeed), new UpdateDownloader(http, UpdateDownloader.DefaultFolder), new SetupRunner(),
            new ConnectionCost(), _preferences, threads, TimeProvider.System, zone, culture, Updater.RunningVersion(version),
            (release, ready) => _tray?.Announce(
                $"PowerLedger {release.Name} is {(ready ? "ready" : "available")}",
                ready
                    ? "Open PowerLedger and choose Restart to update."
                    : "PowerLedger waits for a connection that isn't metered; open it to take this one now.",
                ShowWindow),
            OpenPage);
        _updates.PropertyChanged += OnUpdatesChanged;
        _settings = new SettingsViewModel(_link, history, _preferences, threads, TimeProvider.System, zone, culture, RegionCurrency(), _updates);
        _wizard = new WizardViewModel(_link, history, _preferences, threads, TimeProvider.System, zone, culture, RegionCurrency());
        _shell = new ShellViewModel(_now, _breakdown, _report, _settings, _wizard, version, _updates);
        _tray = new TrayIcon(ShowWindow, ExitUi, autostart);
        _monthly = new MonthlyReports(
            history, sleep, Pdf, MonthlyReports.DefaultFolder, TimeProvider.System, zone, culture, preferences.Co2KgPerKwh,
            written => Dispatcher.InvokeAsync(() =>
            {
                var (title, text) = MonthlyReports.Toast(written);
                _tray?.Notify(title, text, written[0].Path);
            }));
        // One reader for the whole run: it remembers which monitors failed, and leaves them alone until a display
        // change or a resume says they might answer differently now.
        _brightnessReader = new DdcBrightness();
        _brightness = new BrightnessReporter(_link, _brightnessReader, _preferences, TimeProvider.System);
        _now.PropertyChanged += OnNowChanged;
        _instance.OnShowRequested(() => Dispatcher.InvokeAsync(ShowWindow));
        // The installer asks this before it replaces or removes the App (installer\PowerLedger.iss).
        _instance.OnExitRequested(() => Dispatcher.InvokeAsync(ExitUi));

        _link.Start();
        _now.Start();
        _monthly.Start();
        _brightness.Start();
        _updates.Start();
        if (!options.StartInTray) ShowWindow();
    }

    private void OnNowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NowViewModel.TrayTooltip)) _tray?.Show(_now?.Last?.TotalW, _now?.TrayTooltip ?? "PowerLedger");
    }

    /// <summary>The tray menu offers the update the card offers.</summary>
    private void OnUpdatesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Updater.ReadyVersion)) _tray?.OfferUpdate(_updates?.ReadyVersion, () => _updates?.Install());
    }

    /// <summary>A release's page in the browser; with no browser set up, nothing happens.</summary>
    private static void OpenPage(Uri page)
    {
        try
        {
            using var browser = Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            // Nothing opens web pages here; the page is still on GitHub.
        }
    }

    private void ShowWindow()
    {
        if (_exiting || _shell is null) return;
        if (_preferences is { Current.FirstRunDone: false } && !_shell.IsSetup) _shell.BeginSetup();   // spec §9: the first window is the wizard
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

    /// <summary>"Exit UI" in the tray menu, and the installer's request to exit. <see cref="_exiting"/> is set first, so the
    /// window closes for good, shown or hidden. It is an event handler, so nothing may escape it: the App ends either way.</summary>
    private async void ExitUi()
    {
        _exiting = true;
        try
        {
            if (_updates is not null)
            {
                _updates.PropertyChanged -= OnUpdatesChanged;
                _updates.Dispose();
            }
            _monthly?.Dispose();
            _brightness?.Dispose();
            _brightnessReader?.Dispose();
            _window?.Close();
            _tray?.Dispose();
            if (_now is not null)
            {
                _now.PropertyChanged -= OnNowChanged;
                _now.Dispose();
            }
            _breakdown?.Dispose();
            _report?.Dispose();
            _settings?.Dispose();
            _wizard?.Dispose();
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

    /// <summary>A new CO₂ factor from Settings reaches every screen that shows CO₂, and the monthly reports.</summary>
    private void UseCo2(double factor)
    {
        if (_now is not null) _now.Co2KgPerKwh = factor;
        if (_report is not null) _report.Co2KgPerKwh = factor;
        if (_monthly is not null) _monthly.Co2KgPerKwh = factor;
    }

    /// <summary>The currency a new tariff starts in: the region's.</summary>
    private static string RegionCurrency()
    {
        try
        {
            return RegionInfo.CurrentRegion.ISOCurrencySymbol;
        }
        catch (ArgumentException)
        {
            return "USD";
        }
    }

    private static string Version()
        => (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];
}
