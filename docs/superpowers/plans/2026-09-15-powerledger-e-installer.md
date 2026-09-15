# PowerLedger Plan E — The installer — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship PowerLedger as one Windows installer. It installs the App and the service, fetches the .NET 10 Desktop Runtime when it is missing, registers the service, and opens the App on its first-run wizard. Uninstalling removes it all, and asks whether to keep the history.

**Architecture:** A publish script builds both programs for win-x64, framework-dependent, into `artifacts\publish`. An Inno Setup 6 script packs them, and its `[Code]` section does what Inno's sections cannot:
- It checks for the runtime and downloads it.
- It stops and starts the service around an upgrade.
- It registers the service with `sc.exe`.
- It asks about the history on uninstall.

The App turns on starting with Windows itself on its first run. The Run entry then lands in the hive of the person who uses the App, not of the account that elevated the installer. A build script and a GitHub Actions workflow make the installer the same way anywhere.

**Tech Stack:** Inno Setup 6 (its download page needs 6.1 or later), `sc.exe` and `net.exe`, PowerShell 7, GitHub Actions on `windows-latest`. Spec: `docs/superpowers/specs/2026-09-08-powerledger-design.md` §13.

**Git rule (from the owner):** commit locally after every task. Never add a remote, push, or create a GitHub repo. The workflow file is committed for the day the owner provides a repository; nothing here runs it.

**What this plan can't do here:** Inno Setup is not installed on the development machine, and fetching it is a download that needs the owner's go-ahead. So the installer script is written, and read line by line against Inno Setup 6's documentation, but not compiled here. CI compiles it, and so does `installer\build.ps1` wherever Inno Setup 6 is installed. The first compiled installer should be tried on a clean Windows 10 or 11 machine before release (Task 5 lists what to check).

---

## What already exists

`main` holds Plans A to D3. D3 left rules for Plan E, and this plan honours them:
- Register the service as exactly `PowerLedger`, with the quoted path to `PowerLedger.Service.exe` as its image path. The App compares the pipe server's executable with that path.
- Leave `C:\ProgramData\PowerLedger` to the service, which creates it with its ACL and refuses a folder another account owns.
- Write the Run entry as the user, not the elevating account.
- Start the App after installing, without `--tray`, so its first window is the wizard.
- Publish for win-x64.

`scripts\dev-service.ps1` shows the service's registration: automatic start, LocalSystem, and restarts after 5 seconds, up to three times a day.

## Decisions made while planning

- **The App installs to `Program Files\PowerLedger`, the service to its `Service` folder.** The two programs share the PowerLedger libraries but not the rest. Separate folders keep either from loading the other's copies.
- **The runtime is downloaded, not bundled.** Spec §13 wants an installer of about 15 MB, and the Desktop Runtime alone is about 55 MB. The download address is Microsoft's stable `aka.ms/dotnet/10.0` link, which always points at the newest 10.0 patch.
- **The App sets its own Run entry.** Inno can write `HKCU` only for the account running setup, which is the elevating account when someone types an administrator's password. The App already writes the entry as whoever runs it (`StartWithWindows`), so on its first run it turns the entry on. The installer opens the App as the original user.
- **A silent uninstall keeps the history.** The question uses Inno's suppressible message box, which answers "keep" when there is no one to ask.
- **No licence page yet.** Spec §16 leaves the licence to the owner.

## File structure

```
src/PowerLedger.App/Preferences/AppPreferences.cs   Modify: ApplyFirstRunDefaults
src/PowerLedger.App/App.xaml.cs                     Modify: calls it
tests/PowerLedger.App.Tests/AppPreferencesTests.cs  Modify
scripts/publish.ps1                                  both programs, win-x64, framework-dependent
installer/PowerLedger.iss                            the Inno Setup script
installer/build.ps1                                  publish, then compile
.github/workflows/ci.yml                             build, test, installer artifact
README.md                                            what it is, how to build, test, run and install
.gitignore                                           Modify: artifacts/
docs/superpowers/specs/2026-09-08-powerledger-design.md   Modify: §13
```

---

### Task 1: Start with Windows from the first run

**Files:**
- Modify: `src/PowerLedger.App/Preferences/AppPreferences.cs`
- Modify: `src/PowerLedger.App/App.xaml.cs`
- Test: `tests/PowerLedger.App.Tests/AppPreferencesTests.cs`

