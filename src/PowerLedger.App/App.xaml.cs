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
    /// <summary>Review round: an Approve prompt replacing one that closed within this long is taken as the same
    /// request, whose code may have changed since.</summary>
    private static readonly TimeSpan ApprovePromptRecentlyClosed = TimeSpan.FromMinutes(2);

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
    private DashboardViewModel? _dashboard;
    private TrayIcon? _tray;
    private LookSwitcher? _looks;
    private AddPcWindow? _addPcWindow;
    private ApprovePromptWindow? _approvePromptWindow;
    private DateTimeOffset? _approvePromptClosedAt;
    private ConfirmJoinWindow? _confirmJoinWindow;
    private SendFeedbackWindow? _feedbackWindow;
    private FeedbackSender? _feedbackSender;
    private ITimer? _feedbackRetryTimer;
    private string? _dataFolder;
    private WhatsNewWindow? _whatsNewWindow;

    /// <summary>Review finding A4, follow-up: every open Join/Approve/Confirm join/Recovery code prompt, by its
    /// promptId, so a pushed <see cref="NoticeKind.Withdraw"/> can close the one it names and leave any others
    /// untouched.</summary>
    private readonly Dictionary<string, Window> _openPrompts = new();

    /// <summary>The modeless windows the shell window owns (Add a PC, Send feedback, What's been sent, a payload), so a
    /// look switch can hand them to the new window before the old one closes and takes its owned windows with it.</summary>
    private readonly List<Window> _owned = new();
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
        _dataFolder = options.DataFolder;
        var version = Version();
        AppLog.Write(options.AfterUpdate ? $"PowerLedger {version} starting, opened by the service after its update." : $"PowerLedger {version} starting.");
        new CrashCatcher(CrashFolder, version, ScrubNames.Here()).Hook(this);   // data-sharing design §5: as early as the App can catch itself
        _theme = new ThemeManager(this, preferences.Theme, preferences.Look);
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
        _report = new ReportViewModel(
            _link, history, householdHistory, sleep, new FileSaver(), Pdf, threads, TimeProvider.System, zone, culture, preferences.Co2KgPerKwh);
        var autostart = new StartWithWindows(Environment.ProcessPath!);
        _preferences = new AppPreferences(store, preferences, choice => _theme.Choose(choice), UseCo2, autostart, look => _looks?.Switch(look));
        _preferences.ApplyFirstRunDefaults();
        _preferences.EnsureFirstRunAt();   // data-sharing design §3: backfills an install from before this field existed
        var signIn = new SignIn(() => new HttpLoopbackServer(), OpenPage, new HttpClient(), TimeProvider.System);
        var account = new SignInViewModel(
            _link, _preferences, signIn, threads, SignInClients.Microsoft, SignInClients.Google, SignInClients.GoogleSecret);
        _household = new HouseholdViewModel(_link, householdHistory, threads, TimeProvider.System, zone, culture, account);
        _household.AddPcRequested += OpenAddPcWindow;
        var http = UpdateHttp.Create(version);
        _feedbackSender = new FeedbackSender(http, FeedbackQueue.DefaultFolder, TimeProvider.System);
        _updates = new Updater(
            GitHubReleaseFeed.For(http, options.UpdateFeed), new UpdateDownloader(http, UpdateDownloader.DefaultFolder), new SetupRunner(),
            new ConnectionCost(), _preferences, threads, TimeProvider.System, zone, culture, Updater.RunningVersion(version),
            (release, ready) => _tray?.Announce(
                $"PowerLedger {release.Name} is {(ready ? "ready" : "available")}",
                ready
                    ? "Open PowerLedger and choose Restart to update."
                    : "PowerLedger waits for a connection that isn't metered; open it to take this one now.",
                ShowWindow),
            OpenPage,
            askService: cancel => _link.UpdateNowAsync(cancel));
        _updates.PropertyChanged += OnUpdatesChanged;
        _updates.NotesRequested += OpenWhatsNewWindow;
        _updates.CloseRequested += ExitUi;   // the blocking window's Close PowerLedger (Plan Q §3)
        var updates = _updates;
        _now.StatusRead += status => updates.Apply(status.Updates);
        _settings = new SettingsViewModel(
            _link, history, _preferences, threads, TimeProvider.System, zone, culture, RegionCurrency(), _updates,
            openSent: OpenSentWindow, openBrowser: OpenPage, copyToClipboard: CopyToClipboard);
        _settings.Privacy.Applied += consent => _usage?.ConsentChanged(consent);   // data-sharing design §3: known to usage counting at once
        _wizard = new WizardViewModel(_link, history, _preferences, threads, TimeProvider.System, zone, culture, RegionCurrency());
        _consentGate = new ConsentGate(_link, threads, TimeProvider.System, OpenConsentDialog);
        _wizard.Finished += () => _consentGate?.CheckOnce();   // spec §2: a new install is asked as soon as the wizard finishes
        _dashboard = new DashboardViewModel(_now, history, history, TimeProvider.System, zone, culture, threads, _preferences, new HardwareNames());
        _shell = new ShellViewModel(_now, _breakdown, _report, _household, _settings, _wizard, version, _updates, _dashboard);
        _shell.FeedbackRequested += OpenFeedbackWindow;
        // Review 6: a saved look that won't open at start opens Classic instead, which is then saved, through Settings as
        // any choice of look is, so the next start doesn't fail the same way; the switcher logs why.
        var settings = _settings;
        _looks = new LookSwitcher(OpenWindow, _theme, Retarget, line => AppLog.Write(line), look => settings.Look = look);
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
        // A minute after start, then hourly (Updater's own cadence): pending feedback goes out once the PC is online again.
        _feedbackRetryTimer = TimeProvider.System.CreateTimer(_ => _ = _feedbackSender!.RetryPendingAsync(), null, Updater.FirstCheck, Updater.CheckEvery);
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
            Page.Dashboard => "dashboard",
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
            nameof(SettingsViewModel.Look) => "look",
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
        if (_looks is null) return;
        var window = _looks.Show();   // opened in the saved look the first time, or in Classic when that one won't open
        if (window.State == WindowState.Minimized) window.State = WindowState.Normal;
        window.Window.Activate();
        // spec §2: an existing install is asked the first time the main window opens; a new install waits for the wizard.
        if (_preferences is { Current.FirstRunDone: true }) _consentGate?.CheckOnce();
    }

    /// <summary>A shell window in <paramref name="look"/> over the one shell (Midnight look design §2). Closing it hides
    /// it to the tray while it is the current one; the service keeps logging either way. On exit, and once a switch has
    /// moved on to another window, a close is a close. The shell's pages read only while the current window shows; a
    /// switch's two windows showing and closing leave that alone, since neither is the current one as it happens.</summary>
    private IShellWindow OpenWindow(Look look)
    {
        IShellWindow window = look == Look.Midnight
            ? new MidnightWindow(_shell!, _looks!, _theme!, _updates!, OpenFeedbackWindow)
            : new MainWindow { DataContext = _shell };
        window.Window.Closing += (_, args) =>
        {
            if (_exiting || !IsCurrent(window)) return;
            args.Cancel = true;
            window.Window.Hide();
        };
        window.Window.IsVisibleChanged += (_, _) =>
        {
            if (!IsCurrent(window)) return;
            if (_shell is not null) _shell.IsShown = window.Window.IsVisible;
            _link?.ReportWindow(window.Window.IsVisible);   // Plan Q §4: the service installs while nobody is looking
        };
        return window;
    }

    private bool IsCurrent(IShellWindow window) => _looks is { IsOpen: true } looks && looks.Current == window;

    /// <summary>A switch has a new window: the modeless windows the old one owned go to it, before the old one closes and
    /// would take them with it.</summary>
    private void Retarget(IShellWindow window)
    {
        foreach (var owned in _owned) owned.Owner = window.Window;
    }

    /// <summary>The shell window, as the dialogs' owner; none until it has opened, since an owner must have shown.</summary>
    private Window? ShellWindow => _looks is { IsOpen: true } looks ? looks.Current.Window : null;

    /// <summary>Owns <paramref name="window"/> by the shell window for as long as it stays open, so a look switch can
    /// hand it on; then shows it.</summary>
    private void ShowOwned(Window window)
    {
        window.Owner = ShellWindow;
        _owned.Add(window);
        window.Closed += (_, _) => _owned.Remove(window);
        window.Show();
    }

    /// <summary>Opens the consent dialog, modal and owned by the main window (data-sharing design §2, owner's round: one
    /// screen, two choices — the status that triggered it no longer has anything left to show).</summary>
    private void OpenConsentDialog(Consent current)
    {
        if (ShellWindow is not { } owner || _link is null || _threads is null) return;
        var model = new ConsentViewModel(_link, _threads, OpenPage);
        model.Applied += consent => _usage?.ConsentChanged(consent);   // data-sharing design §3: known to usage counting at once
        new ConsentDialog(model) { Owner = owner }.ShowDialog();
    }

    /// <summary>Opens one payload file, owned by the main window: each row of "What's been sent".</summary>
    private void OpenPayload(string path)
    {
        if (ShellWindow is null) return;
        ShowOwned(new PayloadWindow(path));
    }

    /// <summary>Add a PC from the Household page (households design §2), modeless and owned by the main window. Review
    /// finding A4: reused while already open, rather than starting a second pairing gate alongside the first.</summary>
    private void OpenAddPcWindow()
    {
        if (_addPcWindow is not null)
        {
            _addPcWindow.Activate();
            return;
        }
        if (ShellWindow is null || _link is null || _threads is null) return;
        _addPcWindow = new AddPcWindow(new AddPcViewModel(_link, _threads, TimeProvider.System));
        _addPcWindow.Closed += (_, _) => _addPcWindow = null;
        ShowOwned(_addPcWindow);
    }

    /// <summary>Send feedback, from the rail's bug button: modeless, owned by the main window, single-instance like Add a PC.</summary>
    private void OpenFeedbackWindow()
    {
        if (_feedbackWindow is not null)
        {
            _feedbackWindow.Activate();
            return;
        }
        if (ShellWindow is not { } owner || _feedbackSender is null || _threads is null) return;
        var model = new FeedbackViewModel(_feedbackSender, _threads, ReadFeedbackLog);
        model.Closed += message =>
        {
            _feedbackWindow?.Close();
            if (message is not null) _tray?.Notify("PowerLedger", message, null);
        };
        _feedbackWindow = new SendFeedbackWindow(model, owner, new ImagePicker());
        _feedbackWindow.Closed += (_, _) => _feedbackWindow = null;
        ShowOwned(_feedbackWindow);
    }

    /// <summary>What's new, from the update card's own link: modeless, owned by the main window, single-instance like
    /// Add a PC and Send feedback (owner's round: in-app instead of the browser).</summary>
    private void OpenWhatsNewWindow()
    {
        if (_whatsNewWindow is not null)
        {
            _whatsNewWindow.Activate();
            return;
        }
        if (ShellWindow is null || _updates is null) return;
        var model = new WhatsNewViewModel(_updates.WhatsNewTitle, _updates.WhatsNewPoints, _updates.OpenNotes);
        model.Closed += () => _whatsNewWindow?.Close();
        _whatsNewWindow = new WhatsNewWindow(model);
        _whatsNewWindow.Closed += (_, _) => _whatsNewWindow = null;
        ShowOwned(_whatsNewWindow);
    }

    /// <summary>The last 300 lines of the App's own log and, if it can be read, of the service's (Send feedback's
    /// attach-log tick), capped together at the Worker's own character limit.</summary>
    private string? ReadFeedbackLog()
    {
        var appTail = FeedbackLog.TailLatestFile(AppLog.Folder, "app-*.log");
        var serviceTail = _dataFolder is null ? null : FeedbackLog.TailLatestFile(Path.Combine(_dataFolder, "logs"), "service-*.log");
        return FeedbackLog.Combined(appTail, serviceTail);
    }

    /// <summary>A pushed household notice (households design §9): a Join, Approve or Confirm join prompt opens a modal
    /// on top of whatever is showing; a Withdraw (task 0.8) closes the one prompt it names; a pairing's own outcome
    /// (review finding A4) shows in Add a PC while that is open, or else as a tray notification; anything else shows as
    /// a tray notification. Raised off the UI thread.</summary>
    private void OnHouseholdNotice(HouseholdNotice notice)
    {
        switch (notice.Kind)
        {
            case NoticeKind.JoinPrompt:
                Dispatcher.InvokeAsync(() => OpenJoinPromptWindow(notice));
                break;
            case NoticeKind.ApprovePrompt:
                Dispatcher.InvokeAsync(() => OpenApprovePromptWindow(notice));
                break;
            case NoticeKind.ConfirmJoin:
                Dispatcher.InvokeAsync(() => OpenConfirmJoinWindow(notice));
                break;
            case NoticeKind.RecoveryCode:
                Dispatcher.InvokeAsync(() => OpenRecoveryCodeWindow(notice));
                break;
            case NoticeKind.Withdraw:
                Dispatcher.InvokeAsync(() => WithdrawPrompt(notice.PromptId));
                break;
            case NoticeKind.PairingProgress:
                // Add a PC, while open, hears every notice itself (it subscribes on its own) and shows this there;
                // closed, there is nowhere else for the pairing it started to say how it went.
                Dispatcher.InvokeAsync(() =>
                {
                    if (_addPcWindow is null) _tray?.Notify("PowerLedger", notice.Text, null);
                });
                break;
            case NoticeKind.Info:
                Dispatcher.InvokeAsync(() => _tray?.Notify("PowerLedger", notice.Text, null));
                break;
        }
    }

    /// <summary>Review finding A4: closes the one open prompt <paramref name="promptId"/> names, if any, leaving any
    /// other prompt open. Its connection or its pairing is gone, so nothing is sent back for it.</summary>
    private void WithdrawPrompt(string? promptId)
    {
        if (promptId is null || !_openPrompts.TryGetValue(promptId, out var window)) return;
        window.Close();
    }

    /// <summary>The Join prompt (households design §2, §3): modal, owned by the main window when it is open.</summary>
    private void OpenJoinPromptWindow(HouseholdNotice notice)
    {
        if (_link is null || _threads is null) return;
        var model = new JoinPromptViewModel(_link, _threads, TimeProvider.System, notice);
        var window = new JoinPromptWindow(model) { Owner = ShellWindow };
        TrackPrompt(notice.PromptId, window);
        window.ShowDialog();
    }

    /// <summary>The Approve prompt (households design §7): modal, owned by the main window when it is open. Plan 0.9:
    /// an unanswered prompt can come back at the service's next turn, under the same or a new PromptId, while the
    /// request is still waiting — this never shows two windows for it, replacing whichever is already open. Review
    /// round: the App has no name or device ID to tell requests apart by, so a prompt that replaces one still open, or
    /// one that closed within the last two minutes, is taken as the same request having changed.</summary>
    private void OpenApprovePromptWindow(HouseholdNotice notice)
    {
        if (_link is null || _threads is null) return;
        var requestChanged = _approvePromptWindow is not null
            || (_approvePromptClosedAt is { } closedAt && TimeProvider.System.GetUtcNow() - closedAt <= ApprovePromptRecentlyClosed);
        _approvePromptWindow?.Close();
        var model = new ApprovePromptViewModel(_link, _threads, TimeProvider.System, notice, requestChanged);
        var window = new ApprovePromptWindow(model) { Owner = ShellWindow };
        _approvePromptWindow = window;
        window.Closed += (_, _) =>
        {
            if (_approvePromptWindow == window) _approvePromptWindow = null;
            _approvePromptClosedAt = TimeProvider.System.GetUtcNow();
        };
        TrackPrompt(notice.PromptId, window);
        window.ShowDialog();
    }

    /// <summary>The Confirm join prompt (households design §7, task 0.8): modal, owned by the main window when it is
    /// open. Service round, review: an unanswered ConfirmJoin can come back at R's next turn, under the same or a new
    /// PromptId, while its request is still waiting — this never shows two windows for it, replacing whichever is
    /// already open.</summary>
    private void OpenConfirmJoinWindow(HouseholdNotice notice)
    {
        if (_link is null || _threads is null) return;
        _confirmJoinWindow?.Close();
        var model = new ConfirmJoinViewModel(_link, _threads, TimeProvider.System, notice);
        var window = new ConfirmJoinWindow(model) { Owner = ShellWindow };
        _confirmJoinWindow = window;
        window.Closed += (_, _) =>
        {
            if (_confirmJoinWindow == window) _confirmJoinWindow = null;
        };
        TrackPrompt(notice.PromptId, window);
        window.ShowDialog();
    }

    /// <summary>Review finding A4: keeps <see cref="_openPrompts"/> current so a Withdraw can find this window by its
    /// promptId, for as long as it stays open however it closes (answered, timed out, or withdrawn).</summary>
    private void TrackPrompt(string? promptId, Window window)
    {
        if (promptId is null) return;
        _openPrompts[promptId] = window;
        window.Closed += (_, _) => _openPrompts.Remove(promptId);
    }

    /// <summary>A first sign-in that linked a household, or Make a new recovery code, made one (households design §7,
    /// task 0.8): shown once, modal and owned by the main window.</summary>
    private void OpenRecoveryCodeWindow(HouseholdNotice notice)
    {
        if (_link is null) return;
        var model = new RecoveryCodeViewModel(_link, notice, new FileSaver(), CopyToClipboard);
        var window = new RecoveryCodeWindow(model) { Owner = ShellWindow };
        TrackPrompt(notice.PromptId, window);
        window.ShowDialog();
    }

    /// <summary>"What's been sent…" in Settings → Privacy (data-sharing design §2).</summary>
    private void OpenSentWindow()
    {
        if (ShellWindow is null || _link is null || _threads is null || _culture is null || _sentFolder is null) return;
        var model = new SentViewModel(_link, _threads, _sentFolder, _culture, OpenPayload);
        ShowOwned(new SentWindow(model));
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
        AppLog.Write("Exiting.");
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
                _updates.NotesRequested -= OpenWhatsNewWindow;
                _updates.CloseRequested -= ExitUi;
                _updates.Dispose();
            }
            _monthly?.Dispose();
            _brightness?.Dispose();
            _brightnessReader?.Dispose();
            _feedbackRetryTimer?.Dispose();
            _feedbackWindow?.Close();
            _whatsNewWindow?.Close();
            if (_looks is { IsOpen: true }) _looks.Current.CloseForSwitch();   // for good, shown or hidden
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
            _dashboard?.Dispose();
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
