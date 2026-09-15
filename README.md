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

## Requirements

Windows 10 1809 or later, or Windows 11, on x64 or Arm64. 32-bit (x86) Windows and Windows in S mode are not
supported. The installer carries everything, the .NET 10 runtime included, and downloads nothing. It is about 92 MB;
installed, PowerLedger takes about 260 MB of disk on x64 and 280 MB on Arm64. On Windows 10, .NET 10 is officially
supported only on the Enterprise LTSC editions, but it runs on the others. Since the runtime is inside the app, .NET's
security fixes arrive with PowerLedger's updates.

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
both builds and installs the one that matches the PC; it downloads nothing. Its compression takes a few minutes;
`-Fast` is quicker, for local builds, and makes a bigger installer. The installer is built with Inno Setup 7.1 or later; Inno
Setup 6's 32-bit compiler can't use its 256 MB compression dictionary. `build.ps1` finds Inno Setup 7 on PATH or in its
usual folders, or takes the compiler's path as `-Iscc`.

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
folder's owner and ACL; that the pipe answers; and that an upgrade, an uninstall and a reinstall keep the history. It
stops at once if `C:\ProgramData\PowerLedger` already exists, so it never touches a real history, and it removes what
it created. Its steps are Preflight, Install, Service, Data, Recovery, ServiceStop, Upgrade, UninstallKeep, Reinstall
and Cleanup, all silent. Three more drive setup's windows with UI Automation: InstallWizard installs with the wizard,
as somebody new to PowerLedger would; UninstallDelete uninstalls and deletes the history; and DriveWizard drives the
wizard of a setup started by a normal user. `-Step` picks the steps to run. Results go to
`installer\output\test-results`.

Then install PowerLedger from `installer\output` and, from an unelevated terminal, run the tests that need it
installed:

```
dotnet test -c Release --filter Category=Installed
```

## Documents

- `docs/superpowers/specs/2026-09-08-powerledger-design.md`: the design.
- `docs/superpowers/plans/`: how it was built, plan by plan.
- `assets/brand/`: the logo, a P on an amber tile whose bowl is a meter's dial and whose stem is a ledger's margin.
  `make-brand.ps1` draws it and writes the Windows icon, the SVGs and PNGs, and the installer's images.
