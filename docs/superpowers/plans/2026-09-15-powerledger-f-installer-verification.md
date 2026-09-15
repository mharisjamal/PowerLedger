# PowerLedger Plan F — Compile the installer and test everything

**Goal:** Compile the installer Plan E wrote, then test the whole product as installed, not only as built: the unit and
integration tests, the installer's own paths (install, upgrade, uninstall either way, the runtime download, its
failures), and the App against the real LocalSystem service it will meet on a user's machine.

**How it ran:** the owner asked for speed and many subagents. Code changes were made test-first in separate worktrees and
cherry-picked onto `plan-f/installer-verification`; the installer script, the test harness and the docs were written in
parallel in the main checkout by agents that owned disjoint files. Two elevated test rounds ran on the development laptop
(Windows 11 Pro 26200, i7-1165G7, .NET SDK 10.0.401 with the Desktop Runtime 10.0.12), each behind one UAC prompt the
owner approved, and the machine was left as it was found.

**Git rule (from the owner):** local commits only. No remote, no push.

---

## What it found

Three bugs only an installed PowerLedger could show. Each was reproduced first, then fixed test-first.

1. **The App could never trust the installed service.** The server check (Plan D3) opened the process serving the pipe
   with `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` to read its path. An unelevated process is refused that on a
   LocalSystem process: tried on eight running services, all answered `ERROR_ACCESS_DENIED`, and on `\\.\pipe\eventlog`
   the old check said "couldn't be identified". Every write from the wizard and Settings would have been refused. Earlier
   live runs used `--pipe`, which trusts any server, so it never showed.
   **Fix:** the pipe's server must be the process the Service Control Manager runs for the service named PowerLedger
   (`GetNamedPipeServerProcessId` against `QueryServiceStatusEx`), which any signed-in user may ask. Only an administrator
   can register that service, and a process squatting the pipe's name can't have its process id.
