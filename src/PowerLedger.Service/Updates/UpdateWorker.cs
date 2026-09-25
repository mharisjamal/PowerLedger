using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PowerLedger.Contracts;
using PowerLedger.Service.Households;
using PowerLedger.Updates;

namespace PowerLedger.Service.Updates;

/// <summary>
/// Silent updates (Plan Q §3, §4). Reads the data server's minimum version at start and with every check; checks for a
/// release two minutes after start and hourly after, and at once when the update becomes required. A newer release with a
/// signatures file is downloaded into the Updates folder (on a metered connection only after a day, unless required),
/// checked, and installed when <see cref="InstallTiming"/> says: setup runs detached and silent, after the service has
/// left itself a <see cref="RelaunchNote"/> and closed the Apps, and the service that starts next opens the App again.
/// Nothing is installed without the release key's signature, from a folder that isn't locked, or that isn't newer than
/// this service. A setup that ends with this service still running, or that stopped it and left it to start again, didn't
/// install; that version is tried once more a day later (<see cref="FailedUpdate"/>), and the App's own updater remains.
/// </summary>
internal sealed class UpdateWorker : BackgroundService, IUpdateRequests
{
    public static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);

    /// <summary>How often a checked download is looked at again while it waits for its moment.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    internal const string NoKey = "This PowerLedger doesn't install updates itself yet.";
    internal const string NothingReady = "The service has no update ready to install.";
    internal const string Soon = "PowerLedger updates in a minute";

    private readonly UpdateEnvironment _env;
    private readonly AppPolicy _policy;
    private readonly StatusBoard _board;
    private readonly NoticeHub _notices;
    private readonly ServiceSignals _signals;
    private readonly TimeProvider _clock;
    private readonly ILogger<UpdateWorker> _log;
    private TaskCompletionSource _wakeup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _checkSoon;
    private volatile bool _installNow;
    private volatile Found? _found;
    private volatile Ready? _ready;
    private volatile Task? _setup;
    private volatile string? _stage;
    private DateTimeOffset? _warnedAt;
    private Version? _failed;

    public UpdateWorker(
        UpdateEnvironment env, AppPolicy policy, StatusBoard board, NoticeHub notices, ServiceSignals signals, TimeProvider clock,
        ILogger<UpdateWorker> log)
    {
        _env = env;
        _policy = policy;
        _board = board;
        _notices = notices;
        _signals = signals;
        _clock = clock;
        _log = log;
    }

    /// <summary>Whether a release key is built in, so this service installs updates.</summary>
    public bool Installs => _env.PublicKey.Length > 0;

    /// <summary>What the worker is doing, in words the App can show; null before there is anything to say.</summary>
    public string? Stage => _stage;

    /// <summary>The watch over the last setup started, or null before any: it ends when setup does.</summary>
    internal Task? Setup => _setup;

    /// <summary>Setup was started and hasn't ended.</summary>
    private bool SetupRunning => _setup is { IsCompleted: false };

    /// <summary>Required by the server, or asked for with Update now.</summary>
    private bool Urgent => _installNow || _policy.IsBelowMinimum(_env.Running.ToString(3));

    public Task<string?> InstallNowAsync(CancellationToken cancel)
    {
        if (!Installs) return Task.FromResult<string?>(NoKey);
        if (_ready is null && _found is null && !SetupRunning) return Task.FromResult<string?>(NothingReady);
        _installNow = true;
        Wake();
        return Task.FromResult<string?>(null);
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        _policy.Changed += OnPolicyChanged;
        try
        {
            RelaunchApp();
            await RefreshPolicyAsync(stop).ConfigureAwait(false);
            if (Urgent) _checkSoon = true;
            var nextCheck = _clock.GetUtcNow() + FirstCheck;
            while (!stop.IsCancellationRequested)
            {
                var woken = Volatile.Read(ref _wakeup).Task;
                if (_clock.GetUtcNow() >= nextCheck || _checkSoon)
                {
                    _checkSoon = false;
                    await RefreshPolicyAsync(stop).ConfigureAwait(false);
                    await CheckAsync(stop).ConfigureAwait(false);
                    nextCheck = _clock.GetUtcNow() + CheckEvery;
                }
                await TickAsync(stop).ConfigureAwait(false);
                var wait = nextCheck - _clock.GetUtcNow();
                if (_ready is not null && !SetupRunning && wait > Tick) wait = Tick;
                if (wait > TimeSpan.Zero) await Task.WhenAny(Task.Delay(wait, _clock, stop), woken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The service is stopping, perhaps for the setup this worker started.
        }
        finally
        {
            _policy.Changed -= OnPolicyChanged;
        }
    }

    /// <summary>Asks the data server for its minimum version; a server that doesn't answer leaves the last one standing.</summary>
    internal async Task RefreshPolicyAsync(CancellationToken cancel)
    {
        _policy.Set(await _env.Policy.MinVersionAsync(cancel).ConfigureAwait(false));
        Publish();
    }

    /// <summary>One check: the newest release, downloaded and checked when it is newer, signed, and the connection allows.
    /// Never throws but for cancellation: what went wrong is the stage, and the next check tries again.</summary>
    internal async Task CheckAsync(CancellationToken cancel)
    {
        if (!Installs || SetupRunning) return;
        string? downloaded = null;
        try
        {
            _env.Installers.Clean(_env.Running);
            var release = await _env.Feed.LatestAsync(cancel).ConfigureAwait(false);
            var now = _clock.GetUtcNow();
            var failed = FailedUpdate.Read(_env.Folder.Path);
            if (failed is not null && (!Version.TryParse(failed.Version, out var failedVersion) || failedVersion != release?.Version))
            {
                TryDelete(Path.Combine(_env.Folder.Path, FailedUpdate.FileName));   // another release starts afresh
                failed = null;
            }
            if (release is null || release.Version <= _env.Running)
            {
                _found = null;
                _ready = null;
                _installNow = false;
                Say("PowerLedger is up to date");
                return;
            }
            if (release.Version == _failed) return;
            if (failed is not null && failed.Holds(release.Version, now))
            {
                Say(failed.Tries >= FailedUpdate.MaxTries
                    ? $"PowerLedger {release.Name} didn't install twice, so the App offers it instead"
                    : $"PowerLedger {release.Name} didn't install, so it's tried again in a day");
                return;
            }
            if (release.Signatures is not { } signatures)
            {
                Say($"PowerLedger {release.Name} isn't signed, so the App offers it instead");
                return;
            }
            if (_ready?.Release.Version == release.Version) return;
            if (_found?.Release.Version != release.Version) _found = new Found(release, now);
            if (!InstallTiming.MayDownload(_env.Cost.Metered, Urgent, _found!.FirstSeen, now))
            {
                Say($"PowerLedger {release.Name} waits for a connection that isn't metered");
                return;
            }
            _env.Folder.Prepare();
            Say($"Downloading PowerLedger {release.Name}");
            downloaded = await _env.Installers.DownloadAsync(release, cancel).ConfigureAwait(false);
            var listed = await _env.Installers.FetchAsync(signatures, cancel).ConfigureAwait(false);
            var signature = ReleaseSignature.For(listed, release.FileName)
                ?? throw new UpdateException($"PowerLedger {release.Name} lists no signature for {release.FileName}, so it isn't installed.");
            using (VerifiedInstaller.Open(downloaded, release.Size, release.Sha256, signature, _env.PublicKey))
            {
                // Checked now so a bad download is found at once; checked again, and held, when setup runs.
            }
            var path = downloaded;
            downloaded = null;
            _ready = new Ready(release, path, signature, _clock.GetUtcNow());
            _warnedAt = null;
            Say($"PowerLedger {release.Name} is ready to install");
        }
        catch (UpdateException error)
        {
            _log.LogWarning("Update check: {Problem}", error.Message);
            Say(error.Message);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            _log.LogError(error, "Checking for an update failed");
            Say("Couldn't check for updates: " + error.Message);
        }
        finally
        {
            if (downloaded is not null) TryDelete(downloaded);   // downloaded but refused: nothing to keep
        }
    }

    /// <summary>Looks at a checked download: waits, warns, or installs. True when setup was started.</summary>
    internal async Task<bool> TickAsync(CancellationToken cancel)
    {
        if (_ready is not { } ready || SetupRunning) return false;
        var console = _env.System.ConsoleSession();
        var atConsole = console != NoticeHub.NoSession && _env.System.UserSignedIn(console);
        var showing = atConsole && _signals.WindowShowingIn(console);
        var now = _clock.GetUtcNow();
        switch (InstallTiming.Decide(Urgent, atConsole, showing, _signals.UserIdleSeconds(), ready.DownloadedAt, _warnedAt, now))
        {
            case InstallMoment.Warn:
                _warnedAt = now;
                _notices.Publish(new HouseholdNotice(NoticeKind.Info, null, Soon, null, null, null));
                Say($"Installing PowerLedger {ready.Release.Name} in a minute");
                return false;
            case InstallMoment.Now:
                return await InstallAsync(ready, showing, cancel).ConfigureAwait(false);
            default:
                return false;
        }
    }

    /// <summary>Setup's command line: silent, no restart, the App's windows left to the service (/SERVICEUPDATE=1).</summary>
    internal static string SetupArguments(string log) => $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE=1 /SERVICEUPDATE=1 /LOG=\"{log}\"";

    private async Task<bool> InstallAsync(Ready ready, bool windowShowing, CancellationToken cancel)
    {
        var release = ready.Release;
        if (release.Version <= _env.Running)
        {
            _ready = null;   // never a version this service already is, or an older one
            return false;
        }
        var noted = false;
        try
        {
            _env.Folder.Prepare();
            var held = VerifiedInstaller.Open(ready.Path, release.Size, release.Sha256, ready.Signature, _env.PublicKey);
            try
            {
                new RelaunchNote(release.Name, windowShowing).Write(_env.Folder.Path);
                noted = true;
                Say($"Installing PowerLedger {release.Name}");
                _log.LogInformation("Installing PowerLedger {Version}", release.Name);
                await _env.System.CloseAppsAsync(cancel).ConfigureAwait(false);
                var log = Path.Combine(_env.Folder.Path, $"setup-{release.Name}.log");
                var setup = _env.System.StartSetup(held, SetupArguments(log));
                _setup = WatchAsync(setup, held, release, cancel);
                return true;
            }
            catch
            {
                held.Dispose();
                throw;
            }
        }
        catch (UpdateException error)
        {
            _ready = null;   // the next check downloads it afresh
            _log.LogWarning("The update wasn't installed: {Problem}", error.Message);
            Say(error.Message);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Failed(release, "Setup couldn't start: " + error.Message);
        }
        if (noted) RelaunchApp(atStart: false);
        return false;
    }

    /// <summary>Waits for setup while holding its installer. Setup stops this service to replace it, so reaching the end
    /// means the update didn't go in: the App opens again and the version isn't tried again this run.</summary>
    private async Task WatchAsync(ISetupProcess setup, VerifiedInstaller held, Release release, CancellationToken cancel)
    {
        int code;
        using (setup)
        using (held)
        {
            try
            {
                code = await setup.ExitAsync(cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;   // the service is stopping: setup carries on, and the next service reads the note
            }
        }
        Failed(release, $"Setup ended without installing PowerLedger {release.Name} (code {code.ToString(CultureInfo.InvariantCulture)})");
        RelaunchApp(atStart: false);
    }

    private void Failed(Release release, string problem)
    {
        _failed = release.Version;
        _ready = null;
        _installNow = false;
        _log.LogError("The update to {Version} failed: {Problem}", release.Name, problem);
        Say(problem);
        NoteFailure(release.Name);
    }

    /// <summary>Records on disk that <paramref name="version"/> didn't install, so a restarted service backs off from it
    /// (<see cref="FailedUpdate"/>).</summary>
    private void NoteFailure(string version)
    {
        try
        {
            _env.Folder.Prepare();
            FailedUpdate.After(FailedUpdate.Read(_env.Folder.Path), version, _clock.GetUtcNow()).Write(_env.Folder.Path);
        }
        catch (Exception error) when (error is UpdateException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("The failed update to {Version} couldn't be recorded: {Problem}", version, error.Message);
        }
    }

    /// <summary>Opens the App in the console user's session when a note asks for it, unless it is already running there,
    /// and deletes the note either way. At start, a note for a version newer than this service means setup stopped the
    /// service and then failed, since the service that started is the old one: that is recorded as a failure, so the same
    /// installer isn't run again every few minutes.</summary>
    internal void RelaunchApp(bool atStart = true)
    {
        RelaunchNote? note;
        try
        {
            _env.Folder.Prepare();
            note = RelaunchNote.Read(_env.Folder.Path);
        }
        catch (UpdateException error)
        {
            _log.LogWarning("The relaunch note couldn't be read: {Problem}", error.Message);
            return;
        }
        if (note is null) return;
        if (atStart && Version.TryParse(note.Version, out var noted) && noted > _env.Running)
        {
            _log.LogError("Setup stopped the service but didn't install PowerLedger {Version}", note.Version);
            NoteFailure(note.Version);
        }
        try
        {
            var console = _env.System.ConsoleSession();
            if (console != NoticeHub.NoSession && _env.System.UserSignedIn(console) && !_env.System.AppRunningIn(console))
            {
                _env.System.LaunchApp(console, _env.AppPath, note.AppArguments);
                _log.LogInformation("Opened the App after the update to {Version}", note.Version);
            }
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            _log.LogWarning(error, "The App couldn't be opened after the update; it opens at the next sign-in");
        }
        finally
        {
            TryDelete(Path.Combine(_env.Folder.Path, RelaunchNote.FileName));
        }
    }

    private void OnPolicyChanged()
    {
        Publish();
        if (Urgent) Wake();
    }

    private void Wake()
    {
        _checkSoon = true;
        Interlocked.Exchange(ref _wakeup, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    private void Say(string stage)
    {
        _stage = stage;
        Publish();
    }

    private void Publish()
        => _board.Publish(new UpdateStatus(Installs, _stage, _policy.MinVersion, _policy.IsBelowMinimum(_env.Running.ToString(3))));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // In use: the next clean removes it.
        }
    }

    /// <summary>A newer signed release, and when it was first seen (for the metered wait).</summary>
    private sealed record Found(Release Release, DateTimeOffset FirstSeen);

    /// <summary>A checked download waiting for its moment.</summary>
    private sealed record Ready(Release Release, string Path, byte[] Signature, DateTimeOffset DownloadedAt);
}
