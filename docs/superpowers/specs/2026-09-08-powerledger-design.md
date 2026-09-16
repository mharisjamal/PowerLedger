# PowerLedger — Design Specification

**Date:** 2026-09-08
**Status:** Approved in brainstorm, awaiting implementation plan
**Placeholder name:** PowerLedger (rename any time; only the solution/namespace prefix changes)

## 1. Summary

PowerLedger is a free Windows desktop application that records how much electrical power a PC draws over time and turns that record into an energy bill: kilowatt-hours, cost at the user's tariff, CO₂, and a breakdown by component. Tools such as HWMonitor and HWiNFO show instantaneous voltages and watts but keep no history and produce no report. PowerLedger fills that gap.

The core loop is: **sample sensors once per second → convert to a whole-system power reading with an honesty label → integrate to energy → store → show live and historical views → export reports.**

### Goals (v1)

- Continuous logging from boot, without the UI open, surviving sleep and resume.
- Whole-system watts with an explicit quality label: **Measured**, **Calibrated**, or **Estimated**.
- Energy bill for any range: kWh, cost, average and peak watts, hours on / idle / asleep.
- Component breakdown over time: CPU, GPU, display, rest of system.
- Extras: CO₂ estimate, everyday comparisons, idle-waste detection with a concrete saving suggestion.
- Exports: PDF, CSV, PNG. Automatic monthly PDF.
- Lightweight: service under 0.5 % CPU and 50 MB RAM at idle.
- Commercial-grade quality, free to the public, no GPL dependencies, no paid SDKs.

### Non-goals (v1)

- Per-application energy attribution (planned v1.1).
- Smart-plug or PSU telemetry integration (planned v1.1).
- Time-of-use tariffs (schema leaves room; UI later).
- macOS and Linux.
- Cloud sync, accounts, telemetry.

## 2. Users and scope decisions

| Decision | Choice |
|---|---|
| Audience | Free public Windows app, laptops and desktops |
| Measurement sources v1 | Sensors + estimate model only; zero extra hardware |
| Report content v1 | Energy bill + component breakdown + extras (CO₂, comparisons, idle waste) |
| Per-app attribution | v1.1 |
| Stack | .NET 10 LTS, WPF + WPF-UI, SQLite; sensors are plain Win32 and WMI |
| Process model | Windows Service (sampler) + unelevated WPF tray app (UI) |
| Visual direction | "Meter & Ledger": instrument-panel dark theme, bench-sheet light theme |
| Repo | `D:\PowerLedger`, license MIT unless the owner decides otherwise |

## 3. Architecture

```
┌──────────────── PowerLedger.Service (Windows Service, LocalSystem) ────────────────┐
│  Sensors ──► Sampler (1/s) ──► Validator ──► PowerModel ──► Writer ──► SQLite (WAL)  │
│   Energy meter, GPU                              │             ▲                     │
│   Battery, Display, Activity                     ▼        Downsampler + retention    │
│                                    NamedPipe server: live readings, status, settings │
└──────────────────────────────────────────┬────────────────────┬─────────────────────┘
                                           │ pipe (live)        │ SQLite read-only (history)
┌──────────────────────────────────────────▼────────────────────▼─────────────────────┐
│  PowerLedger.App (WPF, user session, unelevated)                                    │
│  Tray icon · Now · Breakdown · Report · Settings · First-run wizard · Monthly PDF   │
└─────────────────────────────────────────────────────────────────────────────────────┘
```

### Projects

| Project | Purpose | Depends on |
|---|---|---|
| `PowerLedger.Core` | Pure domain: `PowerModel`, calibration learner, energy integrator, downsampler, tariff/cost/CO₂ math, report builder. No I/O. | nothing |
| `PowerLedger.Contracts` | Pipe message DTOs, settings model, shared enums (`Quality`, `SessionReason`). | nothing |
| `PowerLedger.Sensors` | `ISensorSource` and adapters: `EnergyMeterSource` (CPU rails), `NvidiaSource` (discrete GPU), `BatterySource`, `CpuLoadSource`, `DisplaySource`, `ActivitySource`, `HardwareInventory`. Validator lives here, and so do the external monitors' inventory, catalogue and estimate. | Core, Contracts |
| `PowerLedger.Storage` | SQLite schema, migrations, batched writer, retention jobs, read-side query API. | Core |
| `PowerLedger.Service` | Worker host: sampler loop, writer, downsample scheduler, pipe server, power/session event handling, the monitor board. | all above |
| `PowerLedger.App` | WPF UI, tray icon, charts, exports, monthly report, wizard, the monitors' brightness reader. | Core, Contracts, Storage (read-only) |
| `installer/` | Inno Setup script: one installer holding the x64 and Arm64 builds, runtime included. No driver to bundle. | build output |
| `tests/*` | One xUnit project per library plus a Service integration test project. | |

### Rules

- `Core` never touches sensors or the database; it is 100 % unit-testable.
- `Sensors` never computes totals; each adapter returns nullable raw values and a capability flag.
- The App never writes the database. Settings changes travel over the pipe to the Service, which writes them.
- One adapter per file, one job per adapter. A file that grows past a few hundred lines is a signal to split.

## 4. Sensors and sampling

Sampling runs at 1 Hz (configurable 1–5 s). Every source is read inside its own try/catch with a per-source timeout; a failing source yields `null` for that tick and retries with backoff. A failing source never stops the loop.

