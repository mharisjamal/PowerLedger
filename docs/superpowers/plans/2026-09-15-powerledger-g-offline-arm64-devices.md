# PowerLedger Plan G — One offline installer for every Windows PC, and the device fixes

**Goal:** Answer the owner's two questions with evidence: is the package complete for a brand-new PC, and does it work on
the different kinds of Windows devices? Then act on the answers: the owner chose an offline-only installer, left the Arm
approach to us ("do the best one"), and asked for every device fix the audit found.

**How it ran:** four subagents in parallel with disjoint files. Packaging worked in the main checkout, and the window fit,
the AMD/Intel graphics power and the accuracy fixes each worked in a git worktree, cherry-picked afterwards onto
`plan-g/offline-arm64-devices`. Then a clean-PC run in Windows Sandbox, and an elevated run on the development laptop.

**Git rule (from the owner):** local commits only. No remote, no push.

---

## What the audit found

- **Bundled?** Everything was inside the installer except the .NET 10 Desktop Runtime, which setup downloaded when
  missing. Reading the import tables of every native file shipped (SQLite, QuestPDF's renderer and PDF engine, both
  program launchers) showed they need only what Windows 10 and 11 contain: no Visual C++ Redistributable.
- **Devices.**
  - x64 Windows 10 1809+ and 11 worked.
  - Windows 11 on Arm was **broken**: the installer admitted it, but its .NET check looked in the wrong folder, so the
    install failed after downloading the runtime.
  - 32-bit Windows and Windows 10 on Arm were refused. S mode and Windows 11 SE can't install desktop apps at all.
  - The service kept recording on every kind of hardware, but:
    - AMD and Intel discrete graphics counted as 0 W;
    - a UPS could make a desktop look like a laptop and have its drain recorded as the PC's own;
    - an all-in-one's screen wasn't counted;
    - an energy meter without a package rail made the App claim a measured processor.
  - The window asked for 900 units of height where a 1080p laptop at 125 % has 816, so its bottom was off-screen.

## What changed

1. **One offline installer for x64 and Arm64.**
   - Both programs are published self-contained for win-x64 and win-arm64, so each carries the .NET 10 runtime.
   - Setup downloads nothing, the PC needs no .NET, and Arm64 PCs run native Arm64 code rather than emulated x64. Native
     matters for a program that samples power every second.
   - The installer picks the build that matches the PC (`ArchitecturesAllowed=x64os or arm64`).
   - It is 92 MB. It uses LZMA2 at ultra64 with a 256 MB dictionary, so the service's copy of the runtime compresses
     against the App's. The x64 build alone would be 56 MB.
   - A separate compression chunk per build was tried: 104 MB. It was dropped, since x64 PCs never unpack the Arm64 part
     anyway.
   - Setup states the real disk space: `build.ps1` passes the bigger build's size, because Inno can't count files it
     picks per architecture, and the destination page had said 4.3 MB.
   - Inno Setup 7 is required; Inno 6's 32-bit compiler can't use the dictionary.
   - The runtime download code, and its test builds, are gone.
2. **The window fits the screen.** Before it first shows, the window fits the work area of the monitor it opens on, and
   its minimum size shrinks with it; every page scrolls. On the laptop it now opens 1000 px tall in a 1020 px work
   area, where before it was 1102 px.
3. **AMD and Intel discrete graphics.**
   - DXGI finds the card by vendor, a discrete model name and at least 1 GB of its own memory. Integrated graphics are
     excluded, because their power is already inside the CPU package.
   - Windows' `GPU Engine` counters give its load: the busiest engine, read every 5 s, at about 1 % of the service's CPU
     budget.
   - The existing load model turns that into watts with the card's rated power. The TDP table gained the common RX and
     Arc cards.
4. **Accuracy.**
   - A known desktop enclosure outranks a battery, so an unflagged UPS isn't the PC's own.
   - An all-in-one's built-in screen counts, with its own size class.
   - The energy meter counts as a processor sensor only with a package rail.
   - The wizard, Now and Settings no longer claim a desktop's readings come from a battery.
5. **The installer test** checks that the installed build matches the PC's architecture and carries its own runtime. It
   gained `InstallWizard`, a click-through install as a new user would do it, and finds Inno 7's wizard buttons, which
   drop the arrows ("Next", not "Next >"). CI gained a `windows-11-arm` job that installs the Arm64 build.

## Results

- **Build:** 0 warnings.
- **Tests:** 811, all passing, Hardware and UI included.

  | Project | Tests |
  |---|---|
  | Core | 117 |
  | Storage | 49 |
  | Service | 129 |
  | Sensors | 260 |
  | App | 256 |

- **Clean PC (Windows Sandbox: a fresh Windows 11 with no .NET and no Visual C++ runtime): 66 of 66 installer checks
  passed.**
  - The new-user wizard install: Next, Install, Finish, and the App opened on a machine without .NET.
  - The service's registration, its data folder and access rules, the pipe, readings stored, and recovery after its
    process was killed.
  - A clean stop keeping the write-ahead log.
  - An upgrade and an uninstall with the App open: it closed each time and the history was kept.
  - A reinstall over the kept history.
  - The uninstall answering No: the history deleted.
  - Inside it, the App's first-run wizard saved $0.17/kWh through the real service, and Settings read it back from the
    database.
  - With no energy meter rails and no discrete GPU in the VM, the service fell back to estimates (33.5 W, Estimated)
    and kept recording.
- **The development laptop, elevated: 73 of 73 checks passed.** This included the x64 build on x64 Windows and its own
  runtime present.
- **Unelevated on the laptop:**
  - The `Installed` tests passed 3 of 3: the App trusts the service, a real write goes through, and history reads under
    the folder's ACL.
  - The App's wizard saved the tariff.
  - The window sits inside the work area.
  - The Run value and `ui.json` were written, and no `Documents\PowerLedger` appeared.
- **Reviews:** two whole-branch reviews raised one finding each. Both were false positives:
  - The reviewer read `LZMADictionarySize` as bytes, but Inno takes kilobytes. The measured sizes confirm it.
  - The reviewer read a PowerShell comma-then-pipe as filtering one path. The comma binds first, and the check it
    guards passed.

## Left for the owner

- **An Arm64 PC.** The Arm64 build was published and its files checked as Arm64, but it has not run on Arm hardware.
  The `windows-11-arm` CI job will run it once the repository exists.
- **Real AMD and Intel cards.** The new graphics source was tested against fakes and against this laptop, which it
  correctly leaves to NVIDIA.
- **Windows 10 Home and Pro.** PowerLedger installs and runs there, but Microsoft supports .NET 10 on Windows 10 only
  in the Enterprise LTSC editions.
- **Signing.** Until the installer is signed, SmartScreen warns on first run, and the "setup started by a normal user"
  UAC prompt shows "Unknown publisher". That prompt was never approved here, so that path is still unrun. Sandbox ran
  the same wizard elevated.
- **Updates.** .NET's security fixes now reach users only through PowerLedger updates, since the runtime is inside the
  app.
