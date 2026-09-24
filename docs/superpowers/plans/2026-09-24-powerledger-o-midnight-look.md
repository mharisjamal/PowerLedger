# Midnight Look Implementation Plan (Plan O)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A second front end for the App, "Midnight", chosen by the user next to the existing "Classic" one, per
`docs/superpowers/specs/2026-09-24-powerledger-midnight-look-design.md`.

**Architecture:** Two shell windows over one set of ViewModels. Classic (`Shell/MainWindow`) is untouched apart from a
Switch look button and the Look choice in Settings. Midnight (`Midnight/MidnightWindow`) has its own XAML, views,
styles and controls, and reads the same ViewModels. One palette dictionary at a time at Application level, resolved
from (look, theme choice, Windows); Midnight's palettes define both key sets, so shared dialogs take its colours.

**Tech Stack:** .NET 10 WPF, custom `OnRender` controls (`Controls/Instrument.cs` base), xUnit + Shouldly, the
RenderingTests harness (`tests/PowerLedger.App.Tests/RenderingTests.cs`: `StartUi`, `OnUi`, `UseTheme`, PNGs under
`%TEMP%\powerledger-renders`), bundled OFL fonts (Manrope, JetBrains Mono; Archivo and Martian Mono for Classic).

---

## Ground rules for every agent

- **Worktrees.** Each agent works in its own worktree on its own branch off `plan-o/base`: `D:\PowerLedger-n\o-f`
  (`plan-o/f`), `D:\PowerLedger-n\o-m1` (`plan-o/m1`), `D:\PowerLedger-n\o-m2` (`plan-o/m2`). Nothing is pushed.
- **Private TEMP.** `TEMP`/`TMP` = `C:\Users\haris\AppData\Local\Temp\pl-<agent>` (short path), because RenderingTests
  write PNGs under `%TEMP%` and two agents share a machine.
- **Tests.** `dotnet test tests/PowerLedger.App.Tests --filter "Category!=Hardware&Category!=Installed"` (UI included).
  The whole solution builds with 0 warnings (`TreatWarningsAsErrors`).
- **Test-first,** one commit per behaviour or tight group. Render tests for every new window or view, both themes,
  plus the short window (880 × 560).
- **CRLF.** The tree is CRLF; use the Edit tool or byte-level writes, never `sed -i` or Python text mode.
- **House style.** Read the neighbouring code before writing: `Theme/Styles.xaml` for how styles and fonts are declared,
  `Controls/Instrument.cs` and `Controls/StackedChart.cs` for how a drawn control takes brushes and describes itself
  for automation, `Now/NowView.xaml` for how a page binds. Comments say why, not what.
- **Classic is untouched** except where this plan names a Classic file. Its existing tests must pass unchanged.
- **Reference image.** The owner's reference is a dark-navy dashboard with a grouped sidebar, KPI cards with hatched
  progress bars, one large area chart with time-range pills, and a table with stage chips. Match its feel; use the
  tokens in Task 0, never ad-hoc colours.
- **Glass and motion** follow the project's `CLAUDE.md`: translucent "glass" only on the top bar, tooltips and menus
  (small surfaces; `BlurEffect` never on page content); motion 150–320 ms, ease-out in, ease-in out, a slight
  overshoot for the active pill; all of it off, except 120 ms opacity, when `SystemParameters.ClientAreaAnimation` is
  false.

## Task 0 — the contract (lead, before Wave 1)

Everything two agents both touch is fixed here first, so they can build in parallel against it.

### 0.1 The Look preference

- `src/PowerLedger.App/Looks/Look.cs`: `public enum Look { Classic = 0, Midnight = 1 }`.
- `Preferences/UiPreferences.cs`: a new field `Look Look` (default `Classic`), serialised as a string like `Theme`
  (`UiJson` context). An unknown value reads as `Classic` (test).
