# Plan H — In-app updates Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Installed copies of PowerLedger find a newer GitHub release, download it quietly, offer "Restart to update" on a
card at the bottom-left of the window, in one tray notification per version and in the tray menu, and come back updated.

**Architecture:** A small updater inside the App (`src/PowerLedger.App/Updates/`): a feed that reads GitHub's latest
release and refuses anything it can't trust, a download checked against GitHub's SHA-256, a runner that starts the
existing Inno Setup installer with `/SILENT /NORESTART /UPDATE=1`, and an `Updater` view model that schedules checks and
drives the card, the tray and Settings. The installer gains one `[Run]` entry that reopens the App as the original user
after an update. Design: `docs/superpowers/specs/2026-09-16-powerledger-updates-design.md`.

**Tech Stack:** .NET 10 WPF (CommunityToolkit.Mvvm), `HttpClient`/`SocketsHttpHandler`, System.Text.Json source
generation, `IncrementalHash`, xUnit + Shouldly + `FakeTimeProvider`, Inno Setup 7.1, PowerShell 7, `gh`.

---

## Execution

The owner asked for speed with parallel subagents. Branch `plan-h/updates` holds the design; work splits in two waves.

| Wave | Who | Where | Tasks |
|---|---|---|---|
| 1 | Agent A | worktree on `plan-h/app`, from `plan-h/updates` | 1–8: the App side, TDD |
| 1 | Agent B | worktree on `plan-h/ship`, from `plan-h/updates` | 9–11: installer, installer test, release script, version, docs |
| 1 | Lead | main checkout | the Sandbox end-to-end harness (Task 12's script) |
| 2 | Lead | main checkout | merge A and B into `plan-h/updates`, Task 12 (build, tests, review, Sandbox, CI), Task 13 |

A and B touch disjoint files. Each commits its own tasks on its branch as `Haris <ai@smhcoders.com>` (the repository's
local identity), stages files by explicit path, and adds no attribution or Co-authored-by lines. Neither pushes.

## Files

Create (App): `src/PowerLedger.App/Updates/Release.cs`, `GitHubReleaseFeed.cs`, `UpdateHttp.cs`, `UpdateDownloader.cs`,
`SetupRunner.cs`, `Updater.cs`.
Create (tests): `tests/PowerLedger.App.Tests/FakeHttp.cs`, `FakeUpdates.cs`, `ReleaseFeedTests.cs`,
`UpdateDownloaderTests.cs`, `SetupRunnerTests.cs`, `UpdaterTests.cs`.
Create (tooling): `scripts/release.ps1`.
Modify (App): `Preferences/UiPreferences.cs`, `Preferences/AppPreferences.cs`, `AppOptions.cs`, `Tray/TrayIcon.cs`,
`Shell/ShellViewModel.cs`, `Shell/MainWindow.xaml`, `Settings/SettingsViewModel.cs`, `Settings/SettingsView.xaml`,
`Theme/Styles.xaml`, `App.xaml.cs`.
Modify (tests): `FakeUiSettings.cs`, `UiPreferencesTests.cs`, `AppPreferencesTests.cs`, `AppOptionsTests.cs`,
`RenderingTests.cs`.
Modify (shipping): `installer/PowerLedger.iss`, `installer/test-installer.ps1`, `Directory.Build.props`, `README.md`,
`docs/superpowers/specs/2026-09-08-powerledger-design.md`.

Commands below run from the repository root (or the worktree's root). The unit-test filter used throughout:

```
dotnet test tests/PowerLedger.App.Tests -c Release --filter "Category!=Hardware&Category!=UI&Category!=Installed"
```

---

### Task 1: The update preferences

**Files:**
- Modify: `src/PowerLedger.App/Preferences/UiPreferences.cs`
- Modify: `src/PowerLedger.App/Preferences/AppPreferences.cs`
- Modify: `tests/PowerLedger.App.Tests/FakeUiSettings.cs`
- Test: `tests/PowerLedger.App.Tests/UiPreferencesTests.cs`, `tests/PowerLedger.App.Tests/AppPreferencesTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `UiPreferencesTests`:

```csharp
    [Fact]
    public void Updates_are_on_by_default_even_in_a_file_from_before_they_existed()
    {
        new UiPreferencesStore(File).Load().CheckForUpdates.ShouldBeTrue();

        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, """{ "Theme": "Dark", "FirstRunDone": true }""");
        var old = new UiPreferencesStore(File).Load();
        old.CheckForUpdates.ShouldBeTrue();
        old.AnnouncedVersion.ShouldBeNull();
        old.LastVersion.ShouldBeNull();
    }

    [Fact]
    public void The_update_bookkeeping_survives_a_save_and_a_load()
    {
        var store = new UiPreferencesStore(File);
        var saved = UiPreferences.Default with { CheckForUpdates = false, AnnouncedVersion = "0.3.0", LastVersion = "0.2.0" };
        store.Save(saved);
        store.Load().ShouldBe(saved);
    }
```

Append to `AppPreferencesTests`:

```csharp
    [Fact]
    public void The_update_preferences_are_saved()
    {
        var preferences = Preferences();
        preferences.CheckForUpdates(false).ShouldBeNull();
        preferences.Announced("0.3.0").ShouldBeNull();
        preferences.Ran("0.2.0").ShouldBeNull();

        var saved = Store.Load();
        saved.CheckForUpdates.ShouldBeFalse();
        saved.AnnouncedVersion.ShouldBe("0.3.0");
        saved.LastVersion.ShouldBe("0.2.0");
    }
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/PowerLedger.App.Tests -c Release`
Expected: FAIL — `UiPreferences` has no `CheckForUpdates`, `AppPreferences` no `CheckForUpdates`/`Announced`/`Ran`.

- [ ] **Step 3: Add the preferences**

In `UiPreferences` (`src/PowerLedger.App/Preferences/UiPreferences.cs`), after `FirstRunDone`:

```csharp
    /// <summary>Look for new versions every few hours and download them quietly (spec §13). On until the user unticks it;
    /// a ui.json from before it existed keeps it on.</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>The newest version the tray has announced, so each version is announced once.</summary>
    public string? AnnouncedVersion { get; init; }

    /// <summary>The version that last ran, so the first start of a newer one can say it was updated.</summary>
    public string? LastVersion { get; init; }
```

In `IUiSettings` (`AppPreferences.cs`), after `StartWithWindows`:

```csharp
    string? CheckForUpdates(bool enabled);

    /// <summary>Remembers that the tray announced <paramref name="version"/>.</summary>
    string? Announced(string version);

    /// <summary>Remembers that <paramref name="version"/> ran.</summary>
    string? Ran(string version);
```

In `AppPreferences`, after `StartWithWindows(bool)`:

```csharp
    public string? CheckForUpdates(bool enabled) => Save(Current with { CheckForUpdates = enabled });

    public string? Announced(string version) => Save(Current with { AnnouncedVersion = version });

    public string? Ran(string version) => Save(Current with { LastVersion = version });
```

In `FakeUiSettings`, make `Current` settable by tests (`public UiPreferences Current { get; set; } = UiPreferences.Default;`)
and add:

```csharp
    public string? CheckForUpdates(bool enabled)
    {
        Current = Current with { CheckForUpdates = enabled };
        Changes.Add($"updates {enabled}");
        return null;
    }

    public string? Announced(string version)
    {
        Current = Current with { AnnouncedVersion = version };
        Changes.Add($"announced {version}");
        return null;
    }

    public string? Ran(string version)
    {
        Current = Current with { LastVersion = version };
        Changes.Add($"ran {version}");
        return null;
    }
```

- [ ] **Step 4: Run the tests**

Run: the unit-test filter above. Expected: PASS, the new tests included.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.App/Preferences/UiPreferences.cs src/PowerLedger.App/Preferences/AppPreferences.cs tests/PowerLedger.App.Tests/FakeUiSettings.cs tests/PowerLedger.App.Tests/UiPreferencesTests.cs tests/PowerLedger.App.Tests/AppPreferencesTests.cs
git commit -m "Remember whether to check for updates, the version announced and the version that last ran"
```

### Task 2: The `--update-feed` switch

**Files:**
- Modify: `src/PowerLedger.App/AppOptions.cs`
- Test: `tests/PowerLedger.App.Tests/AppOptionsTests.cs`

- [ ] **Step 1: Write the failing tests**

In `With_no_switches_the_App_reaches_the_installed_service` add `options.UpdateFeed.ShouldBeNull();`, then append:

```csharp
    [Theory]
    [InlineData("http://127.0.0.1:8765", "http://127.0.0.1:8765/")]
    [InlineData("http://localhost:8765/feed/", "http://localhost:8765/feed/")]
    [InlineData("https://example.org/pl", "https://example.org/pl/")]
    public void A_test_feed_is_https_or_on_this_machine(string given, string expected)
        => AppOptions.Parse(["--update-feed", given]).UpdateFeed.ShouldBe(new Uri(expected));

    [Theory]
    [InlineData("http://example.org/")]
    [InlineData("ftp://127.0.0.1/")]
    [InlineData("not a url")]
    [InlineData(@"C:\feed")]
    public void Any_other_feed_is_ignored(string given)
        => AppOptions.Parse(["--update-feed", given]).UpdateFeed.ShouldBeNull();
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/PowerLedger.App.Tests -c Release` — Expected: FAIL, `AppOptions` has no `UpdateFeed`.

- [ ] **Step 3: Add the switch**

Replace `src/PowerLedger.App/AppOptions.cs` with:

```csharp
using System.IO;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <param name="PipeName">The service's pipe; --pipe names a development service's.</param>
/// <param name="DataFolder">Where the service keeps power.db; --data names a development run's folder.</param>
/// <param name="StartInTray">--tray: start with only the tray icon, as the Run entry does.</param>
/// <param name="UpdateFeed">--update-feed: a stand-in for GitHub's releases, to test updates against; null means GitHub.</param>
internal sealed record AppOptions(string PipeName, string DataFolder, bool StartInTray, Uri? UpdateFeed = null)
{
    public static string DefaultDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PowerLedger");

    public string DatabasePath => Path.Combine(DataFolder, "power.db");

    public static AppOptions Parse(IReadOnlyList<string> args)
    {
        var pipe = PipeProtocol.PipeName;
        var data = DefaultDataFolder;
        var tray = false;
        Uri? feed = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--pipe" when i + 1 < args.Count:
                    pipe = args[++i];
                    break;
                case "--data" when i + 1 < args.Count:
                    data = args[++i];
                    break;
                case "--tray":
                    tray = true;
                    break;
                case "--update-feed" when i + 1 < args.Count:
                    feed = TestFeed(args[++i]);
                    break;
            }
        }
        return new AppOptions(pipe, data, tray, feed);
    }

    /// <summary>A feed to test updates against: HTTPS anywhere, or plain HTTP on this machine only; anything else is ignored.
    /// The address ends in a slash, so the feed's paths go under it.</summary>
    internal static Uri? TestFeed(string text)
    {
        if (!Uri.TryCreate(text.EndsWith('/') ? text : text + "/", UriKind.Absolute, out var feed)) return null;
        return feed.Scheme == Uri.UriSchemeHttps || (feed.Scheme == Uri.UriSchemeHttp && feed.IsLoopback) ? feed : null;
    }
}
```

- [ ] **Step 4: Run the tests** — the unit-test filter. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.App/AppOptions.cs tests/PowerLedger.App.Tests/AppOptionsTests.cs
git commit -m "Let a test point the App at a stand-in for GitHub's releases with --update-feed"
```

### Task 3: The release feed

**Files:**
- Create: `src/PowerLedger.App/Updates/Release.cs`, `src/PowerLedger.App/Updates/GitHubReleaseFeed.cs`,
  `src/PowerLedger.App/Updates/UpdateHttp.cs`
- Create: `tests/PowerLedger.App.Tests/FakeHttp.cs`
- Test: `tests/PowerLedger.App.Tests/ReleaseFeedTests.cs`

- [ ] **Step 1: Write the test helpers and the failing tests**

`tests/PowerLedger.App.Tests/FakeHttp.cs`:

```csharp
using System.IO;
using System.Net;
using System.Net.Http;

namespace PowerLedger.App.Tests;

/// <summary>An HttpClient whose answers the test decides, recording what was asked.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>What to answer; 404 until the test says otherwise.</summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Answer { get; set; } =
        (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

    public HttpClient Client() => new(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    public void Reply(HttpStatusCode status, byte[] body)
        => Answer = (_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });

    public void Reply(HttpContent content)
        => Answer = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Answer(request, cancellationToken);
    }
}

