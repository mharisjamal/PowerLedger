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
Until its first-run wizard is finished, the App turns on starting with Windows for whichever `PowerLedger.exe` was
started, so finish the wizard and untick it in Settings, or delete
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\PowerLedger`.
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
