# Plan I — Updates that respect mobile data

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An update never spends mobile data unasked — on a metered connection the card offers Download instead of
downloading quietly — and when it does download it takes a per-architecture installer of about 56 MB instead of the 96 MB
universal one.

**Architecture:** Two small additions to the updater shipped in v0.2.0: `IConnectionCost` (Windows' network-list COM
interface) gates the quiet download, and `GitHubReleaseFeed` prefers the asset built for this PC's architecture. The
installer script learns to build one architecture at a time, and the release script publishes all three installers.
Design: `docs/superpowers/specs/2026-09-16-powerledger-metered-updates-design.md`.

**Tech Stack:** .NET 10 WPF, COM interop (`INetworkCostManager`), Inno Setup 7 preprocessor, PowerShell 7, xUnit +
Shouldly + `FakeTimeProvider`.

---

## Execution

| Wave | Who | Tasks |
|---|---|---|
| 1 | Agent A, worktree `plan-i/app` | 1–4: the App |
| 1 | Agent B, worktree `plan-i/ship` | 5–8: installer, release script, CI, docs, version |
| 2 | Lead | integrate, review, build, Sandbox, CI, publish 0.3.0 |

Same rules as Plan H: commit per task with the given message, repo identity (Haris), stage by explicit path, no
attribution lines, never push, don't touch the other agent's files. Unit tests:
`dotnet test tests/PowerLedger.App.Tests -c Release --filter "Category!=Hardware&Category!=UI&Category!=Installed"`.
`dotnet build -c Release` must end with 0 warnings.

## Files

Create: `src/PowerLedger.App/Updates/ConnectionCost.cs`, `tests/PowerLedger.App.Tests/ConnectionCostTests.cs`.
Modify (App): `Updates/Updater.cs`, `Updates/GitHubReleaseFeed.cs`, `App.xaml.cs`, `Shell/MainWindow.xaml` (nothing
structural: the card already binds Title/Detail/ActionLabel), tests `UpdaterTests.cs`, `ReleaseFeedTests.cs`,
`FakeUpdates.cs`.
Modify (shipping): `installer/PowerLedger.iss`, `installer/build.ps1`, `installer/test-installer.ps1`,
`scripts/release.ps1`, `.github/workflows/ci.yml`, `README.md`,
`docs/superpowers/specs/2026-09-08-powerledger-design.md`, `Directory.Build.props`.

---

### Task 1: Reading the connection's cost

**Files:** Create `src/PowerLedger.App/Updates/ConnectionCost.cs`, `tests/PowerLedger.App.Tests/ConnectionCostTests.cs`.

- [ ] **Step 1: Write the test**

```csharp
using Shouldly;

namespace PowerLedger.App.Tests;

public class ConnectionCostTests
{
    /// <summary>Windows' own flags: anything that isn't plainly unrestricted is data somebody pays for.</summary>
    [Theory]
    [InlineData(0x0u, false)]        // unknown
    [InlineData(0x1u, false)]        // unrestricted
    [InlineData(0x2u, true)]         // fixed allowance
    [InlineData(0x4u, true)]         // variable, paid by the byte
    [InlineData(0x10000u, true)]     // over the data limit
    [InlineData(0x20000u, false)]    // congested, but not charged for
    [InlineData(0x40000u, true)]     // roaming
    [InlineData(0x80000u, true)]     // approaching the data limit
    [InlineData(0x1u | 0x20000u, false)]
    [InlineData(0x2u | 0x20000u, true)]
    public void A_cost_says_whether_the_connection_is_metered(uint cost, bool metered)
        => ConnectionCost.IsMetered(cost).ShouldBe(metered);

    /// <summary>The real interface answers on this machine, whatever the answer is.</summary>
    [Fact]
    [Trait("Category", "Hardware")]
    public void Windows_gives_this_machine_a_cost()
    {
        var cost = new ConnectionCost();
        cost.Metered.ShouldBe(cost.Metered);   // twice: it must not throw, and must agree with itself
    }
}
```

