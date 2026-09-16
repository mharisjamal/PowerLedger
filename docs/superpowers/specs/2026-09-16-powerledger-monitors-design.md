# Monitors that know themselves — design

Status: awaiting the owner's approval. Follows the research of 2026-09-16 (Energy Star and EPREL registries, measured
power-versus-brightness data). Changes the main spec's §5, §9, §12 and §16.

## Goal

Nobody should type how many monitors they have or what they draw. PowerLedger reads which monitors are attached, works
out what each one uses, and only asks when it genuinely doesn't know. The numbers must be honest: every monitor figure
says where it came from.

## What is wrong today

- The wizard asks for a count of external monitors and a watts-per-monitor figure, even on a laptop with one screen.
- The default is **25 W per monitor**. The median certified monitor draws **14.3 W**; a 24" 1080p about **12.9 W**. The
  default overstates a typical monitor by about 75%, and is too high for 86% of the models in the Energy Star list.
- The sleep default, 0.5 W, is the EU's legal ceiling. The certified median is **0.2 W**.

## Owner's choices

| Question | Choice |
|---|---|
| Read each monitor's brightness over the display cable | Yes, carefully: read-only, capability-gated, cached |
| A monitor that isn't in the table | Ask during setup, with the estimate filled in |
| The wizard on a laptop with no external screen | Skip the monitor question entirely |

## Where the numbers come from

**The Energy Star certified-display list** (`data.energystar.gov`, dataset `qbg3-d468`): 1,585 monitors with measured
on-mode, sleep and off watts, screen size, resolution and panel type. US government work, public domain, no key, no
obligation to stay fresh. A script distils it to `brand, key, inches, pixels, on_w, sleep_w` — about **5,100 rows, 25 KB
compressed** — and the result is committed and shipped inside PowerLedger, so nothing is fetched at runtime.

The EU's EPREL registry was considered and rejected: its terms oblige anyone storing the data locally to keep it current,
which an offline installer cannot promise, and its figures run about **1.5× higher** than Energy Star's for the same
monitor because the test differs. Mixing the two would make two identical monitors report different power.

Attribution, in About and the README: the figures come from the US EPA's Energy Star certified-display list; PowerLedger
is not certified by or affiliated with Energy Star, and doesn't use its mark.

## How a monitor's power is worked out

1. **Which monitors.** `WmiMonitorID` gives each display's maker, model name and product code; `WmiMonitorBasicDisplayParams`
   its physical size; Windows' display settings its resolution. Internal panels are already told apart from external ones
   and keep their existing treatment (the panel is part of the laptop's own draw).
2. **Recognised model.** The model name is normalised (upper case, letters and digits only, maker prefix dropped, trailing
   revision letters dropped) and looked up against an index built from the list's model number, model name and the
   alternative identifiers it carries, anchored rather than by substring. A hit must also agree with the monitor's
   physical size to within an inch, which is what stops vague names like "LG HDR 4K" matching the wrong panel.
3. **Unrecognised model.** A figure from size and resolution: the median of the certified monitors of that size and
   resolution class where there are at least three, otherwise Energy Star's own area-and-pixels formula. Measured against
   the list itself, this lands within **±9%** for half of monitors and ±25% for nine in ten — as good, in practice, as a
   model match while brightness is unknown.
4. **Brightness.** Where the monitor answers a read over the display cable (DDC/CI: capability check first, then
   brightness), the figure is scaled. Power is close to linear in screen luminance, and a monitor's fixed electronics
   are 28–51% of its full-brightness draw, so PowerLedger uses `P(b) = P_full × (0.45 + 0.55 × b)`, with the listed
   figure taken as its 75% point. A monitor that doesn't answer is assumed to sit at that same 75%.
5. **Anything else.** The typed override stays, per monitor, and always wins.

Each monitor's figure carries how it was arrived at — measured for this model, estimated from its size, or typed — and
the quality shown for the display component follows the weakest of them.

## Talking to monitors, carefully

Microsoft warns that many monitors implement the display-cable commands badly. So: ask `GetMonitorCapabilities` once per
monitor and only continue when it reports brightness; never write, only read; treat any failure as "no answer" and stop
asking that monitor for the rest of the session; read at most every five minutes and never on the sampling tick (a read
takes about 40 ms); and do all of it on a background thread. Settings lists which monitors answered, and a switch stops
PowerLedger talking to monitors at all.

## What the user sees

- **The wizard.** With no external monitor, the monitor question is gone. With one or more, they are listed by name with
  their size and the figure PowerLedger worked out; the only question is whether to count them (they are counted by
  default), and any figure can be corrected there. A monitor that isn't recognised says so plainly — "estimated from its
  size" — and invites a correction.
- **Settings.** A row per monitor: name, size, watts, where the figure came from, whether brightness could be read, a
  typed override, and whether it counts. Adding a monitor later shows it here without another wizard.

## Components

- `src/PowerLedger.Sensors/Monitors/MonitorInventory.cs` — what Windows says is attached, external only.
- `src/PowerLedger.Sensors/Monitors/MonitorCatalogue.cs` — the shipped table, its index, and the normalisation.
- `src/PowerLedger.Sensors/Monitors/MonitorEstimate.cs` — the size-and-resolution figure and the brightness scaling.
- `src/PowerLedger.Sensors/Monitors/MonitorBrightness.cs` — the capability-gated, read-only DDC/CI reader.
- `assets/monitors/energy-star-displays.csv.gz` and `assets/monitors/make-monitor-table.ps1` — the shipped table and the
  script that rebuilds it from the public dataset.
- The service's settings gain a list of monitors (id, whether counted, typed watts) in place of a count and one figure;
  old settings are migrated by keeping the count and the typed figure as an override.

## Testing

- The catalogue: the awkward names the research found — `U2723QE` against `U2723QEt`, Acer's `B196L_q`, HP's "HP 322pb
  Monitor", BenQ's `GW2480-B`, Lenovo's internal part code, and "LG HDR 4K" matching nothing without a size to agree with.
- The estimate: held-out rows from the list, asserting the median and 90th-percentile error stay within the figures above.
- Brightness: the scaling function against the measured ratios; the reader itself behind `Category=Hardware`.
- The wizard: no external monitor means no monitor step; one external monitor means a named row with a figure.
- Migration: an existing install keeps counting what it counted, with its typed watts.

## Honest limits

- Monitors older than about 2019 are mostly absent from the list, so the estimate is the common path, not the exception.
- Brightness is the largest remaining error. Where it can't be read, a monitor's figure can be out by a third in either
  direction, because the same monitor draws about half as much dim as it does bright.
- OLED monitors vary with what is on screen as much as with brightness; they are estimated like any other panel, and
  their figure says "estimated".