/// <summary>A body whose length isn't known up front, as a server streaming without Content-Length sends it.</summary>
internal sealed class Unsized(byte[] body) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(body).AsTask();

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

/// <summary>A body that sends nothing and never ends, as a connection gone quiet does.</summary>
internal sealed class Stalling : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
```

`tests/PowerLedger.App.Tests/ReleaseFeedTests.cs`:

```csharp
using System.Net;
using System.Net.Http;
using System.Text;
using Shouldly;

namespace PowerLedger.App.Tests;

public class ReleaseFeedTests
{
    private const string Downloads = GitHubReleaseFeed.Downloads;
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>GitHub's answer for a release, trimmed to what the App reads plus fields it ignores.</summary>
    internal static byte[] Answer(
        string tag = "v0.2.0", string? asset = null, long size = 100_297_944, string? url = null,
        string digest = $"\"sha256:{Sha}\"", bool draft = false, bool prerelease = false)
    {
        asset ??= $"PowerLedger-{tag.TrimStart('v')}-setup.exe";
        url ??= $"{Downloads}{tag}/{asset}";
        return Encoding.UTF8.GetBytes($$"""
            {
              "tag_name": "{{tag}}",
              "name": "PowerLedger {{tag}}",
              "html_url": "https://github.com/mharisjamal/PowerLedger/releases/tag/{{tag}}",
              "draft": {{(draft ? "true" : "false")}},
              "prerelease": {{(prerelease ? "true" : "false")}},
              "body": "Notes",
              "assets": [
                { "name": "notes.txt", "size": 12, "browser_download_url": "{{Downloads}}{{tag}}/notes.txt", "digest": null },
                { "name": "{{asset}}", "size": {{size}}, "browser_download_url": "{{url}}", "digest": {{digest}} }
              ]
            }
            """);
    }

    [Fact]
    public void A_release_with_its_installer_and_digest_is_trusted()
    {
        var release = GitHubReleaseFeed.Parse(Answer(), Downloads).ShouldNotBeNull();
        release.Version.ShouldBe(new Version(0, 2, 0));
        release.Name.ShouldBe("0.2.0");
        release.FileName.ShouldBe("PowerLedger-0.2.0-setup.exe");
        release.Size.ShouldBe(100_297_944);
        release.Installer.ShouldBe(new Uri($"{Downloads}v0.2.0/PowerLedger-0.2.0-setup.exe"));
        release.Page.ShouldBe(new Uri("https://github.com/mharisjamal/PowerLedger/releases/tag/v0.2.0"));
        Convert.ToHexStringLower(release.Sha256).ShouldBe(Sha);
    }

    [Fact]
    public void Drafts_and_pre_releases_are_not_offered()
    {
        GitHubReleaseFeed.Parse(Answer(draft: true), Downloads).ShouldBeNull();
        GitHubReleaseFeed.Parse(Answer(prerelease: true), Downloads).ShouldBeNull();
    }

    [Theory]
    [InlineData("0.2.0")]
    [InlineData("v0.2")]
    [InlineData("v0.2.0-beta")]
    [InlineData("v0.2.0.1")]
    [InlineData("v٠.٢.٠")]
    public void A_tag_that_is_not_a_version_is_refused(string tag)
        => Should.Throw<UpdateException>(() => GitHubReleaseFeed.Parse(Answer(tag: tag, asset: "PowerLedger-0.2.0-setup.exe"), Downloads))
            .Message.ShouldContain("isn't a version");

    [Fact]
    public void A_release_without_this_versions_installer_is_refused()
    {
        Should.Throw<UpdateException>(() => GitHubReleaseFeed.Parse(Answer(asset: "PowerLedger-0.1.0-setup.exe"), Downloads))
            .Message.ShouldBe("Release 0.2.0 has no PowerLedger-0.2.0-setup.exe.");
        Should.Throw<UpdateException>(() => GitHubReleaseFeed.Parse(Answer(asset: "powerledger-0.2.0-setup.exe"), Downloads));
    }

    [Theory]
    [InlineData("https://github.com/someone/PowerLedger/releases/download/v0.2.0/PowerLedger-0.2.0-setup.exe")]
    [InlineData("https://example.org/mharisjamal/PowerLedger/releases/download/v0.2.0/PowerLedger-0.2.0-setup.exe")]
    [InlineData("http://github.com/mharisjamal/PowerLedger/releases/download/v0.2.0/PowerLedger-0.2.0-setup.exe")]
    [InlineData("https://github.com/mharisjamal/PowerLedger/releases/download/../../../someone/x/PowerLedger-0.2.0-setup.exe")]
    [InlineData("not a url")]
    public void An_installer_kept_anywhere_else_is_refused(string url)
        => Should.Throw<UpdateException>(() => GitHubReleaseFeed.Parse(Answer(url: url), Downloads))
            .Message.ShouldContain("isn't where PowerLedger's releases are kept");

    [Theory]
    [InlineData("null")]
    [InlineData("\"md5:0123456789abcdef0123456789abcdef\"")]
    [InlineData("\"sha256:0123\"")]
    [InlineData("\"sha256:zz23456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"")]
    public void An_installer_without_a_sha256_is_refused(string digest)
        => Should.Throw<UpdateException>(() => GitHubReleaseFeed.Parse(Answer(digest: digest), Downloads))
            .Message.ShouldContain("no SHA-256");

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(GitHubReleaseFeed.MaxInstaller + 1)]
    public void An_impossible_size_is_refused(long size)
        => Should.Throw<UpdateException>(() => GitHubReleaseFeed.Parse(Answer(size: size), Downloads))
            .Message.ShouldContain("impossible size");

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("null")]
    public void An_answer_that_is_not_a_release_is_refused(string json)
        => Should.Throw<UpdateException>(() => GitHubReleaseFeed.Parse(Encoding.UTF8.GetBytes(json), Downloads));

    [Fact]
    public async Task The_latest_release_is_asked_of_GitHub_as_its_API_wants()
    {
        var http = new FakeHttp();
        http.Reply(HttpStatusCode.OK, Answer());
        using var client = http.Client();

        var release = await GitHubReleaseFeed.For(client, null).LatestAsync(CancellationToken.None);

        release.ShouldNotBeNull().Version.ShouldBe(new Version(0, 2, 0));
        var request = http.Requests.Single();
        request.RequestUri.ShouldBe(new Uri("https://api.github.com/repos/mharisjamal/PowerLedger/releases/latest"));
        request.Headers.Accept.ToString().ShouldBe("application/vnd.github+json");
        request.Headers.GetValues("X-GitHub-Api-Version").Single().ShouldBe("2022-11-28");
    }