- [ ] **Step 2: Run it to see it fail** — `dotnet build tests/PowerLedger.App.Tests -c Release`: no `ConnectionCost`.

- [ ] **Step 3: Write it**

```csharp
using System.Runtime.InteropServices;

namespace PowerLedger.App;

/// <summary>Whether the connection this PC uses charges for what it carries.</summary>
internal interface IConnectionCost
{
    /// <summary>True when Windows says the connection is metered. A cost Windows won't give counts as unmetered, which is
    /// how PowerLedger behaved before it asked at all.</summary>
    bool Metered { get; }
}

/// <summary>
/// Windows' network list (spec §13): the cost of the connection the machine uses now. Everything but "unrestricted" and
/// "unknown" counts as metered — a fixed allowance, paying by the byte, roaming, or near or over the limit — because
/// Windows itself treats those as data the user pays for. Congestion alone is not about money.
/// </summary>
internal sealed class ConnectionCost : IConnectionCost
{
    private const uint Fixed = 0x2;
    private const uint Variable = 0x4;
    private const uint OverDataLimit = 0x10000;
    private const uint Roaming = 0x40000;
    private const uint ApproachingDataLimit = 0x80000;

    public bool Metered
    {
        get
        {
            try
            {
                var manager = (INetworkCostManager)new NetworkListManager();
                manager.GetCost(out var cost, IntPtr.Zero);
                return IsMetered(cost);
            }
            catch (Exception error) when (error is COMException or InvalidCastException or NotSupportedException)
            {
                return false;   // no answer: treat it as a connection nobody pays by the byte for
            }
        }
    }

    /// <summary>NLM_CONNECTION_COST's flags, as money rather than speed.</summary>
    internal static bool IsMetered(uint cost) => (cost & (Fixed | Variable | OverDataLimit | Roaming | ApproachingDataLimit)) != 0;

    [ComImport]
    [Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")]
    private sealed class NetworkListManager;

    /// <summary>netlistmgr.h's INetworkCostManager. Every method has to be declared, in order, for the vtable to line up,
    /// even though only the first is called.</summary>
    [ComImport]
    [Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetworkCostManager
    {
        void GetCost(out uint cost, IntPtr destination);

        void GetDataPlanStatus(IntPtr status, IntPtr destination);

        void SetDestinationAddresses(uint length, IntPtr addresses, [MarshalAs(UnmanagedType.VariantBool)] bool append);
    }
}
```

If the `Category=Hardware` test throws `InvalidCastException` (E_NOINTERFACE), the interface GUID is wrong: check it
against Microsoft's `INetworkCostManager` documentation or the Windows SDK's `netlistmgr.h`, fix it, and say so in your
report.

- [ ] **Step 4: Run the tests** — the unit filter, then
  `dotnet test tests/PowerLedger.App.Tests -c Release --filter "FullyQualifiedName~ConnectionCostTests"` (Hardware
  included) on this machine. Expected: PASS; report what `Metered` said here.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.App/Updates/ConnectionCost.cs tests/PowerLedger.App.Tests/ConnectionCostTests.cs
