# PowerLedger

<p>
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/brand/logo-dark.svg">
    <img src="assets/brand/logo.svg" alt="PowerLedger" width="320">
  </picture>
</p>

PowerLedger records how much power a Windows PC uses, hour by hour, and turns it into a ledger: kWh, cost at your
tariff, which parts used it (processor, graphics, display, the rest), CO₂, and the energy spent while nobody was
using the machine. Hardware monitors show watts now; PowerLedger remembers them.

It is two programs:

- **The service** (`PowerLedger.Service`) runs as a Windows service, samples the machine every second through
  interfaces Windows already provides (no kernel driver), and keeps the history in SQLite under
  `C:\ProgramData\PowerLedger`.
- **The App** (`PowerLedger.exe`) lives in the tray and opens a window with four screens: Now, Breakdown, Report and
  Settings. It reads the history, talks to the service over a local named pipe, and writes a PDF report for each
  finished month to `Documents\PowerLedger`.

Readings say how they were known: *measured* on battery from Windows' battery report, *calibrated* when plugged in
from a model with a baseline learned on battery (within about 10%), or *estimated* from load and the machine profile
alone (within about 20%).

## Monitors

External monitors are detected: every minute the service asks Windows which are attached, with each one's maker, model,
size and resolution. Each is counted at the figure the ENERGY STAR Certified Displays list gives its model or, for a
model the list doesn't have, at an estimate from its size and resolution. Where a monitor allows it, the App reads its
brightness over the display cable, read-only, and the figure follows it; a monitor it can't read is taken to be at 75%
brightness. The App also asks each monitor once a minute whether it is on: one that says it is switched off, or on
standby, counts at its off or sleep figure, and where a monitor doesn't say, your **Count it** tick decides. An LCD
monitor that Windows drives above 60 Hz counts a little more for its refresh rate, and a monitor with HDR on is marked
as one that may draw much more, since no figure for that is known. The setup wizard lists the monitors it found, and
asks nothing about monitors on a laptop on its own screen. A portable monitor running off a laptop's USB-C port is part
of what the laptop draws, so it always counts and is never counted twice; on a laptop, a monitor of 17.3 inches or less
is taken to be one. If yours has a plug of its own, tick **has its own plug** beside it, in the wizard or in Settings.
Settings saves each change by itself: a tick at once, a typed value when you leave its box or press Enter. There you can
leave out a monitor with its own plug, type a figure of your own, which is used as typed, or untick **Read monitors'
brightness and whether they're on (read-only)** to stop the App sending requests to the monitors; refresh rate and HDR
still come from Windows.