- `Preferences/IUiSettings`: `string? SetLook(Look look)` next to `SetTheme`; `AppPreferences` saves it and calls
  `LookSwitcher.Switch` (injected as `Action<Look>` so preferences don't depend on windows).
- `Looks/LookRules.cs` (pure): `static Palette PaletteFor(Look look, Theme theme)` returning the pack URI of one of
  `Theme/Palette.Dark.xaml`, `Palette.Light.xaml`, `Palette.Midnight.Dark.xaml`, `Palette.Midnight.Light.xaml`.
- `Theme/ThemeManager.cs`: `Choose(ThemeChoice)` stays; a new `Apply(Look look)` re-resolves with the current choice.
  It keeps the rule that the palette is exactly `Application.Current.Resources.MergedDictionaries[0]`.

### 0.2 Palette keys

Midnight's two palettes define **every Classic key** (`Brush.Ground`, `Brush.Panel`, `Brush.Raised`, `Brush.Line`,
`Brush.LineStrong`, `Brush.Ink`, `Brush.Ink2`, `Brush.Ink3`, `Brush.Amber`, `Brush.AmberSoft`, `Brush.OnAmber`,
`Brush.Measured`, `Brush.Calibrated`, `Brush.Estimated`, `Brush.PartCpu`, `Brush.PartGpu`, `Brush.PartDisplay`,
`Brush.PartRest`) with Midnight's values (`Brush.Amber` → `M.Accent`, `Brush.AmberSoft` → `M.AccentSoft`,
`Brush.OnAmber` → `M.OnAccent`, `Brush.Measured/Calibrated/Estimated` → the chip text colours), **and** the `M.*`
keys from the spec's §3 table, each a `SolidColorBrush`, plus:

| Key | Dark | Light | Used for |
|---|---|---|---|
| `M.GlassFill` | #10142A at 72 % | #FFFFFF at 70 % | the top bar and tooltips over content |
| `M.GlassBorder` | #FFFFFF at 8 % | #151A33 at 8 % | their 1 px edge |
| `M.GlassHighlight` | #FFFFFF at 14 % | #FFFFFF at 90 % | a 1 px inner top line |
| `M.Shadow` | #000000 at 45 % | #151A33 at 16 % | drop shadow colour for cards and tooltips |
| `M.Hatch` | #FFFFFF at 10 % | #151A33 at 10 % | the diagonal hatch on bars' remainders |
| `M.ChartFillTop` | `M.Accent` at 35 % | `M.Accent` at 28 % | the area chart's gradient, top |
| `M.ChartFillBottom` | `M.Accent` at 0 % | `M.Accent` at 0 % | the gradient, bottom |
| `M.Focus` | #8FA5FF | #3A5BFF | the 2 px focus ring |

Numbers that are not brushes live in `Styles.Midnight.xaml` as `sys:Double`: `M.Radius.Card` 14, `M.Radius.Pill` 999,
`M.Radius.Control` 8, `M.Blur.Tooltip` 12, `M.Elevation.Card` 12 (shadow depth), `M.Elevation.Tooltip` 20,
`M.Motion.Fast` 150 ms, `M.Motion.Base` 220 ms, `M.Motion.Slow` 320 ms (as `Duration`).

### 0.3 Style keys in `Midnight/Styles.Midnight.xaml` (owned by M1)

