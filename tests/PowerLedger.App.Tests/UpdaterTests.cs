using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.App.Tests;

public class UpdaterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeFeed _feed = new();
    private readonly FakeDownloader _downloader = new();
    private readonly FakeSetup _setup = new();
    private readonly FakeCost _cost = new();
    private readonly FakeUiSettings _ui = new();
    private readonly List<(Release Release, bool Ready)> _announced = [];
    private readonly List<Uri> _opened = [];

    internal static Release Release(string version) => new(
        Version.Parse(version), new Uri($"https://github.com/mharisjamal/PowerLedger/releases/tag/v{version}"),
        new Uri($"{GitHubReleaseFeed.Downloads}v{version}/PowerLedger-{version}-setup.exe"), $"PowerLedger-{version}-setup.exe", 1000, new byte[32]);

    private Updater Updater(string running = "0.2.0") => new(
        _feed, _downloader, _setup, _cost, _ui, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, Version.Parse(running), (release, ready) => _announced.Add((release, ready)), _opened.Add);

    [Fact]
    public async Task A_newer_release_is_downloaded_quietly_then_offered()
    {
        _feed.Latest = Release("0.3.0");
        var updater = Updater();

        await updater.CheckAsync();

        _downloader.Downloads.Single().Name.ShouldBe("0.3.0");
        updater.Stage.ShouldBe(UpdateStage.Ready);
        updater.ShowCard.ShouldBeTrue();
        updater.Title.ShouldBe("PowerLedger 0.3.0 is ready");
        updater.Detail.ShouldBeNull();
        updater.ActionLabel.ShouldBe("Restart to update");
        updater.HasNotes.ShouldBeTrue();
        updater.ReadyVersion.ShouldBe("0.3.0");
        updater.Status.ShouldBe("PowerLedger 0.3.0 is ready to install");
    }

    [Fact]
    public async Task Nothing_shows_while_the_download_runs()
    {
        _feed.Latest = Release("0.3.0");
        var gate = new TaskCompletionSource();
        _downloader.Gate = gate.Task;
        var updater = Updater();

        var check = updater.CheckAsync();
        updater.ShowCard.ShouldBeFalse();
        updater.Status.ShouldBe("Downloading 0.3.0… 50%");

        gate.SetResult();
        await check;
        updater.ShowCard.ShouldBeTrue();
    }

    [Fact]
    public async Task On_a_metered_connection_an_update_is_offered_rather_than_downloaded()
    {
        _cost.Metered = true;
        _feed.Latest = Release("0.3.0") with { Size = 58 * 1024 * 1024 };
        var updater = Updater();

        await updater.CheckAsync();

        _downloader.Downloads.ShouldBeEmpty();
        updater.Stage.ShouldBe(UpdateStage.Available);
        updater.ShowCard.ShouldBeTrue();
        updater.Title.ShouldBe("PowerLedger 0.3.0 is available");
        updater.Detail.ShouldBe("58 MB · waiting for a connection that isn't metered");
        updater.ActionLabel.ShouldBe("Download");
        updater.ReadyVersion.ShouldBeNull();                    // nothing to restart into yet
        updater.Status.ShouldBe("PowerLedger 0.3.0 is available · 58 MB · waiting for a connection that isn't metered");
        _announced.Single().Release.Name.ShouldBe("0.3.0");
        _announced.Single().Ready.ShouldBeFalse();              // the tray offers it, rather than saying it is ready to restart into
    }

    [Fact]
    public async Task Download_takes_an_update_over_a_metered_connection_because_it_was_asked_for()
    {
        _cost.Metered = true;
        _feed.Latest = Release("0.3.0");
        var updater = Updater();
        await updater.CheckAsync();

        updater.Act.Execute(null);                              // the card's Download

        await WaitFor.True(() => updater.Stage == UpdateStage.Ready);
        _downloader.Downloads.Single().Name.ShouldBe("0.3.0");
        updater.ActionLabel.ShouldBe("Restart to update");
    }

    /// <summary>Download is one attempt, not leave for the allowance: a download that fails asks again.</summary>
    [Fact]
    public async Task A_download_that_fails_on_a_metered_connection_asks_again_rather_than_spending_more()
    {
        _cost.Metered = true;
        _feed.Latest = Release("0.3.0");
        var updater = Updater();
        await updater.CheckAsync();
        _downloader.Failure = new UpdateException("The download stalled; PowerLedger tries again later.");

        updater.Act.Execute(null);                              // the card's Download
        await WaitFor.True(() => updater.Status.StartsWith("The download stalled", StringComparison.Ordinal));

        _downloader.Failure = null;
        await updater.CheckAsync();                             // the next hourly check, still metered

        updater.Stage.ShouldBe(UpdateStage.Available);
        _downloader.Downloads.ShouldBeEmpty();
    }

    [Fact]
    public async Task Off_the_metered_connection_the_next_check_downloads_it_quietly()
    {
        _cost.Metered = true;
        _feed.Latest = Release("0.3.0");
        var updater = Updater();
        await updater.CheckAsync();
        updater.Stage.ShouldBe(UpdateStage.Available);

        _cost.Metered = false;
        await updater.CheckAsync();

        updater.Stage.ShouldBe(UpdateStage.Ready);
        _downloader.Downloads.Count.ShouldBe(1);
        _announced.Count.ShouldBe(1);                           // still only announced once
    }

    [Fact]
    public async Task The_tray_announces_each_version_once()
    {
        _feed.Latest = Release("0.3.0");
        var updater = Updater();
        await updater.CheckAsync();
        await updater.CheckAsync();
        _announced.Select(a => a.Release.Name).ShouldBe(new[] { "0.3.0" });
        _ui.Current.AnnouncedVersion.ShouldBe("0.3.0");

        var later = Updater();                       // the App's next start
        await later.CheckAsync();
        _announced.Count.ShouldBe(1);

        _feed.Latest = Release("0.4.0");
        await later.CheckAsync();
        _announced.Select(a => a.Release.Name).ShouldBe(new[] { "0.3.0", "0.4.0" });
        later.Title.ShouldBe("PowerLedger 0.4.0 is ready");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0.2.0")]
    [InlineData("0.1.9")]
    public async Task Nothing_newer_is_up_to_date(string? latest)
    {
        _feed.Latest = latest is null ? null : Release(latest);
        var updater = Updater();

        await updater.CheckAsync();

        updater.Stage.ShouldBe(UpdateStage.None);
        updater.ShowCard.ShouldBeFalse();
        updater.Status.ShouldBe("PowerLedger is up to date · checked 09:00");
        _downloader.Downloads.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_check_or_download_says_why_in_settings_and_shows_no_card()
    {
        _feed.Failure = new UpdateException("Couldn't reach GitHub.");
        var updater = Updater();
        await updater.CheckAsync();
        updater.Status.ShouldBe("Couldn't reach GitHub. · 09:00");
        updater.ShowCard.ShouldBeFalse();

        _feed.Failure = null;
        _feed.Latest = Release("0.3.0");
        _downloader.Failure = new UpdateException("The download stalled; PowerLedger tries again later.");
        await updater.CheckAsync();
        updater.Status.ShouldBe("The download stalled; PowerLedger tries again later. · 09:00");
        updater.ShowCard.ShouldBeFalse();
        _announced.ShouldBeEmpty();
    }

    [Fact]
    public void It_checks_a_minute_after_starting_and_every_hour_while_allowed()
    {
        var updater = Updater();
        updater.Start();
        _clock.Advance(TimeSpan.FromSeconds(59));
        _feed.Asked.ShouldBe(0);
        _clock.Advance(TimeSpan.FromSeconds(1));
        _feed.Asked.ShouldBe(1);
        _clock.Advance(TimeSpan.FromHours(1));
        _feed.Asked.ShouldBe(2);

        updater.CheckAutomatically = false;
        _ui.Current.CheckForUpdates.ShouldBeFalse();
        _clock.Advance(TimeSpan.FromHours(12));
        _feed.Asked.ShouldBe(2);

        updater.CheckNow.Execute(null);            // asked for, so it checks anyway
        _feed.Asked.ShouldBe(3);
    }

    [Fact]
    public async Task A_check_while_one_runs_does_nothing()
    {
        _feed.Latest = Release("0.3.0");
        var gate = new TaskCompletionSource();
        _downloader.Gate = gate.Task;
        var updater = Updater();

        var first = updater.CheckAsync();
        await updater.CheckAsync();
        _feed.Asked.ShouldBe(1);

        gate.SetResult();
        await first;
    }

    [Fact]
    public async Task Restart_to_update_starts_setup_with_the_checked_download()
    {
        _feed.Latest = Release("0.3.0");
        var updater = Updater();
        await updater.CheckAsync();
        var exit = new TaskCompletionSource<int>();
        _setup.Exit = exit.Task;

        updater.Act.Execute(null);

        updater.Stage.ShouldBe(UpdateStage.Installing);
        updater.Title.ShouldBe("Installing 0.3.0…");
        updater.Detail.ShouldBe("Windows asks for permission");
        updater.ActionLabel.ShouldBeNull();
        updater.ReadyVersion.ShouldBeNull();
        _setup.Started.Single().ShouldBe((@"C:\Updates\PowerLedger-0.3.0-setup.exe", @"C:\Updates\PowerLedger-0.3.0-setup.log"));

        exit.SetResult(5);                          // setup ended with the App still here: nothing went in
        await WaitFor.True(() => updater.Stage == UpdateStage.Failed);
        updater.Title.ShouldBe("The update didn't install");
        updater.Detail.ShouldBe("Setup ended without installing (code 5).");
        updater.ActionLabel.ShouldBe("Try again");
    }

    [Theory]
    [InlineData("0.2.0", "0.2.0")]
    [InlineData("0.3.0-beta", "0.3.0")]
    [InlineData("not a version", "0.0.0")]
    public void The_running_version_is_read_from_the_numbers_before_any_suffix(string informational, string expected)
        => PowerLedger.App.Updater.RunningVersion(informational).ShouldBe(Version.Parse(expected));

    /// <summary>Inno Setup elevates itself, so a declined permission prompt comes back as setup stopping before it started.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Setup_that_stops_before_installing_asks_for_a_yes_next_time(int code)
    {
        _feed.Latest = Release("0.3.0");
        var updater = Updater();
        await updater.CheckAsync();
        _setup.Exit = Task.FromResult(code);

        updater.Act.Execute(null);

        await WaitFor.True(() => updater.Stage == UpdateStage.Failed);
        updater.Detail.ShouldBe($"Setup stopped before installing (code {code}). Try again, and choose Yes when Windows asks for permission.");
        updater.ReadyVersion.ShouldBe("0.3.0");
    }

    [Fact]
    public async Task A_declined_permission_prompt_leaves_the_update_ready_to_try_again()
    {
        _feed.Latest = Release("0.3.0");
        var updater = Updater();
        await updater.CheckAsync();
        _setup.Failure = new Win32Exception(SetupRunner.Declined);

        updater.Act.Execute(null);
        await WaitFor.True(() => updater.Stage == UpdateStage.Failed);
        updater.Detail.ShouldBe("Windows didn't get permission, so nothing was installed.");
        updater.ReadyVersion.ShouldBe("0.3.0");     // the tray still offers it

        _setup.Failure = null;
        _setup.Exit = new TaskCompletionSource<int>().Task;
        updater.Act.Execute(null);                   // the download is still good, so setup starts again
        updater.Stage.ShouldBe(UpdateStage.Installing);
        _setup.Started.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_download_that_changed_on_disk_is_fetched_again_on_try_again()
    {
        _feed.Latest = Release("0.3.0");
        var updater = Updater();
        await updater.CheckAsync();
        _setup.Failure = new UpdateException("The downloaded update changed on disk, so it wasn't run. PowerLedger downloads it again.");

        updater.Act.Execute(null);
        await WaitFor.True(() => updater.Stage == UpdateStage.Failed);
        updater.ReadyVersion.ShouldBeNull();         // nothing checked to offer until it is downloaded again

        _setup.Failure = null;
        updater.Act.Execute(null);                   // Try again: download afresh, then offer it again
        await WaitFor.True(() => updater.Stage == UpdateStage.Ready);
        _downloader.Downloads.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Later_hides_the_card_until_the_App_starts_again_but_the_tray_keeps_offering()
    {
        _feed.Latest = Release("0.3.0");
        var updater = Updater();
        await updater.CheckAsync();

        updater.Dismiss.Execute(null);
        updater.ShowCard.ShouldBeFalse();
        updater.ReadyVersion.ShouldBe("0.3.0");
        await updater.CheckAsync();                  // the same release again: the card stays away
        updater.ShowCard.ShouldBeFalse();

        updater.Install();                           // from the tray menu
        updater.ShowCard.ShouldBeTrue();             // a new stage brings the card back
    }

    [Fact]
    public void The_first_start_of_a_newer_version_says_it_was_updated()
    {
        _ui.Current = UiPreferences.Default with { LastVersion = "0.1.0" };
        var updater = Updater("0.2.0");
        updater.Start();

        updater.Stage.ShouldBe(UpdateStage.Updated);
        updater.Title.ShouldBe("Updated to 0.2.0");
        updater.HasNotes.ShouldBeTrue();
        updater.ActionLabel.ShouldBeNull();
        _ui.Current.LastVersion.ShouldBe("0.2.0");
        updater.OpenNotes.Execute(null);
        _opened.Single().ShouldBe(new Uri("https://github.com/mharisjamal/PowerLedger/releases/tag/v0.2.0"));

        var next = Updater("0.2.0");                 // only the first start says so
        next.Start();
        next.Stage.ShouldBe(UpdateStage.None);
    }

    [Fact]
    public void A_first_install_is_not_called_an_update_and_old_downloads_go()
    {
        var updater = Updater("0.2.0");
        updater.Start();

        updater.Stage.ShouldBe(UpdateStage.None);
        _ui.Current.LastVersion.ShouldBe("0.2.0");
        _downloader.Cleaned.ShouldBe(new[] { new Version(0, 2, 0) });
    }

    [Fact]
    public async Task Each_check_clears_downloads_the_running_version_no_longer_needs()
    {
        var updater = Updater();
        await updater.CheckAsync();
        await updater.CheckAsync();
        _downloader.Cleaned.ShouldBe(new[] { new Version(0, 2, 0), new Version(0, 2, 0) });
    }

    [Fact]
    public async Task Whats_new_opens_the_release_page()
    {
        _feed.Latest = Release("0.3.0");
        var updater = Updater();
        await updater.CheckAsync();

        updater.OpenNotes.Execute(null);

        _opened.Single().ShouldBe(new Uri("https://github.com/mharisjamal/PowerLedger/releases/tag/v0.3.0"));
    }

    [Fact]
    public async Task The_card_hears_of_every_change()
    {
        _feed.Latest = Release("0.3.0");
        var updater = Updater();
        var changed = new List<string?>();
        updater.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await updater.CheckAsync();

        changed.ShouldContain(nameof(updater.ShowCard));
        changed.ShouldContain(nameof(updater.Title));
        changed.ShouldContain(nameof(updater.ReadyVersion));
        changed.ShouldContain(nameof(updater.Status));
    }
}