The monitor figures come from the dataset
[ENERGY STAR Certified Displays](https://data.energystar.gov/Active-Specifications/ENERGY-STAR-Certified-Displays/qbg3-d468),
published by the US Environmental Protection Agency. It is a work of the US Government, in the public domain, published
under the [EPA Data License](https://edg.epa.gov/EPA_Data_License.html). PowerLedger is not certified by, endorsed by or
affiliated with ENERGY STAR or the EPA, and doesn't use the ENERGY STAR mark. The table ships inside PowerLedger, so
nothing is fetched.

## Requirements

Windows 10 1809 or later, or Windows 11, on x64 or Arm64. 32-bit (x86) Windows and Windows in S mode are not
supported. The installer carries everything, the .NET 10 runtime included, and downloads nothing. It is about 96 MB;
installed, PowerLedger takes about 260 MB of disk on x64 and 280 MB on Arm64. On Windows 10, .NET 10 is officially
supported only on the Enterprise LTSC editions, but it runs on the others. Since the runtime is inside the app, .NET's
security fixes arrive with PowerLedger's updates.

## Updates

PowerLedger updates itself from this repository's releases. A minute after the App starts, and every hour after, it
asks GitHub for the latest release; when there is a newer one, it downloads the installer quietly into
`%LOCALAPPDATA%\PowerLedger\Updates` and keeps it only when its size and SHA-256 are the ones GitHub lists. On a metered
connection — a phone hotspot, mobile data — nothing is downloaded: the card says the version is available with its size
and a **Download** button, so you can still take it there and then, and otherwise it waits for a connection that isn't
metered. An update takes the installer built for the PC, about 56 MB, and falls back to the universal one when a release
doesn't carry one. Then a card at the bottom of the window's rail, one notification from the tray and an item in
the tray menu offer **Restart to update**:
Windows asks for permission, setup closes the App, updates the service and opens the new version. The check sends nothing
about you or your PC; GitHub sees the request, with your IP address and `PowerLedger/<version>` as its user agent.
Settings turns the checks off (**Download new versions quietly, then ask**) and has **Check now**. Drafts and
pre-releases are never offered. Version 0.1.0 has no updater: install a newer version over it by hand once.

## Build and test

```
dotnet build -c Release
dotnet test -c Release --filter "Category!=Hardware&Category!=UI&Category!=Installed"
```

Tests tagged `Hardware` read this machine's sensors; tests tagged `UI` draw the window to pictures in
`%TEMP%\powerledger-renders`. Run them with `dotnet test -c Release --filter "Category!=Installed"` on a real machine.
Tests tagged `Installed` check the App against the installed service: that it trusts the service, that a settings
change goes through, and that it can read the database under the data folder's ACL. Run them unelevated, after
installing; see Test the installer below.

## Run it from source

In one terminal, the service as a console program with its own data folder and pipe:

```
src\PowerLedger.Service\bin\Release\net10.0-windows\PowerLedger.Service.exe --data %TEMP%\pl-dev --pipe PowerLedger.dev
```

In another, the App against it:

```
src\PowerLedger.App\bin\Release\net10.0-windows\PowerLedger.exe --pipe PowerLedger.dev --data %TEMP%\pl-dev
```

A run started with `--pipe` sends settings to that pipe's server without checking it is the installed service.
Until its first-run wizard is finished, the App turns on starting with Windows for whichever `PowerLedger.exe` was
started, so finish the wizard and untick it in Settings, or delete
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\PowerLedger`.
`scripts\dev-service.ps1` installs the service for real, from an elevated PowerShell.

## Make the installer

The first time, get Inno Setup; then build:

```
pwsh installer\get-inno-setup.ps1
pwsh installer\build.ps1
```

`get-inno-setup.ps1` installs the pinned Inno Setup 7.1 for this user, so it needs no administrator. It downloads the
release from github.com/jrsoftware/issrc and refuses it unless its SHA-256 matches and it is validly signed by
Pyrsys B.V., Inno Setup's publisher. `build.ps1` publishes both programs self-contained, for win-x64 and win-arm64,
into `artifacts\publish`, and compiles `installer\PowerLedger.iss` into `installer\output`. The one installer holds
both builds and installs the one that matches the PC; it downloads nothing. `-For` picks which to compile: `both`
(default) holds every build; `x64` and `arm64` hold only their own, about 40% smaller, which is what an update
downloads; `-For both,x64,arm64` makes all three, as a release does. Its compression takes a few minutes; `-Fast` is
quicker, for local builds, and makes a bigger installer. The installer is built with Inno Setup 7.1 or later; Inno
Setup 6's 32-bit compiler can't use its 256 MB compression dictionary. `build.ps1` finds Inno Setup 7 in its usual
folders, then on PATH, or takes the compiler's path as `-Iscc`; it refuses an older compiler, even one first on PATH.

## Test the installer

From an elevated PowerShell:

```
pwsh installer\build.ps1 -TestVariants
pwsh installer\test-installer.ps1
```

`-TestVariants` also compiles the upgrade the test needs, the next patch version, into `installer\output\test`, with
fast compression since its size doesn't matter. `test-installer.ps1` installs the real PowerLedger, service and all,
and checks the files, shortcut and uninstall entry; that the build installed is the one for this PC's architecture and
carries its own .NET runtime; the service's registration, and its recovery when its process is killed; the data
folder's owner and ACL; that the pipe answers; that an upgrade, run as the App's Restart to update runs it, opens the
App again; and that an upgrade, an uninstall and a reinstall keep the history. It stops at once if
`C:\ProgramData\PowerLedger` already exists, so it never touches a real history, and it removes what it created. Its
steps are Preflight, Install, Service, Data, Recovery, ServiceStop, Upgrade, UninstallKeep, Reinstall and Cleanup, all
silent. Three more drive setup's windows with UI Automation: InstallWizard installs with the wizard, as somebody new to
PowerLedger would; UninstallDelete uninstalls and deletes the history; and DriveWizard drives the wizard of a setup
started by a normal user. `-Step` picks the steps to run. Results go to `installer\output\test-results`.

Then install PowerLedger from `installer\output` and, from an unelevated terminal, run the tests that need it
installed:

```
dotnet test -c Release --filter Category=Installed
```

## Publish a release

Raise `<Version>` in `Directory.Build.props`, commit and push `main`, write the release notes, then:

```
pwsh scripts\release.ps1 -Notes notes.md
```

It checks that `main` is clean and pushed and that the version isn't released yet, builds the universal installer and
one per architecture, creates the GitHub release `v<version>` at that commit with the notes and all three installers,
and checks that the SHA-256 GitHub lists for each is the local file's: every installed copy checks its download
against it, taking whichever matches the PC. `-Draft` makes a draft, which nobody is offered until it is published on
GitHub; `-SkipBuild` uses the installers already in `installer\output`. `gh` has to be signed in to an account that
can publish to the repository, or `GH_TOKEN` has to hold a token for one.

## Documents

- `docs/superpowers/specs/2026-09-08-powerledger-design.md`: the design.
- `docs/superpowers/plans/`: how it was built, plan by plan.
- `assets/brand/`: the logo, a P on an amber tile whose bowl is a meter's dial and whose stem is a ledger's margin.
  `make-brand.ps1` draws it and writes the Windows icon, the SVGs and PNGs, and the installer's images.
- `assets/monitors/`: the table of certified monitors PowerLedger ships. `make-monitor-table.ps1` rebuilds it from the
  public dataset, and its README says where it comes from and how it is cleaned.