    [Fact]
    public async Task Nothing_published_is_no_release()
    {
        using var client = new FakeHttp().Client();   // answers 404
        (await GitHubReleaseFeed.For(client, null).LatestAsync(CancellationToken.None)).ShouldBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "GitHub is limiting requests from this network; PowerLedger tries again later.")]
    [InlineData(HttpStatusCode.TooManyRequests, "GitHub is limiting requests from this network; PowerLedger tries again later.")]
    [InlineData(HttpStatusCode.InternalServerError, "GitHub answered 500.")]
    public async Task An_error_from_GitHub_is_said_plainly(HttpStatusCode status, string message)
    {
        var http = new FakeHttp();
        http.Reply(status, []);
        using var client = http.Client();
        (await Should.ThrowAsync<UpdateException>(GitHubReleaseFeed.For(client, null).LatestAsync(CancellationToken.None)))
            .Message.ShouldBe(message);
    }

    [Fact]
    public async Task No_network_and_no_answer_in_time_are_said_plainly()
    {
        var http = new FakeHttp { Answer = (_, _) => throw new HttpRequestException("No such host is known.") };
        using var client = http.Client();
        (await Should.ThrowAsync<UpdateException>(GitHubReleaseFeed.For(client, null).LatestAsync(CancellationToken.None)))
            .Message.ShouldBe("Couldn't reach GitHub.");

        http.Answer = async (_, cancel) =>
        {
            await Task.Delay(Timeout.Infinite, cancel);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        var slow = new GitHubReleaseFeed(client, GitHubReleaseFeed.Latest, Downloads, TimeSpan.FromMilliseconds(50));
        (await Should.ThrowAsync<UpdateException>(slow.LatestAsync(CancellationToken.None))).Message.ShouldBe("GitHub didn't answer in time.");
    }

    [Fact]
    public async Task An_oversized_answer_is_refused()
    {
        var http = new FakeHttp();
        http.Reply(HttpStatusCode.OK, new byte[GitHubReleaseFeed.MaxAnswer + 1]);
        using var client = http.Client();
        (await Should.ThrowAsync<UpdateException>(GitHubReleaseFeed.For(client, null).LatestAsync(CancellationToken.None)))
            .Message.ShouldContain("too large");
    }

    [Fact]
    public async Task A_test_feed_answers_at_its_own_address_and_serves_installers_there()
    {
        var http = new FakeHttp();
        http.Reply(HttpStatusCode.OK, Answer(url: "http://127.0.0.1:8765/download/v0.2.0/PowerLedger-0.2.0-setup.exe"));
        using var client = http.Client();

        var release = await GitHubReleaseFeed.For(client, new Uri("http://127.0.0.1:8765/")).LatestAsync(CancellationToken.None);

        http.Requests.Single().RequestUri.ShouldBe(new Uri("http://127.0.0.1:8765/releases/latest"));
        release.ShouldNotBeNull().Installer.ShouldBe(new Uri("http://127.0.0.1:8765/download/v0.2.0/PowerLedger-0.2.0-setup.exe"));
    }

    [Fact]
    public void Requests_name_PowerLedger_and_its_version()
    {
        using var http = UpdateHttp.Create("0.2.0");
        http.DefaultRequestHeaders.UserAgent.ToString().ShouldBe("PowerLedger/0.2.0");
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet build tests/PowerLedger.App.Tests -c Release` — Expected: FAIL, `GitHubReleaseFeed`, `Release`, `UpdateException`
and `UpdateHttp` don't exist.

- [ ] **Step 3: Write the feed**

`src/PowerLedger.App/Updates/Release.cs`:

```csharp
namespace PowerLedger.App;

/// <summary>A published release of PowerLedger as the App trusts it (spec §13): its version, its page on GitHub, and the
/// installer GitHub holds for it, with the size and SHA-256 GitHub lists.</summary>
internal sealed record Release(Version Version, Uri Page, Uri Installer, string FileName, long Size, byte[] Sha256)
{
    /// <summary>The version as people read it: "0.2.0".</summary>
    public string Name => Version.ToString(3);
}

/// <summary>An update step failed; the message is a sentence for the user.</summary>
internal sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);
```

`src/PowerLedger.App/Updates/GitHubReleaseFeed.cs`:

```csharp
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PowerLedger.App;

/// <summary>Where the newest release comes from.</summary>
internal interface IReleaseFeed
{
    /// <summary>The newest published release, or null when nothing is published. Throws <see cref="UpdateException"/> when
    /// the answer can't be had or can't be trusted.</summary>
    Task<Release?> LatestAsync(CancellationToken cancel);
}

/// <summary>
/// PowerLedger's latest release on GitHub (spec §13); GitHub leaves out drafts and pre-releases. A release is trusted only
/// when its tag is vX.Y.Z and it carries PowerLedger-X.Y.Z-setup.exe under the repository's own download address, with a
/// plausible size and the SHA-256 GitHub computed for it. Anything else is refused rather than guessed at.
/// </summary>
internal sealed partial class GitHubReleaseFeed(HttpClient http, Uri latest, string downloads, TimeSpan? timeout = null) : IReleaseFeed
{
    public const string Repository = "mharisjamal/PowerLedger";
    public const string Releases = $"https://github.com/{Repository}/releases";
    public const string Downloads = $"{Releases}/download/";
    public static readonly Uri Latest = new($"https://api.github.com/repos/{Repository}/releases/latest");
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    internal const int MaxAnswer = 1 << 20;
    internal const long MaxInstaller = 1L << 30;

    /// <summary>GitHub, or a stand-in at <paramref name="feed"/> (the App's --update-feed) that answers at
    /// feed/releases/latest and serves installers under feed/download/.</summary>
    public static GitHubReleaseFeed For(HttpClient http, Uri? feed)
        => feed is null ? new(http, Latest, Downloads) : new(http, new Uri(feed, "releases/latest"), new Uri(feed, "download/").AbsoluteUri);

    /// <summary>One version's release page, for "What's new".</summary>
    public static Uri PageOf(Version version) => new($"{Releases}/tag/v{version.ToString(3)}");

    public async Task<Release?> LatestAsync(CancellationToken cancel)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout ?? Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, latest);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;   // nothing published yet
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new UpdateException("GitHub is limiting requests from this network; PowerLedger tries again later.");
            if (!response.IsSuccessStatusCode) throw new UpdateException($"GitHub answered {(int)response.StatusCode}.");
            return Parse(await ReadAsync(response.Content, limit.Token).ConfigureAwait(false), downloads);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new UpdateException("GitHub didn't answer in time.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            throw new UpdateException("Couldn't reach GitHub.", error);
        }
    }

    /// <summary>The release GitHub describes, or null for a draft or a pre-release; refuses one it can't trust.
    /// <paramref name="downloads"/> is where installers must be.</summary>
    internal static Release? Parse(byte[] json, string downloads)
    {
        GitHubRelease release;
        try
        {
            release = JsonSerializer.Deserialize(json, ReleaseJson.Default.GitHubRelease) ?? throw NotARelease(null);
        }
        catch (JsonException error)
        {
            throw NotARelease(error);
        }
        if (release.Draft || release.Prerelease) return null;
        var tag = Tag().Match(release.TagName ?? "");
        if (!tag.Success) throw new UpdateException($"The latest release's tag, {release.TagName}, isn't a version PowerLedger knows.");
        var version = new Version(Number(tag.Groups[1]), Number(tag.Groups[2]), Number(tag.Groups[3]));
        var name = $"PowerLedger-{version.ToString(3)}-setup.exe";
        var asset = release.Assets?.FirstOrDefault(a => a.Name == name) ?? throw new UpdateException($"Release {version.ToString(3)} has no {name}.");
        if (!Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var installer)
            || !installer.AbsoluteUri.StartsWith(downloads, StringComparison.Ordinal))
            throw new UpdateException($"{name} isn't where PowerLedger's releases are kept.");
        if (asset.Size is <= 0 or > MaxInstaller) throw new UpdateException($"{name} is listed at an impossible size.");
        var digest = Digest().Match(asset.Digest ?? "");
        if (!digest.Success) throw new UpdateException($"GitHub lists no SHA-256 for {name}, so it can't be checked.");
        var page = Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var html) && html.AbsoluteUri.StartsWith(Releases + "/", StringComparison.Ordinal)
            ? html
            : PageOf(version);
        return new Release(version, page, installer, name, asset.Size, Convert.FromHexString(digest.Groups[1].Value));
    }

    /// <summary>The answer's bytes, refusing more than a release could need.</summary>
    private static async Task<byte[]> ReadAsync(HttpContent content, CancellationToken cancel)
    {
        if (content.Headers.ContentLength > MaxAnswer) throw TooLarge();
        using var buffer = new MemoryStream();
        await using var stream = await content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancel).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxAnswer) throw TooLarge();
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static int Number(Group group) => int.Parse(group.ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);

    private static UpdateException NotARelease(Exception? inner) => new("GitHub's answer wasn't a release.", inner);

    private static UpdateException TooLarge() => new("GitHub's answer was too large to be a release.");

    [GeneratedRegex("^v([0-9]{1,5})\\.([0-9]{1,5})\\.([0-9]{1,5})$")]
    private static partial Regex Tag();

    [GeneratedRegex("^sha256:([0-9a-fA-F]{64})$")]
    private static partial Regex Digest();
}

/// <summary>The part of GitHub's release that the App reads.</summary>
internal sealed class GitHubRelease
{
    public string? TagName { get; init; }

    public string? HtmlUrl { get; init; }

    public bool Draft { get; init; }

    public bool Prerelease { get; init; }

    public List<GitHubAsset>? Assets { get; init; }
}

/// <summary>One file attached to a release.</summary>
internal sealed class GitHubAsset
{
    public string? Name { get; init; }

    public long Size { get; init; }

    public string? BrowserDownloadUrl { get; init; }

    public string? Digest { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class ReleaseJson : JsonSerializerContext;
```

`src/PowerLedger.App/Updates/UpdateHttp.cs`:

```csharp
using System.Net.Http;
using System.Net.Http.Headers;

namespace PowerLedger.App;

/// <summary>The one HttpClient updates use for the App's life: the system's proxy, the user agent GitHub's API asks for,
/// and no overall timeout, since the feed and the download each set their own.</summary>
internal static class UpdateHttp
{
    public static HttpClient Create(string version)
    {
        var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) })
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PowerLedger", version));
        return http;
    }
}
```

- [ ] **Step 4: Run the tests** — the unit-test filter. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.App/Updates/Release.cs src/PowerLedger.App/Updates/GitHubReleaseFeed.cs src/PowerLedger.App/Updates/UpdateHttp.cs tests/PowerLedger.App.Tests/FakeHttp.cs tests/PowerLedger.App.Tests/ReleaseFeedTests.cs
git commit -m "Read PowerLedger's latest GitHub release, trusting only a vX.Y.Z tag with its installer and GitHub's SHA-256"
```

### Task 4: The checked download

**Files:**
- Create: `src/PowerLedger.App/Updates/UpdateDownloader.cs`
- Test: `tests/PowerLedger.App.Tests/UpdateDownloaderTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/PowerLedger.App.Tests/UpdateDownloaderTests.cs`:

```csharp
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class UpdateDownloaderTests : IDisposable
{
    private static readonly byte[] Installer = [.. Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7))];
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"powerledger-updates-{Guid.NewGuid():N}");
    private readonly FakeHttp _http = new();

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    /// <summary>A release whose installer is <paramref name="file"/>.</summary>
    internal static Release ReleaseOf(byte[] file, string version = "0.2.0") => new(
        Version.Parse(version), GitHubReleaseFeed.PageOf(Version.Parse(version)),
        new Uri($"{GitHubReleaseFeed.Downloads}v{version}/PowerLedger-{version}-setup.exe"), $"PowerLedger-{version}-setup.exe",
        file.Length, SHA256.HashData(file));

    private UpdateDownloader Downloader(TimeSpan? stall = null) => new(_http.Client(), _folder, stall);

    private string[] Files()
        => Directory.Exists(_folder) ? [.. Directory.GetFiles(_folder).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)] : [];

    [Fact]
    public async Task The_installer_is_downloaded_checked_and_kept_under_its_own_name()
    {
        _http.Reply(HttpStatusCode.OK, Installer);
        var fractions = new Fractions();

        var path = await Downloader().DownloadAsync(ReleaseOf(Installer), fractions, CancellationToken.None);

        path.ShouldBe(Path.Combine(_folder, "PowerLedger-0.2.0-setup.exe"));
        File.ReadAllBytes(path).ShouldBe(Installer);
        Files().ShouldBe(new[] { "PowerLedger-0.2.0-setup.exe" });
        fractions.Seen.ShouldBeInOrder();
        fractions.Seen[^1].ShouldBe(1.0);
        _http.Requests.Single().RequestUri.ShouldBe(new Uri($"{GitHubReleaseFeed.Downloads}v0.2.0/PowerLedger-0.2.0-setup.exe"));
    }

    [Fact]
    public async Task A_checked_copy_already_there_is_used_without_downloading_again()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(Path.Combine(_folder, "PowerLedger-0.2.0-setup.exe"), Installer);

        var path = await Downloader().DownloadAsync(ReleaseOf(Installer), null, CancellationToken.None);

        path.ShouldBe(Path.Combine(_folder, "PowerLedger-0.2.0-setup.exe"));
        _http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_copy_that_does_not_match_is_downloaded_again()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(Path.Combine(_folder, "PowerLedger-0.2.0-setup.exe"), Installer[..1000]);
        _http.Reply(HttpStatusCode.OK, Installer);

        var path = await Downloader().DownloadAsync(ReleaseOf(Installer), null, CancellationToken.None);

        File.ReadAllBytes(path).ShouldBe(Installer);
        _http.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_download_that_does_not_match_the_checksum_is_deleted()
    {
        var tampered = (byte[])Installer.Clone();
        tampered[1234] ^= 0xFF;
        _http.Reply(HttpStatusCode.OK, tampered);

        (await Should.ThrowAsync<UpdateException>(Downloader().DownloadAsync(ReleaseOf(Installer), null, CancellationToken.None)))
            .Message.ShouldBe("PowerLedger-0.2.0-setup.exe didn't match GitHub's checksum, so it was deleted.");
        Files().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_download_of_the_wrong_size_is_refused_and_deleted()
    {
        byte[] longer = [.. Installer, 1, 2, 3];
        _http.Reply(HttpStatusCode.OK, longer);   // the length is known up front
        (await Should.ThrowAsync<UpdateException>(Downloader().DownloadAsync(ReleaseOf(Installer), null, CancellationToken.None)))
            .Message.ShouldBe("PowerLedger-0.2.0-setup.exe isn't the size GitHub lists, so it was deleted.");

        _http.Reply(new Unsized(longer));          // found out only as it arrives
        (await Should.ThrowAsync<UpdateException>(Downloader().DownloadAsync(ReleaseOf(Installer), null, CancellationToken.None)))
            .Message.ShouldBe("PowerLedger-0.2.0-setup.exe isn't the size GitHub lists, so it was deleted.");
        Files().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_download_cut_short_is_deleted()
    {
        _http.Reply(new Unsized(Installer[..5000]));
        (await Should.ThrowAsync<UpdateException>(Downloader().DownloadAsync(ReleaseOf(Installer), null, CancellationToken.None)))
            .Message.ShouldBe("PowerLedger-0.2.0-setup.exe arrived incomplete; PowerLedger tries again later.");
        Files().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_download_that_stalls_is_abandoned()
    {
        _http.Reply(new StreamContent(new Stalling()));
        (await Should.ThrowAsync<UpdateException>(Downloader(TimeSpan.FromMilliseconds(100)).DownloadAsync(ReleaseOf(Installer), null, CancellationToken.None)))
            .Message.ShouldBe("The download stalled; PowerLedger tries again later.");
        Files().ShouldBeEmpty();
    }

    [Fact]
    public async Task An_error_answer_downloads_nothing()
    {
        _http.Reply(HttpStatusCode.NotFound, []);
        (await Should.ThrowAsync<UpdateException>(Downloader().DownloadAsync(ReleaseOf(Installer), null, CancellationToken.None)))
            .Message.ShouldBe("GitHub answered 404 for PowerLedger-0.2.0-setup.exe.");
        Files().ShouldBeEmpty();
    }

    [Fact]
    public void Starting_a_version_clears_what_it_no_longer_needs()
    {
        Directory.CreateDirectory(_folder);
        foreach (var name in new[]
        {
            "PowerLedger-0.1.0-setup.exe", "PowerLedger-0.1.0-setup.log", "PowerLedger-0.2.0-setup.exe", "PowerLedger-0.2.0-setup.log",
            "PowerLedger-0.3.0-setup.exe", "PowerLedger-0.3.0-setup.exe.partial", "notes.txt",
        })
        {
            File.WriteAllText(Path.Combine(_folder, name), name);
        }

        Downloader().Clean(new Version(0, 2, 0));

        Files().ShouldBe(new[] { "PowerLedger-0.2.0-setup.log", "PowerLedger-0.3.0-setup.exe", "notes.txt" });
    }

    [Fact]
    public void Cleaning_a_folder_that_is_not_there_does_nothing() => Downloader().Clean(new Version(0, 2, 0));
}
```

