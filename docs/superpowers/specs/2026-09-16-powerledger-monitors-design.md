# Monitors that know themselves — design

Status: built in Plan J (`docs/superpowers/plans/2026-09-16-powerledger-j-monitors.md`), and corrected to match what was
built, the decisions made at merge and the fixes that followed two reviews. Plan K
(`docs/superpowers/plans/2026-09-17-powerledger-k-autosave-monitor-power.md`, designed in
`docs/superpowers/specs/2026-09-17-powerledger-autosave-monitor-power-design.md`) added whether each monitor is on, its
refresh rate, the 200-nit anchor and HDR. Follows the research of 2026-09-16 (ENERGY STAR and EPREL registries, measured
power-versus-brightness data) and Plan K's research on refresh rate. Changes the main spec's §3 to §6, §8, §9, §11, §12,
§14 and §16.

## Goal

Nobody should type how many monitors they have or what they draw. PowerLedger reads which monitors are attached, works
out what each one uses, and only asks when it genuinely doesn't know. The numbers must be honest: every monitor figure
says where it came from.

## What was wrong before

- The wizard asked for a count of external monitors and a watts-per-monitor figure, even on a laptop with one screen.
- The default was **25 W per monitor**. The median certified monitor draws **14.3 W**; a 24" 1080p about **12.9 W**. The
  default overstated a typical monitor by about 75%, and was too high for 86% of the models in the ENERGY STAR list.
- The sleep default, 0.5 W, is the EU's legal ceiling. The certified median is **0.23 W**.

## Owner's choices

| Question | Choice |
|---|---|
| Read each monitor's brightness over the display cable | Yes, carefully: read-only, capability-gated, cached |
| A monitor that isn't in the table | Ask during setup, with the estimate filled in |
| The wizard on a laptop with no external screen | Skip the monitor question entirely |
| Settings: a Save button, or saving by itself | Saving by itself; the wizard still saves when Next is pressed |
| Whether a monitor is switched off | Ask the monitor once a minute; where it can't say, the Count it tick decides |
| Refresh rate, the 200-nit anchor and HDR | All three, in 0.4.1 |

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
times the median for its size. The table holds **1,580 monitors**, one row each: brand, model number and name, the other
identifiers it is listed under, size, resolution, panel, on, sleep and off watts, maximum luminance, HDR tier and
certification date. PowerLedger reads each row's on, sleep and off watts, its maximum luminance, and whether its panel
is OLED, as 32 are. It is committed and embedded in `PowerLedger.Sensors`, so nothing is fetched at runtime;
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
   size, and a monitor listed more than once takes the median of its listings' on, sleep and off watts, and of the
   maximum luminance of those that give one. A listing without off watts takes the table's median, 0.16 W over the 1,572
   listings that give them. Over the 33,385 named monitors with a size in linuxhw's DigitalDisplay collection of real
   EDIDs, these rules find a listing for 2,301, and the 55 of those whose resolution disagrees with their listing's are
   all exact names or series words.
3. **Unrecognised model.** A figure from size and resolution: the median of the certified monitors in the same size
   class (16, 19, 22, 24, 27, 32, 34, 38, 43 or 49 inches, with portable monitors in the first) and resolution class (up
   to 1.3, 2.4, 4.2, 5.1 or 9 megapixels, or more) where there are at least three, otherwise ENERGY STAR Displays v8's
   yearly allowance for the screen's area and pixels (§3.3.2, Table 1) scaled to typical draw: `0.296 × E_TEC_MAX − 0.5`
   W, never under 3 W. Its sleep and off watts are the class's medians, and its maximum luminance the median of those in
   the class that give one; with the formula, it takes the table's medians, 0.23 W asleep and 0.16 W off, and no
   luminance. A monitor that gives no size or resolution takes the medians of all; the list's entries over 57" take no
   part, and a bigger screen takes the formula. An estimated monitor is taken for an LCD one. With each certified
   monitor estimated from all the others, this lands within **9.04%** for half of them and **25.6%** for nine in ten —
   as good, in practice, as a model match while brightness is unknown.
