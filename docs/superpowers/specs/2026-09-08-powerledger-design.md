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
- macOS, Linux, ARM64.
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
| `PowerLedger.Sensors` | `ISensorSource` and adapters: `EnergyMeterSource` (CPU rails), `NvidiaSource` (discrete GPU), `BatterySource`, `CpuLoadSource`, `DisplaySource`, `ActivitySource`, `HardwareInventory`. Validator lives here. | Core, Contracts |
| `PowerLedger.Storage` | SQLite schema, migrations, batched writer, retention jobs, read-side query API. | Core |
| `PowerLedger.Service` | Worker host: sampler loop, writer, downsample scheduler, pipe server, power/session event handling. | all above |
| `PowerLedger.App` | WPF UI, tray icon, charts, exports, monthly report, wizard. | Core, Contracts, Storage (read-only) |
| `installer/` | Inno Setup script and runtime bootstrap. No driver to bundle. | build output |
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
| Discrete GPU | power W, load, present | NVML through the installed NVIDIA driver (`nvml.dll`, loaded from System32 only). A card Windows has switched off (D3, read from the device's power data without waking it) counts as 0 W and is not queried. Many laptop GPUs, the GeForce MX330 included, report no power at all; those fall back to the load model. AMD and Intel cards are not read yet; the `GPU Engine` performance counters are the driver-free route to their load | no |
| Battery | discharge/charge rate mW, AC line status | `CallNtPowerInformation(SystemBatteryState)` (Rate is negative when discharging). A UPS on USB also appears as a battery, flagged short-term in `SystemPowerCapabilities`; it powers more than the machine and is ignored | no |
| Display | brightness %, display on/off, monitor count and size | WMI `WmiMonitorBrightness` ("not supported" on a desktop means no brightness), `PowerSettingRegisterNotification(GUID_CONSOLE_DISPLAY_STATE)` with a callback, `WmiMonitorBasicDisplayParams` + `WmiMonitorConnectionParams`: the built-in panel is the monitor with an internal or embedded connection, never simply the first one listed | no |
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

1. **Laptop with a usable discharge rate** (on battery; rate finite and above zero, since zero or negative means charging or a transition blip) → `totalW = batteryRateW + monitors`. Quality **Measured** (±3 %). Components are still computed for the breakdown; `rest = measured − cpu − gpu − display`, which may go negative when the parts over-report (the honest sensor-disagreement signal). `Components.Sum` always equals `totalW`.
2. **Any machine on AC** → `totalW = (cpu + gpu + display + baseline) / psuEfficiency + monitors`. External monitors are wall-powered, so they are added after the supply-efficiency division and never contribute to PSU loss.
   - Quality **Calibrated** (±10 %) when a learned baseline exists for the current brightness bucket (see below).
   - Quality **Estimated** (±20 %) otherwise.

Desktops divide by PSU efficiency; laptops use 1.0 on battery and 0.90 (adapter efficiency) on AC. Desktops are always Estimated: battery rates (a UPS) and learned baselines apply to laptops only. A learned baseline was observed on battery and therefore already contains any extras drawing from the battery, so `Extras` is reported as 0 in calibrated mode.

### Baseline auto-calibration (laptops)

Every second on battery provides ground truth: `baselineObserved = measured − cpu − dGpu − displayModel`. The learner keeps one running average per brightness bucket (10 % steps, plus a display-off bucket), keyed by the hardware inventory hash, with an exponential weighting whose half-life is 10 minutes of samples. A young bucket uses a plain running mean until it holds roughly one half-life of samples, so the first tick after a brightness change does not dominate. Residuals are averaged as they are, negative ones included (a clamp would bias the baseline upward), and the reported baseline is clamped at zero. Suspect samples, samples with non-finite parts, and the 3 s after transitions are excluded. The learner is thread-safe because the Service samples on one thread and exports or resets from the pipe thread.

A bucket counts as calibrated once it holds ≥ 5 minutes of samples and the machine has ≥ 30 minutes of battery samples in total. Calibrated baselines replace the defaults in AC mode. Calibration progress and a reset button appear in Settings.

### Default estimate values (used until calibrated, or on desktops)

| Component | Default |
|---|---|
| Laptop baseline (board, RAM, SSD, radios) | 5 W |
| Laptop internal panel | `1.5 W + 4.5 W × brightness`, scaled ×0.8 up to 14", ×1.0 above 14" and below 17", ×1.3 from 17" (diagonal from EDID; unknown = ×1.0) |
| Desktop board | 12 W |
| RAM per stick | DDR4 2.5 W, DDR5 1.5 W (`Win32_PhysicalMemory.SMBIOSMemoryType`) |
| Drive | SSD 2 W, HDD 6 W (`MSFT_PhysicalDisk.MediaType`) |
| Fan | 1 W each, counted from the machine profile rather than measured |
| Extras (RGB, pumps, USB devices) | user slider 0–100 W, default 0 |
| External monitors | opt-in, default 25 W each while display on, 0.5 W when off |
| PSU efficiency | 80+ White 82 %, Bronze 85 % (default), Silver 87 %, Gold 90 %, Platinum 92 %, Titanium 94 % |
| dGPU without power sensor | `3 W + (TDP − 3 W) × load`; TDP from a bundled model table, else 25 W laptop / 75 W desktop; user override |
| CPU without power sensor | `idle + (TDP − idle) × load`, idle 2 W laptop / 8 W desktop; TDP from bundled table, else 15 W laptop / 65 W desktop; user override |

The model tables ship as JSON resources in `Sensors` (`tdp-table.json`) and are editable via Settings overrides.

### Machine profile detection

Chassis type from `Win32_SystemEnclosure.ChassisTypes` plus battery presence decides laptop vs desktop. The wizard shows what was detected and lets the user correct it. Detected items: CPU and GPU names, RAM sticks and type, drives and media type, monitors and sizes, fan count, PSU tier (asked, not detectable).

## 6. Energy accounting

- Energy is integrated per tick: `Wh += totalW × Δt / 3600`, with Δt from a monotonic clock.
- If Δt is within the gap threshold the tick is counted at the current reading. Beyond it (sleep, hibernate, service stop, Modern Standby throttling) the tick contributes zero energy and the interval is recorded as a gap. The threshold is `max(5 s, 2 × sample interval)`, so a slower sampling setting never turns timer jitter into gaps. Non-finite readings contribute nothing.
- Energy attribution bands: CPU, GPU, display (internal panel plus opted-in external monitors), and rest (everything else, including RAM, board, PSU loss and the learned or measured remainder). In measured mode the rest band can go negative when the parts over-report; storage keeps it raw and the UI clamps and annotates at display time.
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
- Messages: `subscribe` (the server pushes a `reading` frame each tick, saying which parts were measured), `getStatus` (service version, per-source health and suspect counts, sensor restarts, calibration progress, DB size, write problems), `getSettings`, `setSettings`, `setTariff` (inserts a `tariffs` row; `effectiveFrom` defaults to now and may be backdated by the user), `resetCalibration`, and `reportActivity` (the App's idle seconds every few seconds, because the service in session 0 cannot see input).
- Multiple clients supported. The App reconnects with backoff from 1 s to 30 s.
- `SetSettings` accepts only tariff, machine profile, idle threshold, sample interval, and retention, each range-checked. No paths or commands travel over the pipe. Before sending any change the App checks the process serving the pipe: it must be the process the Service Control Manager runs for the service named PowerLedger (`GetNamedPipeServerProcessId` against the process id `QueryServiceStatusEx` reports), because anyone could create a pipe with this name before the service does. Only an administrator can register that service, and a process squatting the pipe's name can't have the service's process id. The check doesn't compare executables, since an unelevated App can't read the path of a process running as LocalSystem. A development run started with `--pipe` skips the check. A refused or failed change comes back to the App as a sentence it shows under the Save that sent it.

## 9. UI

### Stack

WPF on .NET 10 with its own `WindowChrome`, `CommunityToolkit.Mvvm`, QuestPDF for PDF. The meter, the budget bar, the sparkline and the charts are lightweight controls drawn with `DrawingContext`, whose geometry is tested as pure functions; WPF-UI's Fluent styles and LiveCharts2's SkiaSharp were dropped in Plan D1 because the design overrides the first almost everywhere and the second is far larger than four simple drawings need. The tray icon is WinForms' `NotifyIcon`, which also shows the monthly report's notification. One ViewModel per screen; no logic in code-behind.

### Screens

1. **Tray** — icon renders the live watts as text and updates only when the rounded value changes. Tooltip: now W and quality, today kWh and cost. Menu: Open, Start with Windows (on by default, set by the installer), Exit UI (service keeps logging).
2. **Now** — large live watts with quality badge, 60 s sparkline, meter scale with average and peak marks, today ledger (kWh, cost, avg, peak, on, idle, asleep, CO₂), month-to-date with projected month cost, power-budget bar and per-component rows, today's stacked-area chart by component.
3. **Breakdown** — stacked area CPU / GPU / display / rest over today / 7 d / 30 d / custom, W↔Wh toggle, per-component kWh and percentage table. Display means the internal panel plus opted-in external monitors. A negative rest band (measured mode, parts over-reporting) is clamped to zero on the chart and called out in a footnote. Ranges offered: today, 7 days, 30 days, this month, last month and custom days from two date pickers (the drop-down calendar keeps Windows' look). Buckets are 5 minutes for a day, 15 up to three days, an hour for a week, 6 hours for a month and a day beyond. The chart spans the whole range with the future left empty, a dashed line at now, and hatching where the machine slept through most of a bucket.
4. **Report** — range summary: kWh, cost, CO₂ kg, avg/peak, on / idle / asleep / unmonitored hours (asleep is time the machine slept while the service was running; unmonitored is range time that produced no rows at all, such as before install), idle waste with saving suggestion, comparisons (LED-bulb hours at 10 W, phone charges at 15 Wh, EV km at 0.18 kWh/km), quality mix, daily bars. Export PDF, CSV (raw / 1 m / 1 h), PNG. The screen opens on this month. Exports are named from the range (`PowerLedger-2026-08` for a finished month, first to last day otherwise) and written beside the target, then moved into place. PNG is the report's sheet at screen resolution; the PDF is one A4 page in the light palette, drawn with QuestPDF in Windows' fonts (Segoe UI with the script fonts behind it). The saving suggestion quotes the plugged-in sleep and display timeouts, read with `powrprof`, the API behind `powercfg`. The cost note quotes the tariff in force at the end of the range, with the day it started when that falls inside the range, since energy before then was priced otherwise or not at all.
5. **Settings** — tariff and currency with history (a new tariff applies from the chosen day's local midnight, and may be backdated), CO₂ factor (typed, default 0.40 kg/kWh; the country picker waits for the table's source, §16), machine profile (chassis, PSU tier, memory, drives, fans, panel size, monitors and whether to count them, extras, GPU/CPU TDP overrides) with what was detected, idle threshold in minutes, sample interval, retention, calibration status and a reset that asks first, theme and start with Windows (both apply when chosen). About: service version and readings, per-source health, database size, and no driver. Choices are segmented buttons and numbers are typed in the user's culture; the service's settings are saved whole with one Save, a tariff with its own.
6. **First-run wizard** — tariff (in the region's currency; no suggested rate until the owner chooses a source, §16; blank means later) → confirm detected hardware (chassis, PSU tier, monitors, extras) → "Measured vs Estimated" explainer that says which qualities this machine will show. It is a page of the main window, shown whenever the window opens until it has been finished once (`FirstRunDone` in `ui.json`), and Settings can run it again. It reads the service again when the pipe connects, so it works when the App starts before the service does.

UI-only preferences (theme, start with Windows, units) live in `%LOCALAPPDATA%\PowerLedger\ui.json`. Everything else is service-owned.

### Monthly report

The App checks at startup and once per hour while running. When a month has ended and `Documents\PowerLedger\PowerLedger-YYYY-MM.pdf` does not yet exist for it, the App generates that PDF and shows a toast with the headline numbers. The App owns this because the service runs as SYSTEM and has no user Documents folder. The first check comes 30 seconds after startup. It looks back at most twelve finished months, no earlier than the month history began in, skips a month with no readings at all, and waits for the next hour when history can't be read or Documents can't be written. The notification names the newest month's energy and cost, and clicking it opens that PDF.

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
- All data stays on the machine. Exports happen only when the user asks. No telemetry in v1; a future opt-in crash reporter would be a separate decision.
- The installer is code-signed (Azure Trusted Signing) before the first public release so SmartScreen does not flag it.

## 12. Testing

Framework: xUnit, Shouldly (BSD; FluentAssertions 8+ requires a paid commercial license), FsCheck for property tests, `Microsoft.Extensions.TimeProvider.Testing` for a fake clock.

- **Core**: `PowerModel` table tests (sample → reading for each mode); integrator invariants (energy ≥ 0, Δt cap, gap yields zero, resume reset); downsampler conservation (Σ 1-minute Wh equals the raw integral); tariff-at-time cost across rate changes; calibration convergence on synthetic battery traces; report and comparison math; property tests on the integrator.
- **Sensors**: validator driven by fake sources with glitch sequences (wrap, blip, spike, transition). Real-hardware adapter tests carry `Trait("Category","Hardware")` and are skipped in CI.
- **Storage**: temp-file SQLite; migration from every prior schema version; retention purge; a reader querying while the writer commits under WAL.
- **Service**: host the worker with fake sources and a temp DB, advance the fake clock through 10 simulated minutes including a suspend/resume, assert rows, aggregates, and sessions. Pipe round-trip and reconnect tests.
- **App**: ViewModel, geometry and chart unit tests; the pipe client against a real pipe; history against a temp database; the PDF generated from a report; the drawn controls' screen-reader names; a rendering test that draws every screen in both themes to PNG, presses PNG export and writes the PDF (Category UI, skipped in CI); the sleep-timeout reader (Category Hardware); and, against the installed service, that the App trusts it, that a real settings write goes through, and that the App reads the database under the data folder's ACL (Category Installed: run unelevated after installing, excluded from every other run). A manual QA checklist per screen for v1.
- **Accuracy (manual, once)**: battery mode within ±5 % of `powercfg /batteryreport`; calibrated AC estimate within ±15 % of an inexpensive wall meter, on the developer's Dell Inspiron 3501.
- **Performance gate**: service < 0.5 % CPU and < 50 MB private working set, the figure Task Manager shows (the full working set adds shared system and driver DLL images, about 50 MB more on the development laptop, where the service measured 0.04 % CPU and 36.8 MB); App < 120 MB with a window open; measured with `dotnet-counters` and the process counters; 7-day soak on the developer machine with zero crashes.

## 13. Distribution

- Requirements: Windows 10 1809 or later, Windows 11, x64 only.
- Framework-dependent build; Inno Setup installs the .NET 10 Desktop Runtime if missing, so the installer stays around 15 MB. Publish for win-x64, so only that platform's native libraries ship: QuestPDF and SQLite bring natives for eight platforms, 116 MB in a platform-neutral build.
- Layout (Plan E): the App in `Program Files\PowerLedger`, the service in its `Service` folder, a Start menu shortcut. An upgrade stops the service before copying and starts it after; the App turns on its Run entry on its first run, as the user who runs it, since the elevated installer can't write that user's `HKCU`. Setup, once its checks pass, and uninstall, once the user confirms, close a running App before replacing or removing files: they signal the named event `Local\PowerLedger.App.Exit`, on which the App exits as its tray's Exit does, and wait up to 10 s; Inno's `AppMutex` check remains the fallback. A silent upgrade therefore works while the App runs, and the App starts again at the next sign-in through its Run entry, or when an interactive setup ends with "Open PowerLedger" ticked. Uninstall asks whether to keep the history and keeps it when silent. A silent install needs the runtime already present. `installer\build.ps1` makes the installer with Inno Setup 7.1, which `installer\get-inno-setup.ps1` installs for the current user from its GitHub release, refusing it unless its SHA-256 matches and it is validly signed by Pyrsys B.V.; Inno Setup 6.3 or later also compiles it (checked with 6.7.3, which `get-inno-setup.ps1 -Version 6.7.3` installs). CI does the same.
- The installer registers the service with recovery options and opens the App, whose first window is the first-run wizard; the App adds its own Run entry. It logs the exit code of every `sc.exe` and `net.exe` it runs, and tells the user when registering the service fails. There is no driver to install. Uninstall stops and deletes the service, removes its event-log source, and asks whether to keep the database.
- Releases on GitHub with a winget manifest after the first stable build. v1 has a "check for updates" link; an in-app updater is v1.1.
- Installer tests: `installer\build.ps1 -TestVariants` also compiles test builds into `installer\output\test\`: an upgrade (the patch version plus one), one that behaves as if the .NET runtime were missing (`noruntime`), and one whose runtime download fails (`badurl`). `installer\test-installer.ps1`, run from an elevated PowerShell, installs the real PowerLedger and checks a silent install; the files, shortcut and uninstall entry; the service's registration (quoted image path, automatic start, LocalSystem, restart on failure); the data folder's owner and ACL; the pipe answering; recovery after the service process is killed; an upgrade keeping the history, an uninstall keeping it and a reinstall over it; and a silent install refusing when the runtime is missing. Its interactive steps drive setup's windows with UI Automation: the failed runtime download, an uninstall that deletes the history, and the wizard of a setup started by a normal user. It stops at once if `C:\ProgramData\PowerLedger` already exists, removes what it created, and writes its results to `installer\output\test-results`.
- CI (GitHub Actions): build, tests outside the Hardware, UI and Installed categories, the installer and its test builds, the installer test's silent steps (hosted Windows runners are administrators), and artifacts: the installer, and the test results even when a step fails.

## 14. Repository layout and conventions

```
PowerLedger.sln
Directory.Build.props        net10.0-windows, nullable enabled, warnings as errors, shared version
src/
  PowerLedger.Core/
  PowerLedger.Contracts/
  PowerLedger.Sensors/
  PowerLedger.Storage/
  PowerLedger.Service/
  PowerLedger.App/
tests/
  PowerLedger.Core.Tests/
  PowerLedger.Sensors.Tests/
  PowerLedger.Storage.Tests/
  PowerLedger.Service.Tests/
  PowerLedger.App.Tests/
installer/
  PowerLedger.iss, build.ps1, get-inno-setup.ps1, test-installer.ps1
docs/
  design/                    mockups
  superpowers/specs/         this document
.github/workflows/ci.yml
```

Third-party licenses in use: CommunityToolkit.Mvvm (MIT), Microsoft.Data.Sqlite (MIT), Serilog (Apache-2.0), QuestPDF (Community license, free below USD 1M revenue), FsCheck (BSD-3), fonts Archivo, Archivo Narrow, Martian Mono (OFL). No GPL.

## 15. Success criteria for v1

- Installs on a clean Windows 10/11 x64 machine in under a minute with no manual driver steps.
- Logs from boot without the UI, survives sleep and resume, and keeps the database under 100 MB after two years of use.
- Meets the performance gate and the accuracy targets in §12.
- Runs a 7-day soak on the developer laptop with zero crashes.
- Produces the monthly PDF automatically.

## 16. Open decisions

- Final product name (PowerLedger is a placeholder).
- License: MIT by default; the owner may choose otherwise before the first public release.
- Timing of code signing: optional for private testing, required before public release.
- Source of the bundled CO₂ grid-intensity table (any published national averages; a world-average fallback of 0.40 kg/kWh applies regardless).
- **What to show a Windows 10 machine with no energy-meter rails.** CPU watts fall back to the load model, so every reading is Estimated and a desktop has no better signal at all. Options: say so plainly in the wizard and carry on, or offer an opt-in kernel driver later. The driver was dropped from v1 once Windows 11 turned out to expose the rails without one.

## 17. Prerequisites to verify before implementation

- The development machine has the .NET 10 Desktop Runtime (10.0.11) but no .NET SDK (`dotnet --list-sdks` is empty). Install the .NET 10 SDK first.
- Confirmed 2026-09-10 on the development laptop, unelevated: the Energy Meter Interface is present and enabled, and reports the package, cores, integrated-graphics and memory rails. No sensor library or kernel driver is needed, so `LibreHardwareMonitorLib` was dropped from the design along with the PawnIO driver it now requires.
- Confirmed 2026-09-10: the GeForce MX330 reports no power at all through NVML or NVAPI. It is a whole class of low-end laptop GPUs with no measurement hardware, so the load-model fallback in §5 is the normal path, not the exception.
- Inno Setup 7.1 installed for the installer step (`installer\get-inno-setup.ps1`); 6.3 or later also compiles the script.