git commit -m "Read from Windows whether this PC's connection is one somebody pays by the byte for"
```

### Task 2: The installer built for this PC

**Files:** Modify `src/PowerLedger.App/Updates/GitHubReleaseFeed.cs`, `tests/PowerLedger.App.Tests/ReleaseFeedTests.cs`.

- [ ] **Step 1: Write the failing tests**

In `ReleaseFeedTests`, `Answer(...)` gains a parameter for extra assets. Replace its `assets` array so callers can add
per-architecture installers, and add the tests:

```csharp
    /// <summary>A release carrying the universal installer and one per architecture, as releases from 0.3.0 do.</summary>
    internal static byte[] AnswerWithArchitectures(string tag = "v0.3.0")
    {
        var version = tag.TrimStart('v');
        return Encoding.UTF8.GetBytes($$"""
            {
              "tag_name": "{{tag}}",
              "html_url": "https://github.com/mharisjamal/PowerLedger/releases/tag/{{tag}}",
              "draft": false,
              "prerelease": false,
              "assets": [
                { "name": "PowerLedger-{{version}}-setup.exe", "size": 100000000,
                  "browser_download_url": "{{Downloads}}{{tag}}/PowerLedger-{{version}}-setup.exe", "digest": "sha256:{{Sha}}" },
                { "name": "PowerLedger-{{version}}-setup-x64.exe", "size": 58000000,
                  "browser_download_url": "{{Downloads}}{{tag}}/PowerLedger-{{version}}-setup-x64.exe", "digest": "sha256:{{Sha}}" },
                { "name": "PowerLedger-{{version}}-setup-arm64.exe", "size": 60000000,
                  "browser_download_url": "{{Downloads}}{{tag}}/PowerLedger-{{version}}-setup-arm64.exe", "digest": "sha256:{{Sha}}" }
              ]
            }
            """);
    }

    [Theory]
    [InlineData(Architecture.X64, "PowerLedger-0.3.0-setup-x64.exe", 58000000L)]
    [InlineData(Architecture.Arm64, "PowerLedger-0.3.0-setup-arm64.exe", 60000000L)]
    [InlineData(Architecture.X86, "PowerLedger-0.3.0-setup.exe", 100000000L)]
    public void The_installer_for_this_PC_comes_first(Architecture architecture, string name, long size)
    {
        var release = GitHubReleaseFeed.Parse(AnswerWithArchitectures(), Downloads, architecture).ShouldNotBeNull();
        release.FileName.ShouldBe(name);
        release.Size.ShouldBe(size);
        release.Installer.ShouldBe(new Uri($"{Downloads}v0.3.0/{name}"));
    }

    [Fact]
    public void A_release_with_only_the_universal_installer_still_updates_every_PC()
    {
        foreach (var architecture in new[] { Architecture.X64, Architecture.Arm64 })
        {
            GitHubReleaseFeed.Parse(Answer(), Downloads, architecture).ShouldNotBeNull().FileName.ShouldBe("PowerLedger-0.2.0-setup.exe");
        }
    }

    [Fact]
    public void A_release_with_no_installer_for_this_PC_is_refused()
        => Should.Throw<UpdateException>(() => GitHubReleaseFeed.Parse(Answer(asset: "PowerLedger-0.2.0-setup-x86.exe"), Downloads, Architecture.X64))
            .Message.ShouldBe("Release 0.2.0 has no installer for this PC.");
```

(`using System.Runtime.InteropServices;` for `Architecture`.)

- [ ] **Step 2: Run them to see them fail** — `Parse` takes two arguments.

- [ ] **Step 3: Choose the asset**

In `GitHubReleaseFeed`, replace the single-name lookup in `Parse` and give `Parse` the architecture:

```csharp
    /// <summary>The release GitHub describes, or null for a draft or a pre-release; refuses one it can't trust.
    /// <paramref name="downloads"/> is where installers must be, and <paramref name="architecture"/> which build this PC
    /// wants.</summary>
    internal static Release? Parse(byte[] json, string downloads, Architecture? architecture = null)
    {
        ...
        var version = new Version(Number(tag.Groups[1]), Number(tag.Groups[2]), Number(tag.Groups[3]));
        var wanted = InstallerNames(version, architecture ?? RuntimeInformation.OSArchitecture);
        var asset = wanted.Select(name => release.Assets?.FirstOrDefault(a => a.Name == name)).FirstOrDefault(found => found is not null)
            ?? throw new UpdateException($"Release {version.ToString(3)} has no installer for this PC.");
        var name = asset.Name!;
        ... (the URL, size and digest checks are unchanged, with `name` in their messages)
    }

    /// <summary>The installers a release may carry, best first: the one built for this PC, then the one with every build.</summary>
    internal static IReadOnlyList<string> InstallerNames(Version version, Architecture architecture)
    {
        var number = version.ToString(3);
        var universal = $"PowerLedger-{number}-setup.exe";
        return architecture switch
        {
            Architecture.X64 => [$"PowerLedger-{number}-setup-x64.exe", universal],
            Architecture.Arm64 => [$"PowerLedger-{number}-setup-arm64.exe", universal],
            _ => [universal],
        };
    }
