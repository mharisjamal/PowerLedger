# Plan AE: Apps page, energy and cost by app (owner approved 2026-10-16)

Base: plan-ae/base (= plan-ad/contrast adaptive ink + Storage V6 `app_hours`, see Schema.V6 doc). Ships with the contrast fix as 0.12.0, owner draft first.
Rules: D:\PowerLedger-tmp\review\plan-ac.md "Rules for every stream" (TEMP D:\PowerLedger-tmp\ae-<stream>; filter add `&FullyQualifiedName!~HouseholdEndToEnd`). Screen tests take turns via the ScreenCapture.Aware mutex already.

## Product (approved)
- Main job: energy and cost by app, a ledger. Apps page is the hero: Today / Week / Month, apps sorted most to least, each row icon, name, share bar, kWh, cost; tap a row: processor vs graphics, how long it ran, "mostly in background" tag.
- Rows add up to the PC total. Apps, then (styled like Bands' Beside rows) "Programs that already closed" (::closed), "Other apps" (::other), "Other users" (::otherusers), then three fixed rows set apart: "Screen" (display_wh), "Windows and idle" (::windows + ::idle), "Rest of your PC" (rest_wh + asleep + charging losses, as Bands counts them). Shares of that whole, same scale and rules as Bands.Rows.
- Grouped by app, not process. Windows' own processes = one row. PowerLedger lists itself.
- Local only: never in data sharing, outbox, household sync or CSV/PDF shared exports unless the owner later asks.
- Dashboard: one line under "Where the power went": "Top app today: <name>, <kWh>" that opens the Apps page. Midnight and Classic: same list as a table on their History/Breakdown pages.
- Empty state (hours before 0.12.0 or first minutes): plain direct line, e.g. "Apps show here once PowerLedger has watched them for a few minutes."
- No em or en dashes in any text. Plain words.

## Attribution (service)
- No new timers: runs inside the existing tick on the sensor thread, at whatever cadence the loop runs (1 s watched / 5 s unwatched).
- Processes: one NtQuerySystemInformation(SystemProcessInformation) per tick, reused buffer; per process cycle time (CycleTime) and kernel+user time, session id, create time (pid reuse). Image path via OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) + QueryFullProcessImageName, cached per (pid, create time); Store apps by package family (GetPackageFamilyName). Display name from the exe's version info (FileDescription, else ProductName, else file name), cached per path.
- Processor: idle baseline = the CPU package's resting watts (a slow, decayed low estimate, e.g. low percentile of recent minutes at under 5% load) goes to ::idle. The rest (cpu W minus baseline, at least 0) is split by each live process's cycle share. Busy time no live process explains (GetSystemTimes busy minus the sum of live processes' time deltas) takes its share as ::closed. Integrated graphics engine time (iGPU sits in the package on laptops) may add weight to the package split; document what you do.
- Graphics: the discrete card's watts above its idle go by per pid Running Time on that card's LUID, from the GPU Engine counters GpuEngines already reads (no extra PDH query); its idle to ::idle.
- Keys: path in lower case, or "pkg:<family>"; ::windows for images under %SystemRoot% plus the kernel's System, Registry, Secure System and Memory Compression processes (pid 0 Idle is never an app); ::otherusers for processes in another interactive session (not 0, not the console session); session 0 non-Windows services are their own apps.
- Foreground: the App adds the foreground window's pid (GetForegroundWindow + GetWindowThreadProcessId) to its existing activity report (optional field in Contracts, old Apps send none); the service books front_s to that app.
- Hourly rows: accumulate per app for the current hour in memory (cap 500 keys, overflow to ::other), keep the busiest 25 plus ::other and the specials, upsert with the existing minute write. Each hour's cpu_wh / gpu_wh summed over keys equal samples_1h's for that hour (the same watts and dt the integrator used; gaps excluded the same way). Retention 400 days.
- Cost tiny: report per tick cost (target under 1 ms at 300 processes) and allocations.
- Check: a busy loop process gets its measured extra watts within about 15%.

## App
- New Aero page "Apps" in the nav (follow existing Aero pages: Parts/History patterns, RowsInPlace, GlassInk adaptive ink, glass only where the Aero spec puts it, motion rules in CLAUDE.md, the period switch's morphing indicator as on the Dashboard). Icons from the path (SHGetFileInfo / Icon.ExtractAssociatedIcon), cached; Store apps and specials get a plain glyph.
- Reader in Storage (new AppQueries, read only, like ReportQueries): range -> rows by app with cpu_wh, gpu_wh, run_s, front_s, plus the hour rows' display/rest/asleep/battery for the fixed rows; days cut like reports (half-hour zones, DST).
- Cost: the range's tariff the same way reports price energy.
- Midnight and Classic: a table with the same rows. Dashboard top-app line in Aero and Midnight.
- Accessibility: keyboard, focus visible, screen reader names ("Google Chrome, 0.32 kWh, 18 percent, 4 rupees"), reduced motion.
