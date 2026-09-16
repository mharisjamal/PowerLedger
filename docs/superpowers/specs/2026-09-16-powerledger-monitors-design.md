# Monitors that know themselves — design

Status: built in Plan J (`docs/superpowers/plans/2026-09-16-powerledger-j-monitors.md`), and corrected to match what was
built, the decisions made at merge and the fixes that followed two reviews. Follows the research of 2026-09-16 (ENERGY
STAR and EPREL registries, measured power-versus-brightness data). Changes the main spec's §3 to §6, §8, §9, §11, §12,
§14 and §16.

## Goal

Nobody should type how many monitors they have or what they draw. PowerLedger reads which monitors are attached, works
out what each one uses, and only asks when it genuinely doesn't know. The numbers must be honest: every monitor figure
says where it came from.

## What was wrong before

- The wizard asked for a count of external monitors and a watts-per-monitor figure, even on a laptop with one screen.
- The default was **25 W per monitor**. The median certified monitor draws **14.3 W**; a 24" 1080p about **12.9 W**. The
  default overstated a typical monitor by about 75%, and was too high for 86% of the models in the ENERGY STAR list.
- The sleep default, 0.5 W, is the EU's legal ceiling. The certified median is **0.2 W**.

## Owner's choices

| Question | Choice |
|---|---|
| Read each monitor's brightness over the display cable | Yes, carefully: read-only, capability-gated, cached |
| A monitor that isn't in the table | Ask during setup, with the estimate filled in |
| The wizard on a laptop with no external screen | Skip the monitor question entirely |

## Decided at merge

1. A figure the user types for a monitor is used exactly as typed, never scaled by brightness.
2. A monitor's choice is saved only when it says what the service wouldn't assume of a monitor without one: a plug other
   than the one guessed, a count other than the default, or a typed figure. Choices for monitors not attached now are
   kept, most recently seen first, within the limit of 16.
3. When the form lists monitors, in the wizard or in Settings, saving clears the old count fields, so nothing from
   before monitors were detected is carried over after it.
4. A monitor has a plug of its own or runs off the PC, as a portable monitor on a laptop's USB-C port does. On a laptop,
   a monitor of 17.3" or less is taken to run off it until the user ticks **has its own plug**. A monitor that runs off
   the PC always counts, and what it draws is never added on top of a reading that already holds it.
5. Settings from before monitors were detected that left monitors out leave out every monitor with a plug of its own
   that the user hasn't chosen for, one attached later included.
6. A family written with placeholders, or a name EDID cut short, matches only a listing at the monitor's resolution,
   when the monitor gives one; an exact name or a series word keeps its listing whatever the resolution. A maker code
   without a brand matches exact names only.
7. A read in which WMI doesn't say which monitors are attached leaves the monitors as they were, and a sensor set
   abandoned because a read hung can't overwrite what its replacement found. A monitor class WMI refuses as having no
   instances, as it does once the last monitor is unplugged, is an answer: no monitors.
8. The reading's quality label keeps describing the PC's own reading; the labels beside it say how the monitors in the
   reading were figured.
9. A monitor that fails a brightness request before it has given a brightness this session is left alone until a display
   change or a resume. One that has given a brightness is left alone only for a while: it is asked again at the next
   read, and each failure in a row doubles the wait, up to an hour.
10. A save never undoes what the service carried over from old settings: when the settings the form loaded are behind
    the service's, the save reads them again first and takes from them what the form doesn't show. A save that changes
    the chassis says each listed monitor's plug.

## Where the numbers come from

**The ENERGY STAR Certified Displays list** (`data.energystar.gov`, dataset `qbg3-d468`): monitors with measured
on-mode, sleep and off watts, screen size, resolution and panel type. A work of the US Government, in the public domain,
published under the EPA Data License; no key, no obligation to stay fresh. `assets/monitors/make-monitor-table.ps1`
distils it into `assets/monitors/energy-star-monitors.csv`. It keeps monitors only, not signage displays, and drops one
without a brand, a model, a size, a resolution or on-mode watts, or whose on-mode watts are under 1 W or over three
times the median for its size. The table holds **1,580 monitors**, one row each: brand, model number and name, the
other identifiers it is listed under, size, resolution, panel, on, sleep and off watts, maximum luminance, HDR tier and
certification date. It is committed and embedded in `PowerLedger.Sensors`, so nothing is fetched at runtime;
`assets/monitors/README.md` says how it is cleaned and refreshed.