```

- [ ] **Step 4: Run the tests** — the unit filter. Expected: PASS, the older feed tests included.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.App/Updates/GitHubReleaseFeed.cs tests/PowerLedger.App.Tests/ReleaseFeedTests.cs
git commit -m "Take the installer built for this PC when a release carries one"
```

### Task 3: Waiting for a connection nobody pays for

**Files:** Modify `src/PowerLedger.App/Updates/Updater.cs`, `tests/PowerLedger.App.Tests/FakeUpdates.cs`,
`tests/PowerLedger.App.Tests/UpdaterTests.cs`.

- [ ] **Step 1: Write the failing tests**

Add to `FakeUpdates.cs`:

```csharp
/// <summary>A connection whose cost the test sets.</summary>
internal sealed class FakeCost : IConnectionCost
{
    public bool Metered { get; set; }
}
```

In `UpdaterTests`, add the field `private readonly FakeCost _cost = new();`, pass it to the `Updater` constructor
(after `_setup`), and add:

```csharp
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
        _announced.Select(r => r.Name).ShouldBe(new[] { "0.3.0" });   // the tray still says so once
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
```

- [ ] **Step 2: Run them to see them fail** — the constructor takes no cost, and there is no `Available` stage.

- [ ] **Step 3: Add the stage and the gate**

In `Updater`: add `Available` to `UpdateStage` with the comment "Found, but the connection is metered, so it waits for
Download or a connection that isn't."; take `IConnectionCost cost` as the fourth constructor parameter and keep it in
`_cost`; add the field `private string? _wanted;` (the version the user asked for over metered data).

In `CheckAsync`, between finding the release and downloading it:

```csharp
            if (_cost.Metered && _wanted != release.Name)
            {
                _threads.Post(() => Waiting(release));
                return;
            }
```

Add, beside `Offer`:

```csharp
    /// <summary>A newer release, found while somebody is paying for every byte: the card offers it rather than taking it.</summary>
    private void Waiting(Release release)
    {
        if (_stage is UpdateStage.Installing or UpdateStage.Ready) return;
        _release = release;
        _installer = null;
        Status = $"PowerLedger {release.Name} is available · {Megabytes(release)} · waiting for a connection that isn't metered";
        Show(UpdateStage.Available);
        Announce(release);
    }

    /// <summary>The download's size as the card gives it: whole megabytes.</summary>
    private string Megabytes(Release release) => $"{(release.Size / (1024.0 * 1024.0)).ToString("0", _culture)} MB";
```

Move the "announce once per version" lines out of `Offer` into `Announce(Release release)` so both stages use it:

```csharp
    /// <summary>The tray says a version is there once, whether it is downloaded or waiting.</summary>
    private void Announce(Release release)
    {
        if (_ui.Current.AnnouncedVersion == release.Name) return;
        _ui.Announced(release.Name);
        _announce(release);
    }
```

Extend the card's properties for `Available`: `Title` → `$"PowerLedger {_release?.Name} is available"`; `Detail` →
`$"{Megabytes(_release)} · waiting for a connection that isn't metered"` (null when `_release` is null); `ActionLabel` →
`"Download"`; `CanDismiss` and `HasNotes` include `Available`; `ReadyVersion` stays null there (nothing to restart into).

`OnAct` gains the first branch:

```csharp
        if (_stage == UpdateStage.Available)
        {
            Download();
            return;
        }
```

and:

```csharp
    /// <summary>The card's Download: this version may use the metered connection, because the user asked for it.</summary>
    public void Download()
    {
        if (_stage != UpdateStage.Available || _release is not { } release) return;
        _wanted = release.Name;
        _threads.Background(() => _ = CheckAsync());
    }
```

Update the class comment: the check runs on any connection, but a metered one is offered rather than taken.

