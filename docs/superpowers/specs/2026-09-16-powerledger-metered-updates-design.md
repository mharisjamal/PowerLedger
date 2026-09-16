# Updates that respect mobile data — design

Status: approved by the owner on 2026-09-16, after the in-app updater shipped in v0.2.0
(`2026-09-16-powerledger-updates-design.md`). Changes the main spec's §9, §11 and §13.

## Goal

An update should never spend someone's mobile data without being asked, and should be smaller when it does download.
PowerLedger is used on laptops on phone hotspots; today an update quietly pulls 96 MB whatever the connection.

## Owner's choices

| Question | Choice |
|---|---|
| On a metered connection | Wait, but offer a Download button on the card, so it can still be taken on purpose |
| Download size | Per-architecture installers for updates (~56 MB); the universal one stays for people downloading by hand |

## How it works

1. **The check still runs** on any connection: it is a couple of kilobytes of JSON.
2. **Metered connections.** `INetworkCostManager`, the network list's COM interface, gives the current connection's
   cost. It is there on Windows 8 and later but not on Windows Server, so a machine without it counts as unmetered.
   Anything but "unrestricted" counts as metered: fixed allowances, variable rates, roaming, over or near the data
   limit. A cost that can't be read counts as unrestricted, which is today's behaviour.
3. **Found while metered.** No download. The card says "PowerLedger X.Y.Z is available · 56 MB" with **Download**, and
   Settings says it is waiting for a connection that isn't metered. Download takes it there and then, over the metered
   connection, because the user asked. The tray still announces each version once.
4. **Off the metered connection.** The next check (an hour, or sooner at a start) downloads it quietly, and the card
   becomes the usual "ready · Restart to update".
5. **The right installer.** Each release carries three installers: the universal one, plus `-x64` and `-arm64` builds of
   about 56 MB. An update takes the one matching this PC and falls back to the universal one when a release has no
   per-architecture build, so old and new versions keep working together.

## What each installer is

`installer\build.ps1 -For both,x64,arm64` compiles one installer per value. A per-architecture build sets
`ArchitecturesAllowed` to that architecture alone, packs only that build's files, states that build's size as the disk
space setup needs, and is named `PowerLedger-X.Y.Z-setup-x64.exe` or `-arm64.exe`. All three share the installer's AppId,
so any of them upgrades any other. `scripts\release.ps1` builds and uploads all three.

## Trust, unchanged

A release still counts only when its tag is `vX.Y.Z` and the installer it offers is under this repository's releases with
the size and SHA-256 GitHub lists. The only change is which asset name is looked for first:
`PowerLedger-X.Y.Z-setup-<architecture>.exe`, then `PowerLedger-X.Y.Z-setup.exe`.

## Components

- `src/PowerLedger.App/Updates/ConnectionCost.cs` — `IConnectionCost` with `bool Metered { get; }`, and the
  `INetworkCostManager` implementation. One place for the interop, so the updater stays testable.
- `Updater` — gains the `Available` stage (found, not downloaded) and asks the cost before downloading.
- `GitHubReleaseFeed` — chooses the asset for this PC's architecture, falling back to the universal one.
- `installer/PowerLedger.iss`, `installer/build.ps1`, `scripts/release.ps1` — the per-architecture builds.

## The card

| Stage | Title | Detail | Button |
|---|---|---|---|
| Available | PowerLedger X.Y.Z is available | 56 MB · waiting for a connection that isn't metered | Download |
| Ready | PowerLedger X.Y.Z is ready | — | Restart to update |

The other stages (Installing, Failed, Updated) don't change.

## Testing

- Unit tests: the metered gate with a fake cost (metered → Available, no download; Download → downloads; unrestricted →
  quiet download as now); asset choice for x64, Arm64 and a release without per-architecture builds; the new card state.
- A `Category=Hardware` test reads this machine's real connection cost, so the interop is exercised somewhere.
- The installer test installs a per-architecture build, on x64 and on the Arm64 runner.
- Windows Sandbox end to end again, with the stand-in feed offering per-architecture assets.
- Metered behaviour on a real hotspot is the owner's one manual check: Sandbox has no network at all.