The EU's EPREL registry was considered and rejected: its terms oblige anyone storing the data locally to keep it
current, which an offline installer cannot promise, and its figures run about **1.5× higher** than ENERGY STAR's for the
same monitor because the test differs. Mixing the two would make two identical monitors report different power.

Attribution, in About and the README: the figures come from the US EPA's ENERGY STAR Certified Displays list, in the
public domain; PowerLedger is not certified by, endorsed by or affiliated with ENERGY STAR or the EPA, and doesn't use
the ENERGY STAR mark. About says it in one line: "Monitor figures come from the ENERGY STAR Certified Displays list (US
EPA, public domain). PowerLedger isn't endorsed by ENERGY STAR."

## How a monitor's power is worked out

1. **Which monitors.** The service reads four WMI classes with the display source's own query, on its sensor thread: at
   a sensor set's first tick and then once a minute. `WmiMonitorID` gives each display's maker, product code, serial
   number and name; `WmiMonitorConnectionParams` its connection; `WmiMonitorBasicDisplayParams` whether it is active and
   its physical size; `WmiMonitorListedSupportedSourceModes` its preferred mode, which is the native resolution. They
   are joined by device instance. Internal panels are told apart by their connection and keep their existing treatment
   (the panel is part of the laptop's own draw), and a monitor Windows lists but isn't showing anything on is left out.
   EDID gives the size in whole centimetres, so the diagonal is snapped to the nearest common panel size within half an
   inch; a size under 10" is an aspect ratio or nonsense and counts as unknown. Once no display is left, as when a
   desktop's only monitor is unplugged, WMI refuses the monitor classes with "Not supported" rather than list none, and
   a class that isn't registered gives "Invalid class". Both mean the class has no instances, which is an answer
   (`Wmi.MeansNoInstances`): the display query still answers, and the read finds no monitors, so the board is emptied at
   that query, within a minute, and a desktop's last monitor stops counting. When `WmiMonitorID`,
   `WmiMonitorConnectionParams` or `WmiMonitorBasicDisplayParams` fails in any other way, a timeout, access denied or a
   provider failure among them, the read gives no answer, because without any one of them an attached monitor would look
   unplugged, and nothing is handed over. The modes give only the resolution, and some drivers fail to answer for them
   now and then: a monitor a read gives no resolution, because the class failed, had no instances or left it out, keeps
   the one last read for its device instance while the service runs, so its figure holds, and one never given a
   resolution stays at 0 by 0. A monitor is keyed by maker, product code and serial number, or by its device instance
   when the serial number has fewer than four characters or is one character repeated, as an empty one, "0", "0000" and
   "1111111" are: a monitor saying it has none, or one its maker gives every unit. Once two attached monitors are found
   sharing a serial key, monitors with that key are keyed by their instances for as long as the service runs, so a twin
   left on its own when the other is unplugged keeps its key, and the choice saved under it. The service's monitor board
   is told only when the monitors change, and works out a figure only for a monitor that is new or changed. Each sensor
   set hands its monitors over with a token that is cancelled the moment the set is abandoned because a read hung, or
   thrown away after a resume, before a replacement is built; the board checks the token under the lock it keeps the
   monitors under, so a set that finishes a stuck read late can't replace what its replacement found.
2. **Recognised model.** EDID's maker code gives the brand the list uses (`MonitorMakers`): the codes of the PNP ID
   registry, and a few that a maker's monitors report though the registry doesn't give them to that maker, as ASUS's
   older monitors report `ACI` and AOpen's `AOP`; Apple's, Toshiba's and Hisense's codes (`APP`, `TSB`, `HEC`) are there
   too. Both sides are normalised the same way (upper case, letters and digits only, without the words "monitor" and
   "display", and without the brand's name or maker code in front) and matched whole, never by substring, against an
   index of the list's model number, model name and the alternative identifiers it carries. The index also holds Dell's
   identifiers without their revision letter (`U2723QEt`), Acer's without their suffix (`B196L_q`), and the last word of
   an identifier of several, because monitors leave out the series word (`FlexScan EV2740X`). EDID holds at most 13
   characters of a name, so a name that long also matches the identifiers it begins. Placeholders at the end of an
   identifier (`*`, `#`, `?`, or a run of three or more `X` or `Y`) stand for up to as many characters. Only a whole
   identifier, with or without Dell's letter or Acer's suffix, is a model's exact name; a series word left out, a
   placeholder and a name cut short can each take in other models. So a maker code without a brand in `MonitorMakers`,
   which could be any maker's, matches exact names only. A placeholder or a name cut short must also be listed at the
   monitor's resolution, either way round, when the monitor gives one: ViewSonic's 1080p VX2418 is listed as
   `VX24***********`, which would otherwise take in the 4K VX2478-4K-HD. An exact name or a series word keeps its
   listing whatever resolution the monitor gives, because most of those that disagree are the listed model giving a mode
   other than its panel's, as Dell's 5120 × 2160 U4025QW gives 2560 × 1080. A hit must also agree with the monitor's
   size to within an inch, which is what stops vague names like "LG HDR 4K" matching the wrong panel; for a monitor that
   gives no size, the listings its name matches must agree on one. Among the hits, those at the monitor's resolution
   win, then a whole name over a placeholder, and more characters before a placeholder over fewer, then the closest
   size, and a monitor listed more than once takes the median of its listings. Over the 33,385 named monitors with a
   size in linuxhw's DigitalDisplay collection of real EDIDs, these rules find a listing for 2,301, and the 55 of those
   whose resolution disagrees with their listing's are all exact names or series words.
3. **Unrecognised model.** A figure from size and resolution: the median of the certified monitors in the same size
   class (16, 19, 22, 24, 27, 32, 34, 38, 43 or 49 inches, with portable monitors in the first) and resolution class (up
   to 1.3, 2.4, 4.2, 5.1 or 9 megapixels, or more) where there are at least three, otherwise ENERGY STAR Displays v8's
   yearly allowance for the screen's area and pixels (§3.3.2, Table 1) scaled to typical draw: `0.296 × E_TEC_MAX − 0.5`
   W, never under 3 W. Sleep is the class median, or 0.2 W with the formula. A monitor that gives no size or resolution
   takes the median of all; the list's entries over 57" take no part, and a bigger screen takes the formula. With each
   certified monitor estimated from all the others, this lands within **9.04%** for half of them and **25.6%** for nine
   in ten — as good, in practice, as a model match while brightness is unknown.
4. **Brightness.** Where the monitor answers a read over the display cable (DDC/CI: capability check first, then
   brightness), the figure is scaled. Power is close to linear in screen luminance, and a monitor's fixed electronics
   are 28–51% of its full-brightness draw, so PowerLedger uses `P(b) = P_full × (0.45 + 0.55 × b)`, with the listed
   figure taken as its 75% point. `MonitorPower.At` gives it as `P = listed × (0.45 + 0.55 × b) / (0.45 + 0.55 × 0.75)`.
   A monitor that doesn't answer, or whose last reading is older than 15 minutes, is assumed to sit at that same 75%.
   With the display off, a monitor draws its sleep figure.
5. **Where it draws from.** A monitor has a plug of its own, or runs off the PC, as a portable monitor on a laptop's
   USB-C port does. On a laptop, a monitor of 17.3" or less is taken to run off it, since portable monitors come in 13
   to 17.3 inches; a larger one, one that gives no size, and every monitor on a desktop are taken to have a plug of
   their own. The user's word wins over the guess. What a monitor running off the PC draws is inside what the PC draws,
   so it always counts, whatever its choice says. On battery the measured rate already holds it, so it isn't added, and
   it comes out of the reading's unattributed remainder; in every other mode it is one of the parts the supply's or
   adapter's efficiency divides, and only a monitor with a plug of its own is added after the division. The board gives
   the model the two kinds apart, and the reading's monitors component holds both. The calibration learner takes what
   the monitors running off a laptop drew, at the figure the same reading used, out of the battery rate with the
   display, so the baseline it learns never holds them.
6. **Anything else.** The typed override stays, per monitor, and always wins. It is used exactly as typed, never scaled
   by brightness. Unticking a monitor with a plug of its own leaves it out. One the user hasn't chosen for counts,
   unless the settings carried over from before monitors were detected left monitors out.

Each monitor's figure carries how it was arrived at — measured for this model, estimated (from its size, when the
monitor gave both its size and its resolution), or typed — and the wizard and Settings show it beside the figure. The
reading's quality label is the power model's, as before, and describes the PC's own reading; the monitors' sources don't
change it, and the labels beside it say how the monitors were figured.

## Talking to monitors, carefully

The service runs in session 0, where the monitors can't be reached, so the App reads them in the user's session
(`DdcBrightness`) and reports what it read (`BrightnessReporter`). Microsoft warns that many monitors implement the
display-cable commands badly. So:

- Ask `GetMonitorCapabilities` once per monitor and only continue when it reports brightness, waiting 50 ms after it
  answered before asking for the brightness. Those are the only two requests a monitor is sent, and the answer about
  what it supports is kept for the session.
- Never write, only read.
- A monitor that reports no brightness is asked nothing again while the App runs, until a display change (a monitor
  plugged in or out, or display settings changed) or a resume from sleep.
- A monitor that fails either request before it has given a brightness this session is left alone the same way, as its
  firmware may be one the requests upset. One that has given a brightness has shown they don't, and most often fails for
  a reason Windows doesn't announce: it was switched off at its own button, set to another input, or is still waking. So
  it is asked again at the next read, each failure in a row doubles the wait, to 10, 20 and 40 minutes and then an hour
  at a time, and answering its brightness request ends the run. The read due when a wait ends asks it even if the read
  starts a moment early, and what the monitor supports isn't asked again.
- A display change or a resume forgets what every monitor has answered, what it supports included, and every failure and
  wait, because a monitor commonly fails, or answers wrongly, while it wakes or is being plugged in; the next read asks
  each afresh, capabilities first. Which monitors have given a brightness is kept, so one still waking at the first read
  after a resume is asked again at the next. SystemEvents raises both events through the synchronization context of the
  thread that subscribed, the App's UI thread, so neither may wait for a read, which can take seconds: each only marks a
  reset, which the read carries out before it looks at a display's monitors and before it asks each one.
- A display whose physical monitors can't be matched to its attached monitors, by count and description, is not asked at
  all, rather than one monitor's brightness being reported under another's name. Every handle a read opens is destroyed
  before the read returns.
- Read a minute after the App starts and every five minutes after, off the UI thread (a read takes about 40 ms a
  monitor), and only when the service lists external monitors and its latest reading doesn't say the displays are off.
- Report with `reportBrightness` only the monitors the service listed, under the service's name for each, at most 16 at
  a time. The service takes a reading only for a monitor attached now, and one older than 15 minutes counts as unknown.

Settings shows each monitor's brightness, read or assumed, and **Read brightness from monitors (read-only)** among its
preferences stops the reads.

## What the user sees

- **The wizard.** With no external monitor, as on a laptop on its own screen, the machine step shows no monitor rows and
  asks nothing about monitors. With one or more, they are listed by name with their size and the figure PowerLedger
  worked out, and any figure can be corrected there. Each row has a **Count it** box and, on a laptop, a **has its own
  plug** box, which follow the service's defaults until the user ticks them. A monitor that runs off the PC shows
  **Count it** ticked and greyed, with a tooltip saying it counts as part of what the PC draws. A desktop shows the plug
  box for a monitor held to run off it, so that can be undone, and keeps showing one the user has ticked or unticked, so
  it doesn't go from under the pointer. On a laptop the note under the rows says that a monitor of 17.3 inches or less
  is taken to run off it, to tick "has its own plug" if it has one, and to untick a monitor with its own plug to leave
  it out; on a desktop it says only how to leave a monitor out. A recognised monitor says "measured for this model". One
  that isn't says so plainly — "estimated from its size — correct it if you know better", or "estimated — correct it if
  you know better" when it didn't give both its size and its resolution — and invites a correction.
- **Settings.** A row per monitor: name, size and resolution, watts, where the figure came from, what it draws now,
  whether brightness could be read ("brightness 60%, read from the monitor", "brightness unknown, assumed 75%", or just
  "brightness unknown" for a typed figure, which no brightness scales), a typed override, and the wizard's **Count it**
  and **has its own plug** boxes, with a note like the wizard's. Clearing a typed figure goes back to PowerLedger's own.
  The rows follow the service's status every ten seconds without losing a figure being typed or a box the user ticked,
  while a box the user hasn't touched follows the service's defaults and the choice its settings hold, so adding a
  monitor later shows it here without another wizard. With no external monitor, the row says "none detected". About
  credits the ENERGY STAR list.
- **Now, Breakdown and Report.** The Now screen's display row adds the monitors counted ("plus 2 monitors") and how the
  least sure of their figures was got: ", estimated" when any was estimated, otherwise ", brightness assumed" when a
  listed figure's brightness couldn't be read; figures measured for their models at a brightness read, and typed
  figures, add nothing. With monitors in the reading, the live note says their watts come from their own figures
  ("Windows battery report, with the monitors' own figures", or the model's note ending "plus the monitors' own
  figures"), and the quality badge still describes the PC's own reading. Breakdown's footnote says what the display band
  holds over any range: the built-in panel, on a machine with one, and the external monitors counted at the time. The
  report's quality legend, on screen and in the PDF, ends "External monitors' watts come from their own figures in every
  mode."
- **Saving.** A monitor's choice is saved only when it says what the service wouldn't assume: a plug other than the one
  guessed, a typed figure, or, for a monitor with a plug of its own, counting where monitors aren't counted by default
  or the other way round. Whether a monitor that runs off the PC counts saves nothing. The choices for the monitors
  listed come first, then those loaded for monitors not attached now that still say something, in the order loaded, as
  many as fit within the service's limit of 16 (`ServiceSettings.MaxMonitors`). Each save puts the monitors attached
  first, so the choices dropped are for the monitors unseen longest. Once the form lists a monitor, saving clears the
  old monitor count and the choice to count monitors; while it lists none, they go back as the service sent them, so the
  service can still carry them over.
- **Saving settings that are behind.** The form may hold settings older than the service's: loaded before the service
  carried the old monitor settings over, at the first reading that finds a monitor, they still hold the old count or the
  choice to count monitors, or say a monitor without a choice counts otherwise than the monitors listed do. A save then
  reads the service's settings again first, and sends nothing if it can't. It takes from them what the form doesn't
  show: whether a monitor without a choice counts, the old monitor settings and the choices for monitors not listed.
  Each row takes its choice from them where the user hasn't typed or ticked, so a figure carried over shows in its box.
  So a save never undoes the carry-over, and what the user typed and ticked stays. The service guesses a monitor's plug
  from the chassis, so a save that changes the chassis says each listed monitor's plug as it shows, and a plug a choice
  already says is said again while the row shows it unchanged, since the service's status may still be for the chassis
  before.

## Components

- `src/PowerLedger.Sensors/Monitors/MonitorInventory.cs` — what Windows says is attached, external only, with each
  monitor's key, and the serial keys found shared and the resolutions last read, kept while the service runs.
- `src/PowerLedger.Sensors/Monitors/MonitorCatalogue.cs` — the shipped table, its index, and the normalisation.
- `src/PowerLedger.Sensors/Monitors/MonitorMakers.cs` — EDID maker codes and the brands the list spells them as.
- `src/PowerLedger.Sensors/Monitors/MonitorEstimate.cs` — the size-and-resolution figure.
- `src/PowerLedger.Sensors/DisplaySource.cs` — reads the monitors with its WMI query and hands them over when they
  change, and not when WMI gives no answer.
- `src/PowerLedger.Sensors/Wmi.cs` — every WMI query, and `MeansNoInstances`, which tells a class refused as having no
  instances, an answer, apart from WMI failing to answer.
- `src/PowerLedger.Service/SensorWorker.cs` — builds each sensor set with a token cancelled when the set is abandoned or
  thrown away, which `ServiceHost` hands the board with the monitors the set finds.
- `src/PowerLedger.Core/MonitorPower.cs` — the brightness scaling, `IMonitorDraw`, which the power model asks, and
  `MonitorWatts`, its answer split between the monitors with a plug of their own and those running off the PC.
- `src/PowerLedger.Core/PowerModel.cs` — where each kind of monitor goes in a reading; `SamplingLoop` gives the learner
  what the monitors running off the PC drew in it.
- `src/PowerLedger.Service/MonitorBoard.cs` — the service's monitors: each one's figure, the user's choices, the
  profile's default for counting, the guess at each one's plug, and the brightness reported; what the model counts and
  the status lists.
- `src/PowerLedger.Contracts/Monitors.cs` — a monitor's choice, status and brightness, and `MonitorKeys`, one name for a
  monitor whether WMI or a device path gives it. `PipeMessages.cs` adds `reportBrightness`, and
  `ServiceSettings.MaxMonitors` bounds both the choices and a report.
- `src/PowerLedger.App/Monitors/DdcBrightness.cs` — the capability-gated, read-only DDC/CI reader, in the App because
  session 0 can't reach the monitors, and how long it leaves a monitor that failed alone.
- `src/PowerLedger.App/Monitors/BrightnessReporter.cs` — reads the monitors on its schedule and reports to the service.
- `src/PowerLedger.App/Monitors/MonitorRow.cs` — one monitor's row in the wizard and Settings;
  `src/PowerLedger.App/Settings/ServiceForm.cs` saves the choices, reading the service's settings again first when those
  loaded are behind.
- `assets/monitors/energy-star-monitors.csv`, `assets/monitors/make-monitor-table.ps1` and `assets/monitors/README.md` —
  the shipped table, the script that rebuilds it from the public dataset, and where it comes from.
- The service's settings gain a list of monitor choices (key, whether counted, typed watts, and whether the monitor has
  a plug of its own) in place of a count and one figure, and `MachineProfile.CountMonitorsByDefault`, which settings
  stored before it existed read as true. The old fields stay only so old settings can be carried over. `ProfilePolicy`
  (in `src/PowerLedger.Service/SettingsStore.cs`) carries them over once, at the first tick that finds a monitor:
  whether they counted monitors becomes `CountMonitorsByDefault`, so a monitor with a plug of its own attached later is
  counted or left out as the old settings said, and only monitors counted at a figure other than the old default of 25 W
  need choices, one for each monitor attached, holding that figure. The old count and the choice to count monitors are
  then cleared and the settings saved; if the save fails, the next start tries again.

## Testing

- The catalogue: the awkward names the research found — `U2723QE` against `U2723QEt`, Acer's `B196L_q`, HP's "HP 322pb
  Monitor", BenQ's `GW2480-B`, Lenovo's internal part code, and "LG HDR 4K" matching nothing — and placeholders, names
  cut short by EDID, series words, ties on resolution and size, and monitors listed more than once. A family and a name
  cut short take in a monitor only where they are listed at its resolution, either way round, or when it gives none; an
  exact name and a series word keep their listing at any resolution. The codes ASUS's and AOpen's monitors report find
  only their own brand, and an unknown maker matches only a name the list gives whole.
- The estimate: each certified monitor estimated from all the others; the test holds the median error to 12% and the
  90th percentile to 35%, margins above the 9.04% and 25.6% measured.
- The inventory: WMI rows with a built-in panel, an inactive monitor, zero-padded names, no native mode, serial numbers
  that are missing, placeholders or shared, twins still known by their instances after one is unplugged, and the snapped
  diagonals; no answer, rather than no monitors, when a class that says which monitors are attached fails, and no
  monitors when the classes are refused as having no instances; the resolution last read for an instance kept until
  another is read, when the modes class fails or has no instances; on this machine behind `Category=Hardware`. WMI's
  refusals are told apart: "Not supported" and "Invalid class" mean no instances, and a timeout, access denied, a
  provider failure, an invalid query or namespace mean no answer. The display source hands the monitors on at first and
  when they change, hands on none once the last monitor goes and WMI refuses its classes as having no instances, and
  keeps them when WMI gives no answer.
- Brightness: the scaling function (the listed figure at 75%, the fixed share with none, a straight line between); the
  reader's rules against a fake Windows layer: a failure before a monitor has given a brightness, and an answer of no
  brightness, remembered until a display change or a resume clears them, and a power change that isn't a resume clearing
  nothing; a monitor that has given a brightness asked again at the next read after it fails, left alone for 10, 20 and
  40 minutes and then an hour at a time while it keeps failing, its run ended by an answer, asked by the read due when a
  wait ends though that read starts a moment early, and asked again at the next read when it fails as it wakes from a
  resume; a display change or a resume raised while a read waits on a monitor returning at once and carried out before
  the next monitor is asked; the reader itself behind `Category=Hardware`; the reporter's schedule and the preference.
- The board: figures, choices, the profile's default for counting, a laptop's small monitor taken to run off it and the
  user's word over the guess, a monitor running off the PC counted whatever its choice, brightness going stale, a
  monitor unplugged, a monitor keeping its figure when the modes fail to answer once, and callers on several threads at
  once; a list ignored once its sensor set is retired and, through the real worker, display source and board, the
  monitors an abandoned set finds when its stuck read returns never reaching the board; a brightness report over a real
  pipe, and a bad one refused.
- The model: a monitor with a plug of its own added to a measured rate and after the supply or adapter division, and one
  running off the PC already in a measured rate and divided with the other parts, on a laptop, with a learned baseline
  and without, and on a desktop; the loop keeping a monitor running off a laptop out of what the learner learns, one the
  user left out included.
- The wizard and Settings: no external monitor means no monitor rows and nothing saved about monitors; detected monitors
  are listed by name, size, figure and source; a figure typed for an estimated monitor is saved as its watts; a choice
  is saved only when it differs from the service's defaults or holds a typed figure, and whether a monitor running off
  the PC counts saves nothing; choices for monitors not attached now are kept in the order loaded, within the limit; the
  old count goes back while no monitor is listed and is cleared once one is; a refresh keeps what the user ticked. A
  save after the service carried the old settings over leaves monitors out as it did, keeps the figure typed then and
  shows it, and keeps what it carried over when no monitor is listed; a save that reads the settings again keeps what
  the user typed and ticked, and one that can't read them sends nothing. Correcting the chassis saves the plug each
  listed monitor shows, a save before the status catches up keeps each plug said until it is changed, and a desktop
  shows the plug box of a monitor held to run off it and keeps one the user has ticked. The rendering tests draw the
  wizard's machine step for a desktop and a laptop, each fitting the small window whole, and Settings and the Now screen
  with a portable monitor running off the laptop, and check that only a laptop asks about plugs, that a monitor running
  off the PC stays counted, and that a desktop's plug box stays on screen as it is ticked and unticked.
- The labels: the Now screen's display row and live note, Breakdown's footnote for any range and machine, and the
  report's quality legend.
- Migration: an existing install keeps counting what it counted, with its typed watts, once a monitor is detected; one
  that didn't count monitors leaves them out, one attached later included; settings stored before the default for
  counting existed count monitors; a fresh install carries nothing over.

## Honest limits

- Monitors older than about 2019 are mostly absent from the list, so the estimate is the common path, not the exception.
- Brightness is the largest remaining error. Where it can't be read, the figure assumes 75%, and the monitor may draw as
  little as about half of it, fully dimmed, or about a sixth more, at full brightness: the same monitor draws about half
  as much dim as it does bright. A typed figure doesn't follow brightness at all.
- DDC/CI gives a setting between the monitor's own minimum and maximum, with no real-world unit, so two monitors at the
  same setting can be differently bright.
- OLED monitors vary with what is on screen as much as with brightness. They are treated like any other panel: one the
  list knows takes its listed figure, and one it doesn't is estimated and says so.
- On a laptop, whether a monitor runs off it is guessed from its size alone: a desk monitor of 17.3" or less is taken to
  run off the laptop, and a portable monitor that gives no size to have a plug of its own, until the user says
  otherwise.
- A few monitors land on the wrong side of the catalogue's rules. ASRock's monitors report `ASR`, which the registry
  doesn't list, so they match exact names only, and the PG27FF1A and its siblings miss the `PG27FF********` family
  ASRock lists them under. Koorui's monitors report `HKC`, which stands for HKC here, so they miss Koorui's own
  listings. ASUS's 4K PA328Q names itself PA328, the list's model number for its 1440p PA328CGV, and Acer's 1366 × 768
  V206HQLB is taken for its 1600 × 900 V206HQL b, because names are compared in upper case; as exact names, both keep
  those listings.
- Twins that share a serial number are known for twins only once both have been attached since the service started.
  After a restart with one of them attached, it takes the shared key until the other is attached again.
- A monitor switched off at its own button while the PC stays awake may still be counted at its on figure, if Windows
  keeps listing it as active. One that drops off the cable when switched off is taken as unplugged at the next display
  query.