- [ ] **Step 4: Run the tests** — the unit filter. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.App/Updates/Updater.cs tests/PowerLedger.App.Tests/FakeUpdates.cs tests/PowerLedger.App.Tests/UpdaterTests.cs
git commit -m "Offer an update instead of downloading it when somebody pays for every byte"
```

### Task 4: Wiring, and the card drawn

**Files:** Modify `src/PowerLedger.App/App.xaml.cs`, `tests/PowerLedger.App.Tests/RenderingTests.cs`.

- [ ] **Step 1: Wire the cost in** — in `App.xaml.cs`, pass `new ConnectionCost()` as the `Updater`'s fourth argument
  (after `new SetupRunner()`).
- [ ] **Step 2: Draw it** — in `RenderingTests`, add `("available", AvailableUpdate())` to the pairs the card test draws,
  with

```csharp
    /// <summary>0.3.0 found while the connection is metered.</summary>
    private static Updater AvailableUpdate()
    {
        var feed = new FakeFeed { Latest = UpdaterTests.Release("0.3.0") with { Size = 58 * 1024 * 1024 } };
        var updater = new Updater(feed, new FakeDownloader(), new FakeSetup(), new FakeCost { Metered = true }, new FakeUiSettings(),
            UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc, English, new Version(0, 2, 0), _ => { }, _ => { });
        updater.CheckAsync().GetAwaiter().GetResult();
        return updater;
    }
```

  and let the existing `ReadyUpdate()`/`UpdatedApp()` pass `new FakeCost()`.
- [ ] **Step 3: Run the App tests, UI included** —
  `dotnet test tests/PowerLedger.App.Tests -c Release --filter "Category!=Hardware&Category!=Installed"`. Expected: PASS.
  Look at `%TEMP%\powerledger-renders\update-available-Dark.png` and `-Light.png`: the card reads "UPDATE / PowerLedger
  0.3.0 is available / 58 MB · waiting for a connection that isn't metered / Download / What's new", wrapped, nothing
  clipped. Report what you saw.
- [ ] **Step 4: Commit**

```bash
git add src/PowerLedger.App/App.xaml.cs tests/PowerLedger.App.Tests/RenderingTests.cs
git commit -m "Give the App the real connection cost, and draw the card that waits for Wi-Fi"
```

### Task 5: One installer per architecture

**Files:** Modify `installer/PowerLedger.iss`, `installer/build.ps1`.

- [ ] **Step 1: The script** — in `PowerLedger.iss`, after the `PayloadBytes` define, add

```
#ifndef Architecture
  #define Architecture "both"
