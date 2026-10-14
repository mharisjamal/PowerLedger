# Plan AC: accuracy + performance (owner picked all bundles, 2026-10-14)

Base: main df3d5d4 (0.10.10). Reports: D:\PowerLedger-tmp\review\{accuracy,perf,research}\report.md. "A#" = accuracy finding, "P#" = perf finding, "R#" = research rec.

## Rules for every stream
- Work only in your worktree on your branch. Commit there (normal messages, no Co-authored-by). Never push. Never write in D:\PowerLedger.
- Env for every dotnet call: TEMP=TMP=D:\PowerLedger-tmp\ac-<stream>, NUGET_PACKAGES=D:\PowerLedger-tmp\nuget.
- Tests: TDD. Run your projects' tests often; before finishing run the whole solution once: `dotnet test PowerLedger.sln -c Release --filter "Category!=Installed&Category!=Hardware"`. Report pass counts and any failure verbatim. Known flakes: memory note, don't raise timeouts, find root cause or report.
- Files are CRLF: edit with the Edit tool; never sed -i / Python text writes that turn files LF.
- No em or en dashes in any user-visible text (UI, What's new, tooltips). Ranges use "to".
- Never touch the installed app or service, the owner's profile (%LOCALAPPDATA%\PowerLedger, %ProgramData%\PowerLedger), system or security settings, global shortcuts. Harness-only UI captures.
- Stay inside your owned files. A one-line hook in a file another stream owns is OK if unavoidable; say so in your report.
- Finish with: branch, commits, what changed per item, tests, anything skipped and why. Keep it under 300 words.

## Stream sensors (plan-ac/sensors)
Owns: src/PowerLedger.Sensors/** (GpuEngines.cs included) except SampleValidator.cs, Ups.cs, Nut/**, TdpTable.cs, tdp-table.json. Plus Core/DisplayModel.cs, the lid field in Core sample types.
1. EnergyMeter (P5, R2): one PDH query per tick (PdhOpenQuery + PdhAddEnglishCounter x4 + one PdhCollectQueryData), reuse buffers; watts from the meter's own `\Energy Meter(*)\Time` (ms) delta, not Stopwatch. Same for WindowsPowerMeters (PowerMeterSource.cs:160-168). Keep pWh maths.
2. Snapdragon (R1, R5, A18): QualcommRails knows SYS, PSU_USB and Microsoft's EMI names (DISPLAY_*, STORAGE_*, PSYS ...) per research report sec 1.1/1.5. Self-check: on battery minutes compare system rail with discharge; trust rail only at ratio 0.9 to 1.1, else model + one log line. May touch PowerModel.cs:148-158 only.
3. Battery + NVIDIA caching (P6): short-term battery flag once per sensor set; battery status every 5 s or on change; NVIDIA devnode located once, power state every few s (DevicePowerState.cs). Failed lookup must not poll the dGPU every tick (A19).
4. Display (P9): brightness from GUID_VIDEO_CURRENT_MONITOR_BRIGHTNESS notification, monitor set from GUID_DEVINTERFACE_MONITOR arrival/removal + GUID_CONSOLE_DISPLAY_STATE; slow WMI safety poll 15 min.
5. Lid shut (A3): carry MonitorInventory Active flag for the internal panel into the sample; DisplayModel uses 0 W when inactive; don't feed the calibration learner while shut (guard at the feed site).
6. GPU load (A16): only 3D/compute engines count as load (GpuEngines.cs:52-62); video decode does not.

## Stream loop (plan-ac/loop)
Owns: src/PowerLedger.Service/** except MonitorBoard.cs, ModelFactory.cs; plus Core/EnergyIntegrator.cs, Sensors/SampleValidator.cs, Storage/SqliteDatabase.cs.
1. Dedicated tick thread (P7): sensor thread is the loop, one waitable timer + command event; hang watchdog fires only on overrun; pipe pump async only while subscribed. Carry over loop command-order and watchdog tests.
2. 5 s when unwatched (P4, R3): configured rate while any App window or the overlay shows (ServiceSignals.WindowShowingIn), else 5 s. GapThreshold from each tick's own interval; validator windows time-based.
3. EcoQoS (P3, R4): SetProcessInformation(ProcessPowerThrottling, EXECUTION_SPEED) at service start.
4. Resume (P12): HardwareInventory.Detect at start and on device change only; on resume reuse facts, refresh display only, defer the rest a minute.
5. SQLite (P16): PRAGMA journal_size_limit = 4194304 on the writer.
6. Spike filter (A11): counter-derived GPUs (NVML Volta+, Level Zero, IGCL) only range-checked, like the CPU.
7. Awake stalls (A17): a gap only after a resume or past 60 s.

## Stream model (plan-ac/model)
Owns: Core/** except EnergyIntegrator.cs, DisplayModel.cs; plus Sensors/Ups.cs, Nut/**, TdpTable.cs, tdp-table.json, Service/MonitorBoard.cs, Service/ModelFactory.cs, Contracts/MachineProfile.cs. PowerModel.cs:79 (idle flag) belongs to stream insights; PowerModel.cs:148-158 to sensors.
1. Laptop calibration (A1): uncalibrated prior rest = 1 W + 0.5 x CPU. Learner: minute-average (or 20 s low-pass on CPU), fit rest = a_bucket + k x CPU from decayed sums, total slope clamped 1.0 to 2.2, intercepts shrunk toward a brightness line, half-life in battery hours (6 h). Migrate stored buckets as intercepts with k at the prior. Show k in status. Validate with a replay on D:\PowerLedger-tmp\review\accuracy db copies (scripts there); report bias before and after.
2. CO2 (A2): default = GridFactors.Suggested(region) on first run; migrate anyone exactly at 0.40; world figure 481 g.
3. Efficiency (A6): PsuRatedW parsed from the PSU model name when it carries one (e.g. RM750 -> 750), else 650 W; PsuEfficiency.At(tier, load/rated). Adapter curve with 65 W default rating and 0.1 to 0.2 W no-load. Fix NZXT E tier. No new UI.
4. GPU fallback (A8): add missing laptop keys (MX130/150/250/550/570, 940MX, GTX 960M, RTX 4050, laptop variants so "RTX 3050 Laptop GPU" does not match desktop); desktop idle = max(3 W, f x rating) with a cited f; GCN rest-of-card 1.4 (A16).
5. Totals (A13, A14): subtract charge / adapter efficiency in UPS and ACPI meter paths with a floor at the parts; a total under max(1 W, 0.3 x modelled parts), or a battery rate on a suspect transition tick, is no reading: fall back to the model. HID tiny-exponent misread covered.
6. UPS (A15): 0.6 W per VA up to 3 kVA, about 0.9 for apparent power; NUT input.realpower when present.
7. Monitors (A12): HDR on and not OLED -> brightness 1; a monitor that answered DDC then went quiet draws its off figure.

## Stream insights (plan-ac/insights)
Owns: App/Insights/**, App/Now/MonthOutlook.cs, Storage/ReportQueries.cs, App/Now/NowViewModel.cs lines 300 to 340 only, Core/PowerModel.cs idle flag (line 79 region) only.
1. Forecast (A4): count every day after the first seen day, 0 when no rows; projection = month so far + daily average x days remaining; remaining days priced at today's tariff.
2. Idle (A5): idle waste = user idle AND CPU load under 10% AND dGPU load under 5%; busy idle is active. App closed stays not idle. Today's "Wh wasted" counts both kinds like month and report; fix the 10-minute saving threshold maths; saving does not assume 0 W asleep.
3. Reports (A7): clip asleep gaps to the range; use an hour row only for hours older than the oldest kept minute row; clamp unmonitored w.To to `to`.
4. Minor (A19 [C]): Today trend and forecast "rest of today" use the hour-of-day shape of recent days, not an even spread; peak time on hour rows not shown as the hour's start.

## Stream app (plan-ac/app)
Owns: App/** except Aero/LiquidGlass/**, App/Insights/**, MonthOutlook.cs, NowViewModel.cs lines 300 to 340; plus scripts/publish.ps1, installer build, App csproj.
1. Hidden App (P1): while no shell window shows, NowViewModel keeps only last frame, peak, tray text; Dashboard, AeroWindow, DashboardView, SettingsView skip Live changes unless visible and rebuild once on show. Overlay keeps its own feed.
2. Tray (P2): cache TrayArt.Fit per (font, slots, size), clear on DPI change; tooltip written only when its whole-watt text changes; icons rendered lazily into a small LRU.
3. Hidden polling (P13): status 60 s, history 5 min, activity 15 s while hidden.
4. Midnight + Classic lists (P14): update rows in place or text-compare guard like Aero Plan U.
5. Build (P15): PublishReadyToRun for App and Service in publish.ps1 (and installer build if separate); MvvmToolkitEnableINotifyPropertyChangingSupport false. Check installer size change and report it.
6. App EcoQoS while every window is hidden, cleared on show.

## Stream glass (plan-ac/glass)
Owns: App/Aero/LiquidGlass/**, the glass setting row in Aero Settings, GlassSettings, docs/superpowers/specs/2026-10-01-aero-liquid-glass-engine.md.
1. Capture idle (P8): AcquireNextFrame long timeout (250 to 500 ms) while nothing is pending; short only while WantsFrame or a compare is outstanding; anything that relied on the timeout wakes the thread via an event. No per-round array alloc.
2. Battery (P10): when Windows Energy Saver is on (GUID_POWER_SAVING_STATUS / SystemStatusFlag), background glass refresh capped at 15 frames/s and 2% budget; moves still drawn at once (DrawNow). New Settings switch "Lighter glass on battery" (off by default) applies the same cap whenever on battery. Look unchanged otherwise.
3. Dirty rects (P11): copy only this and the previous frame's drawn rects, Present1 with dirty rects (or IDCompositionSurface BeginDraw). Pixel-diff before/after renders must match.
4. Measure CPU at rest and over video before/after with the spec's method; add numbers to the spec. Check for jank.

## Wave 2 (after merge): stream energy
1. Sleep energy (A10): export remaining capacity from SYSTEM_BATTERY_STATE; learn drain across suspend on battery; AC = drain / adapter efficiency; desktops 2 W; booked as "asleep (estimated)".
2. Battery round trip (A9): battery-sourced kWh and cost x about 1.22 as "wall equivalent"; live watts stay at the pack.

## Lead
Merge streams into plan-ac/merge, resolve conflicts, whole-branch review, 0.11.0 version + What's new, installers, owner draft (not public unless owner says publish).

## Wave 2 details (stream energy, worktree D:\PowerLedger-q\ac-energy, branch plan-ac/energy, base = merge of sensors+loop+model+insights+app)
Owns: whatever these need (Win32 battery state, loop resume hook, EnergyIntegrator/rollups, Storage schema V5 if needed, report/App display of the new figures), but not App/Aero/LiquidGlass/**.
1. Sleep energy (A10): export RemainingCapacity (mWh) from SYSTEM_BATTERY_STATE; on battery learn drain W across each suspend (capacity before minus after / hours, decayed average); on AC use that drain / the model's adapter efficiency; before any learning 0.5 W laptop, 2 W desktop (insights stream's IdleHabits already assumes these: make it read the same source). Book it as "asleep (estimated)" energy: totals, cost, reports and Today include it, shown separately labelled. Short sleeps fine; drop absurd readings (capacity rose, under 10 min, battery swapped).
2. Battery round trip (A9): energy and cost drawn from the battery count at the wall: x (1 / (adapter eff x charger eff x cell eff)), about 1.22, labelled "wall equivalent" where shown; live watts stay at the pack. Don't double count with the AC charge subtraction the model stream added (Sample.BatteryChargeW).
3. Cleanups: remove unused RollingMedian; fix HardwareInventory's stale "on resume" doc.
Give a short What's new line per item (plain, dash-free).
