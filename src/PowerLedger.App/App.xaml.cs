using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using PowerLedger.Contracts;
using PowerLedger.Core;
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
    private HouseholdViewModel? _household;
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
    private UiThreads? _threads;
    private CultureInfo? _culture;
    private string? _sentFolder;
    private ConsentGate? _consentGate;
    private CrashForwarder? _crashForwarder;
    private UsageCounter? _usage;
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
        _culture = culture;
        _sentFolder = Path.Combine(options.DataFolder, "Sent");
        var version = Version();
        new CrashCatcher(CrashFolder, version, ScrubNames.Here()).Hook(this);   // data-sharing design §5: as early as the App can catch itself
        _theme = new ThemeManager(this, preferences.Theme);
        _database = new SqliteDatabase(options.DatabasePath, readOnly: true);
        IServerCheck check = options.PipeName == PipeProtocol.PipeName ? InstalledServiceCheck.FromServiceManager() : new TrustAnyServer();
        _link = new PipeServiceLink(options.PipeName, new LastInputIdleSource(), TimeProvider.System, check);
        var threads = new UiThreads(action => Dispatcher.InvokeAsync(action), action => Task.Run(action));
        _threads = threads;
        _crashForwarder = new CrashForwarder(_link, threads, CrashFolder, TimeProvider.System);
        _crashForwarder.Start();
        var history = new HistoryReader(_database);
        var householdHistory = new HouseholdHistory(_database);
        var sleep = new SleepSettings();
        byte[] Pdf(ReportData data) => ReportDocument.Generate(data, version, DateTimeOffset.Now, culture);

        _now = new NowViewModel(_link, history, threads, TimeProvider.System, zone, culture, preferences.Co2KgPerKwh, ServiceStarter.Start);
        _breakdown = new BreakdownViewModel(_link, history, threads, TimeProvider.System, zone, culture);
        _report = new ReportViewModel(history, householdHistory, sleep, new FileSaver(), Pdf, threads, TimeProvider.System, zone, culture, preferences.Co2KgPerKwh);
        var autostart = new StartWithWindows(Environment.ProcessPath!);
        _preferences = new AppPreferences(store, preferences, choice => _theme.Choose(choice), UseCo2, autostart);
        _preferences.ApplyFirstRunDefaults();
        _preferences.EnsureFirstRunAt();   // data-sharing design §3: backfills an install from before this field existed
        var signIn = new SignIn(() => new HttpLoopbackServer(), OpenPage, new HttpClient());
        var account = new SignInViewModel(_link, _preferences, signIn, threads, SignInClients.Microsoft, SignInClients.Google);
        _household = new HouseholdViewModel(_link, householdHistory, threads, TimeProvider.System, zone, culture, account);
        _household.AddPcRequested += OpenAddPcWindow;
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
        _settings = new SettingsViewModel(
            _link, history, _preferences, threads, TimeProvider.System, zone, culture, RegionCurrency(), _updates,
            openSent: OpenSentWindow, openBrowser: OpenPage, copyToClipboard: CopyToClipboard);
        _settings.Privacy.Applied += consent => _usage?.ConsentChanged(consent);   // data-sharing design §3: known to usage counting at once
        _wizard = new WizardViewModel(_link, history, _preferences, threads, TimeProvider.System, zone, culture, RegionCurrency());
        _consentGate = new ConsentGate(_link, threads, TimeProvider.System, OpenConsentDialog);
        _wizard.Finished += () => _consentGate?.CheckOnce();   // spec §2: a new install is asked as soon as the wizard finishes
        _shell = new ShellViewModel(_now, _breakdown, _report, _household, _settings, _wizard, version, _updates);
        _usage = new UsageCounter(_link, _preferences, threads, TimeProvider.System, zone, CultureInfo.CurrentUICulture);
        _shell.PropertyChanged += OnShellChanged;
        _settings.PropertyChanged += OnSettingsChanged;
        _settings.Tariff.Saved += CountTariffChanged;
        _settings.Service.Saved += CountMachineChanged;
        _report.PropertyChanged += OnReportChanged;
        _tray = new TrayIcon(ShowWindow, ExitUi, autostart);
        _link.HouseholdNoticeReceived += OnHouseholdNotice;
        _monthly = new MonthlyReports(
            history, sleep, Pdf, MonthlyReports.DefaultFolder, TimeProvider.System, zone, culture, preferences.Co2KgPerKwh,
            written => Dispatcher.InvokeAsync(() =>
            {
                var (title, text) = MonthlyReports.Toast(written);
                _tray?.Notify(title, text, written[0].Path);
            }));
        // One reader for the whole run: it remembers which monitors have given a brightness or a power state and which have
        // failed, and so which to leave alone, and for how long.
        _brightnessReader = new DdcBrightness();
        _brightness = new BrightnessReporter(_link, _brightnessReader, new DisplayConfigReader(), _preferences, TimeProvider.System);
        _now.PropertyChanged += OnNowChanged;
        _instance.OnShowRequested(() => Dispatcher.InvokeAsync(ShowWindow));
        // The installer asks this before it replaces or removes the App (installer\PowerLedger.iss).
        _instance.OnExitRequested(() => Dispatcher.InvokeAsync(ExitUi));

        _link.Start();
        _now.Start();
        _monthly.Start();
        _brightness.Start();
        _updates.Start();
        _usage.Start();
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
        // data-sharing design §3: "Restart to update" starts an install
        if (e.PropertyName == nameof(Updater.Stage) && _updates?.Stage == UpdateStage.Installing) _usage?.CountUpdateInstalled();
    }

    /// <summary>Usage counts which page was opened, by its camelCase name (data-sharing design §3).</summary>
    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellViewModel.Page) || _shell is null) return;
        _usage?.CountPage(_shell.Page switch
        {
            Page.Now => "now",
            Page.Breakdown => "breakdown",
            Page.Report => "report",
            Page.Household => "household",
            _ => "settings",
        });
    }

    /// <summary>Usage counts the App's own preferences as they are ticked or chosen (data-sharing design §3); the tariff
    /// and the machine form count themselves when they save, since they save on their own schedule.</summary>
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        var name = e.PropertyName switch
        {
            nameof(SettingsViewModel.Theme) => "theme",
            nameof(SettingsViewModel.StartWithWindows) => "startWithWindows",
            nameof(SettingsViewModel.ReadMonitorBrightness) => "readMonitorBrightness",
            _ => null,
        };
        if (name is not null) _usage?.CountSetting(name);
    }

    private void CountTariffChanged() => _usage?.CountSetting("tariff");

    private void CountMachineChanged(ServiceSettings settings) => _usage?.CountSetting("machine");

    /// <summary>Usage counts a written PDF, not a PNG or a CSV (data-sharing design §3).</summary>
    private void OnReportChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReportViewModel.Saved) && _report?.Saved?.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) == true)
            _usage?.CountReportExported();
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
        _usage?.CountAppOpen();   // data-sharing design §3: every time the main window is shown
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
        // spec §2: an existing install is asked the first time the main window opens; a new install waits for the wizard.
        if (_preferences is { Current.FirstRunDone: true }) _consentGate?.CheckOnce();
    }

    /// <summary>Opens the consent dialog, modal and owned by the main window (data-sharing design §2).</summary>
    private void OpenConsentDialog(Consent current)
    {
        if (_window is null || _link is null || _threads is null) return;
        var model = new ConsentViewModel(_link, _threads, current, OpenPage, OpenPayload);
        model.Applied += consent => _usage?.ConsentChanged(consent);   // data-sharing design §3: known to usage counting at once
        new ConsentDialog(model) { Owner = _window }.ShowDialog();
    }

    /// <summary>Opens one payload file, owned by the main window: "See what would be sent" and each row of "What's been sent".</summary>
    private void OpenPayload(string path)
    {
        if (_window is null) return;
        new PayloadWindow(path) { Owner = _window }.Show();
    }

    /// <summary>Add a PC from the Household page (households design §2), modeless and owned by the main window.</summary>
    private void OpenAddPcWindow()
    {
        if (_window is null || _link is null || _threads is null) return;
        new AddPcWindow(new AddPcViewModel(_link, _threads, TimeProvider.System)) { Owner = _window }.Show();
    }

    /// <summary>A pushed household notice (households design §9): a Join prompt opens a modal on top of whatever is
    /// showing; anything else shows as a tray notification. Raised off the UI thread.</summary>
    private void OnHouseholdNotice(HouseholdNotice notice)
    {
        switch (notice.Kind)
        {
            case NoticeKind.JoinPrompt:
                Dispatcher.InvokeAsync(() => OpenJoinPromptWindow(notice));
                break;
            case NoticeKind.Info:
                Dispatcher.InvokeAsync(() => _tray?.Notify("PowerLedger", notice.Text, null));
                break;
        }
    }

    /// <summary>The Join prompt (households design §2, §3): modal, owned by the main window when it is open.</summary>
    private void OpenJoinPromptWindow(HouseholdNotice notice)
    {
        if (_link is null || _threads is null) return;
        var model = new JoinPromptViewModel(_link, _threads, TimeProvider.System, notice);
        new JoinPromptWindow(model) { Owner = _window }.ShowDialog();
    }

    /// <summary>"What's been sent…" in Settings → Privacy (data-sharing design §2).</summary>
    private void OpenSentWindow()
    {
        if (_window is null || _link is null || _threads is null || _culture is null || _sentFolder is null) return;
        var model = new SentViewModel(_link, _threads, _sentFolder, _culture, OpenPayload);
        new SentWindow(model) { Owner = _window }.Show();
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
            _consentGate?.Dispose();   // data-sharing design §2: a run still awaiting the service's answer must not open a dialog now
            _crashForwarder?.Dispose();
            if (_usage is not null)
            {
                await _usage.FlushOnExitAsync();   // data-sharing design §3: send what's held while the pipe still is
                _usage.Dispose();
            }
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
            if (_shell is not null) _shell.PropertyChanged -= OnShellChanged;
            if (_report is not null) _report.PropertyChanged -= OnReportChanged;
            if (_settings is not null)
            {
                _settings.PropertyChanged -= OnSettingsChanged;
                _settings.Tariff.Saved -= CountTariffChanged;
                _settings.Service.Saved -= CountMachineChanged;
            }
            _breakdown?.Dispose();
            _report?.Dispose();
            _household?.Dispose();
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

    /// <summary>"Copy" on the install id; with no clipboard to take it, nothing happens.</summary>
    private static void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            // Nothing to copy to here.
        }
    }

    /// <summary>Where the App's own crashes are caught, before the service takes them on (data-sharing design §5).</summary>
    private static string CrashFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerLedger", "Crashes");

    private static string Version()
        => (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];
}
