using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Service.Households;
using PowerLedger.Service.Updates;
using PowerLedger.Updates;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>The update worker's rules (Plan Q §3, §4), against fakes for GitHub, the data server and Windows. Each test
/// signs with its own key pair.</summary>
public sealed class UpdateWorkerTests : IDisposable
{
    private const string Installer = "PowerLedger-0.9.1-setup-x64.exe";
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("the 0.9.1 installer");

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"powerledger-worker-{Guid.NewGuid():N}");
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly WatchedClock _clock = new(new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeFeed _feed = new();
    private readonly FakeInstallers _installers;
    private readonly FakePolicy _server = new();
    private readonly FakeCost _cost = new();
    private readonly FakeSystem _system = new();
    private readonly AppPolicy _policy = new();
    private readonly StatusBoard _board = new();
    private readonly NoticeHub _notices;
    private readonly ChannelReader<HouseholdNotice> _heard;
    private readonly ServiceSignals _signals;
    private readonly UpdateFolder _folder;

    public UpdateWorkerTests()
    {
        _folder = new UpdateFolder(Path.Combine(_root, "Updates"), [Me], owner => owner == Me);
        _installers = new FakeInstallers(_folder.Path);
        _notices = new NoticeHub(() => 1);
        _heard = _notices.Subscribe(1);
        _signals = new ServiceSignals(_clock);
        _installers.Signatures = SignaturesFor(Content);
    }

    private string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    /// <summary>The signatures file for <see cref="Installer"/> holding <paramref name="content"/>, signed for
    /// <paramref name="version"/>.</summary>
    private byte[] SignaturesFor(byte[] content, string version = "0.9.1")
    {
        var message = Encoding.UTF8.GetBytes($"PowerLedger|{version}|{Installer}|{Convert.ToHexStringLower(SHA256.HashData(content))}");
        var signature = _key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return Encoding.UTF8.GetBytes($$"""{ "{{Installer}}": "{{Convert.ToBase64String(signature)}}" }""");
    }

    private static Release Release(string version = "0.9.1", bool signed = true) => new(
        Version.Parse(version), new Uri($"https://github.com/mharisjamal/PowerLedger/releases/tag/v{version}"),
        new Uri($"https://github.com/mharisjamal/PowerLedger/releases/download/v{version}/{Installer}"), Installer, Content.Length,
        SHA256.HashData(Content),
        signed ? new ReleaseFile(new Uri($"https://github.com/mharisjamal/PowerLedger/releases/download/v{version}/sigs.json"), "sigs.json", 100, new byte[32]) : null);

    private UpdateWorker Worker(string? key = null, string running = "0.9.0") => new(
        new UpdateEnvironment(_feed, _installers, _server, _cost, _folder, _system, key ?? PublicKey, Version.Parse(running), @"C:\Program Files\PowerLedger\PowerLedger.exe"),
        _policy, _board, _notices, _signals, _clock, NullLogger<UpdateWorker>.Instance);

    /// <summary>A console user with the App's window showing and busy at it.</summary>
    private void UserAtWork()
    {
        _signals.ReportWindow("app", 1, visible: true);
        _signals.ReportIdle("app", 0);
    }

    private string NotePath => Path.Combine(_folder.Path, RelaunchNote.FileName);

    // ---- What is installed

    [Theory]
    [InlineData("0.9.0")]
    [InlineData("0.8.1")]
    public async Task A_release_not_newer_than_the_service_is_never_downloaded_or_installed(string version)
    {
        _feed.Latest = Release(version);
        var worker = Worker();

        await worker.CheckAsync(CancellationToken.None);
        (await worker.TickAsync(CancellationToken.None)).ShouldBeFalse();

        _installers.Downloads.ShouldBe(0);
        _system.Setups.ShouldBeEmpty();
        worker.Stage.ShouldBe("PowerLedger is up to date");
    }

    [Fact]
    public async Task A_newer_release_is_installed_silently_once_the_window_is_hidden()
    {
        _feed.Latest = Release();
        var worker = Worker();

        await worker.CheckAsync(CancellationToken.None);
        worker.Stage.ShouldBe("PowerLedger 0.9.1 is ready to install");
        (await worker.TickAsync(CancellationToken.None)).ShouldBeTrue();

        var (path, arguments) = _system.Setups.ShouldHaveSingleItem();
        path.ShouldBe(Path.Combine(_folder.Path, Installer));
        arguments.ShouldBe($"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE=1 /SERVICEUPDATE=1 /LOG=\"{Path.Combine(_folder.Path, "setup-0.9.1.log")}\"");
        _system.Log.ShouldBe(["close apps", "start setup"]);
        RelaunchNote.Read(_folder.Path).ShouldBe(new RelaunchNote("0.9.1", WindowWasVisible: false));
        worker.Stage.ShouldBe("Installing PowerLedger 0.9.1");
    }

    [Fact]
    public async Task The_installer_is_held_while_setup_runs()
    {
        _feed.Latest = Release();
        var worker = Worker();
        await worker.CheckAsync(CancellationToken.None);
        await worker.TickAsync(CancellationToken.None);

        Should.Throw<IOException>(() => File.OpenWrite(Path.Combine(_folder.Path, Installer)).Dispose());
        _system.Running.Single().Exit(0);
        await worker.Setup!;
        File.OpenWrite(Path.Combine(_folder.Path, Installer)).Dispose();
    }

    [Fact]
    public async Task A_release_without_a_signatures_file_is_left_to_the_App()
    {
        _feed.Latest = Release(signed: false);
        var worker = Worker();

        await worker.CheckAsync(CancellationToken.None);

        _installers.Downloads.ShouldBe(0);
        worker.Stage.ShouldBe("PowerLedger 0.9.1 isn't signed, so the App offers it instead");
        (await worker.InstallNowAsync(1, CancellationToken.None)).ShouldBe(UpdateWorker.NothingReady);
    }

    [Fact]
    public async Task An_installer_whose_signature_doesnt_verify_is_deleted_and_never_run()
    {
        _feed.Latest = Release();
        _installers.Signatures = SignaturesFor(Encoding.UTF8.GetBytes("another file"));
        var worker = Worker();

        await worker.CheckAsync(CancellationToken.None);
        (await worker.TickAsync(CancellationToken.None)).ShouldBeFalse();

        _system.Setups.ShouldBeEmpty();
        worker.Stage!.ShouldContain("signature");
        File.Exists(Path.Combine(_folder.Path, Installer)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_signatures_file_that_doesnt_name_the_installer_stops_the_install()
    {
        _feed.Latest = Release();
        _installers.Signatures = """{ "PowerLedger-0.9.1-setup.exe": "AQID" }"""u8.ToArray();
        var worker = Worker();

        await worker.CheckAsync(CancellationToken.None);
        (await worker.TickAsync(CancellationToken.None)).ShouldBeFalse();
        _system.Setups.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_installer_changed_after_its_check_is_not_run()
    {
        _feed.Latest = Release();
        var worker = Worker();
        await worker.CheckAsync(CancellationToken.None);
        File.WriteAllBytes(Path.Combine(_folder.Path, Installer), Encoding.UTF8.GetBytes("the 0.9.1 installEr"));

        (await worker.TickAsync(CancellationToken.None)).ShouldBeFalse();

        _system.Setups.ShouldBeEmpty();
        File.Exists(NotePath).ShouldBeFalse();
    }

    [Fact]
    public async Task Without_a_release_key_the_service_never_checks_or_installs()
    {
        _feed.Latest = Release();
        var worker = Worker(key: "");

        await worker.CheckAsync(CancellationToken.None);
        (await worker.TickAsync(CancellationToken.None)).ShouldBeFalse();

        _feed.Asked.ShouldBe(0);
        worker.Installs.ShouldBeFalse();
        (await worker.InstallNowAsync(1, CancellationToken.None)).ShouldBe(UpdateWorker.NoKey);
    }

    [Fact]
    public async Task A_folder_someone_opened_up_is_emptied_before_the_install_so_the_download_starts_again()
    {
        _feed.Latest = Release();
        var worker = Worker();
        await worker.CheckAsync(CancellationToken.None);
        var security = new DirectoryInfo(_folder.Path).GetAccessControl();
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null), System.Security.AccessControl.FileSystemRights.Modify,
            System.Security.AccessControl.AccessControlType.Allow));
        new DirectoryInfo(_folder.Path).SetAccessControl(security);

        (await worker.TickAsync(CancellationToken.None)).ShouldBeFalse();
        _system.Setups.ShouldBeEmpty();

        await worker.CheckAsync(CancellationToken.None);
        _installers.Downloads.ShouldBe(2);
        (await worker.TickAsync(CancellationToken.None)).ShouldBeTrue();
    }

    // ---- When

    [Fact]
    public async Task With_the_user_at_the_window_it_waits_then_warns_after_five_idle_minutes_and_installs_a_minute_later()
    {
        _feed.Latest = Release();
        UserAtWork();
        var worker = Worker();
        await worker.CheckAsync(CancellationToken.None);

        (await worker.TickAsync(CancellationToken.None)).ShouldBeFalse();
        _heard.TryRead(out _).ShouldBeFalse();

        _signals.ReportIdle("app", 300);
        (await worker.TickAsync(CancellationToken.None)).ShouldBeFalse();
        _heard.TryRead(out var notice).ShouldBeTrue();
        notice!.ShouldBe(new HouseholdNotice(NoticeKind.Info, null, "PowerLedger updates in a minute", null, null, null));

        _clock.Advance(TimeSpan.FromSeconds(59));
        (await worker.TickAsync(CancellationToken.None)).ShouldBeFalse();
        _clock.Advance(TimeSpan.FromSeconds(1));
        (await worker.TickAsync(CancellationToken.None)).ShouldBeTrue();
        RelaunchNote.Read(_folder.Path).ShouldBe(new RelaunchNote("0.9.1", WindowWasVisible: true));
    }

    [Fact]
    public async Task However_busy_the_user_it_goes_in_six_hours_after_the_download()
    {
        _feed.Latest = Release();
        var worker = Worker();
        await worker.CheckAsync(CancellationToken.None);
        for (var minutes = 0; minutes < 359; minutes++)
        {
            UserAtWork();
            (await worker.TickAsync(CancellationToken.None)).ShouldBeFalse();
            _clock.Advance(TimeSpan.FromMinutes(1));
        }
        UserAtWork();
        (await worker.TickAsync(CancellationToken.None)).ShouldBeFalse();   // 5 h 59 m: the notice
        _heard.TryRead(out _).ShouldBeTrue();
        _clock.Advance(TimeSpan.FromMinutes(1));
        UserAtWork();
        (await worker.TickAsync(CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task With_nobody_at_the_console_it_goes_in_at_once()
    {
        _feed.Latest = Release();
        UserAtWork();
        _system.SignedIn = false;
        var worker = Worker();
        await worker.CheckAsync(CancellationToken.None);
        (await worker.TickAsync(CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task Below_the_servers_minimum_it_goes_in_at_once_with_the_window_in_use()
    {
        _feed.Latest = Release();
        UserAtWork();
        _server.Min = "0.9.1";
        var worker = Worker();

        await worker.RefreshPolicyAsync(CancellationToken.None);
        await worker.CheckAsync(CancellationToken.None);

        _board.Publish(Statuses());
        _board.Status!.Updates.ShouldBe(new UpdateStatus(true, "PowerLedger 0.9.1 is ready to install", "0.9.1", UpdateRequired: true));
        (await worker.TickAsync(CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task Update_now_installs_at_once_with_the_window_in_use()
    {
        _feed.Latest = Release();
        UserAtWork();
        var worker = Worker();
        await worker.CheckAsync(CancellationToken.None);

        (await worker.InstallNowAsync(1, CancellationToken.None)).ShouldBeNull();
        (await worker.TickAsync(CancellationToken.None)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(null)]
    public async Task Update_now_from_outside_the_console_session_is_refused_unless_the_update_is_required(uint? session)
    {
        _feed.Latest = Release();
        UserAtWork();
        var worker = Worker();
        await worker.CheckAsync(CancellationToken.None);

        (await worker.InstallNowAsync(session, CancellationToken.None)).ShouldBe(UpdateWorker.NotYours);
        (await worker.TickAsync(CancellationToken.None)).ShouldBeFalse();

        _server.Min = "0.9.1";
        await worker.RefreshPolicyAsync(CancellationToken.None);
        (await worker.InstallNowAsync(session, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Update_now_keeps_the_metered_wait_unless_the_update_is_required()
    {
        _feed.Latest = Release();
        _cost.Metered = true;
        var worker = Worker();
        await worker.CheckAsync(CancellationToken.None);

        (await worker.InstallNowAsync(1, CancellationToken.None)).ShouldBeNull();
        await worker.CheckAsync(CancellationToken.None);
        _installers.Downloads.ShouldBe(0);

        _server.Min = "0.9.1";
        await worker.RefreshPolicyAsync(CancellationToken.None);
        await worker.CheckAsync(CancellationToken.None);
        _installers.Downloads.ShouldBe(1);
    }

    [Fact]
    public async Task On_a_metered_connection_it_waits_a_day_before_downloading()
    {
        _feed.Latest = Release();
        _cost.Metered = true;
        var worker = Worker();

        await worker.CheckAsync(CancellationToken.None);
        _installers.Downloads.ShouldBe(0);
        worker.Stage.ShouldBe("PowerLedger 0.9.1 waits for a connection that isn't metered");

        _clock.Advance(TimeSpan.FromHours(24));
        await worker.CheckAsync(CancellationToken.None);
        _installers.Downloads.ShouldBe(1);
    }

    [Fact]
    public async Task A_required_update_downloads_on_a_metered_connection_at_once()
    {
        _feed.Latest = Release();
        _cost.Metered = true;
        _server.Min = "0.9.1";
        var worker = Worker();
        await worker.RefreshPolicyAsync(CancellationToken.None);
        await worker.CheckAsync(CancellationToken.None);
        _installers.Downloads.ShouldBe(1);
    }

    // ---- After setup

    [Fact]
    public async Task A_setup_that_ends_with_the_service_still_running_didnt_install_so_the_App_comes_back_and_the_version_isnt_retried()
    {
        _feed.Latest = Release();
        var worker = Worker();
        await worker.CheckAsync(CancellationToken.None);
        await worker.TickAsync(CancellationToken.None);

        _system.Running.Single().Exit(2);
        await worker.Setup!;

        _system.Launches.ShouldBe([(1u, @"C:\Program Files\PowerLedger\PowerLedger.exe", "--after-update --tray")]);
        File.Exists(NotePath).ShouldBeFalse();
        worker.Stage.ShouldBe("Setup ended without installing PowerLedger 0.9.1 (code 2)");
        await worker.CheckAsync(CancellationToken.None);
        (await worker.TickAsync(CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task At_start_a_relaunch_note_opens_the_App_in_the_console_session_with_its_window_if_it_was_showing()
    {
        _folder.Prepare();
        new RelaunchNote("0.9.1", WindowWasVisible: true).Write(_folder.Path);

        Worker().RelaunchApp();

        _system.Launches.ShouldBe([(1u, @"C:\Program Files\PowerLedger\PowerLedger.exe", "--after-update")]);
        File.Exists(NotePath).ShouldBeFalse();
    }

    [Fact]
    public async Task A_note_for_a_version_newer_than_the_service_at_start_is_a_failed_setup_tried_once_more_a_day_later_then_left_to_the_App()
    {
        _feed.Latest = Release();
        _folder.Prepare();
        new RelaunchNote("0.9.1", WindowWasVisible: false).Write(_folder.Path);
        var worker = Worker();

        worker.RelaunchApp();                                                  // setup stopped this service and failed
        await worker.CheckAsync(CancellationToken.None);
        _installers.Downloads.ShouldBe(0);
        worker.Stage.ShouldBe("PowerLedger 0.9.1 didn't install, so it's tried again in a day");

        _clock.Advance(TimeSpan.FromHours(24));
        await worker.CheckAsync(CancellationToken.None);
        (await worker.TickAsync(CancellationToken.None)).ShouldBeTrue();

        var again = Worker();                                                  // and failed again
        again.RelaunchApp();
        _clock.Advance(TimeSpan.FromDays(3));
        await again.CheckAsync(CancellationToken.None);
        _installers.Downloads.ShouldBe(1);
        again.Stage.ShouldBe("PowerLedger 0.9.1 didn't install twice, so the App offers it instead");

        _feed.Latest = Release("0.9.2");                                        // a newer release starts afresh
        _installers.Signatures = SignaturesFor(Content, "0.9.2");
        await again.CheckAsync(CancellationToken.None);
        _installers.Downloads.ShouldBe(2);
        File.Exists(Path.Combine(_folder.Path, FailedUpdate.FileName)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_note_for_the_version_now_running_is_a_setup_that_worked()
    {
        _feed.Latest = Release("0.9.2");
        _folder.Prepare();
        new RelaunchNote("0.9.1", WindowWasVisible: false).Write(_folder.Path);
        var worker = Worker(running: "0.9.1");

        worker.RelaunchApp();
        await worker.CheckAsync(CancellationToken.None);

        File.Exists(Path.Combine(_folder.Path, FailedUpdate.FileName)).ShouldBeFalse();
        _installers.Downloads.ShouldBe(1);
    }

    [Fact]
    public void An_App_already_running_there_is_not_opened_twice_and_the_note_still_goes()
    {
        _folder.Prepare();
        new RelaunchNote("0.9.1", WindowWasVisible: false).Write(_folder.Path);
        _system.AppRunning = true;

        Worker().RelaunchApp();

        _system.Launches.ShouldBeEmpty();
        File.Exists(NotePath).ShouldBeFalse();
    }

    [Fact]
    public void With_nobody_signed_in_the_App_waits_for_the_next_sign_in()
    {
        _folder.Prepare();
        new RelaunchNote("0.9.1", WindowWasVisible: false).Write(_folder.Path);
        _system.SignedIn = false;

        Worker().RelaunchApp();

        _system.Launches.ShouldBeEmpty();
        File.Exists(NotePath).ShouldBeFalse();
    }

    [Fact]
    public void Without_a_note_nothing_is_opened()
    {
        Worker().RelaunchApp();
        _system.Launches.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_status_says_who_installs_and_the_servers_minimum()
    {
        _server.Min = "0.8.0";
        var worker = Worker(key: "");
        await worker.RefreshPolicyAsync(CancellationToken.None);
        _board.Publish(Statuses());
        _board.Status!.Updates.ShouldBe(new UpdateStatus(false, null, "0.8.0", UpdateRequired: false));
    }

    // ---- The schedule

    /// <summary>Moves the clock on in small steps until <paramref name="done"/> holds, each step only once the worker is
    /// waiting on a timer still to come, so a step never lands while the worker, on its own thread, is still between one
    /// wait and the next; returns how far it went.</summary>
    private async Task<TimeSpan> AdvanceUntil(Func<bool> done, TimeSpan step)
    {
        var moved = TimeSpan.Zero;
        await WaitFor.True(() =>
        {
            if (done()) return true;
            if (!_clock.Waiting) return false;
            _clock.Advance(step);
            moved += step;
            return false;
        });
        return moved;
    }

    [Fact]
    public async Task The_worker_asks_the_server_at_start_and_checks_two_minutes_later_then_hourly()
    {
        _feed.Latest = Release("0.9.0");
        var worker = Worker();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitFor.True(() => _server.Asked == 1);
            var first = await AdvanceUntil(() => _feed.Asked == 1, TimeSpan.FromSeconds(10));
            first.ShouldBeInRange(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(10));
            var next = await AdvanceUntil(() => _feed.Asked == 2, TimeSpan.FromMinutes(1));
            next.ShouldBeInRange(TimeSpan.FromHours(1), TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
            _server.Asked.ShouldBe(3);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task An_update_becoming_required_is_checked_for_and_installed_at_once()
    {
        _feed.Latest = Release();
        UserAtWork();
        var worker = Worker();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitFor.True(() => _server.Asked == 1);
            _policy.Set("0.9.1");
            await WaitFor.True(() => _system.Setups.Count == 1);
            _feed.Asked.ShouldBe(1);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // ---- The data server's policy

    [Theory]
    [InlineData("""{ "minVersion": "0.9.0" }""", "0.9.0")]
    [InlineData("""{ "minVersion": 9 }""", null)]
    [InlineData("""[]""", null)]
    [InlineData("""nope""", null)]
    public void The_app_policy_answer_gives_its_minimum(string json, string? expected)
        => AppPolicyClient.Parse(Encoding.UTF8.GetBytes(json)).ShouldBe(expected);

    [Fact]
    public async Task The_app_policy_is_asked_for_with_the_services_version()
    {
        var handler = new Answering("""{ "minVersion": "0.9.0" }""");
        using var http = new HttpClient(handler);
        (await new AppPolicyClient(http, new Uri("http://127.0.0.1:8787/"), "0.9.0").MinVersionAsync(CancellationToken.None)).ShouldBe("0.9.0");
        handler.Request!.RequestUri.ShouldBe(new Uri("http://127.0.0.1:8787/v1/app-policy"));
        handler.Request.Headers.GetValues("X-PowerLedger-Version").ShouldBe(["0.9.0"]);
    }

    [Fact]
    public async Task A_server_that_doesnt_answer_leaves_no_minimum()
    {
        using var http = new HttpClient(new Answering("", HttpStatusCode.ServiceUnavailable));
        (await new AppPolicyClient(http, new Uri("http://127.0.0.1:8787/"), "0.9.0").MinVersionAsync(CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public void A_test_feed_is_taken_only_on_this_machine()
    {
        UpdateFeedSetting.Resolve(() => "http://127.0.0.1:8765").ShouldBe(new Uri("http://127.0.0.1:8765/"));
        UpdateFeedSetting.Resolve(() => "https://example.com/").ShouldBeNull();
        UpdateFeedSetting.Resolve(() => null).ShouldBeNull();
    }

    private static ServiceStatus Statuses() => new(
        "0.9.0", DateTimeOffset.UnixEpoch, 0, [], 0, 0, new CalibrationStatus(0, 0, 0, 0), "", 0, null, null, null);

    public void Dispose()
    {
        _key.Dispose();
        foreach (var setup in _system.Running) setup.Exit(0);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A temp folder left behind.
        }
    }

    /// <summary>A fake clock that knows when the last timer made on it is due, so a test can tell the worker is waiting.</summary>
    private sealed class WatchedClock(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private long _dueTicks = long.MinValue;

        /// <summary>The last timer made is still to come.</summary>
        public bool Waiting => GetUtcNow().UtcTicks < Interlocked.Read(ref _dueTicks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime != Timeout.InfiniteTimeSpan) Interlocked.Exchange(ref _dueTicks, (GetUtcNow() + dueTime).UtcTicks);
            return timer;
        }
    }

    private sealed class FakeFeed : IReleaseFeed
    {
        public Release? Latest { get; set; }

        public int Asked { get; private set; }

        public Task<Release?> LatestAsync(CancellationToken cancel)
        {
            Asked++;
            return Task.FromResult(Latest);
        }
    }

    private sealed class FakeInstallers(string folder) : IInstallerSource
    {
        public byte[] Signatures { get; set; } = [];

        public int Downloads { get; private set; }

        public Task<string> DownloadAsync(Release release, CancellationToken cancel)
        {
            Downloads++;
            var path = Path.Combine(folder, release.FileName);
            File.WriteAllBytes(path, Content);
            return Task.FromResult(path);
        }

        public Task<byte[]> FetchAsync(ReleaseFile file, CancellationToken cancel) => Task.FromResult(Signatures);

        public void Clean(Version running)
        {
        }
    }

    private sealed class FakePolicy : IAppPolicySource
    {
        public string? Min { get; set; }

        public int Asked { get; private set; }

        public Task<string?> MinVersionAsync(CancellationToken cancel)
        {
            Asked++;
            return Task.FromResult(Min);
        }
    }

    private sealed class FakeCost : IConnectionCost
    {
        public bool Metered { get; set; }
    }

    private sealed class FakeSystem : IUpdateSystem
    {
        public bool SignedIn { get; set; } = true;

        public bool AppRunning { get; set; }

        public List<string> Log { get; } = [];

        public List<(string Path, string Arguments)> Setups { get; } = [];

        public List<FakeSetup> Running { get; } = [];

        public List<(uint Session, string Path, string Arguments)> Launches { get; } = [];

        public uint ConsoleSession() => 1;

        public bool UserSignedIn(uint session) => SignedIn;

        public Task CloseAppsAsync(CancellationToken cancel)
        {
            Log.Add("close apps");
            return Task.CompletedTask;
        }

        public ISetupProcess StartSetup(VerifiedInstaller installer, string arguments)
        {
            Log.Add("start setup");
            Setups.Add((installer.Path, arguments));
            var setup = new FakeSetup();
            Running.Add(setup);
            return setup;
        }

        public bool AppRunningIn(uint session) => AppRunning;

        public void LaunchApp(uint session, string path, string arguments) => Launches.Add((session, path, arguments));
    }

    private sealed class FakeSetup : ISetupProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Exit(int code) => _exit.TrySetResult(code);

        public Task<int> ExitAsync(CancellationToken cancel) => _exit.Task.WaitAsync(cancel);

        public void Dispose()
        {
        }
    }

    private sealed class Answering(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