Every Midnight view uses only these keys (M2 asks the lead for any it lacks; page-only styles go in M2's own
`Midnight/Styles.Midnight.Pages.xaml`, merged after M1's).

- Fonts: `M.Font.Ui` (`./Fonts/#Manrope, Segoe UI Variable, Segoe UI`), `M.Font.Numbers`
  (`./Fonts/#JetBrains Mono, Cascadia Mono, Consolas`), `M.Font.Glyphs` (Segoe Fluent Icons, Segoe MDL2 Assets).
- Text: `M.Text.Display` (36, 600, numbers font), `M.Text.Title` (20, 600), `M.Text.Heading` (15, 600),
  `M.Text.Body` (13, 400), `M.Text.Secondary` (12, 400, `M.Ink2`), `M.Text.Muted` (12, 400, `M.Ink3`),
  `M.Text.Eyebrow` (10.5, 600, `M.Ink3`, tracking 1.4, upper-case through the `Upper` converter),
  `M.Text.Number` (13, 500, numbers font, tabular).
- Surfaces: `M.Card` (Border: `M.Panel` fill, 1 px `M.LineStrong`, `M.Radius.Card`, padding 20, `M.Shadow` drop
  shadow at `M.Elevation.Card`), `M.Card.Flat` (no shadow), `M.Glass` (Border: `M.GlassFill`, `M.GlassBorder`, the
  inner highlight line).
- Navigation: `M.NavItem` (RadioButton: 40 px tall, glyph + label, hover `M.Raised`, checked = filled `M.Accent`
  pill with `M.OnAccent` text; the pill morphs between items, see 0.5), `M.NavGroup` (the eyebrow label),
  `M.Badge` (a `M.Accent` circle with a white count).
- Controls: `M.Button.Primary`, `M.Button.Outline`, `M.Button.Quiet`, `M.IconButton` (36 × 36 round),
  `M.Pill` (RadioButton for range pills: 28 px, `M.Raised` track, checked `M.Accent`), `M.Switch` (a toggle),
  `M.Field` (TextBox), `M.Tick` (CheckBox), `M.Chip.Measured`, `M.Chip.Calibrated`, `M.Chip.Estimated`
  (Border + TextBlock, 22 px, `M.Radius.Pill`), `M.StatusPill.Good/Warn/Bad`.
- Tables: `M.Table.Header` (eyebrow text row with `M.Line` under it), `M.Table.Row` (44 px, hover `M.Raised`,
  bottom hairline), `M.ShareTrack` (the bar's track).
- Implicit, window-scoped: `ScrollBar` (thin, `M.LineStrong` thumb), `ToolTip` (`M.Glass`, `M.Blur.Tooltip`),
  `ContextMenu`/`MenuItem`, `TextBox`, `CheckBox`, `Button` (= `M.Button.Quiet`), `FocusVisualStyle` (`M.Focus`).

### 0.4 The shell contract

- `Looks/IShellWindow.cs`:

  ```csharp
  internal interface IShellWindow
  {
      Rect Bounds { get; set; }                 // left, top, width, height in device-independent pixels
      WindowState State { get; set; }
      Page Page { get; set; }                   // ShellViewModel.Page; setting it shows that page
      void Show();
      void CloseForSwitch();                    // closes without the hide-to-tray behaviour
      event EventHandler? Closed;
      Window Window { get; }                    // for the tray and dialog owners
  }
  ```

  `Shell/MainWindow` implements it (`CloseForSwitch` sets a flag `OnClosing` checks). So does `MidnightWindow`.
- `Looks/LookSwitcher.cs`:

  ```csharp
  internal sealed class LookSwitcher(Func<Look, IShellWindow> open, ThemeManager theme, Action<IShellWindow> retarget, ILogger log)
  {
      public IShellWindow Current { get; private set; }
      public Look Look { get; private set; }
      public string? Switch(Look target)   // null on success; a message when the new window can't open (the old stays)
  }
  ```

  Order: `theme.Apply(target)`; open the target; copy `Bounds`, `State`, `Page`; `Show()`; `retarget` (the tray and
  the dialogs' owner); `CloseForSwitch()` the old one. On an exception from `open`, the palette is reapplied for the
  old look and the message returned.
- `Shell/ShellViewModel.cs`: `Page` gains `Dashboard`; `Current` returns `Dashboard` (the new `DashboardViewModel`)
  for `Page.Dashboard`. Classic's rail never selects `Dashboard`; Midnight's sidebar never selects `Now`. When a
  window opens with the other look's page (`Now` ↔ `Dashboard`) it maps it.
- Both title bars get a **Switch look** button: glyph `&#xE8AB;` (Segoe Fluent "Switch"), tooltip "Switch to the
  Midnight look" / "Switch to the Classic look", calling `LookSwitcher.Switch`.
- The tray (`Tray/`) takes the window through `retarget`; the App's dialogs take their owner from
  `LookSwitcher.Current.Window` at the time they open.

### 0.5 Motion and glass tokens

- Durations from 0.2; `M.Ease.In` = `CubicEase EaseOut`, `M.Ease.Out` = `CubicEase EaseIn`, `M.Ease.Pill` =
  `BackEase EaseOut Amplitude 0.2`.
- `Midnight/Motion.cs`: `static bool Reduced => !SystemParameters.ClientAreaAnimation`; `static Duration Of(Duration d)`
  returns 0 when reduced (opacity fades keep 120 ms). Every animation goes through it.
- Page change: the outgoing view fades out (`Fast`), the incoming fades in and rises 8 px (`Base`, `M.Ease.In`).
- The active nav pill: one `Border` behind the items animates its `Canvas.Top` to the checked item (`Base`,
  `M.Ease.Pill`); reduced → jumps.
- Buttons: hover = a lighter fill (`Fast`); press = scale 0.97 around the centre (`Fast`); release = back with
  `M.Ease.Pill`. Cards: hover raises the shadow depth by 4 (`Fast`). Tooltips fade in (`Fast`).
- Glass: `M.Glass` surfaces use `M.GlassFill` over content; the tooltip alone adds `BlurEffect` (`M.Blur.Tooltip`)
  on the content behind it through a `VisualBrush` snapshot, never on the live tree.

### 0.6 Test conventions

- `tests/PowerLedger.App.Tests/Midnight/MidnightRenderingTests.cs` (`[Trait("Category","UI")]`) uses the same harness
  as `RenderingTests` (share `StartUi`/`OnUi`/`UseTheme` by making them `internal static` in a `UiHarness` class if
  they aren't already; F does this refactor first, in one commit, with the existing tests unchanged).
- PNGs: `midnight-{page}-{Dark|Light}.png`, `midnight-short-{page}.png`, `midnight-{dialog}-{theme}.png`.
- Contrast: `tests/PowerLedger.App.Tests/Theme/ContrastTests.cs` reads both Midnight palettes and asserts the pairs
  in the spec's §3 (text on `M.Ground`/`M.Panel`/`M.Raised` ≥ 4.5:1; `M.Accent`, `M.Good`, `M.Bad`, `M.Warn`, the
  four parts and `M.LineStrong` on `M.Panel` ≥ 3:1; chip text on chip ground ≥ 4.5:1). `Theme/Contrast.cs` holds the
  WCAG maths (relative luminance, ratio).

---

## Wave 1 — three agents in parallel

### Agent F — foundation and data (`plan-o/f`)

- [ ] **F1. Fonts.** Merge `plan-o/fonts` (Manrope, JetBrains Mono, Archivo, Martian Mono; `THIRD-PARTY-NOTICES.md`;
  the font-loading test). Confirm `Font.Ui`/`Font.Numbers` now resolve to Archivo/Martian Mono in a render.
- [ ] **F2. UiHarness.** Lift `StartUi`, `OnUi`, `UseTheme`, `Render(...)`, `Find<T>` and the PNG folder out of
  `RenderingTests` into `tests/PowerLedger.App.Tests/UiHarness.cs` (`internal static`), leaving every existing test
  passing unchanged.
- [ ] **F3. Look preference and rules** (0.1): `Look`, `UiPreferences.Look`, `IUiSettings.SetLook`, `LookRules`,
  `ThemeManager.Apply(Look)`. Tests: `UiPreferencesTests` (round trip, unknown value → Classic),
  `LookRulesTests` (4 combinations), `ThemeRulesTests` extended, `AppPreferencesTests` (SetLook saves and calls the
  switch action).
- [ ] **F4. Palettes** (0.2): `Theme/Palette.Midnight.Dark.xaml`, `Theme/Palette.Midnight.Light.xaml` with both key
  sets; `Theme/Contrast.cs`; `ContrastTests` over every pair in 0.6. Also a test that both Midnight palettes define
  every key the Classic palettes define (read the four dictionaries and compare key sets).
- [ ] **F5. Shell contract** (0.4): `IShellWindow`, `LookSwitcher`, `MainWindow : IShellWindow` (`CloseForSwitch`),
  `ShellViewModel.Page.Dashboard` and the `Now ↔ Dashboard` mapping, the Classic title-bar Switch look button
  (next to Minimise, `Caption` style), `App.xaml.cs` wiring (the window factory: `Look.Classic → new MainWindow`,
  `Look.Midnight → new MidnightWindow` — F adds a placeholder `Midnight/MidnightWindow.xaml` that is an empty window
  implementing `IShellWindow`, which M1 replaces; agree the constructor signature in the contract:
  `MidnightWindow(ShellViewModel shell, LookSwitcher looks, ThemeManager theme, Updater updates, Action feedback)`).
  Tests: `LookSwitcherTests` with fake windows (bounds, state, page carried; retarget called; a throwing factory
  keeps the old window and returns a message; the palette reapplied), `ShellViewModelTests` for the page mapping,
  a Classic render test showing the Switch look button.
- [ ] **F6. Settings → Look.** In `Settings/SettingsViewModel`: `Look` property (get/set → `_ui.SetLook`, message on
  error); in Classic's `Settings/SettingsView.xaml` PREFERENCES section: "Look" with two radio choices, Classic and
  Midnight, under the Theme choice. Tests: VM + render.
- [ ] **F7. Ranges.** `History/Ranges.cs`: `LastHour(now, zone, culture)` (bucket 1 minute, title "Last hour"),
  `LastYear` = `LastDays(365)` with a 1-day bucket, `All(first, now, …)` (from the first row's day, 1-day bucket).
  `RangeChoice` gains `LastHour`, `LastYear`, `All`. `HistoryReader.Read` honours a 1-minute bucket by reading
  minute rows (`ReadMinutes`) and a 1-day bucket by reading day totals; the 5-minute and 1-hour paths stay.
  `IHistory` gains `DateTimeOffset? FirstRow()` for All. Tests: `RangesTests` (bounds, buckets, titles, DST day),
  `HistoryReaderTests` (a minute-bucket read returns minute rows; a day-bucket read sums days; All starts at the
  first row) with the test database fixtures the App tests already use.
- [ ] **F8. DashboardViewModel** (`Midnight/Dashboard/DashboardViewModel.cs`, view-agnostic, no XAML):
  - takes `NowViewModel now, IRangeHistory history, IHistory summary, TimeProvider clock, TimeZoneInfo zone, CultureInfo culture, IUiThread ui`;
  - `Live`, `Today`, `Month`, `Status` forwarded from `now`;
  - `Kpis`: three `KpiCard` records `(string Label, string Big, string Small, string? Trend, TrendKind Kind, double Fill)`
    with `TrendKind { Up, Down, Flat, Quality, Text }`, per the spec's §4: Power now, Today, Idle waste this month;
  - `Range` (`RangePill` enum: Hour, Day, Week, Month, Year, All; default Day), `Chart` (`ChartModel`) rebuilt on
    change through `history.Read(range)`, and every minute for Hour/Day (a `PeriodicTimer` on `clock`);
  - `Parts`: rows `(Part, Glyph, string NowW, string Wh, double Share, Quality, string? Trend, TrendKind)` for
    `PartsRange` (Today, SevenDays, ThirtyDays) with the previous range for the trend;
  - `Show()`/`Hide()` like the other page VMs (start/stop the timer).
  - Maths in `Midnight/Dashboard/DashboardMaths.cs` (pure): `AverageDayWh(days)`, `TodayTrend(todayWh, avgDayWh, fractionOfDay)`,
    `Fill(value, max)`, `MonthTrend(thisWh, lastWh)`.
  - Tests: `DashboardMathsTests`, `DashboardViewModelTests` (FakeLink + fake history fixtures; KPIs, pills, timer,
    service down → "—", no history → empty chart, first month → "first month").

### Agent M1 — the Midnight shell and Dashboard (`plan-o/m1`)

Codes against Task 0 and F's placeholder; merges `plan-o/f` as F lands (the lead says when).

- [ ] **M1-1. `Midnight/Styles.Midnight.xaml`** with every key in 0.3 and the numbers/durations in 0.2, and
  `Midnight/Motion.cs` (0.5). A test reads the dictionary and asserts each key in 0.3 exists with the right type.
- [ ] **M1-2. Controls** (`Midnight/Controls/`, each an `Instrument` where drawn, with `Describe()` for automation):
  - `HatchBar` (a rounded track, the fill in a brush DP, the remainder in 45° hatch lines from `M.Hatch`, 6 px tall);
  - `ShareBar` (a thin bar in the part's colour, value 0–1);
  - `AreaChart` (from `ChartModel`: the total line 2 px `M.Accent`, the gradient fill top→bottom, stacked thin part
    bands below the line at 35 % opacity, sleep hatched, the dashed "now" line, x labels by the range's bucket
    ("12:00 AM", "Mon 12", "Jan"), y labels "0", "40 W" …; hover: a crosshair and a `ToolTip`-styled popup
    "12:00 PM · 92 W"; keyboard: Left/Right move the hover point, so the tooltip is reachable without a mouse);
  - `StatusPill` (dot + text, Good/Warn/Bad), `Initials` (a 28 px circle with 1–2 letters from a name, background
    from a 6-colour ring by name hash), `TrendMark` (▲/▼/— + text in Good/Bad/Ink3).
  - Geometry in `Midnight/Controls/AreaGeometry.cs` (pure: points, path, hit test) with `AreaGeometryTests`.
- [ ] **M1-3. `MidnightWindow.xaml(.cs)`** implementing `IShellWindow`: custom chrome (caption height 56, drag area
  = top bar), `WindowFit` on `OnSourceInitialized` like `MainWindow`, min 960 × 640; the sidebar (232 px, brand mark
  + "PowerLedger", groups OVERVIEW/INSIGHTS/SYSTEM per the spec, the morphing pill, badges: Household =
  `Household.PendingApprovals`; Support = feedback command), the foot (status dot + `Now.Status.State`, the update
  card restyled as `M.Card.Flat`, the bug button as `M.IconButton`); the top bar (`M.Glass`: page title, theme toggle
  sun/moon → `ThemeManager.Choose(Dark/Light)`, Switch look, Settings gear, PC name + kind glyph, signed-in e-mail);
  the page header row (title in `M.Text.Title`, `StatusPill` from `Now.Status`, members' `Initials` stack → Household);
  the page host (`ContentControl` + `DataTemplate`s for `DashboardViewModel`, `BreakdownViewModel` (M2's HistoryView),
  `ReportViewModel`, `HouseholdViewModel`, `SettingsViewModel`, `WizardViewModel` (Classic's WizardView), with the
  fade/rise transition); caption buttons; `Closing` → hide to tray unless `CloseForSwitch`.
- [ ] **M1-4. `Midnight/Dashboard/DashboardView.xaml`**: the three `KpiCard`s (`M.Card`: eyebrow label, `M.Text.Display`
  number, small line, `TrendMark`/quality chip, `HatchBar`), the chart card (title "Power over time", the pills
  1H · 1D · 1W · 1M · 1Y · All as `M.Pill`, `AreaChart`), the parts table card ("Where the power went", the range
  chooser Today / 7 days / 30 days as `M.Pill`, `M.Table.*` rows with glyph, name, now W, Wh, `ShareBar`, chip,
  `TrendMark`). Column minimums so 960 px wide still fits; cards wrap to one column under 1100 px.
- [ ] **M1-5. Render tests** (`MidnightRenderingTests`): the window on Dashboard in Dark and Light; the short window
  (880 × 560 → the sidebar and top bar fixed, the page scrolls); the tooltip at the chart's middle point; the update
  card and the bug button in the foot; the pill on each nav item after a click (position asserted); reduced motion
  (durations 0 through `Motion`). VM-free checks via `UiHarness.Find<T>`.

### Agent M2 — Midnight's other pages (`plan-o/m2`)

Codes against Task 0; merges `plan-o/m1` when M1's `Styles.Midnight.xaml` and `MidnightWindow` land (the lead
says when). Until then it renders each view inside a test host window that merges `Styles.Midnight.xaml` from M1's
branch (the lead gives M2 a copy of M1-1's commit as soon as it exists).

- [ ] **M2-1. `Midnight/History/HistoryView.xaml`** over `BreakdownViewModel`: the range pills (`M.Pill` bound to
  `RangeChoice`, the date pickers in `M.Field` style for Custom), the W/Wh `M.Pill` pair, the `StackedChart` in an
  `M.Card` with Midnight brushes through `Instrument` DPs, and the parts table in `M.Table.*` with `ShareBar` and
  chips. Render tests both themes + short.
- [ ] **M2-2. `Midnight/Report/ReportView.xaml`** over `ReportViewModel`: the export row (`M.Button.Outline` × 5, the
  "Include my household" `M.Tick`), the sheet as a 2-column grid of `M.Card`s: BILL and TIME ledgers, by-part rows,
  everyday equivalents, idle waste with advice, `QualityBar` restyled, `DailyBars` restyled. `SavePicture` keeps
  working (it renders the named sheet element; keep the name `Sheet`). Render tests.
- [ ] **M2-3. `Midnight/Household/HouseholdView.xaml`** over `HouseholdViewModel`: the explainer card with Add a PC
  (`M.Button.Primary`); the ledgers as three small cards; members as `M.Table.*` rows (`Initials`, name, kind glyph,
  last synced, `ShareBar`, the Remove/Remove its rows button as `M.Button.Quiet`); MANAGE and SIGN IN as cards; the
  Ask again / new recovery code lines. Render tests, including the no-household and left-member states.
- [ ] **M2-4. `Midnight/Settings/SettingsView.xaml`** over `SettingsViewModel`: each section an `M.Card` with an
  eyebrow title; controls in `M.Field`/`M.Tick`/`M.Switch`; PREFERENCES gains the Look choice (Classic / Midnight)
  and keeps Theme; PRIVACY's switches; ABOUT. `TakeTyped` (Enter saves) is shared: move it to
  `Settings/SettingsEntry.cs` as an attached behaviour used by both looks (a Classic-side change, in one commit, its
  tests unchanged). Render tests.
- [ ] **M2-5. Dialogs under Midnight's palette:** render the consent dialog, the join prompt, Add a PC, the recovery
  code, sign-in and the feedback window with `Palette.Midnight.Dark` and `.Light` applied (no XAML change expected;
  fix any hardcoded brush the render shows, e.g. `Caption.Close` hover keeps its red).
- [ ] **M2-6. `Midnight/Styles.Midnight.Pages.xaml`** for page-only styles (ledger rows, the sheet grid), merged by
  `MidnightWindow` after M1's dictionary.

---

## Wave 2 — the lead

- [ ] **L1. Merge** F, M1, M2 into `plan-o/base`; build with 0 warnings; every App test green three runs.
- [ ] **L2. Design review.** A reviewer compares the Midnight renders (dark, light, short) with the reference: hierarchy,
  spacing rhythm (8 px grid), the hatch bars, chip colours, the pill morph, contrast pairs, glass only where the
  `CLAUDE.md` allows, motion off under reduced motion. Findings fixed test-first.
- [ ] **L3. Code review** of the whole branch (Classic untouched beyond the named files; no hardcoded colours; no
  duplicated maths; the switch never loses the page or the tray).
- [ ] **L4. Sandbox.** The regression run (the installed App can't be driven there, so this checks install, upgrade
  from 0.7.0, `ui.json` with `"Look":"Midnight"` read without error by the service-side status, and uninstall).
- [ ] **L5. Docs.** README: a "Two looks" paragraph with a screenshot of each; PRIVACY unchanged (no new data).
- [ ] **L6. Release 0.8.0** (or together with 0.7.0 if that is still waiting): version, installers, CI, `release.ps1`.
- [ ] **L7. Results** below, and the memory.

## Results

Filled in after the release.