2. **The installer couldn't close the running App.** The tray App runs all the time, and Inno's `AppMutex` check stops
   Setup and Uninstall at their start: a "close it" box, or, silent with `/SUPPRESSMSGBOXES`, an abort (read in Inno
   7.1.0's source). The uninstaller has no Restart Manager fallback.
   **Fix:** the App listens on the named event `Local\PowerLedger.App.Exit` and exits as its tray's Exit does; Setup,
   once its checks pass, and Uninstall, once the user confirms, set it and wait up to 10 s. `AppMutex` stays as the
   fallback.
3. **History vanished while the service was stopped.** SQLite deletes `power.db-wal` and `power.db-shm` when the last
   connection closes, and a reader that may only read the folder (the App, as a member of Users) then can't open a WAL
   database: `SQLite Error 14: 'unable to open database file'`.
   **Fix:** the service sets `SQLITE_FCNTL_PERSIST_WAL` on its read-write connections, SQLite's documented answer for
   readers without write access to the folder, so the files stay after a clean stop.

Smaller things fixed on the way:

- The installer logs every `sc.exe` and `net.exe` exit code and the runtime installer's, says so when registering the
  service fails (1072 means a Services window holds it), checks the runtime's folder after installing it, removes the
  service's event-log source on uninstall, and keeps an uninstall log.
- `AppPreferencesTests` left `HKCU\Software\PowerLedgerTests` behind; it now uses one key per test and deletes it whole.
- The service tests' waits allowed 5 s per tick; with every test assembly running at once a tick stalled past that, so
  they allow 15 s.
- The spec's installer lines that predated Plan E (§13, §14, §17) now say what the installer does.

## What was added

| File | What |
|---|---|
| `installer/get-inno-setup.ps1` | installs the pinned Inno Setup 7.1.0 (or 6.7.3) for this user, refused unless its SHA-256 matches and Pyrsys B.V. signed it |
| `installer/build.ps1` | finds Inno Setup 7 or 6 on its own or takes `-Iscc`; `-SkipPublish`; `-TestVariants` builds an upgrade, a build that acts as if the runtime were missing, and one whose download fails |
| `installer/test-installer.ps1` | the installer test: silent steps CI can run, and window-driven ones (UI Automation) on a desktop |
| `tests/PowerLedger.App.Tests/InstalledTests.cs` | category `Installed`: run unelevated after installing, against the real service |
| `.github/workflows/ci.yml` | the pinned Inno Setup, the test builds, and the installer test's silent steps |

## Results

**Build:** `dotnet build -c Release`, 0 warnings, 0 errors. **Vulnerable packages:** none (`dotnet list package
--vulnerable --include-transitive`).

**Tests** (`dotnet test -c Release --filter "Category!=Installed"`, Hardware and UI included), all passing:

| Project | Tests |
|---|---|
| Core | 110 |
| Storage | 49 |
| Service | 127 |
| Sensors | 99 |
| App | 228 |

With CI's filter (`Category!=Hardware&Category!=UI&Category!=Installed`): Core 110, Storage 49, Service 125, Sensors 94,
App 226, all passing.

**The installer:** compiles with Inno Setup 7.1.0 and 6.7.3, 0 warnings; `PowerLedger-0.1.0-setup.exe` is 11.7 MB
(App 18.7 MB and service 6.4 MB published).

**Installed, elevated (`test-installer.ps1`, second round, all fixes in):** 89 checks, 88 passed; the one left is below.

- A silent install refused without the runtime (exit 1, logged). Installed: files, the Start menu shortcut, the
  uninstall entry.
- The service: the quoted image path, automatic start as LocalSystem, the description, three restarts 5 s apart counted
  over a day, event 7045; running within 30 s.
- The data folder: owned by Administrators, rules protected, exactly SYSTEM and Administrators full control and Users
  read. The database and the log written; the pipe answering with this version and no database notice.
- Recovery: the service process killed, running again in a new process, event 7031.
- A clean stop keeps `power.db-wal` and `power.db-shm` (bug 3).
- A silent upgrade to 0.1.1 **with the App running**: the App closed on its exit event, the version shown, every reading
  kept (bug 2).
- A silent uninstall keeping the history: service, program folder, shortcut, uninstall entry, Run value and event-log
  source all gone; `power.db` kept; a reinstall over it kept every reading and set nothing aside. Round 1 checked the
  same uninstall with the App running: the App closed.
- The failed download (a 404 URL): "could not be downloaded: Download failed: 404 Not Found", setup stays on Ready,
  Cancel then Yes exits with 2, the installed version untouched.
- The interactive uninstall answering No to keeping the history: the history deleted.
- The runtime download for real: the wizard's "Getting .NET" page fetched the 60 MB runtime from
  `aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe` (10.0.12 on builds.dotnet.microsoft.com), its installer
  returned 0, and the install finished.

**Installed, unelevated (the App as the signed-in user meets it):**

- The `Installed` tests: 3 of 3. The App trusts the installed service (bug 1), a real settings write goes through, and
  history reads under the folder's ACL.
- With the service stopped and no App ever connected, history still reads (bug 3; it failed before the fix).
- The App run unelevated: its wizard saved 0.17 USD and the machine through the real service with no refusal; Settings
  showed "From 15 Sep 2026 · $0.17 / kWh" from the database; Now showed today's average, peak and cost from it; the Run
  value became `"C:\Program Files\PowerLedger\PowerLedger.exe" --tray`; `ui.json` recorded the finished wizard; no
  `Documents\PowerLedger` appeared.

## Left for the owner

- **A setup started by a normal user, ending with "Open PowerLedger" ticked.** It should open the App unelevated
  (`runasoriginaluser`). Its UAC prompt shows "Unknown publisher" until the installer is signed, and it wasn't approved in
  either round, so this one check didn't run. The App itself was checked unelevated against the installed service. To
  check: run `installer\output\PowerLedger-0.1.0-setup.exe`, finish with the box ticked, and see the wizard open.
- A clean Windows without .NET (Windows Sandbox isn't installed here). The download path above ran for real, but the
  runtime installer had nothing to do.
- A standard user who types an administrator's password (it needs a second account), and a reboot (checked instead:
  automatic start, the Run value).
- Signing the installer (SmartScreen) and choosing the licence (spec §16).
- ARM64: `ArchitecturesAllowed=x64compatible` lets ARM64 install, where the x64 runtime lives in `dotnet\x64` and the
  runtime check would look in the wrong folder. Either allow `x64os` only or look there too.
- Known limits, documented: a silent upgrade leaves the App closed until the next sign-in; an App in another user's
  session isn't asked to exit (Setup's Restart Manager still sees its files in use; Uninstall would remove them after a
  restart).

## Running it again

```
pwsh installer\get-inno-setup.ps1
pwsh installer\build.ps1 -TestVariants
```

Then, from an elevated PowerShell, `pwsh installer\test-installer.ps1` for the silent steps (add `-Step` with BadUrl,
UninstallDelete, RuntimeWizard or DriveWizard for the window-driven ones), and, installed, from an unelevated terminal:
`dotnet test -c Release --filter Category=Installed`.