#endif
```

  replace the `ArchitecturesAllowed`/`ArchitecturesInstallIn64BitMode`/`OutputBaseFilename` lines with

```
#if Architecture == "x64"
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
OutputBaseFilename=PowerLedger-{#AppVersion}-setup-x64
#elif Architecture == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
OutputBaseFilename=PowerLedger-{#AppVersion}-setup-arm64
#else
; A native build for each: x64 Windows gets the x64 build, Arm64 Windows 10 and 11 the Arm64 build (see [Files]).
; 32-bit Windows is refused with Inno Setup's own message.
ArchitecturesAllowed=x64os or arm64
ArchitecturesInstallIn64BitMode=x64os or arm64
OutputBaseFilename=PowerLedger-{#AppVersion}-setup
#endif
```

  and wrap the `[Files]` entries so a single-architecture installer carries only its own build:

```
#if Architecture != "arm64"
Source: "{#Publish}\win-x64\App\*"; DestDir: "{app}"; Check: not IsArm64; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Publish}\win-x64\Service\*"; DestDir: "{app}\Service"; Check: not IsArm64; Flags: ignoreversion recursesubdirs createallsubdirs
#endif
#if Architecture != "x64"
Source: "{#Publish}\win-arm64\App\*"; DestDir: "{app}"; Check: IsArm64; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Publish}\win-arm64\Service\*"; DestDir: "{app}\Service"; Check: IsArm64; Flags: ignoreversion recursesubdirs createallsubdirs
#endif
```

- [ ] **Step 2: The build** — in `build.ps1`, add the parameter

```powershell
    [ValidateSet('both', 'x64', 'arm64')]
    [string[]]$For = @('both'),
```

  documented as "Which installers to compile: `both` holds every build, `x64` and `arm64` only their own and are about
  40% smaller, which is what an update downloads." Replace the single compile with a loop, where each architecture states
  its own payload:

```powershell
$sizes = @{}
foreach ($folder in Get-ChildItem (Join-Path $root 'artifacts\publish') -Directory) {
    $sizes[$folder.Name] = (Get-ChildItem $folder.FullName -Recurse -File | Measure-Object Length -Sum).Sum
}
if ($sizes.Count -eq 0) { throw 'artifacts\publish is empty; run this without -SkipPublish.' }
function Payload([string]$Architecture) {
    $bytes = switch ($Architecture) {
        'x64' { $sizes['win-x64'] }
        'arm64' { $sizes['win-arm64'] }
        default { ($sizes.Values | Measure-Object -Maximum).Maximum }
    }
    "/DPayloadBytes=$([long]$bytes)"
}

$made = @()
foreach ($architecture in $For) {
    $options = @("/DAppVersion=$version", (Payload $architecture), "/DArchitecture=$architecture")
    if ($Fast) { $options += $fastCompression }
    Invoke-Iscc $options
    $made += Join-Path $PSScriptRoot ("output\PowerLedger-$version-setup{0}.exe" -f $(if ($architecture -eq 'both') { '' } else { "-$architecture" }))
}
```

  `-TestVariants` keeps compiling only the universal upgrade build.

- [ ] **Step 3: Compile all three** — `pwsh installer/build.ps1 -Fast -For both,x64,arm64`. Expected: three files in
  `installer\output`, the per-architecture ones roughly half the universal one's size, no ISCC warnings. Check with
  `installer\output\PowerLedger-<v>-setup-x64.exe /?`-style inspection is not possible; instead confirm the sizes and that
  the log lists only `win-x64` sources for the x64 build (build.ps1 prints each file it makes). Then delete
  `artifacts\` and `installer\output\` from your worktree.
- [ ] **Step 4: Commit**

```bash
git add installer/PowerLedger.iss installer/build.ps1
git commit -m "Compile an installer for one architecture as well as the one that holds both"
```

### Task 6: The release carries all three

**Files:** Modify `scripts/release.ps1`.

- [ ] **Step 1: Build and upload all three** — where `release.ps1` builds, call
  `& (Join-Path $root 'installer\build.ps1') -For both,x64,arm64`; make `$installer` a list:

```powershell
$installers = @(
    Join-Path $root "installer\output\PowerLedger-$version-setup.exe"
    Join-Path $root "installer\output\PowerLedger-$version-setup-x64.exe"
    Join-Path $root "installer\output\PowerLedger-$version-setup-arm64.exe"
)
foreach ($file in $installers) { if (-not (Test-Path $file)) { throw "There is no installer at $file; build it with installer\build.ps1 -For both,x64,arm64." } }
$shas = @{}
foreach ($file in $installers) { $shas[(Split-Path $file -Leaf)] = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() }
```

  pass every installer to `gh release create`, and check every digest in the loop that waits for GitHub, failing with the
  name of any that doesn't match. Print each file's SHA-256 at the end. Update the script's `.DESCRIPTION` to say it
  publishes the universal installer and one per architecture, since an update downloads the one that matches the PC.
- [ ] **Step 2: Check it parses and still refuses** — `[System.Management.Automation.Language.Parser]::ParseFile` gives 0
  errors; running it on the branch throws "Release from main, not plan-i/ship."
- [ ] **Step 3: Commit**

```bash
git add scripts/release.ps1
git commit -m "Publish the per-architecture installers beside the universal one"
```

### Task 7: The installer test and CI

**Files:** Modify `installer/test-installer.ps1`, `.github/workflows/ci.yml`.

- [ ] **Step 1: A step that installs this PC's own build** — add `Architecture` to the `-Step` set and to the default
  steps, after `Reinstall`, and the function:

```powershell
# The per-architecture installer an update downloads: it must install over what is already there, on this PC's architecture.
function Step-Architecture {
    $architecture = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
    $own = Join-Path $output "PowerLedger-$version-setup-$architecture.exe"
    if (-not (Test-Path $own)) {
        Skip Architecture "the $architecture installer installs over what is there" "no $([IO.Path]::GetFileName($own)); build it with installer\build.ps1 -For both,x64,arm64"
        return
    }
    Check Architecture "the $architecture installer exits 0" { $code = Invoke-Setup $own $Silent; Assert ($code -eq 0) "exit code $code" }
    Check Architecture 'service running' { Assert (Wait-ServiceRunning 30) (Get-ServiceState) }
    Check Architecture 'App and service are this PC's build' {
        $expected = $PeMachines["$([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)"]
        $found = foreach ($exe in $AppExe, $ServiceExe) { '{0} 0x{1:X4}' -f (Split-Path $exe -Leaf), (Get-PeMachine $exe) }
        Assert (@($AppExe, $ServiceExe | Where-Object { (Get-PeMachine $_) -ne $expected }).Count -eq 0) ($found -join ', ')
    }
}
```

  and mention the step in the script's `.DESCRIPTION` list.
- [ ] **Step 2: CI builds all three** — in `.github/workflows/ci.yml`, the Installer step becomes
  `run: ./installer/build.ps1 -For both,x64,arm64 -TestVariants`; the uploaded artifact already globs
  `installer/output/*.exe`, so the new files travel to the Arm64 job.
- [ ] **Step 3: Check it parses** — 0 parse errors for `test-installer.ps1`; `ci.yml` stays valid YAML
  (`pwsh -c "ConvertFrom-Yaml"` isn't available — just re-read the file).
- [ ] **Step 4: Commit**

```bash
git add installer/test-installer.ps1 .github/workflows/ci.yml
git commit -m "Install the per-architecture build in the installer test, on both runners"
```

### Task 8: Version 0.3.0 and the words

**Files:** Modify `Directory.Build.props`, `README.md`, `docs/superpowers/specs/2026-09-08-powerledger-design.md`.

- [ ] **Step 1: Version** — `<Version>0.2.1</Version>` → `<Version>0.3.0</Version>`.
- [ ] **Step 2: README** — in the Updates section, after the sentence about downloading quietly, add: "On a metered
  connection — a phone hotspot, mobile data — nothing is downloaded: the card says the version is available with its size
  and a **Download** button, so you can still take it there and then, and otherwise it waits for a connection that isn't
  metered." In the same section, note that an update takes the installer built for the PC, about 56 MB, and falls back to
  the universal one. In "Make the installer", document `-For both,x64,arm64`. In "Publish a release", note that the
  release carries all three installers.
- [ ] **Step 3: Spec** — §9's card table gains the Available row; §11's update bullet gains "Nothing is downloaded on a
  metered connection unless the user asks for it."; §13's in-app updates bullet gains the per-architecture installers and
  the fallback, and the releases bullet says each release carries three installers.
- [ ] **Step 4: Build** — `dotnet build -c Release`: 0 warnings.
- [ ] **Step 5: Commit**

```bash
git add Directory.Build.props README.md docs/superpowers/specs/2026-09-08-powerledger-design.md
git commit -m "Make this 0.3.0, and say how updates behave on mobile data"
```

### Task 9: Verification and release (lead)

- [ ] Merge both branches, `dotnet build -c Release` (0 warnings) and every test outside `Installed`.
- [ ] One reviewer subagent over `git diff main...plan-i/updates`, security first as in Plan H (nothing may install an
  installer GitHub doesn't list; the metered gate must not be a way to skip the checks).
- [ ] `installer/build.ps1 -For both,x64,arm64 -TestVariants`; report the three sizes.
- [ ] Windows Sandbox end to end, with the stand-in feed offering all three assets: the App must take `-x64`, and the
  update must go through as in Plan H. Then a second pass with the feed offering only the universal installer, which must
  still work.
- [ ] Push, CI green on both runners, then `scripts\release.ps1` for 0.3.0 with notes.
- [ ] The owner's own check: on a phone hotspot, the card offers rather than downloads.

## Results

(Filled in after Task 9.)