| Source | Reads | Mechanism | Needs driver |
|---|---|---|---|
| CPU | package W, cores W, iGPU W, DRAM W, load | Windows Energy Meter Interface, which Windows 11 fills with the processor's RAPL rails, read through the `Energy Meter` performance counters as raw picowatt-hour values in well under a millisecond. `GetSystemTimes` for load | no |
| Discrete GPU | power W, load, present | NVML through the installed NVIDIA driver (`nvml.dll`, loaded from System32 only). A card Windows has switched off (D3, read from the device's power data without waking it) counts as 0 W and is not queried. Many laptop GPUs, the GeForce MX330 included, report no power at all; those fall back to the load model. An AMD Radeon RX or Intel Arc card (found through DXGI by vendor, a discrete model name and at least 1 GB of its own memory; integrated graphics are inside the CPU package reading) has no power reading, so its load, the busiest engine in Windows' `GPU Engine` performance counters, read every 5 s, feeds the same load model with the card's rated power from the TDP table | no |
| Battery | discharge/charge rate mW, AC line status | `CallNtPowerInformation(SystemBatteryState)` (Rate is negative when discharging). A UPS on USB also appears as a battery. It powers more than the machine, so it is ignored when Windows flags it short-term in `SystemPowerCapabilities`, and a known desktop enclosure (below) keeps an unflagged one from making the machine a laptop | no |
| Display | brightness %, display on/off, monitor count and size | WMI `WmiMonitorBrightness` ("not supported" on a desktop means no brightness), `PowerSettingRegisterNotification(GUID_CONSOLE_DISPLAY_STATE)` with a callback, `WmiMonitorBasicDisplayParams` + `WmiMonitorConnectionParams`: the built-in panel is the monitor with an internal or embedded connection, never simply the first one listed | no |
| External monitors | maker, product code, serial number, name, size, native resolution | WMI `WmiMonitorID`, `WmiMonitorConnectionParams`, `WmiMonitorBasicDisplayParams` and `WmiMonitorListedSupportedSourceModes` (the preferred mode), joined by device instance. Built-in panels are left out by the same connection test, and so is a monitor Windows lists but isn't showing anything on. EDID gives the size in whole centimetres, so the diagonal is snapped to the nearest common panel size within 0.5"; a size under 10" is an aspect ratio or nonsense and counts as unknown. When any of the first three classes fails, the read gives no answer rather than no monitors; a class WMI refuses as having no instances ("Not supported", as once no display is left, or "Invalid class") is read as none, so once the last monitor is unplugged the read answers no monitors. A monitor a read gives no resolution, because the modes class failed or left it out, keeps the one last read for its device instance while the service runs. A monitor is keyed by maker, product code and serial number, or by its device instance when the serial number has fewer than four characters or is one character repeated, or once two attached monitors have been found sharing it, for as long as the service runs | no |
| Monitor brightness | 0–1 for each external monitor that answers | DDC/CI through Dxva2: `GetMonitorCapabilities`, then `GetMonitorBrightness`. Read by the App, because session 0 can't reach the monitors, and reported over the pipe (§8); read-only and capability-gated (§11) | no |
| Activity | user idle seconds, session locked | `GetLastInputInfo`, which only describes the caller's own session, so the App reports it over the pipe; session-change events from the service control manager for the lock state | no |
| Fans | count only | Taken from the machine profile, not measured; reading fan tachometers needs a kernel driver and buys about a watt | no |

### No kernel driver anywhere

PowerLedger installs no driver and needs no elevation to read a sensor. That is a deliberate constraint, and Windows 11 makes it affordable.

Reading CPU package power used to mean reading model-specific registers, which means ring 0, which means shipping a kernel driver. Windows 11 removes that: the inbox processor power-management driver publishes the processor's RAPL rails through the **Energy Meter Interface**, an ordinary device interface any process can open for reading. Verified on the development laptop from a non-elevated process: package 5.59 W, cores 1.83 W, integrated graphics 0.07 W, memory 0 W, alongside a monotonic picowatt-hour energy counter.

Consequences that shape the rest of the design:

- The sensor layer is plain Win32 and WMI. No third-party sensor library, no driver installer, no mixed licences, and nothing for Windows memory integrity or an anti-cheat to object to.
- The installer stays small and the service could run unelevated if it ever needed to. It runs as LocalSystem for session and power events, not for sensors.
- **Windows 10 is the caveat.** The interface exists there, but only machines with real metering hardware populate it. On a Windows 10 machine with no rails, CPU watts are unavailable and the model falls back to `idle + (TDP − idle) × load`, quality **Estimated**, exactly as on a desktop with no battery. The source must treat an empty rail list as "no value", never as zero.
- Two rails are worth more than their names suggest: the integrated-graphics rail makes iGPU power measured rather than guessed, and the memory rail replaces the RAM estimate where it is populated. Neither is present on every processor; the development laptop reports the first and zeroes the second.

Battery remains the best whole-system signal on a laptop, and is the only source that measures the machine rather than its parts.

### Performance rules

- Only CPU, GPU, and battery are updated every tick (about 2 ms).
- The display source's WMI queries run at a sensor set's first tick (at start, after a resume, after a read that hung) and then once a minute; every tick in between reuses their answer. The external monitors are read with them, on the sensor thread, and handed to the monitor board (§5) when WMI first answers and then only when they change. They are read only after the display query has just succeeded, and a read that gets no answer hands nothing over, so a WMI hiccup doesn't unplug them. The display query reads its classes as the inventory does, taking a class refused as having no instances as none, so once the last monitor is unplugged it still succeeds and the empty list is handed over within a minute. A set abandoned because a read hung may finish that read after its replacement has handed the monitors over; what it finds then is ignored, because each set's token is cancelled before its replacement is built and the board checks it under its lock.
- Motherboard / SuperIO chips are read once at startup for fan count, then disabled.
- SMART / storage sensors are never polled (slow, can spin up disks).
- `HardwareInventory` runs at service start and on resume, not per tick.

### Sample record

```
Sample {
  ts (UTC), monotonicΔt,
  cpuPackageW?, iGpuW?, cpuLoad, dGpuW?, dGpuLoad?, dGpuPresent,
  batteryRateW?, onBattery, brightness?, displayOn, monitorCount,
  userIdleSeconds, sessionLocked
}
```

### Validator

Runs before the model. Rejects implausible values and marks the sample **suspect** rather than throwing.

- Plausible ranges: CPU 0–400 W, GPU 0–700 W, battery 0–300 W.
- Single-tick spike: a GPU or battery value more than 3× the rolling 30-sample median → reuse the last good value, count as suspect. A second such value in a row is a real rise: accept it and restart the median, or a sustained change would be rejected forever. CPU watts from the energy meter are exact over their tick, so they are range-checked only.
- A rail whose energy counter goes backwards (wrap or reset) reports nothing that tick.
- First 3 s after an AC↔battery transition: battery value excluded from calibration; quality still switches immediately. The transition also restarts the battery median.
- Suspect counts are exposed in status so users can see sensor health.

## 5. Power model and calibration

The model converts a `Sample` into a `Reading`:

```
Reading { ts, totalW, quality, components: { cpu, gpu, display, ram, storage, board, extras, psuLoss, monitors, rest }, flags }
```

### Modes

1. **Laptop with a usable discharge rate** (on battery; rate finite and above zero, since zero or negative means charging or a transition blip) → `totalW = batteryRateW + ownPlugMonitors`. Quality **Measured** (±3 %). The battery delivers what the monitors running off the laptop draw, so the rate already holds them and only monitors with a plug of their own are added. Components are still computed for the breakdown; `rest = measured − cpu − gpu − display − monitorsFromPc`, which may go negative when the parts over-report (the honest sensor-disagreement signal), and the monitors component holds both kinds. `Components.Sum` always equals `totalW`.
2. **Any machine on AC** → `totalW = (cpu + gpu + display + baseline + monitorsFromPc) / psuEfficiency + ownPlugMonitors`. A monitor with a plug of its own sits outside the PC's supply, so it is added after the supply-efficiency division and never contributes to PSU loss; a monitor running off the PC, as a portable monitor on a laptop's USB-C port does, draws through the supply like any other part.
   - Quality **Calibrated** (±10 %) when a learned baseline exists for the current brightness bucket (see below).
   - Quality **Estimated** (±20 %) otherwise.

Desktops divide by PSU efficiency; laptops use 1.0 on battery and 0.90 (adapter efficiency) on AC. Desktops are always Estimated: battery rates (a UPS) and learned baselines apply to laptops only. A learned baseline was observed on battery and therefore already contains any extras drawing from the battery, so `Extras` is reported as 0 in calibrated mode. It doesn't contain the monitors running off the laptop, which the learner takes out with the display.

### Baseline auto-calibration (laptops)

Every second on battery provides ground truth: `baselineObserved = measured − cpu − dGpu − displayModel − monitorsFromPc`, where the last is what the monitors running off the laptop drew, at the figure the same reading used: the battery delivered it, but it is no part of the laptop's rest. The learner keeps one running average per brightness bucket (10 % steps, plus a display-off bucket), keyed by the hardware inventory hash, with an exponential weighting whose half-life is 10 minutes of samples. A young bucket uses a plain running mean until it holds roughly one half-life of samples, so the first tick after a brightness change does not dominate. Residuals are averaged as they are, negative ones included (a clamp would bias the baseline upward), and the reported baseline is clamped at zero. Suspect samples, samples with non-finite parts, and the 3 s after transitions are excluded. The learner is thread-safe because the Service samples on one thread and exports or resets from the pipe thread.

A bucket counts as calibrated once it holds ≥ 5 minutes of samples and the machine has ≥ 30 minutes of battery samples in total. Calibrated baselines replace the defaults in AC mode. Calibration progress and a reset button appear in Settings.

### Default estimate values (used until calibrated, or on desktops)

| Component | Default |
|---|---|
| Laptop baseline (board, RAM, SSD, radios) | 5 W |
| Built-in panel (a laptop's, or an all-in-one's when its size is known) | `1.5 W + 4.5 W × brightness`, scaled ×0.8 up to 14", ×1.0 above 14" and below 17", ×1.3 from 17" and below 20", ×3.0 from 20" (an all-in-one's panel; diagonal from EDID; unknown = ×1.0) |
| Desktop board | 12 W |
| RAM per stick | DDR4 2.5 W, DDR5 1.5 W (`Win32_PhysicalMemory.SMBIOSMemoryType`) |
| Drive | SSD 2 W, HDD 6 W (`MSFT_PhysicalDisk.MediaType`) |
| Fan | 1 W each, counted from the machine profile rather than measured |
| Extras (RGB, pumps, USB devices) | user slider 0–100 W, default 0 |
| External monitor | each one detected counts: one running off the PC always, one with a plug of its own unless the user leaves it out or settings carried over from before monitors were detected left monitors out. While the display is on, its ENERGY STAR on-mode figure or an estimate from its size and resolution, at its brightness, or a figure the user typed; while off, its sleep figure, 0.2 W where none is known (see External monitors below) |
| PSU efficiency | 80+ White 82 %, Bronze 85 % (default), Silver 87 %, Gold 90 %, Platinum 92 %, Titanium 94 % |
| dGPU without power sensor | `3 W + (TDP − 3 W) × load`; TDP from a bundled model table, else 25 W laptop / 75 W desktop; user override |
| CPU without power sensor | `idle + (TDP − idle) × load`, idle 2 W laptop / 8 W desktop; TDP from bundled table, else 15 W laptop / 65 W desktop; user override |

The model tables ship as JSON resources in `Sensors` (`tdp-table.json`) and are editable via Settings overrides.

### External monitors

The service keeps the external monitors on one board (`MonitorBoard`): what detection found (§4), each one's figure, the user's choices, the profile's default for counting, and the brightness the App last reported. The model asks it every tick what the monitors draw, split between the monitors with a plug of their own and those running off the PC, and the status lists them (§8). Detection, the App's reports and the loop reach it from different threads, so it takes one lock. Each monitor's figure says where it came from: the list, the estimate, or the user.

- **A model the list knows.** Its figure is ENERGY STAR's measured on-mode and sleep watts, from the Certified Displays list: 1,580 monitors in `assets/monitors/energy-star-monitors.csv`, embedded in `Sensors`, so nothing is fetched at runtime. `assets/monitors/README.md` says where the list comes from and how it is cleaned. EDID's maker code gives the brand the list uses (`MonitorMakers`), including the codes ASUS's and AOpen's monitors report though the registry doesn't give them to those makers (`ACI`, `AOP`). Names are compared as upper-case letters and digits, without the words "monitor" and "display" and without the brand's name or maker code in front, and matched whole, never by substring. A listing is found under its model number, its model name and its alternative identifiers, also without Dell's revision letter or Acer's suffix, and under the last word of an identifier of several, since monitors leave out the series word. A name of 13 characters, all EDID holds, also matches the longer identifiers it begins, and placeholders at the end of an identifier stand for up to as many characters. Only a whole identifier is an exact name, since the others can take in other models: a maker code without a brand matches exact names only, and a placeholder or a name cut short matches only a listing at the monitor's resolution, either way round, when the monitor gives one, while an exact name or a series word keeps its listing at any resolution. A match must agree with the monitor's size to within an inch; for a monitor that gives no size, the listings its name matches must agree on one. Among matches, those at the monitor's resolution win, then a whole name over a placeholder, then the closest size; a monitor listed more than once takes the median of its listings.
- **A model the list doesn't know.** Its figure is estimated from its size and resolution (`MonitorEstimate`): the median of the certified monitors in the same size class and resolution class when there are at least three, otherwise ENERGY STAR Displays v8's yearly allowance for its area and pixels (§3.3.2, Table 1), scaled to what certified monitors typically draw: `0.296 × E_TEC_MAX − 0.5` W, never under 3 W. A monitor that gives no size or resolution takes the median of all of them. Listings over 57" take no part, and a bigger screen takes the formula. Estimated from all the others, each certified monitor lands within 9.04% of its measured figure for half of them and within 25.6% for nine in ten.
- **Brightness.** Power is close to linear in screen luminance, and a monitor's fixed electronics are 28–51% of its full-brightness draw (measured by TechPowerUp, 2021–2026), so a listed or estimated figure is scaled with `MonitorPower.At`: `P = listed × (0.45 + 0.55 × b) / (0.45 + 0.55 × 0.75)`. The list's figure is taken as the draw at 75% brightness, and a monitor whose brightness is unknown is taken to sit there. Brightness comes from the App (§4, §8); a reading older than 15 minutes counts as unknown.
- **Where a monitor draws from.** A monitor has a plug of its own or runs off the PC, as a portable monitor on a laptop's USB-C port does. On a laptop, a monitor of 17.3" or less is taken to run off it; a larger one, one that gives no size, and every monitor on a desktop are taken to have a plug of their own, and the user's word wins over the guess. What a monitor running off the PC draws is part of what the PC draws, so it always counts: in measured mode it is already in the battery rate and isn't added, in every other mode it is divided by the supply or adapter efficiency with the other parts, and a monitor with a plug of its own is added after the division (Modes, above). The learner takes it out of the battery rate with the display (Baseline auto-calibration, above).
- **The user's choices.** A monitor with a plug of its own counts unless the user unticks it, or the profile's `CountMonitorsByDefault` is false and the user hasn't chosen for it. A figure the user types is used exactly as typed, never scaled by brightness. With the display off, a counted monitor draws its sleep figure. The profile keeps a choice for a monitor only when it says what the service wouldn't assume of a monitor without one: a plug other than the one guessed, a typed figure, or, for a monitor with a plug of its own, a count other than the default. Choices for monitors not attached now are kept, most recently seen first, within the limit of 16 (`ServiceSettings.MaxMonitors`).
- **Settings from before monitors were detected.** They held a count of external monitors, whether to include them, and one figure for all. At the first tick that finds a monitor they are carried over, once (`ProfilePolicy`): whether they included monitors becomes `CountMonitorsByDefault`, so a monitor with a plug of its own attached later is counted or left out as the old settings said, and if they included monitors at a figure other than the old default of 25 W, each monitor attached then is given that figure as if typed. The old count and the choice to include monitors are then cleared and the settings saved; if the save fails, the next start tries again. Settings saved before `CountMonitorsByDefault` existed read it as true. Saving from a form that lists monitors clears the old count and choice too, so nothing is carried over once the user has chosen.

### Machine profile detection

Chassis type from `Win32_SystemEnclosure.ChassisTypes` decides laptop vs desktop: a portable enclosure is a laptop and any other known one a desktop, even with a battery present, which on a desktop is a UPS; only an unknown enclosure falls back to battery presence. The energy meter counts as a CPU sensor only when it publishes a package rail. The wizard shows what was detected and lets the user correct it. Detected items: CPU and GPU names, RAM sticks and type, drives and media type, monitors and sizes, fan count, PSU tier (asked, not detectable).

## 6. Energy accounting

- Energy is integrated per tick: `Wh += totalW × Δt / 3600`, with Δt from a monotonic clock.
- If Δt is within the gap threshold the tick is counted at the current reading. Beyond it (sleep, hibernate, service stop, Modern Standby throttling) the tick contributes zero energy and the interval is recorded as a gap. The threshold is `max(5 s, 2 × sample interval)`, so a slower sampling setting never turns timer jitter into gaps. Non-finite readings contribute nothing.
- Energy attribution bands: CPU, GPU, display (the built-in panel plus the external monitors that count, §5), and rest (everything else, including RAM, board, PSU loss and the learned or measured remainder). In measured mode the rest band can go negative when the parts over-report; storage keeps it raw and the UI clamps and annotates at display time.
- Suspend and resume arrive through the power manager's callback registration (`PowerRegisterSuspendResumeNotification`), which works the same in a console run: on suspend the write buffer is flushed, the current minute folded and the session row closed, with the machine held up to 1.5 s; on resume a new session row opens, the sensor set is rebuilt, the hardware inventory re-runs, and the first Δt is measured by the wall clock so the sleep is recorded as a gap.
- Idle waste = energy of samples where `userIdleSeconds ≥ idleThreshold` (default 5 min, configurable 1–30), split into display-on and display-off. The saving suggestion reads the current Windows sleep timeout (`powercfg /query SCHEME_CURRENT SUB_SLEEP STANDBYIDLE`) so it can say "Windows currently sleeps after 30 min" or "never".
- Timestamps are stored in UTC; the UI converts to local time. Wall-clock jumps produce a session note, never negative energy.

### Sessions

`sessions` records the power-state timeline. Each row stores why it started (`boot`, `service-start`, `resume`, `crash-recovered`) and why it ended (`suspend`, `shutdown`, `service-stop`, or `crash-recovered` when the end was never written because the service died). "Asleep" hours in reports come from gaps between ticks; range time that produced no rows at all, such as before install, is reported separately as unmonitored (§9).

## 7. Storage

- Engine: SQLite via `Microsoft.Data.Sqlite`, hand-written SQL (no EF Core), WAL mode, `synchronous=NORMAL`, `auto_vacuum=INCREMENTAL`.
- File: `C:\ProgramData\PowerLedger\power.db`. ACL: SYSTEM and Administrators full control, Users read and execute, inherited by everything in the folder and nothing inherited from above. A database users could edit would feed crafted input to a SYSTEM process. A read-only SQLite connection reads a WAL database whose `-wal` and `-shm` it cannot write (verified with SQLite 3.51), so the App can read history while the service runs; with the service stopped the App shows it as not running. The service keeps the write-ahead log and shared-memory files after closing (`SQLITE_FCNTL_PERSIST_WAL`), because the App reads with Users' read-only access to the folder and could not create them, so history stays readable while the service is stopped. The service refuses a data folder another account owns, because anyone may create folders in ProgramData, and sets aside database files another account owns.
- Writes are batched: 60 samples per transaction (one per minute). The buffer flushes on suspend, shutdown and service stop. `ServiceBase` offers no preshutdown hook; a minute of readings writes in milliseconds, well inside the shutdown allowance.

### Tables

| Table | Grain | Retention | Notes |
|---|---|---|---|
| `samples_raw` | 1 s: `ts`, `total_w`, `quality`, per-component W, `on_battery`, `user_idle`, `display_on`, `locked`, loads, brightness, `suspect` | 48 h | ~25 MB |
| `samples_1m` | 1 min: avg/max W, `energy_wh` (integrated, not avg×60), per-component Wh, idle Wh (display on/off), battery seconds, dominant quality, gap seconds, sample count | 2 years | ~60 MB |
| `samples_1h` | 1 h: same aggregates | forever | tiny |
| `sessions` | `start_ts`, `end_ts`, `reason` | forever | |
| `tariffs` | `effective_from`, `price_per_kwh` (decimal as integer micro-units), `currency` (ISO 4217) | forever | |
| `calibration` | `inventory_hash`, `bucket`, `baseline_w`, `samples`, `updated_ts` | — | |
| `hardware_inventory` | `hash`, `detected_ts`, `json` | forever | one row per distinct hardware set |
| `settings` | key / value | — | service-owned settings |
| `schema_version` | | | migrations |

Cost is computed at query time as `Σ energy × tariff effective at that time`, so rate changes never corrupt history and "what-if" comparisons are possible. A currency change starts a new cost history: energy priced under an earlier currency still counts in kWh but is excluded from the cost figure, and the report flags the figure as partial. A future time-of-use tariff adds an hours mask column without rewriting data.

### Jobs

- Every minute: aggregate the previous full minute of raw rows into `samples_1m`.
- Every hour: aggregate `samples_1m` into `samples_1h`.
- Daily at 03:00 local: purge raw rows older than the raw retention (default 48 h, configurable 24–168 h) and 1-minute rows older than the history retention (default 2 years, configurable 1–5 years); run `PRAGMA incremental_vacuum` weekly. Never a full `VACUUM`. Hourly rows are never purged.
- Migrations run at service start inside a transaction, with `power.db.bak` written once per schema version bump through SQLite's online backup API, so uncheckpointed WAL content is included and the backup is a single self-contained file.

### Query API (Storage → App)

`GetLive(seconds)`, `GetSeries(from, to, resolution)`, `GetTotals(range)` → kWh, cost, avg/peak W, hours on / idle / asleep, per-component kWh, quality mix; `GetIdleWaste(range)`; `GetDailyBuckets(range)`; `GetSessions(range)`; `GetTariffs()`; `GetCalibrationStatus()`. `GetSeries` is `ReportQueries.Series`: it buckets the rows the totals read, so a chart and its totals agree. A range of up to three days reads minute rows throughout, so it is exact to the minute and a bucket under an hour has real data; an hour row stands in only where retention has purged the minutes. Each bucket's gap seconds are the sleep inside it, laid back from the moment the machine woke over the buckets the sleep covered.

## 8. IPC

- Named pipe `\\.\pipe\PowerLedger.v1`, ACL allowing local Authenticated Users, remote access denied.
- Newline-delimited JSON with `System.Text.Json` source generation. Messages carry `type` and, for requests, `id`. Max message 64 KB.
- Messages: `subscribe` (the server pushes a `reading` frame each tick, saying which parts were measured), `getStatus` (service version, per-source health and suspect counts, sensor restarts, calibration progress, DB size, write problems, and each external monitor with its key and device instance, name, size and resolution, on and sleep watts, where its figure came from, whether it counts and whether it has a plug of its own, what the service takes both to be when the user hasn't said, its brightness and what it draws now), `getSettings`, `setSettings`, `setTariff` (inserts a `tariffs` row; `effectiveFrom` defaults to now and may be backdated by the user), `resetCalibration`, `reportActivity` (the App's idle seconds every few seconds, because the service in session 0 cannot see input), and `reportBrightness` (the brightness the App read from the monitors the service lists, a minute after it starts and every five minutes, because session 0 can't reach the monitors).
- `reportBrightness` carries at most 16 readings, each a device instance of 1 to 260 characters and a brightness from 0 to 1, and is refused whole otherwise. The service keeps a reading only for a monitor attached now. A service from before monitors lists none in its status and disconnects a client that sends a kind it doesn't know, so the App reports brightness only to a service whose status lists monitors.
- Multiple clients supported. The App reconnects with backoff from 1 s to 30 s.
- `SetSettings` accepts only tariff, machine profile, idle threshold, sample interval, and retention, each range-checked; the profile holds at most 16 monitor choices, each monitor's key 1 to 200 characters and listed once, with a typed figure from 0 to 500 W. No paths or commands travel over the pipe. Before sending any change the App checks the process serving the pipe: it must be the process the Service Control Manager runs for the service named PowerLedger (`GetNamedPipeServerProcessId` against the process id `QueryServiceStatusEx` reports), because anyone could create a pipe with this name before the service does. Only an administrator can register that service, and a process squatting the pipe's name can't have the service's process id. The check doesn't compare executables, since an unelevated App can't read the path of a process running as LocalSystem. A development run started with `--pipe` skips the check. A refused or failed change comes back to the App as a sentence it shows under the Save that sent it.

## 9. UI

### Stack

WPF on .NET 10 with its own `WindowChrome`, `CommunityToolkit.Mvvm`, QuestPDF for PDF. The meter, the budget bar, the sparkline and the charts are lightweight controls drawn with `DrawingContext`, whose geometry is tested as pure functions; WPF-UI's Fluent styles and LiveCharts2's SkiaSharp were dropped in Plan D1 because the design overrides the first almost everywhere and the second is far larger than four simple drawings need. The tray icon is WinForms' `NotifyIcon`, which also shows the monthly report's notification. One ViewModel per screen; no logic in code-behind.

The window asks for 1180 × 900 and fits itself, before it first shows, to the work area of the screen it opens on, with its minimum size shrinking to match; each page scrolls what no longer fits, so a 1080p laptop at 125 % or 150 % scaling and a 1366 × 768 screen show the whole window.

### Screens

1. **Tray** — icon renders the live watts as text and updates only when the rounded value changes. Tooltip: now W and quality, today kWh and cost. Menu: Open, Restart to update to X.Y.Z (only while an update is ready, §13), Start with Windows (on by default, set by the installer), Exit UI (service keeps logging).
2. **Now** — large live watts with quality badge, 60 s sparkline, meter scale with average and peak marks, today ledger (kWh, cost, avg, peak, on, idle, asleep, CO₂), month-to-date with projected month cost, power-budget bar and per-component rows, today's stacked-area chart by component. The display row adds the external monitors counted ("plus 2 monitors") and how the least sure of their figures was got: ", estimated" when any was estimated, otherwise ", brightness assumed" when a listed figure's brightness couldn't be read. With monitors in the reading, the note beside the quality badge says their watts come from their own figures ("Windows battery report, with the monitors' own figures", or the model's note ending "plus the monitors' own figures"); the badge still describes the PC's own reading.
3. **Breakdown** — stacked area CPU / GPU / display / rest over today / 7 d / 30 d / custom, W↔Wh toggle, per-component kWh and percentage table. Display means the built-in panel, where there is one, and the external monitors PowerLedger counted at the time, and a footnote says so for any range, taking whether there is a panel from the settings as the model does: a laptop always has one, a desktop only when it gives a panel size. A negative rest band (measured mode, parts over-reporting) is clamped to zero on the chart and called out in a footnote. Ranges offered: today, 7 days, 30 days, this month, last month and custom days from two date pickers (the drop-down calendar keeps Windows' look). Buckets are 5 minutes for a day, 15 up to three days, an hour for a week, 6 hours for a month and a day beyond. The chart spans the whole range with the future left empty, a dashed line at now, and hatching where the machine slept through most of a bucket.
4. **Report** — range summary: kWh, cost, CO₂ kg, avg/peak, on / idle / asleep / unmonitored hours (asleep is time the machine slept while the service was running; unmonitored is range time that produced no rows at all, such as before install), idle waste with saving suggestion, comparisons (LED-bulb hours at 10 W, phone charges at 15 Wh, EV km at 0.18 kWh/km), quality mix with a legend, on screen and in the PDF, that says what each quality means and that external monitors' watts come from their own figures in every mode, daily bars. Export PDF, CSV (raw / 1 m / 1 h), PNG. The screen opens on this month. Exports are named from the range (`PowerLedger-2026-08` for a finished month, first to last day otherwise) and written beside the target, then moved into place. PNG is the report's sheet at screen resolution; the PDF is one A4 page in the light palette, drawn with QuestPDF in Windows' fonts (Segoe UI with the script fonts behind it). The saving suggestion quotes the plugged-in sleep and display timeouts, read with `powrprof`, the API behind `powercfg`. The cost note quotes the tariff in force at the end of the range, with the day it started when that falls inside the range, since energy before then was priced otherwise or not at all.
5. **Settings** — tariff and currency with history (a new tariff applies from the chosen day's local midnight, and may be backdated), CO₂ factor (typed, default 0.40 kg/kWh; the country picker waits for the table's source, §16), machine profile (chassis, PSU tier, memory, drives, fans, panel size, the external monitors, extras, GPU/CPU TDP overrides) with what was detected, idle threshold in minutes, sample interval, retention, calibration status and a reset that asks first, theme, start with Windows, reading monitors' brightness (read-only), and updates (download new versions quietly, then ask; Check now; a line saying where things stand, §13), each applying when chosen. Each external monitor the service lists has a row: its name, size and resolution, its figure in a box, a Count it tick and, on a laptop, a has its own plug tick, where the figure came from ("measured for this model", "estimated from its size — correct it if you know better", "estimated — correct it if you know better" for a monitor that didn't give both its size and its resolution, or "typed"), what it draws now, and its brightness ("brightness 60%, read from the monitor", "brightness unknown, assumed 75%", or "brightness unknown" for a typed figure). A monitor that runs off the PC always counts, so its Count it tick stays ticked and greyed, with a tooltip that says why; a desktop shows the plug tick for a monitor held to run off it, so that can be undone, and keeps showing one the user has ticked or unticked. The note under the rows says, on a laptop, that a monitor of 17.3 inches or less is taken to run off it and to tick "has its own plug" if it has one, and on a desktop only how to leave a monitor out. Clearing a typed figure goes back to PowerLedger's own. The rows follow the status every ten seconds, so a monitor plugged in appears without the wizard, and a figure being typed or a tick the user changed is kept, while a tick the user hasn't touched follows the service's defaults and the choice its settings hold; with no external monitor the profile says "none detected". A monitor's choice is saved only when it says what the service wouldn't assume (§5), and once a monitor is listed, saving clears the count and the choice to include monitors from before monitors were detected. When the settings loaded are behind the service's, as they are once it has carried those over, a save reads them again first, takes from them what the form doesn't show and keeps what the user typed and ticked, and sends nothing if it can't read them; a save that changes the chassis says each listed monitor's plug. About: service version and readings, per-source health, database size, no driver, and where the monitor figures come from (ENERGY STAR's list, which doesn't endorse PowerLedger). Choices are segmented buttons and numbers are typed in the user's culture; the service's settings are saved whole with one Save, a tariff with its own.
6. **First-run wizard** — tariff (in the region's currency; no suggested rate until the owner chooses a source, §16; blank means later) → confirm detected hardware (chassis, PSU tier, the external monitors, extras) → "Measured vs Estimated" explainer that says which qualities this machine will show. With no external monitor, as on a laptop on its own screen, the machine step shows no monitor rows and asks nothing about monitors. Otherwise it lists each monitor detected by name and size, with its figure, where the figure came from, a Count it tick and, on a laptop, a has its own plug tick, which behave as in Settings; only a monitor whose figure had to be estimated asks to be corrected. It is a page of the main window, shown whenever the window opens until it has been finished once (`FirstRunDone` in `ui.json`), and Settings can run it again. It reads the service again when the pipe connects, so it works when the App starts before the service does.

UI-only preferences (theme, start with Windows, units, checking for updates, reading monitors' brightness, and the versions last announced and last run) live in `%LOCALAPPDATA%\PowerLedger\ui.json`. Everything else is service-owned.

### Monthly report

The App checks at startup and once per hour while running. When a month has ended and `Documents\PowerLedger\PowerLedger-YYYY-MM.pdf` does not yet exist for it, the App generates that PDF and shows a toast with the headline numbers. The App owns this because the service runs as SYSTEM and has no user Documents folder. The first check comes 30 seconds after startup. It looks back at most twelve finished months, no earlier than the month history began in, skips a month with no readings at all, and waits for the next hour when history can't be read or Documents can't be written. The notification names the newest month's energy and cost, and clicking it opens that PDF.

### Updates

The rail's foot holds the update card (§13) above the service's status: "PowerLedger X.Y.Z is available" with its size, Download, What's new and ✕ when found while the connection is metered, waiting for one that isn't; "PowerLedger X.Y.Z is ready" with Restart to update, What's new and ✕ once a newer version has downloaded; "Installing X.Y.Z…" while setup starts; "The update didn't install" with the reason and Try again when it didn't; and "Updated to X.Y.Z" with What's new on the first start of a new version. There is no card while checking or downloading, and nothing on it animates. ✕ puts it away until the App next starts; the tray's menu item stays.

### States

- Empty state on first minute: "Collecting… first numbers in about a minute."
- Service down: "Service not running" with a Start button (UAC); history remains readable.
- Sensorless environment (VM, ancient CPU): honest banner "Power sensors aren't available here", uptime and idle still logged.

### Visual direction — "Meter & Ledger"

The app looks like a bench instrument; the reports read like a utility bill. Reference mockup: `docs/design/mockup-1-now-screen.html` (also published as an artifact during the brainstorm).

| Token | Dark (instrument) | Light (bench sheet) |
|---|---|---|
| Ground | `#1B1D1A` | `#E4E6E0` |
| Panel | `#222522` | `#EFF0EC` |
| Raised | `#2B2F2B` | `#F8F8F5` |
| Hairline / strong | `#363B36` / `#4B514B` | `#CDD0C8` / `#AEB2A9` |
| Ink / secondary / muted | `#ECE9DF` / `#A6A99E` / `#71766F` | `#1D1F1B` / `#575B53` / `#858980` |
| Amber (live reading only) | `#F2B233` | `#A2680C` |
| Measured / Calibrated / Estimated | `#8FCB8B` / `#8FB6D4` / `#B9AE93` | `#3E7E43` / `#35678A` / `#7C7355` |
| CPU / GPU / Display / Rest | `#E39B3B` / `#6F97C4` / `#B5C46A` / `#6E736D` | `#C4761C` / `#4A76A6` / `#7C8A2E` / `#8E928A` |

- Type: **Archivo** for UI and body, **Archivo Narrow** for uppercase tracked labels, **Martian Mono** (Light 300, Regular 400) for every number with tabular figures. All three are OFL and bundled as static TTFs, because WPF does not support variable-font axes. Until the TTFs are added to `src/PowerLedger.App/Fonts`, every font reference falls back to Windows' own: Bahnschrift for text, Cascadia Mono then Consolas for numbers.
- Amber is spent only on the live reading and the current position (needle, "now" line). Semantic quality colors are separate from the accent.
- Quality is encoded by color and by form: solid dot, half dot, dashed border.
- Hairlines instead of shadows; corner radii 2–6 px; dotted leaders between ledger labels and values; tick rulers on the scale and budget bar; LED-style dots for navigation state.
- Motion: the live number settles on open (900 ms ease-out) and ticks once per second; nothing else animates. `SystemParameters.ClientAreaAnimation` off → no animation.
- Both themes follow the system by default and can be forced in Settings.

## 10. Error handling and edge cases

| Situation | Behaviour |
|---|---|
| No energy-meter rails (Windows 10 without metering hardware, or a processor that publishes none) | Service keeps running; CPU falls back to the load × TDP model; quality drops to Estimated; the status screen says "CPU power unavailable on this machine" rather than offering a fix that does not exist. |
| Sensor unsupported on this hardware (e.g. GeForce MX330 has no power readout) | Adapter reports `Supported=false` at startup; model uses the fallback; UI marks the value estimated. |
| Glitch values | Validator rules in §4. |
| AC ↔ battery switch | Quality flips immediately; first 3 s excluded from calibration. |
| Suspend / resume, Modern Standby | §6. Gaps over 5 s count as asleep. |
| Hardware change (RAM, processor, a different machine) | Inventory hash changes → new inventory row; calibration buckets are keyed by hash so stale baselines are never reused. The hash covers chassis, processor and memory only: docks, monitors, external drives, eGPUs and driver installs leave it alone, because they do not change the machine the battery measures. |
| Service crash | Windows service recovery restarts after 5 s, up to 3 times; at most 60 s of buffered samples lost; WAL keeps the DB consistent; `crash-recovered` session reason. |
| Database corrupt | Rename to `power.corrupt-<date>.db`, start fresh, alert in UI. |
| Disk full | Pause writes, keep a 1 h in-memory ring buffer, retry every minute, status badge. |
| Schema migration | Transactional, `power.db.bak` before each version bump. |
| App cannot reach the service | "Service not running" state with Start button; pipe reconnect backoff 1 → 30 s. |
| Multiple user sessions | Pipe serves multiple clients; each subscribes independently. |
| Multiple GPUs | Sum discrete GPU power; iGPU is inside the CPU package and never double-counted. |
| Money | `decimal` end to end; ISO 4217 code; formatted with `CultureInfo`. |

Logging: Serilog rolling files in `C:\ProgramData\PowerLedger\logs`, 7 days or 5 MB, hardware names only, no personal data.

## 11. Security and privacy

- The service runs as LocalSystem but exposes only a local named pipe with remote access denied.
- Settings over the pipe are a closed, range-checked set; nothing executable or path-like.
- The App sends changes only to the installed service, identified by the process id the Service Control Manager reports for it rather than by the pipe's name (§8).
- No kernel driver is installed and no sensor read needs elevation. The service runs as LocalSystem for session and power notifications, not for hardware access.
- The App reads monitors' brightness over DDC/CI with two requests and no others, `GetMonitorCapabilities` and `GetMonitorBrightness`; nothing is ever written to a monitor. A monitor is read only when its capabilities report brightness, 50 ms after it answered, and is asked for its capabilities once a session. One that reports no brightness, or fails a call before it has given a brightness, isn't asked anything again while the App runs, until a display change or a resume from sleep. One that has given a brightness and then fails is asked again at the next read, and each failure in a row doubles the wait, up to an hour. A display change or a resume forgets what every monitor has answered, capabilities, failures and waits included, so the next read asks each afresh; it only marks a reset, which the read carries out, so the App's UI thread never waits for a read. The App reads a minute after it starts and every five minutes, off the UI thread, not while the displays are off, and only while Settings allows it; every handle a read opens is destroyed before it returns.
- The service keeps a brightness only for a monitor attached now, from a report of at most 16 checked readings (§8), so a client can't fill its memory with monitors it made up. The monitor table ships inside the program: nothing about monitors is fetched or sent over the network.
- All data stays on the machine. Exports happen only when the user asks. No telemetry in v1; a future opt-in crash reporter would be a separate decision.
- The App asks GitHub for PowerLedger's latest release a minute after it starts and every hour, and downloads a newer installer. The request carries nothing about the user or the PC; GitHub sees the IP address and `PowerLedger/X.Y.Z` as the user agent. An update runs only when its tag is a version newer than the running one and its installer, from this repository's releases over HTTPS, has the size and SHA-256 GitHub lists. Nothing is downloaded on a metered connection unless the user asks for it. Settings turns the checks off.
- The installer is code-signed (Azure Trusted Signing) before the first public release so SmartScreen does not flag it.

## 12. Testing

Framework: xUnit, Shouldly (BSD; FluentAssertions 8+ requires a paid commercial license), FsCheck for property tests, `Microsoft.Extensions.TimeProvider.Testing` for a fake clock.

- **Core**: `PowerModel` table tests (sample → reading for each mode); integrator invariants (energy ≥ 0, Δt cap, gap yields zero, resume reset); downsampler conservation (Σ 1-minute Wh equals the raw integral); tariff-at-time cost across rate changes; calibration convergence on synthetic battery traces; report and comparison math; property tests on the integrator; `MonitorPower`'s brightness scaling, and the monitors' draw split by where it comes from: a monitor with a plug of its own added after the supply division and on top of a measured rate, one running off the PC divided with the other parts and already in a measured rate, on a laptop and a desktop, and the caller given the split the reading used.
- **Sensors**: validator driven by fake sources with glitch sequences (wrap, blip, spike, transition). The monitor inventory from WMI rows: external, active monitors only, snapped diagonals, and keys, placeholder serial numbers and twins still known by their instances after one is unplugged among them; no answer, rather than no monitors, when a class that says which monitors are attached fails, and no monitors when WMI refuses the classes as having no instances; and the resolution last read for an instance kept when a read gives none. The catalogue against awkward listings in the shipped table: Dell's `U2723QEt`, Acer's `B196L_q`, "HP 322pb Monitor", BenQ's `GW2480-B`, Lenovo's internal part code, and "LG HDR 4K" matching nothing; a family or a name cut short only at the monitor's resolution, an exact name or a series word at any; the codes ASUS's and AOpen's monitors report; and an unknown maker matching exact names only. The estimate, with each certified monitor estimated from all the others: the test holds the median error to 12% and the 90th percentile to 35%, above the 9.04% and 25.6% it measures. The display source reads the monitors only when its query is due, hands them over only when they change, hands nothing over when WMI gives no answer, and hands over no monitors once the last one goes. Real-hardware adapter tests, the monitor inventory's among them, carry `Trait("Category","Hardware")` and are skipped in CI.
- **Storage**: temp-file SQLite; migration from every prior schema version; retention purge; a reader querying while the writer commits under WAL.
- **Service**: host the worker with fake sources and a temp DB, advance the fake clock through 10 simulated minutes including a suspend/resume, assert rows, aggregates, and sessions. Pipe round-trip and reconnect tests. The monitor board: figures, choices, the profile's default for counting, the plug guessed for a laptop's small monitor and the user's word over it, a monitor running off the PC counted whatever its choice, brightness going stale, a monitor unplugged, and callers on several threads at once; what a retired sensor set finds ignored, and, through the real worker, display source and board, the monitors an abandoned set finds when its stuck read returns never reaching the board; detected monitors reaching the status and the reading; a monitor running off the laptop kept out of what the learner learns; old monitor settings carried over once, a monitor attached later left out when they didn't count monitors; a brightness report over a real pipe, and a bad one refused.
- **App**: ViewModel, geometry and chart unit tests; the pipe client against a real pipe; the DDC/CI reader's rules against a fake Windows layer (capabilities asked once, an answer of no brightness or a failure before any brightness left alone until a display change or a resume, a monitor that has given a brightness asked again after a failure with a wait that doubles up to an hour, a display change or a resume during a read not waiting for it, handles matched to monitors, 50 ms between requests) and the reader on this machine (Category Hardware); the brightness reporter's schedule and preference; the wizard's and Settings' monitor rows, which keep what is typed and ticked when the status refreshes, save a choice only when it says what the service wouldn't assume, keep the choices for monitors not attached within the limit, most recent first, clear the old monitor count once a monitor is listed, read the service's settings again before a save when those loaded are behind, and say each plug when the chassis changes; the Now screen's display row and note, Breakdown's footnote and the report's quality legend; the updater: the feed's parsing and refusals, the download's checks, stall and cleanup, setup's arguments and last check, and the card's stages and schedule; history against a temp database; the PDF generated from a report; the drawn controls' screen-reader names; a rendering test that draws every screen in both themes to PNG, with two external monitors in the wizard, for a desktop and a laptop, and in Settings and Now, where one runs off the laptop, presses PNG export and writes the PDF, and checks that only a laptop asks whether a monitor has its own plug, that a monitor running off the PC stays counted, and that a desktop's plug tick stays on screen once ticked (Category UI, skipped in CI); the sleep-timeout reader (Category Hardware); and, against the installed service, that the App trusts it, that a real settings write goes through, and that the App reads the database under the data folder's ACL (Category Installed: run unelevated after installing, excluded from every other run). A manual QA checklist per screen for v1.
- **Accuracy (manual, once)**: battery mode within ±5 % of `powercfg /batteryreport`; calibrated AC estimate within ±15 % of an inexpensive wall meter, on the developer's Dell Inspiron 3501.
- **Performance gate**: service < 0.5 % CPU and < 50 MB private working set, the figure Task Manager shows (the full working set adds shared system and driver DLL images, about 50 MB more on the development laptop, where the service measured 0.04 % CPU and 36.8 MB); App < 120 MB with a window open; measured with `dotnet-counters` and the process counters; 7-day soak on the developer machine with zero crashes.

## 13. Distribution

- Requirements: Windows 10 1809 or later, or Windows 11, on x64 or Arm64. Not 32-bit Windows, and not S mode. On Windows 10, .NET 10 is officially supported only on the Enterprise LTSC editions, but it runs on the others.
- Self-contained builds for win-x64 and win-arm64: each program carries the .NET 10 runtime, so the installer downloads nothing, the PC needs no .NET of its own, and Arm64 PCs run native code rather than emulated x64. .NET's security fixes reach users with PowerLedger's updates. A publish per platform ships only that platform's native libraries: QuestPDF and SQLite bring natives for eight platforms, 116 MB in a platform-neutral build.
- One installer holds both builds and installs the one that matches the PC (`ArchitecturesAllowed=x64os or arm64`, so 32-bit Windows gets Inno Setup's own refusal). It is about 96 MB: LZMA2 at ultra64, one solid stream, and a 256 MB dictionary, so the service's copy of the runtime compresses against the App's (the x64 build alone would be 56 MB). Setup needs about 256 MB of memory to unpack it. Installed, PowerLedger takes about 260 MB on x64 and 280 MB on Arm64.
- Layout (Plan E): the App in `Program Files\PowerLedger`, the service in its `Service` folder, a Start menu shortcut. An upgrade stops the service before copying and starts it after; the App turns on its Run entry on its first run, as the user who runs it, since the elevated installer can't write that user's `HKCU`. Setup, once its checks pass, and uninstall, once the user confirms, close a running App before replacing or removing files: they signal the named event `Local\PowerLedger.App.Exit`, on which the App exits as its tray's Exit does, and wait up to 10 s; Inno's `AppMutex` check remains the fallback. A silent upgrade therefore works while the App runs, and the App starts again at the next sign-in through its Run entry, or when an interactive setup ends with "Open PowerLedger" ticked. Uninstall asks whether to keep the history and keeps it when silent. `installer\build.ps1` makes the installer with Inno Setup 7.1, which `installer\get-inno-setup.ps1` installs for the current user from its GitHub release, refusing it unless its SHA-256 matches and it is validly signed by Pyrsys B.V.; Inno Setup 6 won't do, since its 32-bit compiler can't use the 256 MB compression dictionary. CI does the same.
- The installer registers the service with recovery options and opens the App, whose first window is the first-run wizard; the App adds its own Run entry. It logs the exit code of every `sc.exe` and `net.exe` it runs, and tells the user when registering the service fails. There is no driver to install. Uninstall stops and deletes the service, removes its event-log source, and asks whether to keep the database.
- Releases on GitHub (`mharisjamal/PowerLedger`), one per version, tagged `vX.Y.Z` and carrying three installers: `PowerLedger-X.Y.Z-setup.exe`, `-setup-x64.exe` and `-setup-arm64.exe`. `scripts\release.ps1` publishes the version in `Directory.Build.props` from a clean, pushed `main` and checks that GitHub's SHA-256 for each is the local file's. A winget manifest can follow the first stable build.
- In-app updates (Plan H; `2026-09-16-powerledger-updates-design.md`): a minute after the App starts and every hour, while Settings allows it, the App asks GitHub's API for the latest release, which leaves out drafts and pre-releases. A newer one counts only when its tag is `vX.Y.Z` and it carries, under this repository's download address with GitHub's `sha256:` digest, an installer for this PC: `PowerLedger-X.Y.Z-setup-x64.exe` or `-setup-arm64.exe` for that architecture, falling back to the universal `PowerLedger-X.Y.Z-setup.exe` when a release has none. Its installer downloads quietly into `%LOCALAPPDATA%\PowerLedger\Updates` and is kept only when its size and SHA-256 are GitHub's. The card (§9), one tray notification per version and a tray menu item then offer Restart to update, which checks the file once more and starts it with `/SILENT /NORESTART /UPDATE=1`: Windows asks for permission, setup closes the App through its exit event, upgrades the service and, for `/UPDATE=1`, opens the App again as the user who started setup. If permission is refused or setup ends without installing, the card says so and offers Try again; a setup that fails after closing the App starts the service again and reopens the App, whose next check offers the update again. Until the installer is signed, the permission prompt names an unknown publisher.
- Installer tests: `installer\build.ps1 -TestVariants` also compiles the upgrade (the patch version plus one) into `installer\output\test\`, with fast compression since its size doesn't matter. `installer\test-installer.ps1`, run from an elevated PowerShell, installs the real PowerLedger and checks a silent install; the files, shortcut and uninstall entry; that the App and the service are the build for the PC's architecture (their PE header's machine field) and each carry the runtime; the service's registration (quoted image path, automatic start, LocalSystem, restart on failure); the data folder's owner and ACL; the pipe answering; recovery after the service process is killed; and an upgrade keeping the history, run with `/UPDATE=1` as the App runs it and opening the App again, the per-architecture installer of this PC installing over what is there, an uninstall keeping it and a reinstall over it. Its interactive steps drive setup's windows with UI Automation: an install through the wizard, as somebody new to PowerLedger would do it (on a clean Windows such as Windows Sandbox), an uninstall that deletes the history, and the wizard of a setup started by a normal user. It stops at once if `C:\ProgramData\PowerLedger` already exists, removes what it created, and writes its results to `installer\output\test-results`.
- CI (GitHub Actions): build, tests outside the Hardware, UI and Installed categories, the installer and its upgrade build, and the installer test's silent steps on x64; then the same installer's silent steps on an Arm64 runner (`windows-11-arm`), which installs the Arm64 build. Hosted Windows runners are administrators. Artifacts: the installer, and each runner's test results even when a step fails.

## 14. Repository layout and conventions

```
PowerLedger.sln
Directory.Build.props        net10.0-windows, nullable enabled, warnings as errors, shared version
src/
  PowerLedger.Core/
  PowerLedger.Contracts/
  PowerLedger.Sensors/       Monitors/ holds the monitor inventory, catalogue and estimate (§4, §5)
  PowerLedger.Storage/
  PowerLedger.Service/
  PowerLedger.App/           Updates/ holds the updater (§13); Monitors/ the brightness reader and reporter (§4)
tests/
  PowerLedger.Core.Tests/
  PowerLedger.Sensors.Tests/
  PowerLedger.Storage.Tests/
  PowerLedger.Service.Tests/
  PowerLedger.App.Tests/
installer/
  PowerLedger.iss, build.ps1, get-inno-setup.ps1, test-installer.ps1
scripts/
  publish.ps1, release.ps1, pipe-status.ps1, dev-service.ps1
assets/
  brand/                     the logo and make-brand.ps1
  monitors/                  energy-star-monitors.csv, make-monitor-table.ps1 that rebuilds it, and where it comes from
docs/
  design/                    mockups
  superpowers/specs/         this document
.github/workflows/ci.yml
```

Third-party licenses in use: CommunityToolkit.Mvvm (MIT), Microsoft.Data.Sqlite (MIT), Serilog (Apache-2.0), QuestPDF (Community license, free below USD 1M revenue), FsCheck (BSD-3), fonts Archivo, Archivo Narrow, Martian Mono (OFL). No GPL.

Data in use: the US EPA's ENERGY STAR Certified Displays, a work of the US Government in the public domain, published under the EPA Data License (`assets/monitors/README.md`). ENERGY STAR is named only to say where the figures come from: PowerLedger is not certified by, endorsed by or affiliated with ENERGY STAR or the EPA, and doesn't use the ENERGY STAR mark.

## 15. Success criteria for v1

- Installs on a clean Windows 10/11 x64 or Arm64 machine in under a minute with no manual driver steps.
- Logs from boot without the UI, survives sleep and resume, and keeps the database under 100 MB after two years of use.
- Meets the performance gate and the accuracy targets in §12.
- Runs a 7-day soak on the developer laptop with zero crashes.
- Produces the monthly PDF automatically.

## 16. Open decisions

- Final product name (PowerLedger is a placeholder).
- License: MIT by default; the owner may choose otherwise before the first public release.
- Timing of code signing: optional for private testing, required before public release.
- Source of the bundled CO₂ grid-intensity table (any published national averages; a world-average fallback of 0.40 kg/kWh applies regardless).
- How often to refresh the monitor table. It is ENERGY STAR's list as fetched on 2026-09-16, and `assets/monitors/make-monitor-table.ps1` rebuilds it; a monitor certified later is estimated until a release carries a newer table.
- **What to show a Windows 10 machine with no energy-meter rails.** CPU watts fall back to the load model, so every reading is Estimated and a desktop has no better signal at all. Options: say so plainly in the wizard and carry on, or offer an opt-in kernel driver later. The driver was dropped from v1 once Windows 11 turned out to expose the rails without one.

## 17. Prerequisites to verify before implementation

- The development machine has the .NET 10 Desktop Runtime (10.0.11) but no .NET SDK (`dotnet --list-sdks` is empty). Install the .NET 10 SDK first.
- Confirmed 2026-09-10 on the development laptop, unelevated: the Energy Meter Interface is present and enabled, and reports the package, cores, integrated-graphics and memory rails. No sensor library or kernel driver is needed, so `LibreHardwareMonitorLib` was dropped from the design along with the PawnIO driver it now requires.
- Confirmed 2026-09-10: the GeForce MX330 reports no power at all through NVML or NVAPI. It is a whole class of low-end laptop GPUs with no measurement hardware, so the load-model fallback in §5 is the normal path, not the exception.
- Inno Setup 7.1 installed for the installer step (`installer\get-inno-setup.ps1`).
