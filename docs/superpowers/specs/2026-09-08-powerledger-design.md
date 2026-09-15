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
- File: `C:\ProgramData\PowerLedger\power.db`. ACL: SYSTEM and Administrators full control, Users read and execute, inherited by everything in the folder and nothing inherited from above. A database users could edit would feed crafted input to a SYSTEM process. A read-only SQLite connection reads a WAL database whose `-wal` and `-shm` it cannot write (verified with SQLite 3.51), so the App can read history while the service runs; with the service stopped the App shows it as not running. The service refuses a data folder another account owns, because anyone may create folders in ProgramData, and sets aside database files another account owns.
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

`GetLive(seconds)`, `GetSeries(from, to, resolution)`, `GetTotals(range)` → kWh, cost, avg/peak W, hours on / idle / asleep, per-component kWh, quality mix; `GetIdleWaste(range)`; `GetDailyBuckets(range)`; `GetSessions(range)`; `GetTariffs()`; `GetCalibrationStatus()`.

## 8. IPC

- Named pipe `\\.\pipe\PowerLedger.v1`, ACL allowing local Authenticated Users, remote access denied.
- Newline-delimited JSON with `System.Text.Json` source generation. Messages carry `type` and, for requests, `id`. Max message 64 KB.
- Messages: `subscribe` (the server pushes a `reading` frame each tick, saying which parts were measured), `getStatus` (service version, per-source health and suspect counts, sensor restarts, calibration progress, DB size, write problems), `getSettings`, `setSettings`, `setTariff` (inserts a `tariffs` row; `effectiveFrom` defaults to now and may be backdated by the user), `resetCalibration`, and `reportActivity` (the App's idle seconds every few seconds, because the service in session 0 cannot see input).
- Multiple clients supported. The App reconnects with backoff from 1 s to 30 s.
- `SetSettings` accepts only tariff, machine profile, idle threshold, sample interval, and retention, each range-checked. No paths or commands travel over the pipe.

## 9. UI

### Stack

WPF on .NET 10 with its own `WindowChrome`, `CommunityToolkit.Mvvm`, QuestPDF for PDF. The meter, the budget bar, the sparkline and the charts are lightweight controls drawn with `DrawingContext`, whose geometry is tested as pure functions; WPF-UI's Fluent styles and LiveCharts2's SkiaSharp were dropped in Plan D1 because the design overrides the first almost everywhere and the second is far larger than four simple drawings need. The tray icon is WinForms' `NotifyIcon`, which also shows the monthly report's notification. One ViewModel per screen; no logic in code-behind.

### Screens

1. **Tray** — icon renders the live watts as text and updates only when the rounded value changes. Tooltip: now W and quality, today kWh and cost. Menu: Open, Start with Windows (on by default, set by the installer), Exit UI (service keeps logging).
2. **Now** — large live watts with quality badge, 60 s sparkline, meter scale with average and peak marks, today ledger (kWh, cost, avg, peak, on, idle, asleep, CO₂), month-to-date with projected month cost, power-budget bar and per-component rows, today's stacked-area chart by component.
3. **Breakdown** — stacked area CPU / GPU / display / rest over today / 7 d / 30 d / custom, W↔Wh toggle, per-component kWh and percentage table. Display means the internal panel plus opted-in external monitors. A negative rest band (measured mode, parts over-reporting) is clamped to zero on the chart and called out in a footnote.
4. **Report** — range summary: kWh, cost, CO₂ kg, avg/peak, on / idle / asleep / unmonitored hours (asleep is time the machine slept while the service was running; unmonitored is range time that produced no rows at all, such as before install), idle waste with saving suggestion, comparisons (LED-bulb hours at 10 W, phone charges at 15 Wh, EV km at 0.18 kWh/km), quality mix, daily bars. Export PDF, CSV (raw / 1 m / 1 h), PNG.
5. **Settings** — tariff and currency with history, CO₂ factor (country picker with bundled table, default 0.40 kg/kWh, editable), machine profile (chassis, PSU tier, extras, monitors, GPU/CPU TDP overrides), idle threshold, sample interval, retention, calibration status and reset, theme, start with Windows. About: service, driver, and per-source health.
6. **First-run wizard** — tariff (region → suggested rate) → confirm detected hardware → "Measured vs Estimated" explainer.

UI-only preferences (theme, start with Windows, units) live in `%LOCALAPPDATA%\PowerLedger\ui.json`. Everything else is service-owned.

### Monthly report

The App checks at startup and once per hour while running. When a month has ended and `Documents\PowerLedger\PowerLedger-YYYY-MM.pdf` does not yet exist for it, the App generates that PDF and shows a toast with the headline numbers. The App owns this because the service runs as SYSTEM and has no user Documents folder.

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
- No kernel driver is installed and no sensor read needs elevation. The service runs as LocalSystem for session and power notifications, not for hardware access.
- All data stays on the machine. Exports happen only when the user asks. No telemetry in v1; a future opt-in crash reporter would be a separate decision.
- The installer is code-signed (Azure Trusted Signing) before the first public release so SmartScreen does not flag it.

## 12. Testing

Framework: xUnit, Shouldly (BSD; FluentAssertions 8+ requires a paid commercial license), FsCheck for property tests, `Microsoft.Extensions.TimeProvider.Testing` for a fake clock.

- **Core**: `PowerModel` table tests (sample → reading for each mode); integrator invariants (energy ≥ 0, Δt cap, gap yields zero, resume reset); downsampler conservation (Σ 1-minute Wh equals the raw integral); tariff-at-time cost across rate changes; calibration convergence on synthetic battery traces; report and comparison math; property tests on the integrator.
- **Sensors**: validator driven by fake sources with glitch sequences (wrap, blip, spike, transition). Real-hardware adapter tests carry `Trait("Category","Hardware")` and are skipped in CI.
- **Storage**: temp-file SQLite; migration from every prior schema version; retention purge; a reader querying while the writer commits under WAL.
- **Service**: host the worker with fake sources and a temp DB, advance the fake clock through 10 simulated minutes including a suspend/resume, assert rows, aggregates, and sessions. Pipe round-trip and reconnect tests.
- **App**: ViewModel and geometry unit tests; the pipe client against a real pipe; history against a temp database; a rendering test that draws the window in both themes to PNG (Category UI, skipped in CI). A manual QA checklist per screen for v1.
- **Accuracy (manual, once)**: battery mode within ±5 % of `powercfg /batteryreport`; calibrated AC estimate within ±15 % of an inexpensive wall meter, on the developer's Dell Inspiron 3501.
- **Performance gate**: service < 0.5 % CPU and < 50 MB private working set, the figure Task Manager shows (the full working set adds shared system and driver DLL images, about 50 MB more on the development laptop, where the service measured 0.04 % CPU and 36.8 MB); App < 120 MB with a window open; measured with `dotnet-counters` and the process counters; 7-day soak on the developer machine with zero crashes.

## 13. Distribution

- Requirements: Windows 10 1809 or later, Windows 11, x64 only.
- Framework-dependent build; Inno Setup installs the .NET 10 Desktop Runtime if missing, so the installer stays around 15 MB.
- The installer registers the service with recovery options, adds the tray app to HKCU Run, and launches the first-run wizard. There is no driver to install. Uninstall stops the service and asks whether to keep the database.
- Releases on GitHub with a winget manifest after the first stable build. v1 has a "check for updates" link; an in-app updater is v1.1.
- CI (GitHub Actions): build, tests outside the Hardware and UI categories, installer artifact.

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
  setup.iss, driver/
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
- Inno Setup 6 installed for the installer step.