`Fractions` is added to `FakeUpdates.cs` in Task 6; add it now at the end of `FakeHttp.cs` if Task 6 isn't done yet, then
move it:

```csharp
/// <summary>Records what a progress report hears, as it hears it.</summary>
internal sealed class Fractions : IProgress<double>
{
    public List<double> Seen { get; } = [];

    public void Report(double value) => Seen.Add(value);
}
```

- [ ] **Step 2: Run them to see them fail** — `dotnet build tests/PowerLedger.App.Tests -c Release`. Expected: FAIL, no
  `UpdateDownloader`.

- [ ] **Step 3: Write the downloader**

`src/PowerLedger.App/Updates/UpdateDownloader.cs`:

```csharp
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PowerLedger.App;

/// <summary>Brings a release's installer down and keeps it only when it is the file GitHub lists.</summary>
internal interface IUpdateDownloader
{
    /// <summary>The installer's path once it is whole and its SHA-256 is GitHub's; a checked copy already there is reused.
    /// <paramref name="progress"/> hears the fraction done. Throws <see cref="UpdateException"/>.</summary>
    Task<string> DownloadAsync(Release release, IProgress<double>? progress, CancellationToken cancel);

    /// <summary>Removes unfinished downloads, installers for <paramref name="running"/> or older, and setup logs older than
    /// it; the log of the update that brought it stays.</summary>
    void Clean(Version running);
}

/// <summary>
/// The quiet download (spec §13): into the Updates folder under a .partial name, hashed as it arrives, and renamed to the
/// installer's own name only when its size and SHA-256 are the release's. A minute without data abandons it, and a file
/// that doesn't match is deleted; the next check tries again.
/// </summary>
internal sealed partial class UpdateDownloader(HttpClient http, string folder, TimeSpan? stall = null) : IUpdateDownloader
{
    public static readonly TimeSpan Stall = TimeSpan.FromMinutes(1);

    public static string DefaultFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerLedger", "Updates");

    public async Task<string> DownloadAsync(Release release, IProgress<double>? progress, CancellationToken cancel)
    {
        var path = Path.Combine(folder, release.FileName);
        if (Matches(path, release.Size, release.Sha256)) return path;
        var partial = path + ".partial";
        var patience = stall ?? Stall;
        try
        {
            Directory.CreateDirectory(folder);
            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            quiet.CancelAfter(patience);
            using var response = await http.GetAsync(release.Installer, HttpCompletionOption.ResponseHeadersRead, quiet.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new UpdateException($"GitHub answered {(int)response.StatusCode} for {release.FileName}.");
            if (response.Content.Headers.ContentLength is { } length && length != release.Size) throw WrongSize(release);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(quiet.Token).ConfigureAwait(false))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await source.ReadAsync(buffer, quiet.Token).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > release.Size) throw WrongSize(release);
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), quiet.Token).ConfigureAwait(false);
                    quiet.CancelAfter(patience);   // a minute for each piece, however long the whole takes
                    progress?.Report((double)received / release.Size);
                }
            }
            if (received != release.Size) throw new UpdateException($"{release.FileName} arrived incomplete; PowerLedger tries again later.");
            if (!hash.GetHashAndReset().AsSpan().SequenceEqual(release.Sha256))
                throw new UpdateException($"{release.FileName} didn't match GitHub's checksum, so it was deleted.");
            File.Move(partial, path, overwrite: true);
            return path;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new UpdateException("The download stalled; PowerLedger tries again later.");
        }
        catch (Exception error) when (error is HttpRequestException or HttpIOException)
        {
            throw new UpdateException($"Couldn't download {release.FileName}; PowerLedger tries again later.", error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException("Couldn't save the update: " + error.Message, error);
        }
        finally
        {
            Delete(partial);   // nothing is there once the download has moved into place
        }
    }

    public void Clean(Version running)
    {
        string[] files;
        try
        {
            if (!Directory.Exists(folder)) return;
            files = Directory.GetFiles(folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var kept = Kept().Match(name);
            var version = kept.Success ? Version.Parse(kept.Groups[1].Value) : null;
            var old = kept.Groups[2].Value == "exe" ? version <= running : version < running;
            if (name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) || (version is not null && old)) Delete(file);
        }
    }

    /// <summary>Whether <paramref name="path"/> holds exactly this size and SHA-256.</summary>
    internal static bool Matches(string path, long size, byte[] sha256)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return file.Length == size && SHA256.HashData(file).AsSpan().SequenceEqual(sha256);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static UpdateException WrongSize(Release release) => new($"{release.FileName} isn't the size GitHub lists, so it was deleted.");

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // In use or refused: the next start tries again.
        }
    }

    [GeneratedRegex("^PowerLedger-([0-9]{1,5}\\.[0-9]{1,5}\\.[0-9]{1,5})-setup\\.(exe|log)$")]
    private static partial Regex Kept();
}
```

- [ ] **Step 4: Run the tests** — the unit-test filter. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.App/Updates/UpdateDownloader.cs tests/PowerLedger.App.Tests/UpdateDownloaderTests.cs tests/PowerLedger.App.Tests/FakeHttp.cs
git commit -m "Download an update quietly and keep it only when its size and SHA-256 are GitHub's"
```

### Task 5: Running setup

**Files:**
- Create: `src/PowerLedger.App/Updates/SetupRunner.cs`
- Test: `tests/PowerLedger.App.Tests/SetupRunnerTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.IO;
using System.Security.Cryptography;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class SetupRunnerTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"powerledger-setup-{Guid.NewGuid():N}.exe");

    public void Dispose() => File.Delete(_file);

    [Fact]
    public void Setup_is_told_to_update_quietly_and_open_the_App_again()
        => SetupRunner.Arguments(@"C:\Users\a\AppData\Local\PowerLedger\Updates\PowerLedger-0.2.0-setup.log")
            .ShouldBe("/SILENT /NORESTART /UPDATE=1 /LOG=\"C:\\Users\\a\\AppData\\Local\\PowerLedger\\Updates\\PowerLedger-0.2.0-setup.log\"");

    [Fact]
    public async Task An_installer_that_changed_since_its_download_is_not_run()
    {
        File.WriteAllBytes(_file, [1, 2, 3]);
        (await Should.ThrowAsync<UpdateException>(new SetupRunner().RunAsync(_file, 3, SHA256.HashData([9, 9, 9]), _file + ".log", CancellationToken.None)))
            .Message.ShouldBe("The downloaded update changed on disk, so it wasn't run. PowerLedger downloads it again.");
        await Should.ThrowAsync<UpdateException>(new SetupRunner().RunAsync(_file, 4, SHA256.HashData([1, 2, 3]), _file + ".log", CancellationToken.None));
    }

    [Fact]
    public async Task An_installer_that_is_gone_is_downloaded_again()
        => (await Should.ThrowAsync<UpdateException>(new SetupRunner().RunAsync(_file, 3, new byte[32], _file + ".log", CancellationToken.None)))
            .Message.ShouldBe("The downloaded update is gone. PowerLedger downloads it again.");
}
```

- [ ] **Step 2: Run them to see them fail** — build fails: no `SetupRunner`.

- [ ] **Step 3: Write the runner**

`src/PowerLedger.App/Updates/SetupRunner.cs`:

```csharp
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace PowerLedger.App;

/// <summary>Runs a downloaded installer as the update (spec §13).</summary>
internal interface ISetupRunner
{
    /// <summary>Checks the installer once more, starts it, and returns setup's exit code when it ends. Setup closes the App
    /// before it installs, so this returns only when the update didn't go in. Throws <see cref="UpdateException"/> for a
    /// file that changed or went, and Win32Exception when Windows won't start it (<see cref="SetupRunner.Declined"/> for a
    /// declined permission prompt).</summary>
    Task<int> RunAsync(string installer, long size, byte[] sha256, string log, CancellationToken cancel);
}

internal sealed class SetupRunner : ISetupRunner
{
    /// <summary>ERROR_CANCELLED: the permission prompt was declined.</summary>
    public const int Declined = 1223;

    /// <summary>A progress window and no questions, no restart, the App opened again at the end (PowerLedger.iss's
    /// /UPDATE=1), and a log beside the installer.</summary>
    internal static string Arguments(string log) => $"/SILENT /NORESTART /UPDATE=1 /LOG=\"{log}\"";

    public async Task<int> RunAsync(string installer, long size, byte[] sha256, string log, CancellationToken cancel)
    {
        using var setup = Start(installer, size, sha256, log);
        await setup.WaitForExitAsync(cancel).ConfigureAwait(false);
        return setup.ExitCode;
    }

    /// <summary>Holds the installer open against writers from the check until setup is running, so nothing can swap it in
    /// between.</summary>
    private static Process Start(string installer, long size, byte[] sha256, string log)
    {
        FileStream hold;
        try
        {
            hold = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new UpdateException("The downloaded update is gone. PowerLedger downloads it again.", error);
        }
        using (hold)
        {
            if (hold.Length != size || !SHA256.HashData(hold).AsSpan().SequenceEqual(sha256))
                throw new UpdateException("The downloaded update changed on disk, so it wasn't run. PowerLedger downloads it again.");
            return Process.Start(new ProcessStartInfo(installer, Arguments(log)) { UseShellExecute = true })
                ?? throw new UpdateException("Windows didn't start setup.");
        }
    }
}
```

- [ ] **Step 4: Run the tests** — the unit-test filter. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.App/Updates/SetupRunner.cs tests/PowerLedger.App.Tests/SetupRunnerTests.cs
git commit -m "Run a downloaded update's setup only after checking it once more, holding it against writers"
```

