# Monitors that know themselves — design

Status: built in Plan J (`docs/superpowers/plans/2026-09-16-powerledger-j-monitors.md`), and corrected to match what was
built and the decisions made at merge. Follows the research of 2026-09-16 (ENERGY STAR and EPREL registries, measured
power-versus-brightness data). Changes the main spec's §3 to §6, §8, §9, §11, §12, §14 and §16.

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
2. A monitor's choice is saved only when it says something: not counted, or a typed figure. Choices for monitors not
   attached now are kept, most recently seen first, within the limit of 16.
3. When the form lists monitors, in the wizard or in Settings, saving clears the old count fields, so nothing from
   before monitors were detected is carried over after it.

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
   inch; a size under 10" is an aspect ratio or nonsense and counts as unknown. A monitor is keyed by maker, product
   code and serial number, or by its device instance when the serial number is empty or all zeros, or shared by two
   attached monitors. The service's monitor board is told only when the monitors change, and works out a figure only for
   a monitor that is new or changed.
2. **Recognised model.** EDID's maker code gives the brand the list uses (`MonitorMakers`); a code without a brand there
   matches any brand. Both sides are normalised the same way (upper case, letters and digits only, without the words
   "monitor" and "display", and without the brand's name or maker code in front) and matched whole, never by substring,
   against an index of the list's model number, model name and the alternative identifiers it carries. The index also
   holds Dell's identifiers without their revision letter (`U2723QEt`), Acer's without their suffix (`B196L_q`), and the
   last word of an identifier of several, because monitors leave out the series word (`FlexScan EV2740X`). EDID holds at
   most 13 characters of a name, so a name that long also matches the identifiers it begins. Placeholders at the end of
   an identifier (`*`, `#`, `?`, or a run of three or more `X` or `Y`) stand for up to as many characters. A hit must
   also agree with the monitor's size to within an inch, which is what stops vague names like "LG HDR 4K" matching the
   wrong panel; for a monitor that gives no size, the listings its name matches must agree on one. Among the hits, the
   monitor's resolution wins, then a whole name over a placeholder, then the closest size, and a monitor listed more
   than once takes the median of its listings.
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
5. **Anything else.** The typed override stays, per monitor, and always wins. It is used exactly as typed, never scaled
   by brightness. Unticking a monitor leaves it out.

Each monitor's figure carries how it was arrived at — measured for this model, estimated from its size, or typed — and
the wizard and Settings show it beside the figure. The reading's quality label is the power model's, as before; the
monitors' sources don't change it.

## Talking to monitors, carefully

The service runs in session 0, where the monitors can't be reached, so the App reads them in the user's session
(`DdcBrightness`) and reports what it read (`BrightnessReporter`). Microsoft warns that many monitors implement the
display-cable commands badly. So:

- Ask `GetMonitorCapabilities` once per monitor and only continue when it reports brightness, waiting 50 ms after it
  answered before asking for the brightness. Those are the only two requests a monitor is sent.
- Never write, only read.
- A monitor that reports no brightness is not asked again while the App runs. A monitor that fails any call is left
  alone for an hour, since it may only have been asleep, and then asked afresh, capabilities first.
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
  worked out; the only question is whether to count them (they are counted by default), and any figure can be corrected
  there. A recognised monitor says "measured for this model". One that isn't says so plainly — "estimated from its size
  — correct it if you know better" — and invites a correction.
- **Settings.** A row per monitor: name, size and resolution, watts, where the figure came from, what it draws now,
  whether brightness could be read ("brightness 60%, read from the monitor" or "brightness unknown, assumed 75%"), a
  typed override, and whether it counts. Clearing a typed figure goes back to PowerLedger's own. The rows follow the
  service's status every ten seconds without losing a figure being typed, so adding a monitor later shows it here
  without another wizard. With no external monitor, the row says "none detected". About credits the ENERGY STAR list.
- **Now and Breakdown.** The Now screen's display row adds the monitors counted ("plus 2 monitors"), and Breakdown's
  footnote says how many the display band holds.
- **Saving.** The wizard and Settings save the monitors' choices by the rules decided at merge.

## Components

- `src/PowerLedger.Sensors/Monitors/MonitorInventory.cs` — what Windows says is attached, external only.
- `src/PowerLedger.Sensors/Monitors/MonitorCatalogue.cs` — the shipped table, its index, and the normalisation.
- `src/PowerLedger.Sensors/Monitors/MonitorMakers.cs` — EDID maker codes and the brands the list spells them as.
- `src/PowerLedger.Sensors/Monitors/MonitorEstimate.cs` — the size-and-resolution figure.
- `src/PowerLedger.Sensors/DisplaySource.cs` — reads the monitors with its WMI query and hands them over when they
  change.
- `src/PowerLedger.Core/MonitorPower.cs` — the brightness scaling, and `IMonitorDraw`, which the power model asks.
- `src/PowerLedger.Service/MonitorBoard.cs` — the service's monitors: each one's figure, the user's choices and the
  brightness reported; what the model counts and the status lists.
- `src/PowerLedger.Contracts/Monitors.cs` — a monitor's choice, status and brightness, and `MonitorKeys`, one name for a
  monitor whether WMI or a device path gives it. `PipeMessages.cs` adds `reportBrightness`.
- `src/PowerLedger.App/Monitors/DdcBrightness.cs` — the capability-gated, read-only DDC/CI reader, in the App because
  session 0 can't reach the monitors.
- `src/PowerLedger.App/Monitors/BrightnessReporter.cs` — reads the monitors on its schedule and reports to the service.
- `src/PowerLedger.App/Monitors/MonitorRow.cs` — one monitor's row in the wizard and Settings.
- `assets/monitors/energy-star-monitors.csv`, `assets/monitors/make-monitor-table.ps1` and `assets/monitors/README.md` —
  the shipped table, the script that rebuilds it from the public dataset, and where it comes from.
- The service's settings gain a list of monitor choices (key, whether counted, typed watts) in place of a count and one
  figure. The old fields stay only so old settings can be carried over. `ProfilePolicy` (in
  `src/PowerLedger.Service/SettingsStore.cs`) carries them over once, at the first tick that finds a monitor: monitors
  the old settings counted stay counted, at the old figure, or at each one's own figure when the old one was the default
  of 25 W; monitors they didn't count stay uncounted. The old fields are then cleared and the settings saved; if the
  save fails, the next start tries again.

## Testing

- The catalogue: the awkward names the research found — `U2723QE` against `U2723QEt`, Acer's `B196L_q`, HP's "HP 322pb
  Monitor", BenQ's `GW2480-B`, Lenovo's internal part code, and "LG HDR 4K" matching nothing — and placeholders, names
  cut short by EDID, series words, ties on resolution and size, and monitors listed more than once.
- The estimate: each certified monitor estimated from all the others; the test holds the median error to 12% and the
  90th percentile to 35%, margins above the 9.04% and 25.6% measured.
- The inventory: WMI rows with a built-in panel, an inactive monitor, zero-padded names, no native mode, serial numbers
  missing or shared, and the snapped diagonals; on this machine behind `Category=Hardware`.
- Brightness: the scaling function (the listed figure at 75%, the fixed share with none, a straight line between); the
  reader's rules against a fake Windows layer; the reader itself behind `Category=Hardware`; the reporter's schedule and
  the preference.
- The board: figures, choices, brightness going stale, a monitor unplugged, and callers on several threads at once; a
  brightness report over a real pipe, and a bad one refused.
- The wizard: no external monitor means no monitor rows and nothing saved about monitors; detected monitors are listed
  by name, size, figure and source; a figure typed for an estimated monitor is saved as its watts. The rendering test
  draws two monitors in the wizard and in Settings.
- Migration: an existing install keeps counting what it counted, with its typed watts, once a monitor is detected; one
  that didn't count monitors leaves them out; a fresh install carries nothing over.

## Honest limits

- Monitors older than about 2019 are mostly absent from the list, so the estimate is the common path, not the exception.
- Brightness is the largest remaining error. Where it can't be read, the figure assumes 75%, and the monitor may draw as
  little as about half of it, fully dimmed, or about a sixth more, at full brightness: the same monitor draws about half
  as much dim as it does bright. A typed figure doesn't follow brightness at all.
- DDC/CI gives a setting between the monitor's own minimum and maximum, with no real-world unit, so two monitors at the
  same setting can be differently bright.
- OLED monitors vary with what is on screen as much as with brightness. They are treated like any other panel: one the
  list knows takes its listed figure, and one it doesn't is estimated and says so.