4. **Brightness.** Where the monitor answers a read over the display cable (DDC/CI: capability check first, then
   brightness), the figure is scaled. Power is close to linear in screen luminance, and a monitor's fixed electronics
   are 28–51% of its full-brightness draw, so PowerLedger uses `P(b) = P_full × (0.45 + 0.55 × b)`. ENERGY STAR sets
   each monitor to 200 cd/m² for its on-mode test, so the listed figure is the draw where 200 cd/m² sits on the
   monitor's brightness scale, `a = 200 / max nits` clamped to 0.05–1, from the listing's maximum luminance or an
   estimate's, or 0.75 where none is known (`MonitorPower.Anchor`). `MonitorPower.At` gives
   `P = listed × (0.45 + 0.55 × b) / (0.45 + 0.55 × a)`. A monitor that doesn't answer, or whose last reading is older
   than 15 minutes of the displays being on, is assumed to sit at 75%, which isn't where every figure was taken: 20 W
   listed for a 400 cd/m² monitor, taken half way up its scale, counts as 23.8 W at 75%, and for a 200 cd/m² monitor,
   taken at the top, as 17.25 W. With the display off, a monitor draws its sleep figure.
5. **Whether it is on.** The App asks each monitor that answers over the display cable for its power mode (MCCS VCP code
   D6) once a minute while the service's latest reading says the displays are on, and reports what it says: 1 is on; 2
   (standby) and 3 (suspend) are standby; 4 and 5, the last a monitor switched off at its own button, are off. A monitor
   that says it is off draws its off figure, and one on standby its sleep figure, whatever the displays are doing and
   whatever was typed for it; one that says it is on, or hasn't said, draws its figure on while the displays are on and
   its sleep figure while they are off. What a monitor said counts as unknown once it is three minutes old, counted only
   while the service's readings say the displays are on, so a monitor last known to be off goes on counting as off while
   the displays sleep, and after they wake until the App reads it again. This applies to monitors with a plug of their
   own and to those running off the PC alike, and a monitor Windows no longer lists stops counting, as before.
6. **Refresh rate.** On an LCD panel only the electronics, the timing controller and column drivers, work harder at a
   higher refresh rate, not the backlight: ASUS's 27-inch 1440p PG279Q drew 1 W more at 144 Hz than at 60, and
   Monoprice's 3440 × 1440 Dark Matter 34 went from 20.0 to 24.3 W. ENERGY STAR measures at 60 Hz unless the manual
   names another default. So a counted monitor that is on, or hasn't said, adds `0.006 W × megapixels × (Hz − 60)` while
   the displays are on (`MonitorPower.Refresh`), a coefficient fitted to those two measurements, which alone give 0.003
   to 0.010: a 1440p monitor adds 2.3 W at 165 Hz and 4.0 W at 240 Hz, and nothing at 60 Hz or below. Nothing is added
   for a monitor the list gives an OLED panel, for a typed figure, or where the resolution or the refresh rate is
   unknown. The App reads the refresh rate Windows drives each monitor at once a minute with `QueryDisplayConfig`, the
   physical rate where Dynamic Refresh Rate boosts the path, and reports it, since the service can't call it from
   session 0; a rate counts as unknown three minutes after it was read, on the same clock as a power state. The status
   gives the rate and the watts it adds.