### Task 6: The updater

**Files:**
- Create: `src/PowerLedger.App/Updates/Updater.cs`
- Create: `tests/PowerLedger.App.Tests/FakeUpdates.cs` (move `Fractions` here from `FakeHttp.cs`)
- Test: `tests/PowerLedger.App.Tests/UpdaterTests.cs`

- [ ] **Step 1: Write the fakes and the failing tests**

`tests/PowerLedger.App.Tests/FakeUpdates.cs`:

```csharp
namespace PowerLedger.App.Tests;

/// <summary>A feed that answers what the test sets, counting the questions.</summary>
internal sealed class FakeFeed : IReleaseFeed
{
    public Release? Latest { get; set; }

    public Exception? Failure { get; set; }

    public int Asked { get; private set; }

    public Task<Release?> LatestAsync(CancellationToken cancel)
    {
        Asked++;
        return Failure is { } failure ? Task.FromException<Release?>(failure) : Task.FromResult(Latest);
    }
}

/// <summary>A download that is done at once, or waits for <see cref="Gate"/>, or fails as the test says.</summary>
internal sealed class FakeDownloader : IUpdateDownloader
{
    public List<Release> Downloads { get; } = [];

    public List<Version> Cleaned { get; } = [];

    public Exception? Failure { get; set; }

    public Task? Gate { get; set; }

    public async Task<string> DownloadAsync(Release release, IProgress<double>? progress, CancellationToken cancel)
    {
        if (Failure is { } failure) throw failure;
        Downloads.Add(release);
        progress?.Report(0.5);
        if (Gate is { } gate) await gate;
        progress?.Report(1);
        return $@"C:\Updates\{release.FileName}";
    }

    public void Clean(Version running) => Cleaned.Add(running);
}

/// <summary>Setup as the test scripts it: an exit code, a wait the test ends, or a refusal.</summary>
internal sealed class FakeSetup : ISetupRunner
{
    public List<(string Installer, string Log)> Started { get; } = [];

    public Exception? Failure { get; set; }

    /// <summary>When setup ends; at once with exit code 2 unless the test says otherwise.</summary>
    public Task<int> Exit { get; set; } = Task.FromResult(2);

    public Task<int> RunAsync(string installer, long size, byte[] sha256, string log, CancellationToken cancel)
    {
        if (Failure is { } failure) return Task.FromException<int>(failure);
        Started.Add((installer, log));
        return Exit;
    }
}

/// <summary>Records what a progress report hears, as it hears it.</summary>
internal sealed class Fractions : IProgress<double>
{
    public List<double> Seen { get; } = [];

    public void Report(double value) => Seen.Add(value);
}
```

`tests/PowerLedger.App.Tests/UpdaterTests.cs`:

```csharp
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
    private readonly FakeUiSettings _ui = new();
    private readonly List<Release> _announced = [];
    private readonly List<Uri> _opened = [];

    internal static Release Release(string version) => new(
        Version.Parse(version), new Uri($"https://github.com/mharisjamal/PowerLedger/releases/tag/v{version}"),
        new Uri($"{GitHubReleaseFeed.Downloads}v{version}/PowerLedger-{version}-setup.exe"), $"PowerLedger-{version}-setup.exe", 1000, new byte[32]);

    private Updater Updater(string running = "0.2.0") => new(
        _feed, _downloader, _setup, _ui, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, Version.Parse(running), _announced.Add, _opened.Add);

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
    public async Task The_tray_announces_each_version_once()
    {
        _feed.Latest = Release("0.3.0");
        var updater = Updater();
        await updater.CheckAsync();
        await updater.CheckAsync();
        _announced.Select(r => r.Name).ShouldBe(new[] { "0.3.0" });
        _ui.Current.AnnouncedVersion.ShouldBe("0.3.0");

        var later = Updater();                       // the App's next start
        await later.CheckAsync();
        _announced.Count.ShouldBe(1);

        _feed.Latest = Release("0.4.0");
        await later.CheckAsync();
        _announced.Select(r => r.Name).ShouldBe(new[] { "0.3.0", "0.4.0" });
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
    public void It_checks_a_minute_after_starting_and_every_six_hours_while_allowed()
    {
        var updater = Updater();
        updater.Start();
        _clock.Advance(TimeSpan.FromSeconds(59));
        _feed.Asked.ShouldBe(0);
        _clock.Advance(TimeSpan.FromSeconds(1));
        _feed.Asked.ShouldBe(1);
        _clock.Advance(TimeSpan.FromHours(6));
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

        changed.ShouldContain(nameof(Updater.ShowCard));
        changed.ShouldContain(nameof(Updater.Title));
        changed.ShouldContain(nameof(Updater.ReadyVersion));
        changed.ShouldContain(nameof(Updater.Status));
    }
}
```

- [ ] **Step 2: Run them to see them fail** — build fails: no `Updater`, `UpdateStage`.

- [ ] **Step 3: Write the updater**

`src/PowerLedger.App/Updates/Updater.cs`:

```csharp
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
/// Updates (spec §13). A minute after the App starts and every six hours after, while the user allows it, asks the feed for
/// the newest release and downloads a newer one quietly; once it is checked, the card offers it, the tray announces it once
/// per version and its menu offers it too. Installing starts setup, which closes the App and opens the new version. The
/// card and Settings' Updates row bind here, and everything they read changes on the UI thread.
/// </summary>
internal sealed class Updater : ObservableObject, IDisposable
{
    public static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

    private static readonly string[] Card =
        [nameof(Stage), nameof(ShowCard), nameof(Title), nameof(Detail), nameof(ActionLabel), nameof(CanDismiss), nameof(HasNotes), nameof(ReadyVersion)];

    private readonly IReleaseFeed _feed;
    private readonly IUpdateDownloader _downloader;
    private readonly ISetupRunner _setup;
    private readonly IUiSettings _ui;
    private readonly UiThreads _threads;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly CultureInfo _culture;
    private readonly Action<Release> _announce;
    private readonly Action<Uri> _open;
    private readonly CancellationTokenSource _stop = new();
    private ITimer? _timer;
    private int _checking;
    private Release? _release;
    private string? _installer;
    private UpdateStage _stage;
    private bool _dismissed;
    private string? _problem;
    private string _status = "Not checked yet.";
    private string? _message;

    /// <param name="announce">The tray's notification, once per version.</param>
    /// <param name="open">Opens a page in the browser.</param>
    public Updater(
        IReleaseFeed feed, IUpdateDownloader downloader, ISetupRunner setup, IUiSettings ui, UiThreads threads, TimeProvider clock,
        TimeZoneInfo zone, CultureInfo culture, Version running, Action<Release> announce, Action<Uri> open)
    {
        _feed = feed;
        _downloader = downloader;
        _setup = setup;
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

    public UpdateStage Stage => _stage;

    /// <summary>The card shows unless there is nothing to say, or the user put it away until the App next starts.</summary>
    public bool ShowCard => _stage != UpdateStage.None && !_dismissed;

    public string Title => _stage switch
    {
        UpdateStage.Ready => $"PowerLedger {_release?.Name} is ready",
        UpdateStage.Installing => $"Installing {_release?.Name}…",
        UpdateStage.Failed => "The update didn't install",
        UpdateStage.Updated => $"Updated to {Running.ToString(3)}",
        _ => "",
    };

    /// <summary>A line under the title, or null.</summary>
    public string? Detail => _stage switch
    {
        UpdateStage.Installing => "Windows asks for permission",
        UpdateStage.Failed => _problem,
        _ => null,
    };

    /// <summary>The card's button, or null for none.</summary>
    public string? ActionLabel => _stage switch
    {
        UpdateStage.Ready => "Restart to update",
        UpdateStage.Failed => "Try again",
        _ => null,
    };

    public bool CanDismiss => _stage is UpdateStage.Ready or UpdateStage.Failed or UpdateStage.Updated;

    /// <summary>"What's new" shows: the release's page, or for "Updated" the running version's.</summary>
    public bool HasNotes => _stage is UpdateStage.Ready or UpdateStage.Updated;

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
    /// and every six hours after while checking automatically is on. Call on the UI thread.</summary>
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

    /// <summary>Asks the feed, downloads a newer release quietly, and offers it once it is checked. A check already running
    /// makes this one a no-op. Never throws: what went wrong is Settings' line, and the next check tries again.</summary>
    internal async Task CheckAsync()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            _threads.Post(() => Status = "Checking for updates…");
            var release = await _feed.LatestAsync(_stop.Token).ConfigureAwait(false);
            if (release is null || release.Version <= Running)
            {
                _threads.Post(() => Status = $"PowerLedger is up to date · checked {Clock()}");
                return;
            }
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
        if (_ui.Current.AnnouncedVersion == release.Name) return;
        _ui.Announced(release.Name);
        _announce(release);
    }

    /// <summary>The card's button: install what is ready; after a failure, install again while the download is good, or
    /// check and download afresh when it isn't.</summary>
    private void OnAct()
    {
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
            _threads.Post(() => Fail(code == 0
                ? "Setup finished but didn't restart PowerLedger. Choose Exit UI in the tray menu, then open PowerLedger again."
                : $"Setup ended without installing (code {code.ToString(CultureInfo.InvariantCulture)})."));
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
```

Remove `Fractions` from `FakeHttp.cs` if Task 4 put it there.

- [ ] **Step 4: Run the tests** — the unit-test filter. Expected: PASS, all App tests.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.App/Updates/Updater.cs tests/PowerLedger.App.Tests/FakeUpdates.cs tests/PowerLedger.App.Tests/FakeHttp.cs tests/PowerLedger.App.Tests/UpdaterTests.cs
git commit -m "Check for updates on a schedule, download quietly, then offer Restart to update and say how it went"
```

### Task 7: The tray's menu item and notification

**Files:**
- Modify: `src/PowerLedger.App/Tray/TrayIcon.cs`

The tray is WinForms' `NotifyIcon`; its menu and balloon aren't unit-tested (no test can click a balloon). The rendering
and Sandbox checks in Task 12 cover it.

- [ ] **Step 1: Change the tray**

In `TrayIcon`: update the class summary's menu sentence to "the menu opens the window, restarts into a downloaded update
while one is ready (spec §13), toggles start with Windows, and exits the UI while the service keeps logging"; replace the
field `private string? _open;` with

```csharp
    private readonly ToolStripMenuItem _update;
    private Action? _clicked;
    private Action? _install;
```

In the constructor, after `startWithWindows` is created, add the item and put it in the menu after "Open":

```csharp
        _update = new ToolStripMenuItem("Restart to update") { Visible = false };
        _update.Click += (_, _) => _install?.Invoke();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => open());
        menu.Items.Add(_update);
        menu.Items.Add(startWithWindows);
