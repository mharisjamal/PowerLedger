using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PowerLedger.App;

/// <summary>What the update card shows (spec §13).</summary>
internal enum UpdateStage
{
    /// <summary>No card: up to date, not checked yet, or still downloading quietly.</summary>
    None,

    /// <summary>Found, but the connection is metered, so it waits for Download or a connection that isn't.</summary>
    Available,

    /// <summary>Downloaded and checked: "Restart to update".</summary>
    Ready,

    /// <summary>Setup is starting; it closes the App once Windows gives it permission.</summary>
    Installing,

    /// <summary>Setup didn't start, or ended without installing: "Try again".</summary>
    Failed,

    /// <summary>The first start of a newer version.</summary>
    Updated,
}

/// <summary>
/// Updates (spec §13). A minute after the App starts and every hour after, while the user allows it, asks the feed for
/// the newest release: the check itself runs on any connection, being a couple of kilobytes, but a newer release is
/// downloaded quietly only where nobody pays by the byte. On a metered connection the card offers it with Download
/// instead, and taking it there is the user's own choice; otherwise the next check off that connection downloads it. Once
/// it is checked, the card offers it, the tray announces it once per version and its menu offers it too. Installing starts
/// setup, which closes the App and opens the new version. Old downloads are cleared when the App starts and at each check.
/// The card and Settings' Updates row bind here, and everything they read changes on the UI thread.
/// </summary>
internal sealed class Updater : ObservableObject, IDisposable
{
    public static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);

    private static readonly string[] Card =
        [nameof(Stage), nameof(ShowCard), nameof(Title), nameof(Detail), nameof(ActionLabel), nameof(CanDismiss), nameof(HasNotes), nameof(ReadyVersion)];

    private readonly IReleaseFeed _feed;
    private readonly IUpdateDownloader _downloader;
    private readonly ISetupRunner _setup;
    private readonly IConnectionCost _cost;
    private readonly IUiSettings _ui;
    private readonly UiThreads _threads;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly CultureInfo _culture;
    private readonly Action<Release, bool> _announce;
    private readonly Action<Uri> _open;
    private readonly CancellationTokenSource _stop = new();
    private ITimer? _timer;
    private int _checking;
    private Release? _release;
    private string? _installer;
    private string? _wanted;   // the version the user asked for over metered data
    private UpdateStage _stage;
    private bool _dismissed;
    private string? _problem;
    private string _status = "Not checked yet.";
    private string? _message;

    /// <param name="announce">The tray's notification, once per version.</param>
    /// <param name="open">Opens a page in the browser.</param>
    public Updater(
        IReleaseFeed feed, IUpdateDownloader downloader, ISetupRunner setup, IConnectionCost cost, IUiSettings ui, UiThreads threads,
        TimeProvider clock, TimeZoneInfo zone, CultureInfo culture, Version running, Action<Release, bool> announce, Action<Uri> open)
    {
        _feed = feed;
        _downloader = downloader;
        _setup = setup;
        _cost = cost;
        _ui = ui;
        _threads = threads;
        _clock = clock;
        _zone = zone;
        _culture = culture;
        Running = running;
        _announce = announce;
        _open = open;
        Act = new RelayCommand(OnAct);
        Dismiss = new RelayCommand(() =>
        {
            _dismissed = true;
            OnPropertyChanged(nameof(ShowCard));
        });
        OpenNotes = new RelayCommand(() => _open(NotesPage));
        CheckNow = new RelayCommand(() => _threads.Background(() => _ = CheckAsync()));
    }

    /// <summary>The version running now.</summary>
    public Version Running { get; }

    /// <summary>The running version as updates compare it, from the App's version text: the numbers before any pre-release
    /// suffix, or 0.0.0 when there are none, which every release is newer than.</summary>
    internal static Version RunningVersion(string version)
        => Version.TryParse(version.Split('-')[0], out var parsed) ? parsed : new Version(0, 0, 0);

    public UpdateStage Stage => _stage;

    /// <summary>The card shows unless there is nothing to say, or the user put it away until the App next starts.</summary>
    public bool ShowCard => _stage != UpdateStage.None && !_dismissed;

    public string Title => _stage switch
    {
        UpdateStage.Available => $"PowerLedger {_release?.Name} is available",
        UpdateStage.Ready => $"PowerLedger {_release?.Name} is ready",
        UpdateStage.Installing => $"Installing {_release?.Name}…",
        UpdateStage.Failed => "The update didn't install",
        UpdateStage.Updated => $"Updated to {Running.ToString(3)}",
        _ => "",
    };

    /// <summary>A line under the title, or null.</summary>
    public string? Detail => _stage switch
    {
        UpdateStage.Available => _release is { } release ? $"{Megabytes(release)} · waiting for a connection that isn't metered" : null,
        UpdateStage.Installing => "Windows asks for permission",
        UpdateStage.Failed => _problem,
        _ => null,
    };

    /// <summary>The card's button, or null for none.</summary>
    public string? ActionLabel => _stage switch
    {
        UpdateStage.Available => "Download",
        UpdateStage.Ready => "Restart to update",
        UpdateStage.Failed => "Try again",
        _ => null,
    };

    public bool CanDismiss => _stage is UpdateStage.Available or UpdateStage.Ready or UpdateStage.Failed or UpdateStage.Updated;

    /// <summary>"What's new" shows: the release's page, or for "Updated" the running version's.</summary>
    public bool HasNotes => _stage is UpdateStage.Available or UpdateStage.Ready or UpdateStage.Updated;

    /// <summary>The version the tray menu offers to restart into: a checked download that isn't being installed, or null.</summary>
    public string? ReadyVersion => (_stage is UpdateStage.Ready or UpdateStage.Failed) && _installer is not null ? _release?.Name : null;

    /// <summary>Settings' tick box: check and download on the schedule.</summary>
    public bool CheckAutomatically
    {
        get => _ui.Current.CheckForUpdates;
        set
        {
            if (value == _ui.Current.CheckForUpdates) return;
            Message = _ui.CheckForUpdates(value);
            OnPropertyChanged();
        }
    }

    /// <summary>Settings' line: where things stand.</summary>
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>Why the tick box didn't stick, or null.</summary>
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }

    /// <summary>The card's button.</summary>
    public ICommand Act { get; }

    /// <summary>✕: the card goes until the App next starts; the tray menu keeps offering the update.</summary>
    public ICommand Dismiss { get; }

    public ICommand OpenNotes { get; }

    /// <summary>Settings' "Check now", which works with the tick box off.</summary>
    public ICommand CheckNow { get; }

    private Uri NotesPage => _stage == UpdateStage.Updated || _release is null ? GitHubReleaseFeed.PageOf(Running) : _release.Page;

    /// <summary>Clears old downloads, says so when this is the first start of a newer version, and checks a minute from now
    /// and every hour after while checking automatically is on. Call on the UI thread.</summary>
    public void Start()
    {
        _downloader.Clean(Running);
        var last = Version.TryParse(_ui.Current.LastVersion, out var ran) ? ran : null;
        if (last is not null && last < Running) Show(UpdateStage.Updated);
        if (last != Running) _ui.Ran(Running.ToString(3));
        _timer ??= _clock.CreateTimer(_ => ScheduledCheck(), null, FirstCheck, CheckEvery);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _stop.Cancel();   // a check or setup's wait ends: the App is exiting
    }

    /// <summary>Starts setup with the checked download: the card's "Restart to update" and the tray's menu item. Setup closes
    /// the App once Windows gives it permission; if the App is still here when setup ends, the update didn't go in.</summary>
    public void Install()
    {
        if (_release is not { } release || _installer is not { } installer || _stage == UpdateStage.Installing) return;
        Show(UpdateStage.Installing);
        var log = Path.ChangeExtension(installer, ".log");
        _threads.Background(() => _ = RunSetupAsync(release, installer, log));
    }

    /// <summary>The card's Download: this version may use the metered connection, because the user asked for it.</summary>
    public void Download()
    {
        if (_stage != UpdateStage.Available || _release is not { } release) return;
        _wanted = release.Name;
        Status = $"Downloading {release.Name}…";   // said here too, so the press is answered even if a check is already running
        _threads.Background(() => _ = CheckAsync());
    }

    /// <summary>Clears old downloads, asks the feed, downloads a newer release quietly, and offers it once it is checked. The
    /// clearing is here as well as in <see cref="Start"/> because the new version starts while setup is still running from
    /// the installer that brought it, which can't be deleted until setup ends. A check already running makes this one a
    /// no-op. Never throws: what went wrong is Settings' line, and the next check tries again.</summary>
    internal async Task CheckAsync()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            _downloader.Clean(Running);
            _threads.Post(() => Status = "Checking for updates…");
            var release = await _feed.LatestAsync(_stop.Token).ConfigureAwait(false);
            if (release is null || release.Version <= Running)
            {
                _threads.Post(() => Status = $"PowerLedger is up to date · checked {Clock()}");
                return;
            }
            if (_cost.Metered && _wanted != release.Name)
            {
                _threads.Post(() => Waiting(release));
                return;
            }
            _wanted = null;   // one Download is one attempt: a download that fails asks again rather than spending more of the allowance
            var progress = new Percent(done => _threads.Post(() => Status = $"Downloading {release.Name}… {done.ToString("P0", _culture)}"));
            var installer = await _downloader.DownloadAsync(release, progress, _stop.Token).ConfigureAwait(false);
            _threads.Post(() => Offer(release, installer));
        }
        catch (UpdateException error)
        {
            _threads.Post(() => Status = $"{error.Message} · {Clock()}");
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // The App is exiting.
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _threads.Post(() => Status = $"Couldn't check for updates: {error.Message} · {Clock()}");
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    /// <summary>The timer's check, on the timer's thread, while the tick box is on.</summary>
    private void ScheduledCheck()
    {
        if (_ui.Current.CheckForUpdates) _ = CheckAsync();
    }

    /// <summary>A checked download is ready: the card, the tray's announcement once per version, and the menu item.</summary>
    private void Offer(Release release, string installer)
    {
        if (_stage == UpdateStage.Installing) return;
        _release = release;
        _installer = installer;
        Status = $"PowerLedger {release.Name} is ready to install";
        Show(UpdateStage.Ready);
        Announce(release, ready: true);
    }

    /// <summary>A newer release, found while somebody is paying for every byte: the card offers it rather than taking it.</summary>
    private void Waiting(Release release)
    {
        // A download already checked stays: an update that is ready, or one whose setup didn't run, costs nothing to keep.
        if (_stage is UpdateStage.Installing or UpdateStage.Ready || (_stage == UpdateStage.Failed && _installer is not null)) return;
        _release = release;
        _installer = null;
        Status = $"PowerLedger {release.Name} is available · {Megabytes(release)} · waiting for a connection that isn't metered";
        Show(UpdateStage.Available);
        Announce(release, ready: false);
    }

    /// <summary>The tray says a version is there once: ready to restart into, or there to take when the connection is one
    /// somebody pays for.</summary>
    private void Announce(Release release, bool ready)
    {
        if (_ui.Current.AnnouncedVersion == release.Name) return;
        _ui.Announced(release.Name);
        _announce(release, ready);
    }

    /// <summary>The download's size as the card gives it: whole megabytes.</summary>
    private string Megabytes(Release release) => $"{(release.Size / (1024.0 * 1024.0)).ToString("0", _culture)} MB";

    /// <summary>The card's button: download what is waiting for a connection nobody pays for; install what is ready; after a
    /// failure, install again while the download is good, or check and download afresh when it isn't.</summary>
    private void OnAct()
    {
        if (_stage == UpdateStage.Available)
        {
            Download();
            return;
        }
        if (_stage == UpdateStage.Failed && _installer is null)
        {
            Show(UpdateStage.None);
            _threads.Background(() => _ = CheckAsync());
            return;
        }
        Install();
    }

    private async Task RunSetupAsync(Release release, string installer, string log)
    {
        try
        {
            var code = await _setup.RunAsync(installer, release.Size, release.Sha256, log, _stop.Token).ConfigureAwait(false);
            var shown = code.ToString(CultureInfo.InvariantCulture);
            // Inno Setup elevates itself, so a declined permission prompt comes back as setup stopping before it began
            // (codes 1 and 2); errors after that show setup's own message first.
            _threads.Post(() => Fail(code switch
            {
                0 => "Setup finished but didn't restart PowerLedger. Choose Exit UI in the tray menu, then open PowerLedger again.",
                1 or 2 => $"Setup stopped before installing (code {shown}). Try again, and choose Yes when Windows asks for permission.",
                _ => $"Setup ended without installing (code {shown}).",
            }));
        }
        catch (Win32Exception error) when (error.NativeErrorCode == SetupRunner.Declined)
        {
            _threads.Post(() => Fail("Windows didn't get permission, so nothing was installed."));
        }
        catch (UpdateException error)
        {
            _threads.Post(() =>
            {
                _installer = null;   // Try again downloads it afresh
                Fail(error.Message);
            });
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Setup is closing the App; the new version takes over from here.
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _threads.Post(() => Fail("Setup couldn't start: " + error.Message));
        }
    }

    private void Fail(string problem)
    {
        _problem = problem;
        Status = problem;
        Show(UpdateStage.Failed);
    }

    /// <summary>Moves the card to <paramref name="stage"/>; a new stage brings a put-away card back.</summary>
    private void Show(UpdateStage stage)
    {
        if (stage != _stage) _dismissed = false;
        _stage = stage;
        foreach (var name in Card) OnPropertyChanged(name);
    }

    private string Clock() => TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _zone).ToString("HH:mm", _culture);

    /// <summary>Hears each whole percent once, so a download doesn't flood the UI thread.</summary>
    private sealed class Percent(Action<double> report) : IProgress<double>
    {
        private int _last = -1;

        public void Report(double value)
        {
            var percent = (int)(value * 100);
            if (percent == _last) return;
            _last = percent;
            report(value);
        }
    }
}