7. **HDR.** The App reads whether HDR is on for each monitor with `DisplayConfigGetDeviceInfo`: the user's HDR switch
   (`ADVANCED_COLOR_INFO_2`'s `highDynamicRangeUserEnabled`) where Windows has that request, advanced color being on
   (`ADVANCED_COLOR_INFO`'s `advancedColorEnabled`) where it doesn't, and off where Windows gives neither. HDR can
   double what a monitor draws, and no figure per model is known, so nothing is added: the monitor's row and the Now
   screen's display row say HDR is on and that the monitor may draw much more.
8. **Where it draws from.** A monitor has a plug of its own, or runs off the PC, as a portable monitor on a laptop's
   USB-C port does. On a laptop, a monitor of 17.3" or less is taken to run off it, since portable monitors come in 13
   to 17.3 inches; a larger one, one that gives no size, and every monitor on a desktop are taken to have a plug of
   their own. The user's word wins over the guess. What a monitor running off the PC draws is inside what the PC draws,
   so it always counts, whatever its choice says. On battery the measured rate already holds it, so it isn't added, and
   it comes out of the reading's unattributed remainder; in every other mode it is one of the parts the supply's or
   adapter's efficiency divides, and only a monitor with a plug of its own is added after the division. The board gives
   the model the two kinds apart, and the reading's monitors component holds both. The calibration learner takes what
   the monitors running off a laptop drew, at the figure the same reading used, out of the battery rate with the
   display, so the baseline it learns never holds them.
9. **Anything else.** The typed override stays, per monitor, and wins over PowerLedger's figure while the monitor is on.
   It is used exactly as typed, never scaled by brightness, and nothing is added to it for the refresh rate; a monitor
   that says it is off or on standby still draws its off or sleep figure. Unticking a monitor with a plug of its own
   leaves it out. One the user hasn't chosen for counts, unless the settings carried over from before monitors were
   detected left monitors out.

Each monitor's figure carries how it was arrived at — measured for this model, estimated (from its size, when the
monitor gave both its size and its resolution), or typed — and the wizard and Settings show it beside the figure. What a
refresh rate adds is always an estimate. The reading's quality label is the power model's, as before, and describes the
PC's own reading; the monitors' sources don't change it, and the labels beside it say how the monitors were figured.

## Talking to monitors, carefully

The service runs in session 0, where neither the monitors nor the user's display settings can be reached, so the App
reads them in the user's session, over the display cable (`DdcBrightness`) and from Windows' display configuration
(`DisplayConfigReader`), and reports what it read (`BrightnessReporter`). Microsoft warns that many monitors implement
the display-cable commands badly. So:

- Ask `GetMonitorCapabilities` once per monitor and go on only when it reports brightness. Then, at every read, ask its
  power mode, `GetVCPFeatureAndVCPFeatureReply` for VCP code D6, and ask its brightness, `GetMonitorBrightness`, when it
  hasn't answered that since the App started or last forgot, or its last answer is five minutes old, or whenever the
  power-mode request has just failed, as only that answer tells a monitor without power mode from one that has stopped
  answering. Wait 50 ms between one request and the next to the same monitor. Those three Get requests, with D6 the only
  VCP code, are all a monitor is ever sent, and the answer about what it supports is kept for the session.
- Never write, only read.
- A monitor that reports no brightness is asked nothing again while the App runs, its power mode included, until a
  display change (a monitor plugged in or out, or display settings changed) or a resume from sleep. No capability flag
  covers power mode, so supporting brightness is the only sign that a monitor answers the standard's requests properly.
- A monitor that says it is off or on standby is taken at its word: its brightness is left for a read that finds it on,
  and that isn't a failure, so one switched off at its own button that still answers goes on saying so at every read.
  Only the low byte of the answer is read, and a value the standard doesn't define is an answer that gives no state. A
  monitor whose power-mode request fails in three reads in a row, while its brightness answers each time, is taken not
  to support power mode until a display change or a resume; fewer failures are taken as passing, and an answer starts
  the count again.
- A monitor fails a read when its capabilities or brightness request fails, and a power state it gave before failing
  still counts. One that fails before it has given a brightness this session is left alone as one without brightness is,
  as its firmware may be one the requests upset. One that has given a brightness has shown they don't, and most often
  fails for a reason Windows doesn't announce: it was switched off at its own button, set to another input, or is still
  waking. So it is asked again at the next read, a minute later, and each failure in a row after that doubles the wait,
  to 2, 4, 8, 16 and 32 minutes and then an hour at a time; a read it doesn't fail ends the run. The read due when a
  wait ends asks it even if the read starts a moment early, and what the monitor supports isn't asked again.
- A display change or a resume forgets what every monitor has answered, what it supports included, its count of failed
  power-mode requests and when it last gave its brightness, and every failure and wait, because a monitor commonly
  fails, or answers wrongly, while it wakes or is being plugged in; the next read asks each afresh, capabilities first.
  Which monitors have given a brightness is kept, so one still waking at the first read after a resume is asked again at
  the next. SystemEvents raises both events through the synchronization context of the thread that subscribed, the App's
  UI thread, so neither may wait for a read, which can take seconds: each only marks a reset, which the read carries out
  before it looks at a display's monitors and before it asks each one.
- A display whose physical monitors can't be matched to its attached monitors, by count and description, is not asked at
  all, rather than one monitor's brightness being reported under another's name. Every handle a read opens is destroyed
  before the read returns.
- Windows' display configuration is only described, never changed, and no monitor is sent anything for it.
  `QueryDisplayConfig` gives the active paths, asked with `QDC_VIRTUAL_REFRESH_RATE_AWARE` so that a path Dynamic
  Refresh Rate boosts gives its physical rate, the target mode's `vSyncFreq`; Windows 10, which refuses the flag, is
  asked again without it. `DisplayConfigGetDeviceInfo` gives each path's monitor device path and its color state. A rate
  outside 1 to 1000 Hz, or a path whose monitor Windows won't name, gives nothing. So the refresh rate and HDR state of
  a monitor that doesn't answer over its cable are read as well.
- Read a minute after the App starts and every minute after, off the UI thread (a request takes about 40 ms, and one
  monitor's requests are 50 ms apart), and only when the service lists external monitors and its latest reading doesn't
  say the displays are off.
- Report with `reportBrightness` only the monitors the service listed, under the service's name for each, with whatever
  was read: the brightnesses, the power states and how Windows drives each, at most 16 of each, and nothing when all
  three are empty. The service takes a reading only for a monitor attached now, and ages what the App reported only
  while its readings say the displays are on: a brightness counts as unknown after 15 minutes of that, and a power
  state, refresh rate or HDR state after 3.

Settings shows each monitor's brightness, read or assumed, whether it is on, and its refresh rate. **Read monitors'
brightness and whether they're on (read-only)** among its preferences stops the requests sent to monitors; the refresh
rate and HDR state still come from Windows' display settings.

## What the user sees

- **The wizard.** With no external monitor, as on a laptop on its own screen, the machine step shows no monitor rows and
  asks nothing about monitors. With one or more, they are listed by name with their size, resolution and refresh rate,
  as far as those are known, and the figure PowerLedger worked out, and any figure can be corrected there. Each row has
  a **Count it** box and, on a laptop, a **has its own plug** box, which follow the service's defaults until the user
  ticks them. A monitor that runs off the PC shows **Count it** ticked and greyed, with a tooltip saying it counts as
  part of what the PC draws. A desktop shows the plug box for a monitor held to run off it, so that can be undone, and
  keeps showing one the user has ticked or unticked, so it doesn't go from under the pointer. On a laptop the note under
  the rows says that a monitor of 17.3 inches or less is taken to run off it, to tick "has its own plug" if it has one,
  and to untick a monitor with its own plug to leave it out; on a desktop it says only how to leave a monitor out. A
  recognised monitor says "measured for this model". One that isn't says so plainly — "estimated from its size — correct
  it if you know better", or "estimated — correct it if you know better" when it didn't give both its size and its
  resolution — and invites a correction. The machine step is saved when Next is pressed.
- **Settings.** A row per monitor: name, size and resolution with the refresh rate Windows drives it at, to a hundredth
  of a hertz ("27 in · 2560 × 1440 · 165 Hz", "59.94 Hz"), watts, where the figure came from, whether it is on and what
  it draws now, whether brightness could be read ("brightness 60%, read from the monitor", "brightness unknown, assumed
  75%", or just "brightness unknown" for a typed figure, or for a monitor off or on standby, which no brightness
  scales), a typed override, and the wizard's **Count it** and **has its own plug** boxes. What it draws now starts with
  what the monitor said, or, for a counted monitor that hasn't said lately, that PowerLedger can't tell: "on · 24.3 W
  now", "standby · 0.3 W now", "off · 0.2 W now", "can't tell if it's on · 24.3 W now". What a refresh rate above 60 Hz
  adds follows it, unless that is under 0.05 W, as for a 60 Hz mode Windows drives a hair faster, and so does HDR while
  the monitor draws its figure on: "on · 26.6 W now, incl. 2.3 W for 165 Hz · HDR on: may draw much more". A monitor not
  counted says "not counted", after its state when it gave one ("off · not counted"). The note under the rows is like
  the wizard's, and also says that PowerLedger reads whether each monitor is on and that, where it can't tell, unticking
  a monitor while it's off keeps it out. Clearing a typed figure goes back to PowerLedger's own. The rows follow the
  service's status every ten seconds without losing a figure being typed or a box the user ticked, while a box the user
  hasn't touched follows the service's defaults and the choice its settings hold, so adding a monitor later shows it
  here without another wizard. With no external monitor, the row says "none detected". About credits the ENERGY STAR
  list.
- **Now, Breakdown and Report.** The Now screen's display row adds the monitors counted, how many of them said they were
  off or on standby, whether HDR is on for one that draws its figure on, and how the least sure of their figures was
  got: "plus 2 monitors, 1 off, HDR on, estimated", or "both off" and "all on standby" when every one said the same. The
  figures are ", estimated" when any was estimated; otherwise ", brightness assumed" when the brightness of a listed
  monitor that isn't off or on standby couldn't be read; otherwise ", estimated" when a refresh rate adds to any of
  them. Figures measured for their models at a brightness read, or for a monitor off or on standby, and typed figures
  add nothing. With monitors in the reading, the live note says their watts come from their own figures ("Windows
  battery report, with the monitors' own figures", or the model's note ending "plus the monitors' own figures"), and the
  quality badge still describes the PC's own reading. Breakdown's footnote says what the display band holds over any
  range: the built-in panel, on a machine with one, and the external monitors counted at the time. The report's quality
  legend, on screen and in the PDF, ends "External monitors' watts come from their own figures in every mode."
- **Settings saves itself.** Settings sends the service's settings, the monitors' choices among them, whenever the user
  changes something: a tick or a segmented choice at once, and a typed value, a monitor's watts among them, when its box
  is left or Enter is pressed. There is no Save button for them: one line between the Machine and Sampling sections says
  "Saving…", "Saved." or the first problem, and a value that can't be sent is named there, stays in its box and sends
  nothing until it is put right. Filling the form, the ten-second status refresh and what a save reads back send
  nothing. One save is on its way at a time; changes made meanwhile are sent together once it is done, so the last one
  wins. A save fills no box again: it takes the settings it sent as the ones the service holds, so what was typed
  meanwhile stays.
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
  So a save never undoes the carry-over, and what the user typed and ticked stays; once the service has taken it, the
  next save has nothing to read again. The service guesses a monitor's plug from the chassis, so a save that changes the
  chassis says each listed monitor's plug as it shows, and a plug a choice already says is said again while the row
  shows it unchanged, since the service's status may still be for the chassis before.

## Components

- `src/PowerLedger.Sensors/Monitors/MonitorInventory.cs` — what Windows says is attached, external only, with each
  monitor's key, and the serial keys found shared and the resolutions last read, kept while the service runs.
- `src/PowerLedger.Sensors/Monitors/MonitorCatalogue.cs` — the shipped table, its index, and the normalisation; each
  listing's off watts, maximum luminance, and whether its panel is OLED.
- `src/PowerLedger.Sensors/Monitors/MonitorMakers.cs` — EDID maker codes and the brands the list spells them as.
- `src/PowerLedger.Sensors/Monitors/MonitorEstimate.cs` — the size-and-resolution figures: on, sleep and off watts, and
  a maximum luminance.
- `src/PowerLedger.Sensors/DisplaySource.cs` — reads the monitors with its WMI query and hands them over when they
  change, and not when WMI gives no answer.
- `src/PowerLedger.Sensors/Wmi.cs` — every WMI query, and `MeansNoInstances`, which tells a class refused as having no
  instances, an answer, apart from WMI failing to answer.
- `src/PowerLedger.Service/SensorWorker.cs` — builds each sensor set with a token cancelled when the set is abandoned or
  thrown away, which `ServiceHost` hands the board with the monitors the set finds.
- `src/PowerLedger.Core/MonitorPower.cs` — the brightness scaling from the 200-nit anchor, the refresh term, the typical
  sleep and off figures, `IMonitorDraw`, which the power model asks, and `MonitorWatts`, its answer split between the
  monitors with a plug of their own and those running off the PC.
- `src/PowerLedger.Core/PowerModel.cs` — where each kind of monitor goes in a reading; `SamplingLoop` gives the learner
  what the monitors running off the PC drew in it.
- `src/PowerLedger.Service/MonitorBoard.cs` — the service's monitors: each one's figures, the user's choices, the
  profile's default for counting, the guess at each one's plug, and the brightness, power state, refresh rate and HDR
  state reported, aged by the time the displays were on; what the model counts and the status lists.
- `src/PowerLedger.Contracts/Monitors.cs` — a monitor's choice and status, what the App read of it (`MonitorBrightness`,
  `MonitorPowerReading` with `MonitorPowerState`, and `MonitorDisplayReading`), and `MonitorKeys`, one name for a
  monitor whether WMI or a device path gives it. `PipeMessages.cs` adds `reportBrightness`, and
  `ServiceSettings.MaxMonitors` bounds both the choices and each list of a report.
- `src/PowerLedger.App/Monitors/DdcBrightness.cs` — the capability-gated, read-only DDC/CI reader of brightness and
  power mode, in the App because session 0 can't reach the monitors, and how long it leaves a monitor that failed alone.
- `src/PowerLedger.App/Monitors/DisplayConfigReader.cs` — each monitor's refresh rate and HDR state, from Windows'
  display configuration.
- `src/PowerLedger.App/Monitors/BrightnessReporter.cs` — reads the monitors and the display configuration every minute
  and reports to the service.
- `src/PowerLedger.App/Monitors/MonitorRow.cs` — one monitor's row in the wizard and Settings;
  `src/PowerLedger.App/Settings/ServiceForm.cs` saves the choices, by itself in Settings, reading the service's settings
  again first when those loaded are behind.
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
  only their own brand, and an unknown maker matches only a name the list gives whole. A missing sleep or off figure is
  the table's median, a model listed more than once takes the median off watts and maximum luminance of its listings
  that give them, and a panel the list calls OLED is taken for one.
- The estimate: each certified monitor estimated from all the others; the test holds the median error to 12% and the
  90th percentile to 35%, margins above the 9.04% and 25.6% measured. An estimated monitor takes the median off watts
  and maximum luminance of the monitors like it.
- The inventory: WMI rows with a built-in panel, an inactive monitor, zero-padded names, no native mode, serial numbers
  that are missing, placeholders or shared, twins still known by their instances after one is unplugged, and the snapped
  diagonals; no answer, rather than no monitors, when a class that says which monitors are attached fails, and no
  monitors when the classes are refused as having no instances; the resolution last read for an instance kept until
  another is read, when the modes class fails or has no instances; on this machine behind `Category=Hardware`. WMI's
  refusals are told apart: "Not supported" and "Invalid class" mean no instances, and a timeout, access denied, a
  provider failure, an invalid query or namespace mean no answer. The display source hands the monitors on at first and
  when they change, hands on none once the last monitor goes and WMI refuses its classes as having no instances, and
  keeps them when WMI gives no answer.
- Brightness: the scaling function (the listed figure at its anchor, where 200 cd/m² sits on the monitor's scale, or at
  75% where the luminance is unknown; the fixed share with none; a straight line between); the reader's rules against a
  fake Windows layer: a failure before a monitor has given a brightness, and an answer of no brightness, remembered
  until a display change or a resume clears them, and a power change that isn't a resume clearing nothing; a monitor
  that has given a brightness asked again at the next read after it fails, left alone for 2, 4, 8, 16 and 32 minutes and
  then an hour at a time while it keeps failing, its run ended by a read it doesn't fail, asked by the read due when a
  wait ends though that read starts a moment early, and asked again at the next read when it fails as it wakes from a
  resume; a display change or a resume raised while a read waits on a monitor returning at once and carried out before
  the next monitor is asked; the reader itself behind `Category=Hardware`; the reporter's schedule and the preference.
- Power mode: D6's values read as on, standby or off from their low byte, and one the standard doesn't give as an answer
  with no state; the power mode asked before the brightness at every read, and the brightness every five minutes, by the
  read due even when it starts a moment early; a monitor switched off at its own button that still answers saying so
  every read without being left alone; one whose power mode fails three reads in a row, while its brightness answers,
  taken not to support it until a display change or a resume, and a single failure passing; 50 ms between one request
  and the next; and only Get requests declared.
- Refresh rate and HDR: the term, which adds 2.32 W for a 1440p monitor at 165 Hz and nothing without a resolution or a
  rate; the display configuration reader against a fake Windows layer: a monitor read under its device path at its
  target's rate, a boosted path at its target mode's rate, Windows 10 asked again without the newer flag, HDR from the
  user's switch or from advanced color, a monitor Windows gives no color state for taken not to have HDR on, a rate not
  to believe or a path without a monitor giving nothing, and only the calls that describe displays declared; on this
  machine behind `Category=Hardware`. The reporter reads every minute, and sends power states or display settings alone
  when nothing else was read.
- The board: figures, choices, the profile's default for counting, a laptop's small monitor taken to run off it and the
  user's word over the guess, a monitor running off the PC counted whatever its choice, brightness going stale, a
  monitor unplugged, a monitor keeping its figure when the modes fail to answer once, and callers on several threads at
  once; a list ignored once its sensor set is retired and, through the real worker, display source and board, the
  monitors an abandoned set finds when its stuck read returns never reaching the board; a monitor that says it is off
  drawing its off figure and one on standby its sleep figure, whatever the displays do and whatever was typed, and
  keeping the split between plugs; a power state going stale after three minutes, a display reading kept while it is
  fresh, and nothing going stale while a reading says the displays are off; the refresh term for an LCD monitor on or
  unknown with the displays on, and none for an OLED panel, a typed figure or an unknown resolution or rate; HDR
  changing no watts; reports finding their monitors by instance and forgetting one not attached; a brightness report
  over a real pipe, a bad one refused, the limits of its power states and display readings, and one from an older App
  that arrives without them.
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
- Settings saving itself: a tick or a segmented choice sent at once and once; a typed value sent once its box gives it
  to the form, which the render checks by leaving a box and by pressing Enter, and not before; a value that can't be
  sent named and nothing sent until it is put right; loading, the ten-second status refresh and the service coming up
  sending nothing; changes made while a save is on its way sent together after it, the last one winning, with no box
  filled again; a monitor ticked still ticked after leaving Settings and coming back; the wizard's form sending nothing
  until it is saved.
- The labels: the Now screen's display row, with how many of its monitors are off or on standby, HDR, and "estimated"
  for a refresh term, and its live note; a monitor row's state, what it draws now with its refresh term and HDR, and its
  refresh rate to a hundredth of a hertz; Breakdown's footnote for any range and machine; and the report's quality
  legend.
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
- Whether a monitor is on is known only from what the monitor says. Many monitors stop answering once switched off at
  their own button, and some docks and adapters pass nothing on; for those PowerLedger can't tell, and the Count it tick
  decides. A monitor switched off can take a minute to be noticed, and one that stops answering counts at its figure on
  again once three minutes of the displays being on have passed since it last said, if Windows still lists it as active.
  One that drops off the cable when switched off is taken as unplugged at the next display query.
- The refresh term is fitted to two measurements, which alone give 0.003 to 0.010 W a megapixel a hertz. With variable
  refresh on, a game drawing fewer frames runs the monitor slower than the rate Windows reports, and no ordinary program
  can read the actual rate, so the term is an upper bound then. The graphics card's own extra draw at a high refresh
  rate is already in its reading.
- Nothing is known of what a higher refresh rate adds for an OLED panel, so a monitor the list gives an OLED panel adds
  nothing for it, and an OLED monitor the list doesn't know is estimated as an LCD one, the term included.
- HDR can double what a monitor draws, and no figure per model is known, so a monitor with HDR on counts as if HDR were
  off, and its row and the Now screen say it may draw much more.