```

and change the balloon's click to `_icon.BalloonTipClicked += (_, _) => _clicked?.Invoke();`. Replace `Notify` with:

```csharp
    /// <summary>A notification from the tray, the monthly report's (spec §9). Clicking it opens <paramref name="open"/>.</summary>
    public void Notify(string title, string text, string? open) => Balloon(title, text, () => Launch(open));

    /// <summary>A notification whose click runs <paramref name="clicked"/>: an update's, which opens the window (spec §13).</summary>
    public void Announce(string title, string text, Action clicked) => Balloon(title, text, clicked);

    /// <summary>"Restart to update to X.Y.Z" in the menu while <paramref name="version"/> is ready; null takes it away.</summary>
    public void OfferUpdate(string? version, Action install)
    {
        if (_disposed) return;
        _install = install;
        _update.Text = $"Restart to update to {version}";
        _update.Visible = version is not null;
    }

    private void Balloon(string title, string text, Action clicked)
    {
        if (_disposed) return;
        _clicked = clicked;
        _icon.ShowBalloonTip(10_000, title, text, ToolTipIcon.None);
    }
```

- [ ] **Step 2: Build** — `dotnet build src/PowerLedger.App -c Release`. Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/PowerLedger.App/Tray/TrayIcon.cs
git commit -m "Offer a ready update in the tray menu, and announce it with a notification that opens the window"
```

### Task 8: The card, the Settings row and the wiring

**Files:**
- Modify: `src/PowerLedger.App/Theme/Styles.xaml`, `src/PowerLedger.App/Shell/MainWindow.xaml`,
  `src/PowerLedger.App/Shell/ShellViewModel.cs`, `src/PowerLedger.App/Settings/SettingsViewModel.cs`,
  `src/PowerLedger.App/Settings/SettingsView.xaml`, `src/PowerLedger.App/App.xaml.cs`
- Test: `tests/PowerLedger.App.Tests/RenderingTests.cs` (Category UI: draws the card to PNGs)

- [ ] **Step 1: Write the failing rendering test**

In `RenderingTests`, add `using System.Windows.Automation;` and:

```csharp
    [Fact]
    public void The_update_card_draws_in_the_rail_in_both_themes()
    {
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            using var saver = new FakeSaver();
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                UseTheme(theme);
                foreach (var (name, updates) in new[] { ("ready", ReadyUpdate()), ("updated", UpdatedApp()) })
                {
                    var shell = new ShellViewModel(NowScreen(), BreakdownScreen(), ReportScreen(saver), SettingsScreen(), WizardScreen(), "0.2.0", updates);
                    var window = new MainWindow
                    {
                        DataContext = shell, WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
                    };
                    window.Show();
                    try
                    {
                        Pump(TimeSpan.FromMilliseconds(1200));
                        var card = Find<Border>(window, border => AutomationProperties.GetName(border) == "Update").ShouldNotBeNull(name);
                        card.IsVisible.ShouldBeTrue(name);
                        Save(window, (int)window.ActualWidth, (int)window.ActualHeight, $"update-{name}-{theme}.png");
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            }
        });
        new FileInfo(Path.Combine(Folder, "update-ready-Dark.png")).Length.ShouldBeGreaterThan(30_000);
    }

    /// <summary>0.3.0 downloaded and waiting, on 0.2.0.</summary>
    private static Updater ReadyUpdate()
    {
        var feed = new FakeFeed { Latest = UpdaterTests.Release("0.3.0") };
        var updater = new Updater(feed, new FakeDownloader(), new FakeSetup(), new FakeUiSettings(), UiThreads.Inline, new FakeTimeProvider(Now),
            TimeZoneInfo.Utc, English, new Version(0, 2, 0), _ => { }, _ => { });
        updater.CheckAsync().GetAwaiter().GetResult();
        return updater;
    }

    /// <summary>The first start of 0.2.0 after 0.1.0.</summary>
    private static Updater UpdatedApp()
    {
        var ui = new FakeUiSettings { Current = UiPreferences.Default with { LastVersion = "0.1.0" } };
        var updater = new Updater(new FakeFeed(), new FakeDownloader(), new FakeSetup(), ui, UiThreads.Inline, new FakeTimeProvider(Now),
            TimeZoneInfo.Utc, English, new Version(0, 2, 0), _ => { }, _ => { });
        updater.Start();
        return updater;
    }
```

Run: `dotnet build tests/PowerLedger.App.Tests -c Release` — Expected: FAIL, `ShellViewModel` takes no updater.

- [ ] **Step 2: The view models**

`ShellViewModel`: add a last constructor parameter `Updater? updates = null`, assign `Updates = updates;`, and add

```csharp
    /// <summary>The update card in the rail (spec §13); without one the card stays hidden.</summary>
    public Updater? Updates { get; }
```

`SettingsViewModel`: add a last constructor parameter `Updater? updates = null`, assign `Updates = updates;` at the end of
the constructor, and add after `AppMessage`:

```csharp
    /// <summary>The Updates row (spec §13).</summary>
    public Updater? Updates { get; }
```

- [ ] **Step 3: The styles**

In `Styles.xaml`, after the `Banner` style:

```xml
    <!-- The update card in the rail (spec §13): a raised panel with a strong hairline, the rail's width. -->
    <Style x:Key="UpdateCard" TargetType="Border">
        <Setter Property="Background" Value="{DynamicResource Brush.Raised}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.LineStrong}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="CornerRadius" Value="3" />
        <Setter Property="Padding" Value="11,9,8,11" />
        <Setter Property="Margin" Value="10,0,10,10" />
    </Style>

    <!-- The small ✕ that puts a card away. -->
    <Style x:Key="CardClose" TargetType="Button">
        <Setter Property="Width" Value="18" />
        <Setter Property="Height" Value="18" />
        <Setter Property="FontFamily" Value="{StaticResource Font.Glyphs}" />
        <Setter Property="FontSize" Value="8" />
        <Setter Property="Content" Value="&#xE8BB;" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.Ink3}" />
        <Setter Property="Cursor" Value="Hand" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border x:Name="Face" Background="Transparent" CornerRadius="2">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Face" Property="Background" Value="{DynamicResource Brush.Line}" />
                            <Setter Property="Foreground" Value="{DynamicResource Brush.Ink}" />
                        </Trigger>
                        <Trigger Property="IsKeyboardFocused" Value="True">
                            <Setter Property="Foreground" Value="{DynamicResource Brush.Amber}" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Words that act: underlined when hovered, amber when they have the keyboard. -->
    <Style x:Key="Link" TargetType="Button">
        <Setter Property="FontFamily" Value="{StaticResource Font.Ui}" />
        <Setter Property="FontSize" Value="11.5" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.Ink2}" />
        <Setter Property="Cursor" Value="Hand" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <TextBlock x:Name="Words" Background="Transparent" Text="{Binding Content, RelativeSource={RelativeSource TemplatedParent}}" />
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Words" Property="TextDecorations" Value="Underline" />
                            <Setter Property="Foreground" Value="{DynamicResource Brush.Ink}" />
                        </Trigger>
                        <Trigger Property="IsKeyboardFocused" Value="True">
                            <Setter Property="Foreground" Value="{DynamicResource Brush.Amber}" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
```

- [ ] **Step 4: The card**

In `MainWindow.xaml`, in the rail's `DockPanel`, between the status `Border DockPanel.Dock="Bottom"` and the rail's
`StackPanel` (which must stay the last child, since it fills), add:

```xml
                        <!-- The update card (spec §13): above the service's status, and only when there is something to say. -->
                        <Border DockPanel.Dock="Bottom" Style="{StaticResource UpdateCard}" DataContext="{Binding Updates}"
                                Visibility="{Binding ShowCard, Converter={StaticResource VisibleWhen}, FallbackValue=Collapsed}"
                                AutomationProperties.Name="Update">
                            <StackPanel>
                                <DockPanel>
                                    <Button DockPanel.Dock="Right" Style="{StaticResource CardClose}" Command="{Binding Dismiss}" ToolTip="Later"
                                            AutomationProperties.Name="Later" Visibility="{Binding CanDismiss, Converter={StaticResource VisibleWhen}}" />
                                    <TextBlock Style="{StaticResource Text.Eyebrow}" Text="UPDATE" VerticalAlignment="Center" />
                                </DockPanel>
                                <TextBlock Style="{StaticResource Text.Body}" FontSize="12.5" Margin="0,5,0,0" TextWrapping="Wrap" Text="{Binding Title}" />
                                <TextBlock Style="{StaticResource Text.Muted}" FontSize="11.5" Margin="0,3,0,0" TextWrapping="Wrap" Text="{Binding Detail}"
                                           Visibility="{Binding Detail, Converter={StaticResource VisibleWhenText}}" />
                                <Button Style="{StaticResource Quiet}" Margin="0,9,0,0" HorizontalAlignment="Left" Content="{Binding ActionLabel}"
                                        Command="{Binding Act}" Visibility="{Binding ActionLabel, Converter={StaticResource VisibleWhenText}}" />
                                <Button Style="{StaticResource Link}" Margin="0,8,0,0" HorizontalAlignment="Left" Content="What's new"
                                        Command="{Binding OpenNotes}" Visibility="{Binding HasNotes, Converter={StaticResource VisibleWhen}}" />
                            </StackPanel>
                        </Border>
```

- [ ] **Step 5: The Settings row**

In `SettingsView.xaml`, after the `AppMessage` `TextBlock` that follows "Start with Windows":

```xml
            <StackPanel Style="{StaticResource Row}">
                <TextBlock Style="{StaticResource Label}" Text="Updates" />
                <CheckBox Style="{StaticResource Tick}" Content="Download new versions quietly, then ask" IsChecked="{Binding Updates.CheckAutomatically}" />
                <Button Style="{StaticResource Quiet}" Content="Check now" Command="{Binding Updates.CheckNow}" Margin="12,0,0,0" />
            </StackPanel>
            <TextBlock Style="{StaticResource Note}" Margin="230,0,0,0" Text="{Binding Updates.Status}"
                       Visibility="{Binding Updates.Status, Converter={StaticResource VisibleWhenText}, FallbackValue=Collapsed}" />
            <TextBlock Style="{StaticResource Said}" Text="{Binding Updates.Message}"
                       Visibility="{Binding Updates.Message, Converter={StaticResource VisibleWhenText}, FallbackValue=Collapsed}" />
```

- [ ] **Step 6: The wiring**

In `App.xaml.cs`: add `using System.Diagnostics;`, the field `private Updater? _updates;`, and in `OnStartup`, after
`_preferences.ApplyFirstRunDefaults();`:

```csharp
        var http = UpdateHttp.Create(version);
        _updates = new Updater(
            GitHubReleaseFeed.For(http, options.UpdateFeed), new UpdateDownloader(http, UpdateDownloader.DefaultFolder), new SetupRunner(),
            _preferences, threads, TimeProvider.System, zone, culture, System.Version.Parse(version),
            release => _tray?.Announce($"PowerLedger {release.Name} is ready", "Open PowerLedger and choose Restart to update.", ShowWindow),
            OpenPage);
        _updates.PropertyChanged += OnUpdatesChanged;
```

pass `_updates` as the last argument to `new SettingsViewModel(...)` and to `new ShellViewModel(...)`, and after
`_monthly.Start();` add `_updates.Start();`. Add the two methods:

```csharp
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
```

In `ExitUi`, before `_monthly?.Dispose();`:

```csharp
            if (_updates is not null)
            {
                _updates.PropertyChanged -= OnUpdatesChanged;
                _updates.Dispose();
            }
```

(`Version()` is a method of `App`, hence `System.Version.Parse`.)

- [ ] **Step 7: Build and run every App test, UI included**

