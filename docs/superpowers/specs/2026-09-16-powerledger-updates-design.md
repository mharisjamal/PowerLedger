# PowerLedger updates — design

Status: approved by the owner on 2026-09-16. Builds on the main spec (`2026-09-08-powerledger-design.md`), whose §9, §11,
§12, §13 and §14 change with it.

## Goal

Somebody who installed PowerLedger gets new versions without looking for them. Like the Claude desktop app, the update
downloads quietly; a small card at the bottom-left of the window, one Windows notification per version and an item in the
tray menu then offer "Restart to update". One click, Windows' permission prompt, and PowerLedger comes back updated.

## Owner's choices

| Question | Choice |
|---|---|
| Flow | Download quietly, then ask (not "ask, then download") |
| Where | The card in the window, plus one tray notification per version and a tray menu item |
| Trust | GitHub alone: HTTPS, this repository's releases, and the SHA-256 GitHub lists for the installer; no signing key of our own |
| First release | Publish as v0.2.0 once built and tested; 0.1.0 has no updater, so it is updated by hand once |

## Approach

PowerLedger's own small updater on top of the installer it already has, whose silent upgrade is tested with the App
running. Rejected: NetSparkle (its own dialogs and XML feed, which we would restyle and maintain) and Velopack or Squirrel
(they replace Inno Setup with per-user installs and cannot install the LocalSystem service).

## How it works

1. **Check.** One minute after the App starts and every six hours after, while "Download new versions quietly" is ticked
   (the default), the App asks `https://api.github.com/repos/mharisjamal/PowerLedger/releases/latest`. GitHub leaves out
   drafts and pre-releases, so a pre-release can be tried without anyone being offered it. Settings also has "Check now",
   which works with the box unticked.
2. **Trust.** A release counts only when its tag is `vX.Y.Z`, it is newer than the running version, and it carries
   `PowerLedger-X.Y.Z-setup.exe` under `https://github.com/mharisjamal/PowerLedger/releases/download/`, with a plausible
   size and GitHub's `sha256:` digest. Anything missing or odd is refused, never guessed at.
3. **Download.** The installer streams into `%LOCALAPPDATA%\PowerLedger\Updates\PowerLedger-X.Y.Z-setup.exe.partial`,
   hashed as it arrives; it is kept, renamed without `.partial`, only when its size and SHA-256 are GitHub's. A download
   that stalls for a minute, or doesn't match, is thrown away and tried again at the next check. A complete copy already
   there is checked and reused, so a restart doesn't download again. Each start removes installers for the running
   version or older and unfinished downloads.
4. **Offer.** When the download is ready: the card ("PowerLedger X.Y.Z is ready", "Restart to update", "What's new", ✕),
   one balloon from the tray per version (remembered in `ui.json`), and "Restart to update to X.Y.Z" in the tray menu.
   ✕ hides the card until the App next starts; the tray item stays.
5. **Install.** "Restart to update" hashes the installer once more while holding it open against writes, then starts it
   with `/SILENT /NORESTART /UPDATE=1 /LOG="…\PowerLedger-X.Y.Z-setup.log"`. Windows asks for permission (it names an
   unknown publisher until the installer is signed). Setup then closes the App through the exit event it already uses,
   stops the service, replaces the files, registers and starts the service, and — because of `/UPDATE=1` — opens the App
   again as the user who started setup, not as administrator.
6. **After.** The App remembers the version that last ran; the first start of a newer one shows "Updated to X.Y.Z" with
   "What's new" (that version's release page). If the permission prompt is declined or setup ends without installing,
   the App is still running and its card says so, with "Try again".

## The card

It sits in the rail above the service status, in the palette's raised surface with a strong hairline, and takes the rail's
width. Nothing on it animates (spec §9's motion rule). States:

| Stage | Title | Detail | Button | Other |
|---|---|---|---|---|
| Ready | PowerLedger X.Y.Z is ready | — | Restart to update | What's new, ✕ |
| Installing | Installing X.Y.Z… | Windows asks for permission | — | — |
| Failed | The update didn't install | why | Try again | ✕ |
| Updated | Updated to X.Y.Z | — | — | What's new, ✕ |

While checking or downloading there is no card: the quiet part stays quiet, and Settings says what is happening.

## Settings

Preferences gets an "Updates" row: the tick box "Download new versions quietly, then ask", "Check now", and a line that
says where things stand: checking, up to date as of a time, downloading with a percentage, ready, or what went wrong.

## Privacy

The check sends nothing about the user or the PC. GitHub sees the request: the IP address, and `PowerLedger/X.Y.Z` as the
user agent. The main spec's "no telemetry" still holds. Unticking the box stops the checks.

## Components

All in `src/PowerLedger.App/Updates/`, each testable alone:

- `Release` — a release as the App trusts it: version, page, installer address, file name, size, SHA-256.
- `GitHubReleaseFeed` (`IReleaseFeed`) — asks GitHub and turns its answer into a `Release`, or refuses it. Errors reach
  the user as `UpdateException` messages.
- `UpdateDownloader` (`IUpdateDownloader`) — the checked download and the cleanup.
- `SetupRunner` (`ISetupRunner`) — the last check and the start of setup.
- `Updater` — the schedule and the stages; the view model of the card and the Settings row.
- `UpdateHttp` — one `HttpClient` for the App's life, with the user agent and the system proxy.

Around them: `UiPreferences` gains `CheckForUpdates`, `AnnouncedVersion` and `LastVersion`; `AppOptions` gains
`--update-feed <url>` so a test can stand in for GitHub with a feed served on the same machine (nothing else is accepted,
since whatever can change the App's command line could otherwise offer an installer GitHub doesn't list); the tray gains the menu item
and a notification that opens the window; `PowerLedger.iss` gains the `/UPDATE=1` relaunch.

## Releasing

`scripts/release.ps1` publishes the version in `Directory.Build.props`: it checks that `main` is clean and pushed, builds
the installer, creates the GitHub release with its notes and the installer, and checks that GitHub's digest is the local
file's. Every installed copy then finds it within six hours.

## Testing

- Unit tests: the feed's parsing and refusals and its HTTP answers; the download's checks, stall and cleanup; setup's
  arguments and last check; the updater's stages, schedule, announcements and "Updated" card; the new preferences and
  switch.
- The installer test's upgrade runs with `/UPDATE=1` and checks that the App opens again; CI runs it on x64 and Arm64.
- End to end in Windows Sandbox: install X.Y.Z, serve a fake X.Y.Z+1 release from the sandbox itself, let the App find
  and download it, press "Restart to update", and check that the service and the App come back as X.Y.Z+1 with the
  "Updated" card.
- Not covered here: the permission prompt of an unelevated user (the Sandbox account is an administrator); the owner can
  try it on their PC with the next real release.
