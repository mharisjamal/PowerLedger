# PowerLedger.Service

The Windows service that records how much power the PC uses. It samples once a second, stores readings in
`C:\ProgramData\PowerLedger\power.db`, and serves live readings, status and settings on `\\.\pipe\PowerLedger.v1`.

## Running it

- **As a service:** `scripts/dev-service.ps1 install` from an elevated PowerShell. It starts with Windows as
  LocalSystem and restarts itself after a failure.
- **From the console, for development:** `dotnet run --project src/PowerLedger.Service -c Release -- --data <folder> --pipe PowerLedger.dev`.
  `--data` keeps the files out of ProgramData and `--pipe` keeps clear of an installed service. Ctrl+C stops it
  cleanly. The console run reads idle time from its own session and assumes the session is unlocked.

`scripts/pipe-status.ps1 [-Pipe PowerLedger.dev]` prints the service's status.

## Files

| Path | What |
|---|---|
| `power.db` | readings, rollups, sessions, tariffs, calibration, settings |
| `power.db.bak` | a copy taken before each schema upgrade |
| `power.corrupt-*.db`, `power.untrusted-*.db` | a damaged database, or one another account owned, set aside at start |
| `logs\service-*.log` | seven days of logs, 5 MB a file; warnings and errors also go to the Application event log |

Only SYSTEM and administrators can write the folder; users can read it. The service refuses a folder another
account owns.