Run: `dotnet build -c Release` — Expected: 0 warnings, 0 errors.
Run: `dotnet test tests/PowerLedger.App.Tests -c Release --filter "Category!=Hardware&Category!=Installed"` — Expected:
PASS, and `%TEMP%\powerledger-renders\update-ready-Dark.png`, `update-ready-Light.png`, `update-updated-Dark.png`,
`update-updated-Light.png` exist. Look at them: the card sits at the bottom of the rail above the service's status and
reads "UPDATE / PowerLedger 0.3.0 is ready / Restart to update / What's new" and "Updated to 0.2.0 / What's new".

- [ ] **Step 8: Commit**

```bash
git add src/PowerLedger.App/Theme/Styles.xaml src/PowerLedger.App/Shell/MainWindow.xaml src/PowerLedger.App/Shell/ShellViewModel.cs src/PowerLedger.App/Settings/SettingsViewModel.cs src/PowerLedger.App/Settings/SettingsView.xaml src/PowerLedger.App/App.xaml.cs tests/PowerLedger.App.Tests/RenderingTests.cs
git commit -m "Show the update card at the bottom of the rail, the Updates row in Settings, and wire the updater in"
```

### Task 9: The installer reopens the App after an update

**Files:**
- Modify: `installer/PowerLedger.iss`, `installer/test-installer.ps1`

- [ ] **Step 1: Make the installer test expect it**

Replace `Step-Upgrade` in `installer/test-installer.ps1` with:

```powershell
function Step-Upgrade {
    $old = @(Get-AppProcess | ForEach-Object Id)
    $before = Get-History
    Write-Host "  before: the App $(if ($old) { 'running' } else { 'not running' }); $(Format-History $before)"
    # Run as the App's "Restart to update" runs it (spec §13), without the progress window: /UPDATE=1 opens the App again.
    Check Upgrade 'silent upgrade exits 0' { $code = Invoke-Setup $Upgrade ($Silent + '/UPDATE=1'); Assert ($code -eq 0) "exit code $code" }
    if ($old) {
        Check Upgrade 'the running App was closed' {
            Assert (Wait-Until { -not (Get-Process -Id $old -ErrorAction SilentlyContinue) } -Seconds 10) "still running: $(@(Get-Process -Id $old -ErrorAction SilentlyContinue).Id -join ', ')"
        }
    }
    Check Upgrade 'the update opened the App again' {
        Assert (Wait-Until { @(Get-AppProcess | Where-Object Id -notin $old).Count } -Seconds 30) "App processes: $(@(Get-AppProcess).Id -join ', ')"
    }
    Check Upgrade 'uninstall entry shows the new version' {
        $shown = (Get-ItemProperty $UninstallKey).DisplayVersion
        Assert ($shown -eq (Get-SetupVersion $Upgrade)) "DisplayVersion $shown"
    }
    Check Upgrade 'service running' { Assert (Wait-ServiceRunning 30) (Get-ServiceState) }
    Confirm-HistoryKept Upgrade $before
}
```

In the script's `.DESCRIPTION`, after the sentence listing the default steps, add: "Upgrade runs setup as the App's
Restart to update does, with /UPDATE=1, and checks that the App opens again; UninstallKeep then closes it."

- [ ] **Step 2: Add the relaunch to the installer**

In `installer/PowerLedger.iss`, `[Run]` becomes:

```
[Run]
; The first window of a new install is the wizard; the App also turns on starting with Windows for this user.
Filename: "{app}\PowerLedger.exe"; Description: "Open PowerLedger"; Flags: postinstall nowait skipifsilent runasoriginaluser
; An update the App started (spec §13) passes /UPDATE=1: the App opens again, as the user who started setup rather than
; as the administrator setup runs as.
Filename: "{app}\PowerLedger.exe"; Flags: nowait runasoriginaluser; Check: IsUpdate
```

and in `[Code]`, before `RunHidden`:

```pascal
{ The App starts setup with /UPDATE=1 when the user chooses "Restart to update" (spec §13). }
function IsUpdate: Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;
```

- [ ] **Step 3: Compile**

Run: `pwsh installer/build.ps1 -Fast -TestVariants` (publishes, then compiles both).
Expected: "Compiling with …Inno Setup 7\ISCC.exe (Inno Setup 7)", no ISCC errors or warnings about the new entry, and
`installer\output\PowerLedger-<v>-setup.exe` plus `installer\output\test\PowerLedger-<v+1>-setup.exe`. Parse-check the test
script: `pwsh -NoProfile -Command "$null = [System.Management.Automation.Language.Parser]::ParseFile('installer/test-installer.ps1', [ref]$null, [ref]$e); $e.Count"` → `0`.
The installed run of this step happens in CI and in the Sandbox (Task 12); it needs an elevated PowerShell.

- [ ] **Step 4: Commit**

```bash
git add installer/PowerLedger.iss installer/test-installer.ps1
git commit -m "Open the App again after an update started from the App, as the user who started setup"
```

### Task 10: The release script

**Files:**
- Create: `scripts/release.ps1`

- [ ] **Step 1: Write it**

```powershell
#Requires -Version 7
<#
.SYNOPSIS
Publishes the version in Directory.Build.props as a GitHub release with its installer, which installed copies then offer
as an update.

.DESCRIPTION
Run it from main, clean and pushed: the release is tagged at that commit. It refuses a version already released, builds
the installer with installer\build.ps1 (or uses the one already in installer\output with -SkipBuild), creates the release
vX.Y.Z with the notes in -Notes and the installer attached, and checks that the SHA-256 GitHub lists for the installer is
the local file's, since every installed copy checks its download against that digest (spec §13). -Draft makes a draft,
which nobody is offered until it is published on GitHub.

gh must be signed in to an account that may publish to the repository, or GH_TOKEN must hold a token for one.

.EXAMPLE
pwsh scripts/release.ps1 -Notes notes-0.2.0.md
#>
param(
    [Parameter(Mandatory)][string]$Notes,
    [string]$Repo = 'mharisjamal/PowerLedger',
    [switch]$SkipBuild,
    [switch]$Draft
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Runs a native command, and throws when it fails.
function Invoke-Native([string]$What, [scriptblock]$Command) {
    $output = & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE." }
    $output
}

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Select-Object -First 1
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }
$tag = "v$version"
$Notes = (Resolve-Path $Notes).Path
$installer = Join-Path $root "installer\output\PowerLedger-$version-setup.exe"

# The release is tagged at the commit that was built, which must be main as GitHub has it.
$branch = Invoke-Native 'git branch' { git -C $root branch --show-current }
if ($branch -ne 'main') { throw "Release from main, not $branch." }
if (Invoke-Native 'git status' { git -C $root status --porcelain }) { throw 'The working tree has changes; commit or stash them first.' }
Invoke-Native 'git fetch' { git -C $root fetch --quiet origin main } | Out-Null
$head = Invoke-Native 'git rev-parse' { git -C $root rev-parse HEAD }
$pushed = Invoke-Native 'git rev-parse' { git -C $root rev-parse origin/main }
if ($head -ne $pushed) { throw "main ($head) isn't what GitHub has ($pushed); push or pull first." }
gh release view $tag --repo $Repo --json tagName *> $null
if ($LASTEXITCODE -eq 0) { throw "$tag is already released; raise <Version> in Directory.Build.props for a new one." }

if (-not $SkipBuild) { & (Join-Path $root 'installer\build.ps1') }
if (-not (Test-Path $installer)) { throw "There is no installer at $installer; build it with installer\build.ps1." }
$sha = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()

$create = @('release', 'create', $tag, $installer, '--repo', $Repo, '--target', $head, '--title', "PowerLedger $version", '--notes-file', $Notes)
if ($Draft) { $create += '--draft' }
Invoke-Native 'gh release create' { gh @create } | Out-Host

# Installed copies check their download against this digest, so it has to be the file built here.
$listed = (Invoke-Native 'gh release view' { gh release view $tag --repo $Repo --json assets } | ConvertFrom-Json).assets |
    Where-Object name -eq (Split-Path $installer -Leaf)
if ($listed.digest -ne "sha256:$sha") { throw "GitHub lists $($listed.digest) for the installer, not sha256:$sha." }
if (-not $Draft) { Invoke-Native 'git fetch' { git -C $root fetch --quiet --tags origin } | Out-Null }
"Released ${tag}: https://github.com/$Repo/releases/tag/$tag"
"Installer SHA-256: $sha"
```

- [ ] **Step 2: Check it parses and refuses what it should**

Run: `pwsh -NoProfile -Command "$null = [System.Management.Automation.Language.Parser]::ParseFile('scripts/release.ps1', [ref]$null, [ref]$e); $e.Count"` → `0`.
Run on the branch (not main): `pwsh scripts/release.ps1 -Notes README.md` → Expected: throws "Release from main, not plan-h/…".

- [ ] **Step 3: Commit**

```bash
git add scripts/release.ps1
git commit -m "Publish a release in one command, checking GitHub's SHA-256 for the installer against the file built"
```

### Task 11: Version 0.2.0, the README and the spec

**Files:**
- Modify: `Directory.Build.props`, `README.md`, `docs/superpowers/specs/2026-09-08-powerledger-design.md`

- [ ] **Step 1: The version** — in `Directory.Build.props`, `<Version>0.1.0</Version>` → `<Version>0.2.0</Version>`.

- [ ] **Step 2: The README**

In "Requirements", "It is about 92 MB" → "It is about 96 MB". After "Requirements", add:

```markdown
## Updates

PowerLedger updates itself from this repository's releases. A minute after the App starts, and every six hours after, it
asks GitHub for the latest release; when there is a newer one, it downloads the installer quietly into
`%LOCALAPPDATA%\PowerLedger\Updates` and keeps it only when its size and SHA-256 are the ones GitHub lists. Then a card at
the bottom of the window's rail, one notification from the tray and an item in the tray menu offer **Restart to update**:
Windows asks for permission, setup closes the App, updates the service and opens the new version. The check sends nothing
about you or your PC; GitHub sees the request, with your IP address and `PowerLedger/<version>` as its user agent.
Settings turns the checks off (**Download new versions quietly, then ask**) and has **Check now**. Drafts and
pre-releases are never offered. Version 0.1.0 has no updater: install a newer version over it by hand once.
```

In "Test the installer", replace "and that an upgrade, an uninstall and a reinstall keep the history." with "that an
upgrade, run as the App's Restart to update runs it, opens the App again; and that an upgrade, an uninstall and a
reinstall keep the history."

After "Test the installer" (before "Documents"), add:

````markdown
## Publish a release

Raise `<Version>` in `Directory.Build.props`, commit and push `main`, write the release notes, then:

```
pwsh scripts\release.ps1 -Notes notes.md
```

It checks that `main` is clean and pushed and that the version isn't released yet, builds the installer, creates the
GitHub release `v<version>` at that commit with the notes and the installer, and checks that the SHA-256 GitHub lists is
the local file's: every installed copy checks its download against it. `-Draft` makes a draft, which nobody is offered
until it is published on GitHub; `-SkipBuild` uses the installer already in `installer\output`. `gh` has to be signed in
to an account that can publish to the repository, or `GH_TOKEN` has to hold a token for one.
````

- [ ] **Step 3: The spec** (`docs/superpowers/specs/2026-09-08-powerledger-design.md`)

1. §9 Tray: "Menu: Open, Start with Windows (on by default, set by the installer), Exit UI (service keeps logging)." →
   "Menu: Open, Restart to update to X.Y.Z (only while an update is ready, §13), Start with Windows (on by default, set by
   the installer), Exit UI (service keeps logging)."