Spec §9 has starting with Windows on by default. Until the first run is done, the App turns the Run entry on each time it starts. After that the entry is left as the user set it, so unticking it in Settings or the tray sticks.

- [ ] **Step 1: Write the failing tests**

Append to `tests/PowerLedger.App.Tests/AppPreferencesTests.cs`, inside the class:

```csharp
    [Fact]
    public void Until_the_first_run_is_done_starting_with_windows_is_turned_on()
    {
        var preferences = Preferences();
        preferences.StartsWithWindows.ShouldBeFalse();
        preferences.ApplyFirstRunDefaults();
        preferences.StartsWithWindows.ShouldBeTrue();
    }

    [Fact]
    public void After_the_first_run_starting_with_windows_is_left_as_the_user_set_it()
    {
        var preferences = new AppPreferences(
            Store, UiPreferences.Default with { FirstRunDone = true }, _themes.Add, _factors.Add,
            new StartWithWindows(@"C:\Program Files\PowerLedger\PowerLedger.exe", _runKey));
        preferences.ApplyFirstRunDefaults();
        preferences.StartsWithWindows.ShouldBeFalse();
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.App.Tests --filter AppPreferencesTests`
Expected: build error, `ApplyFirstRunDefaults` not found.

- [ ] **Step 3: Turn it on until the first run is done**

In `src/PowerLedger.App/Preferences/AppPreferences.cs`, add after `FinishFirstRun`:

```csharp
    /// <summary>Spec §9: starting with Windows is on by default. Until the first run is done the App turns it on as it
    /// starts, as the user who runs it (the installer can't: it runs as the elevating account); after that it is left
    /// as the user set it.</summary>
    public void ApplyFirstRunDefaults()
    {
        if (Current.FirstRunDone || autostart.IsEnabled) return;
        StartWithWindows(true);
    }
```

In `src/PowerLedger.App/App.xaml.cs`, after `_preferences` is made, add:

```csharp
        _preferences.ApplyFirstRunDefaults();
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter AppPreferencesTests`
Expected: `Passed! - Failed: 0, Passed: 6`.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.App tests/PowerLedger.App.Tests/AppPreferencesTests.cs
git commit -m "Turn on starting with Windows until the first run is done"
```

---

### Task 2: Publish both programs

**Files:**
- Create: `scripts/publish.ps1`
- Modify: `.gitignore`

`publish.ps1` builds the App into `artifacts\publish\App` and the service into `artifacts\publish\Service`. Both are published for win-x64 and framework-dependent, so only Windows x64's native libraries ship (spec §13). It prints each folder's size.

- [ ] **Step 1: Write the script**

`scripts/publish.ps1`
```powershell
<#
.SYNOPSIS
Publishes the App and the service for win-x64, framework-dependent (spec §13), into artifacts\publish.