2. §9 Settings: "theme and start with Windows (both apply when chosen)." → "theme, start with Windows, and updates
   (download new versions quietly, then ask; Check now; a line saying where things stand, §13), each applying when
   chosen."
3. §9: "UI-only preferences (theme, start with Windows, units)" → "UI-only preferences (theme, start with Windows, units,
   checking for updates, and the versions last announced and last run)".
4. §9, after the "Monthly report" subsection, add:

   ```markdown
   ### Updates

   The rail's foot holds the update card (§13) above the service's status: "PowerLedger X.Y.Z is ready" with Restart to
   update, What's new and ✕ once a newer version has downloaded; "Installing X.Y.Z…" while setup starts; "The update
   didn't install" with the reason and Try again when it didn't; and "Updated to X.Y.Z" with What's new on the first start
   of a new version. There is no card while checking or downloading, and nothing on it animates. ✕ puts it away until the
   App next starts; the tray's menu item stays.
   ```

5. §11, after "All data stays on the machine…" add the bullet: "- The App asks GitHub for PowerLedger's latest release a
   minute after it starts and every six hours, and downloads a newer installer. The request carries nothing about the
   user or the PC; GitHub sees the IP address and `PowerLedger/X.Y.Z` as the user agent. An update runs only when its tag
   is a version newer than the running one and its installer, from this repository's releases over HTTPS, has the size
   and SHA-256 GitHub lists. Settings turns the checks off."
6. §12 App bullet: after "the pipe client against a real pipe;" insert "the updater: the feed's parsing and refusals, the
   download's checks, stall and cleanup, setup's arguments and last check, and the card's stages and schedule;".
7. §13: "It is about 92 MB" → "It is about 96 MB". Replace the bullet "Releases on GitHub with a winget manifest after the
   first stable build. v1 has a "check for updates" link; an in-app updater is v1.1." with two bullets:

   ```markdown
   - Releases on GitHub (`mharisjamal/PowerLedger`), one per version, tagged `vX.Y.Z` and carrying `PowerLedger-X.Y.Z-setup.exe`. `scripts\release.ps1` publishes the version in `Directory.Build.props` from a clean, pushed `main` and checks that GitHub's SHA-256 for the installer is the local file's. A winget manifest can follow the first stable build.
   - In-app updates (Plan H; `2026-09-16-powerledger-updates-design.md`): a minute after the App starts and every six hours, while Settings allows it, the App asks GitHub's API for the latest release, which leaves out drafts and pre-releases. A newer one counts only when its tag is `vX.Y.Z` and it carries `PowerLedger-X.Y.Z-setup.exe` under this repository's download address with GitHub's `sha256:` digest. Its installer downloads quietly into `%LOCALAPPDATA%\PowerLedger\Updates` and is kept only when its size and SHA-256 are GitHub's. The card (§9), one tray notification per version and a tray menu item then offer Restart to update, which checks the file once more and starts it with `/SILENT /NORESTART /UPDATE=1`: Windows asks for permission, setup closes the App through its exit event, upgrades the service and, for `/UPDATE=1`, opens the App again as the user who started setup. If permission is refused or setup ends without installing, the card says so and offers Try again. Until the installer is signed, the permission prompt names an unknown publisher.
   ```

8. §13 installer-tests bullet: after "an upgrade keeping the history," insert "run with `/UPDATE=1` as the App runs it and
   opening the App again,".
9. §14 layout: after the two `installer/` lines add the two lines `scripts/` and
   `  publish.ps1, release.ps1, pipe-status.ps1, dev-service.ps1`, and change `  PowerLedger.App/` under `src/` to
   `  PowerLedger.App/           Updates/ holds the updater (§13)`.

- [ ] **Step 4: Build once** — `dotnet build -c Release` (the version flows into both programs). Expected: 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add Directory.Build.props README.md docs/superpowers/specs/2026-09-08-powerledger-design.md
git commit -m "Make this 0.2.0, and describe updates and publishing a release in the README and the spec"
```

### Task 12: Verification (lead)

- [ ] **Step 1: Merge** — in the main checkout on `plan-h/updates`: `git merge --no-ff plan-h/app` and
  `git merge --no-ff plan-h/ship` (disjoint files; no conflicts expected). Remove both worktrees and branches after.
- [ ] **Step 2: Build and test everything**

```
dotnet build -c Release
dotnet test -c Release --filter "Category!=Installed"
```

Expected: 0 warnings; every project passes (Hardware and UI included on this laptop). Report counts per project.

- [ ] **Step 3: Review** — one `caveman:cavecrew-reviewer` on `git diff main...plan-h/updates`, told the design doc and
  that security findings (anything that could run an installer GitHub doesn't list) matter most. Fix what it confirms.
- [ ] **Step 4: Installers** — `pwsh installer/build.ps1 -TestVariants` (full compression; the release build). Expected:
  `installer\output\PowerLedger-0.2.0-setup.exe` (~96 MB) and `installer\output\test\PowerLedger-0.2.1-setup.exe`.
- [ ] **Step 5: End to end in Windows Sandbox** — the scratchpad's `sandbox\update-e2e.ps1` (logon command of
  `sandbox\PowerLedger-update.wsb`, networking off):
  1. Install 0.2.0 silently; mark the first run done in `%LOCALAPPDATA%\PowerLedger\ui.json` (`FirstRunDone: true`,
     `LastVersion: "0.2.0"`), so the window shows the rail.
  2. Serve a fake feed with `System.Net.HttpListener` on `http://127.0.0.1:8765/`: `releases/latest` answers GitHub's
     shape for `v0.2.1` (the test build, its real size and SHA-256), and `download/v0.2.1/PowerLedger-0.2.1-setup.exe`
     serves the file.
  3. Start `PowerLedger.exe --update-feed http://127.0.0.1:8765/`; wait up to 3 minutes for the card to read
     "PowerLedger 0.2.1 is ready" (UI Automation), and check `ui.json`'s `AnnouncedVersion` is `0.2.1` and the checked
     download is in `%LOCALAPPDATA%\PowerLedger\Updates`. Snap the window.
  4. Press "Restart to update"; wait for the old App to exit, setup to finish, the uninstall entry to say 0.2.1, the
     service to run and answer 0.2.1 on the pipe, and a new App process (0.2.1) with the card "Updated to 0.2.1". Snap it.
  5. Check the Updates folder no longer holds the 0.2.1 installer after the new App's start (its log stays), and copy
     setup's log out.
  Expected: every check passes; the snapshots show the card.
- [ ] **Step 6: Record** — results in this plan (below), memory updated.

### Task 13: Ship (lead)

- [ ] **Step 1:** Fast-forward `main` to `plan-h/updates`, delete the branch, push `main` with the `mharisjamal` token
  (see memory `powerledger-git-rules`), and watch CI: both jobs green (the x64 installer test now reopens the App after the
  upgrade; the Arm64 job does the same on Arm64).
- [ ] **Step 2:** Write the 0.2.0 release notes (what's new: updates; how to install; that 0.1.0 needs this one by hand;
  known limits carried from 0.1.0), then `pwsh scripts/release.ps1 -Notes <notes> -SkipBuild` under the `mharisjamal`
  token, the installer being Task 12's full build of the same commit. Expected: "Released v0.2.0: …" and the digest check
  passes.
- [ ] **Step 3:** Check the API as an installed copy will: `GET https://api.github.com/repos/mharisjamal/PowerLedger/releases/latest`
  answers `v0.2.0` with `PowerLedger-0.2.0-setup.exe` and a `sha256:` digest equal to the local file's.

## Results

2026-09-16. Tasks 1–8 and 9–11 were built at the same time in two worktrees, then integrated, reviewed and verified on
`plan-h/updates` (11 task commits and 2 fix commits) and fast-forwarded into `main`.

**Verified**

- `dotnet build -c Release`: 0 warnings, 0 errors. Every test outside `Installed`: 915 pass (Core 117, Storage 49,
  Sensors 260, Service 131, App 358, of which 93 are the updater's).
- The card draws in both themes: `%TEMP%\powerledger-renders\update-{ready,updated}-{Dark,Light}.png`.
- Installers: `PowerLedger-0.2.0-setup.exe`, 95.7 MB. The Sandbox run used the build of `20efa32`
  (sha256 `A41DEDB4…`); the release was built again from the tagged commit, so its programs name it.
- CI (run 35035640460, both jobs green): x64 built, tested and ran the installer test, whose upgrade step now runs setup
  as "Restart to update" does (`/UPDATE=1`) and sees the App open again; then the Arm64 runner did the same.
- Released as **v0.2.0**: <https://github.com/mharisjamal/PowerLedger/releases/tag/v0.2.0>, installer sha256
  `28026c715de9289f20453b78ba3fadca549482826e61c246502638f2fc35bc9a`, which GitHub's API lists as the asset's digest —
  what every installed copy checks its download against.
- **Windows Sandbox, end to end, 18 of 18** (`sandbox\update-e2e.ps1` and `update-feed.ps1` in the session's scratchpad):
  0.2.0 installed; a stand-in feed on `127.0.0.1` offered a real 0.2.1 build; the App asked for it as `PowerLedger/0.2.0`,
  downloaded it quietly, kept it only once its size and SHA-256 matched, remembered announcing it (`AnnouncedVersion` in
  ui.json) and showed the card; "Restart to update" closed the App in 250 ms through its exit event, setup installed
  silently, reopened the App as the original user, then registered and started the service; the App came back as 0.2.1
  with "Updated to 0.2.1", and its next check cleared the used installer and kept setup's log.
  - A first run with the plan's `-TestVariants` upgrade passed 14 of 18: that build carries the *same* programs under a
    higher installer version, so the service and the App still read 0.2.0 and no "Updated" card appears. The real 0.2.1
    build for the second run came from a scratch worktree with `<Version>0.2.1</Version>`.
  - Seen in both runs: the App reopens a moment before setup starts the service, so the status line says "Service not
    running" for a few seconds until the pipe reconnects.

**Review** (whole branch, security first): no way was found to make the App run an installer GitHub doesn't list — tag,
asset name, URL prefix (normalised, so `..` can't escape), size, and GitHub's SHA-256 all have to agree, and the file is
hashed again while held open just before setup starts. Fixed before shipping: `--update-feed` now takes only this
machine's addresses; a setup that fails after closing the App starts the service and reopens the App
(`DeinitializeSetup`), instead of leaving both off until Windows restarts; `release.ps1` reads native exit codes itself
and waits for GitHub to work out the digest; the App reads its own version leniently. Left as they are: a process running
as the same user could still swap the installer once setup has begun reading it (signing closes that); the tray shares one
click action between the monthly-report and update notifications; and `UiPreferences.CheckForUpdates` needs a setter
because the JSON source generator gives an init-only property missing from the file its type's default rather than its
initializer — which also means a ui.json without `Co2KgPerKwh` loads 0 (pre-existing, tracked separately).

**Deviations from the plan's code**: `CheckForUpdates` is `{ get; set; }` for the reason above; tests say
`nameof(updater.X)`, since a helper method shadows the `Updater` type; the updater clears old downloads at every check as
well as at start, because the App that setup reopens cannot delete the installer setup is still running from;
`--update-feed` is loopback-only.

**Not covered**: a failure in the middle of installing (the recovery is written and reasoned about, but forcing one was
out of scope), and the permission prompt as an unelevated user sees it, since the Sandbox's account is an administrator.