.DESCRIPTION
A win-x64 publish ships only Windows x64's native libraries, where a platform-neutral build carries eight
platforms' worth of QuestPDF and SQLite. The installer (installer\PowerLedger.iss) packs both folders.
#>
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\publish'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$programs = @(
    @{ Project = 'src\PowerLedger.App'; Folder = 'App' },
    @{ Project = 'src\PowerLedger.Service'; Folder = 'Service' }
)
foreach ($program in $programs) {
    dotnet publish (Join-Path $root $program.Project) -c $Configuration -r win-x64 --self-contained false `
        -o (Join-Path $out $program.Folder) --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($program.Project)." }
}

foreach ($folder in Get-ChildItem $out -Directory) {
    $bytes = (Get-ChildItem $folder.FullName -Recurse -File | Measure-Object Length -Sum).Sum
    '{0}: {1:N1} MB' -f $folder.Name, ($bytes / 1MB)
}
```

In `.gitignore`, add:

```
artifacts/
```

- [ ] **Step 2: Run it**

Run: `pwsh scripts/publish.ps1`
Expected: both publishes succeed. The App's folder holds `PowerLedger.exe` and `runtimes\win-x64` only, with no other platforms. The sizes print, both well under the 116 MB a platform-neutral build carries.

- [ ] **Step 3: Commit**

```bash
git add scripts/publish.ps1 .gitignore
git commit -m "Publish the App and the service for win-x64"
```

---

### Task 3: The installer

**Files:**
- Create: `installer/PowerLedger.iss`

Installing runs in this order:
- Setup checks for Windows 10 1809 or later on x64.
- It asks a running App to close (`AppMutex`, the name `SingleInstance` uses), and stops the service when upgrading.
- It fetches and installs the Desktop Runtime when `dotnet\shared\Microsoft.WindowsDesktop.App` has no 10.x version.
- It copies the files.
- It registers the service, or on an upgrade points its image path at the installed executable again, and starts it.
- It offers to open PowerLedger, as the original user.

Uninstalling runs in this order:
- It stops and deletes the service.
- It removes this account's Run entry.
- It asks whether to keep `C:\ProgramData\PowerLedger`.

- [ ] **Step 1: Write the script**

`installer/PowerLedger.iss`
```iss
; PowerLedger's installer (spec §13). Build it with installer\build.ps1, which publishes both programs first.
; Framework-dependent: the .NET 10 Desktop Runtime is downloaded when it is missing, so this stays small.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define Publish "..\artifacts\publish"
#define ServiceName "PowerLedger"
#define RuntimeUrl "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"

[Setup]
AppId={{8E496D40-C77E-4F75-8C93-ED9D1E8BAF20}
AppName=PowerLedger
AppVersion={#AppVersion}
AppVerName=PowerLedger {#AppVersion}
AppPublisher=PowerLedger
DefaultDirName={autopf}\PowerLedger
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
AppMutex=PowerLedger.App
CloseApplications=yes
RestartApplications=no
OutputDir=output
OutputBaseFilename=PowerLedger-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\PowerLedger.exe
UninstallDisplayName=PowerLedger
SetupLogging=yes
; Before a public release: sign the installer and the uninstaller (spec §11), e.g.
; SignTool=signtool sign /fd sha256 /tr http://timestamp.acs.microsoft.com /td sha256 $f

[Files]
Source: "{#Publish}\App\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Publish}\Service\*"; DestDir: "{app}\Service"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\PowerLedger"; Filename: "{app}\PowerLedger.exe"; Comment: "How much power this PC uses, and what it costs"

[Run]
; The first window of a new install is the wizard; the App also turns on starting with Windows for this user.
Filename: "{app}\PowerLedger.exe"; Description: "Open PowerLedger"; Flags: postinstall nowait skipifsilent runasoriginaluser

[Code]
var
  RuntimePage: TDownloadWizardPage;

function RunHidden(const FileName, Params: string): Integer;
var
  Code: Integer;
begin
  if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  Result := Code;
end;

function Sc(const Params: string): Integer;
begin
  Result := RunHidden(ExpandConstant('{sys}\sc.exe'), Params);
end;

function ServiceExists: Boolean;
begin
  Result := Sc('query {#ServiceName}') = 0;
end;

{ net stop waits until the service has stopped, so its files can be replaced; a stopped service is not an error. }
procedure StopService;
begin
  if ServiceExists then
    RunHidden(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}');
end;

function ServiceExecutable: string;
begin
  Result := ExpandConstant('{app}\Service\PowerLedger.Service.exe');
end;

{ The image path is quoted: the App checks that the pipe's server is this executable (Plan D3). }
procedure RegisterService;
var
  Path: string;
begin
  Path := '"\"' + ServiceExecutable + '\""';
  if ServiceExists then
    Sc('config {#ServiceName} binPath= ' + Path + ' start= auto obj= LocalSystem')
  else
    Sc('create {#ServiceName} binPath= ' + Path + ' start= auto obj= LocalSystem DisplayName= "PowerLedger"');
  Sc('description {#ServiceName} "Records how much power this PC uses."');
  Sc('failure {#ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/5000');
  Sc('failureflag {#ServiceName} 1');
  RunHidden(ExpandConstant('{sys}\net.exe'), 'start {#ServiceName}');
end;

{ The Desktop Runtime is present when dotnet's shared folder holds a 10.x version of it. }
function DesktopRuntimeInstalled: Boolean;
var
  Found: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), Found);
  if Result then
    FindClose(Found);
end;

procedure InitializeWizard;
begin
  RuntimePage := CreateDownloadPage('Getting .NET', 'PowerLedger needs the .NET 10 Desktop Runtime, which is being downloaded from Microsoft.', nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Code: Integer;
begin
  Result := True;
  if (CurPageID <> wpReady) or DesktopRuntimeInstalled then
    Exit;
  RuntimePage.Clear;
  RuntimePage.Add('{#RuntimeUrl}', 'windowsdesktop-runtime-win-x64.exe', '');
  RuntimePage.Show;
  try
    try
      RuntimePage.Download;
    except
      if not RuntimePage.AbortedByUser then
        SuppressibleMsgBox('The .NET 10 Desktop Runtime could not be downloaded: ' + GetExceptionMessage + #13#10 +
          'Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and run this setup again.', mbError, MB_OK, IDOK);
      Result := False;
      Exit;
    end;
  finally
    RuntimePage.Hide;
  end;
  if not Exec(ExpandConstant('{tmp}\windowsdesktop-runtime-win-x64.exe'), '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, Code)
     or ((Code <> 0) and (Code <> 3010)) then
  begin
    SuppressibleMsgBox('The .NET 10 Desktop Runtime could not be installed (code ' + IntToStr(Code) + ').', mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

{ Silent installs never reach the Ready page, so they need the runtime already. }
function InitializeSetup: Boolean;
begin
  Result := True;
  if WizardSilent and not DesktopRuntimeInstalled then
  begin
    Log('The .NET 10 Desktop Runtime is missing; a silent install cannot fetch it.');
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): string;
begin
  StopService;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    RegisterService;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopService;
    Sc('delete {#ServiceName}');
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'PowerLedger');
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    Data := ExpandConstant('{commonappdata}\PowerLedger');
    if DirExists(Data) and (SuppressibleMsgBox('Keep your PowerLedger history? It is in ' + Data + '.' + #13#10 +
        'Choose No to delete it.', mbConfirmation, MB_YESNO or MB_DEFBUTTON1, IDYES) = IDNO) then
      DelTree(Data, True, True, True);
  end;
end;
```

- [ ] **Step 2: Read it against Inno Setup 6's documentation**

Check each item against the Inno Setup 6 help:
- `CreateDownloadPage`, `TDownloadWizardPage.Add`, `Download` and `AbortedByUser` exist from 6.1.
- `SuppressibleMsgBox` takes the default answer as its last argument.
- `x64compatible` is the 6.3 spelling. On an older compiler, use `x64`.
- `{commonpf64}` is the 64-bit Program Files, and `{autopf}` is the right Program Files for a 64-bit install.
- `runasoriginaluser` belongs in `[Run]` with `postinstall`.
- `PrepareToInstall` runs before files are copied, and `ssPostInstall` after.
- `sc.exe` needs a space after each `=`.

Expected: nothing to change; if something is, fix it here and note it in "After the final review".

- [ ] **Step 3: Commit**

```bash
git add installer/PowerLedger.iss
git commit -m "Add the installer: runtime, service, shortcut, first window, and an uninstall that asks about history"
```

---

### Task 4: Build the installer, locally and in CI

**Files:**
- Create: `installer/build.ps1`
- Create: `.github/workflows/ci.yml`

`build.ps1` publishes, reads the version from `Directory.Build.props` and compiles the installer with Inno Setup 6. It says where to get Inno Setup when it is missing. The workflow builds, runs the tests outside the Hardware and UI categories (spec §13), and uploads the installer. It then waits in the repository for the owner's remote.

- [ ] **Step 1: Write them**

`installer/build.ps1`
```powershell
<#
.SYNOPSIS
Publishes PowerLedger and compiles its installer into installer\output.

.DESCRIPTION
Needs Inno Setup 6.3 or later (https://jrsoftware.org/isinfo.php), found on PATH or in its default folder.
The version comes from Directory.Build.props, so the installer and the programs always agree.
#>
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Select-Object -First 1
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }

& (Join-Path $root 'scripts\publish.ps1') -Configuration $Configuration

$command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
$iscc = if ($command) { $command.Source } else { Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }
if (-not (Test-Path $iscc)) { throw 'Inno Setup 6 is not installed. Get it from https://jrsoftware.org/isinfo.php and run this again.' }

& $iscc "/DAppVersion=$version" (Join-Path $PSScriptRoot 'PowerLedger.iss')
if ($LASTEXITCODE -ne 0) { throw 'The installer did not compile.' }
Get-ChildItem (Join-Path $PSScriptRoot 'output') -Filter '*.exe' | ForEach-Object { '{0}: {1:N1} MB' -f $_.Name, ($_.Length / 1MB) }
```

`.github/workflows/ci.yml`
```yaml
# Build, test outside the Hardware and UI categories, and make the installer (spec §13).
name: CI

on:
  push:
    branches: [main]
  pull_request:

jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json

      - name: Build
        run: dotnet build -c Release

      - name: Test
        run: dotnet test -c Release --no-build --filter "Category!=Hardware&Category!=UI"

      - name: Install Inno Setup
        run: choco install innosetup --no-progress -y

      - name: Installer
        shell: pwsh
        run: ./installer/build.ps1

      - uses: actions/upload-artifact@v4
        with:
          name: installer
          path: installer/output/*.exe
```

- [ ] **Step 2: Run what can run here**

Run: `pwsh installer/build.ps1`
Expected: the publish succeeds and prints both sizes. The script then stops with "Inno Setup 6 is not installed…", which is correct on this machine.

- [ ] **Step 3: Commit**

```bash
git add installer/build.ps1 .github/workflows/ci.yml
git commit -m "Build the installer locally and in CI"
```

---

### Task 5: Say how, and finish

**Files:**
- Create: `README.md`
- Modify: `docs/superpowers/specs/2026-09-08-powerledger-design.md`, this plan

- [ ] **Step 1: Write the README**

`README.md`
````markdown
# PowerLedger

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

Windows 10 1809 or later, or Windows 11, on x64. The installer fetches the .NET 10 Desktop Runtime when it is missing.

## Build and test

```
dotnet build -c Release
dotnet test -c Release --filter "Category!=Hardware&Category!=UI"
```

Tests tagged `Hardware` read this machine's sensors; tests tagged `UI` draw the window to pictures in
`%TEMP%\powerledger-renders`. Run them with `dotnet test -c Release` on a real machine.

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
`scripts\dev-service.ps1` installs the service for real, from an elevated PowerShell.

## Make the installer

```
pwsh installer\build.ps1
```

It publishes both programs for win-x64 into `artifacts\publish` and compiles `installer\PowerLedger.iss` with
Inno Setup 6.3 or later into `installer\output`.

## Documents

- `docs/superpowers/specs/2026-09-08-powerledger-design.md`: the design.
- `docs/superpowers/plans/`: how it was built, plan by plan.
````

- [ ] **Step 2: Update the spec**

In §13, after the framework-dependent line, add the install layout:
- The App is in `Program Files\PowerLedger` and the service in its `Service` folder.
- There is a Start menu shortcut.
- An upgrade stops the service before copying and starts it again after.
- The App turns on its Run entry on its first run, as the user.
- An uninstall asks about the history and keeps it when silent.
- A silent install needs the runtime already present.

- [ ] **Step 3: Verify, and commit**

Run: `dotnet build -c Release` and the tests with the CI filter.
Expected: 0 warnings, and App 225 tests under the CI filter.

```bash
git add README.md docs
git commit -m "Complete Plan E: the installer, its build, and a README"
```

**Before the first release, on a clean Windows 10 or 11 machine, check:**

1. **A fresh install without .NET 10.** The runtime downloads and installs, the service is running (`sc query PowerLedger`), and its image path is the quoted `...\Service\PowerLedger.Service.exe`. The App opens on the wizard, and a tariff saved in the wizard appears in Settings, which shows the check passed. The Run entry exists for the user who installed.
2. **An upgrade over a running install.** The App closes, the service stops and starts, and the history is untouched.
3. **Uninstall.** "Keep" leaves `C:\ProgramData\PowerLedger`; "No" removes it. The service is gone, and so is the Run entry.
4. **A standard user who types an administrator's password.** The Run entry lands in the standard user's hive, because the App wrote it.

---

## Self-review against the spec

| Spec §13 | Where |
|---|---|
| Windows 10 1809 or later, Windows 11, x64 only | Task 3 (`MinVersion`, `ArchitecturesAllowed`) |
| Framework-dependent; the runtime installed if missing; about 15 MB | Tasks 2, 3 |
| The service registered with recovery options | Task 3 |
| The tray App in HKCU Run, set by the installer | Tasks 1, 3 (the installer opens the App, which sets it as the user) |
| The first-run wizard after installing | Task 3 |
| No driver | nothing to install |
| Uninstall stops the service and asks whether to keep the database | Task 3 |
| CI: build, tests outside Hardware and UI, installer artifact | Task 4 |
| Releases on GitHub with a winget manifest after the first stable build | left for then: the owner has no repository yet |
