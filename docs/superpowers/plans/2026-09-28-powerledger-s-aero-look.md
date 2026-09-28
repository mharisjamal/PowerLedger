# Aero Look Implementation Plan (Plan S)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (waves of parallel agents in
> worktrees, one whole-branch review at the end). Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship "Aero", a Liquid Glass look that matches the approved demo (`C:\Users\haris\Downloads\PowerLedger Aero
test.html`, the two Aero videos beside it, and the WPF prototype at `D:\PowerLedger-q\aero\tools\AeroPreview` on
`plan-s/aero-preview`) as PowerLedger's default look, with iPhone-style glass settings, four Insights and a glass watts
overlay, all on real data and fully tested, per `docs/superpowers/specs/2026-09-28-powerledger-aero-look-design.md`.

**Architecture:** Aero is a third `IShellWindow` beside Classic and Midnight, chosen by the same `LookSwitcher`. It
reuses every shared ViewModel (Shell, Now, Dashboard, Breakdown, Report, Household, Settings, Wizard, Updater) and ports
the prototype's glass system (tokens, GlassPanel, backdrop interop, motion, charts) into `src/PowerLedger.App/Aero/`.
Insights are pure maths in `src/PowerLedger.App/Insights/`, fed by the hour rows `IRangeHistory` already returns. The
overlay is an Aero-only topmost tool window bound to `NowViewModel.Live`.

**Tech Stack:** .NET 10 WPF, the DWM system backdrop (acrylic) with a wallpaper fallback, xUnit + Shouldly, the
`UiHarness` render tests (PNGs under `%TEMP%\powerledger-renders`), on-screen BitBlt captures, Geist (SIL OFL).

---

## Ground rules for every agent

- **Worktrees.** Each agent works in its own worktree on its own branch off `plan-s/contract` (Task 0):
  `D:\PowerLedger-q\s-g` (`plan-s/g`), `D:\PowerLedger-q\s-d` (`plan-s/d`), `D:\PowerLedger-q\s-p` (`plan-s/p`),
  `D:\PowerLedger-q\s-s` (`plan-s/s`), `D:\PowerLedger-q\s-i` (`plan-s/i`). **Never write in `D:\PowerLedger`.** Commit
  on your branch; never push. Run on Opus or Sonnet, never Fable.
- **Private TEMP.** `TEMP`/`TMP` = `C:\Users\haris\AppData\Local\Temp\pl-s-<agent>` (short path), because the render
  tests write PNGs under `%TEMP%` and five agents share a machine.
- **Tests.** `dotnet test tests/PowerLedger.App.Tests --filter "Category!=Installed&Category!=Hardware"` (UI included),
  plus any other test project you touch. The whole solution builds with 0 warnings (`TreatWarningsAsErrors`). A test
  that fails once and passes on a rerun is a flake to report with evidence, never a timeout to raise.
- **Test-first,** one commit per behaviour or tight group. Render tests for every new window, view, dialog, menu and
  toast, in both themes.
- **CRLF.** The tree is CRLF. Edit with the Edit tool or byte-level writes; never Git Bash `sed -i` or Python text
  mode on an existing file (they turn it LF or re-encode it). New files are written CRLF too.
- **No em or en dashes** in any user-visible text (XAML, strings, What's new, docs the user reads): ranges with "to",
  a missing value in words ("No reading").
- **House style.** Read the neighbouring code first: `Midnight/MidnightWindow.xaml(.cs)` for a shell,
  `Midnight/Dashboard/DashboardView.xaml` for a page, `Midnight/Controls/*` and `Controls/Instrument.cs` for a drawn
  control that describes itself for automation, `Preferences/UiPreferences.cs` for tolerant preferences. Long doc
  comments that say why and cite the spec's sections (`Aero look design §N`); comments say why, not what.
- **The prototype is a reference, read-only.** Port from `D:\PowerLedger-q\aero\tools\AeroPreview` file by file into
  this tree under the ownership below; never edit the prototype's worktree. Its demo data (`Feed.cs`) is not ported:
  everything binds to the real ViewModels.
- **Classic and Midnight are untouched** except where this plan names one of their files. Their tests pass, changed
  only where Aero joins their choices or page mapping (Task 0 already did that).
- **Review 11.** A render test puts the palette (and Aero's styles) on the window it draws, through
  `tests/PowerLedger.App.Tests/Aero/AeroHost.cs` (`AeroHost.Dressed`, `AeroHost.Window`), never among the application's
  dictionaries: the UI tests share one dispatcher.
- **Glass and motion** follow the project's `CLAUDE.md`: glass for navigation, floating controls, toolbars, menus,
  overlays and the selected or high-priority controls, not every card; `BlurEffect` only on small surfaces (menus,
  tooltips, the top bar), never on page content; spring and glide curves from `AeroMotion`, never linear; reduced motion
  (`AeroMotion.Reduced`) turns travel into short opacity and colour changes. Check for jank before calling a UI done.
- **Tokens, never raw values.** Colours come from the palette's `A.*` tokens (or the Classic/Midnight keys for shared
  UI) through `DynamicResource`; sizes, radii and durations from `Styles.Aero.xaml` and `AeroMotion`.
- **Ownership.** Each agent writes only the files the table below gives it, plus new test files under
  `tests/PowerLedger.App.Tests/Aero/` named for its work. A file you need changed that another agent owns: ask the lead,
  who either makes the change on `plan-s/contract` and tells everyone to merge it, or gives it to the owner.
- **Merging.** Wave 1 branches merge `plan-s/contract` whenever the lead says it moved, and `plan-s/g` once G1 lands
  (the lead says when); nothing else is merged between agents.
- **Report** at the end: commits, files, test counts, anything this plan got wrong.

## File ownership (Wave 1)

| Agent | Owns (new unless marked) |
|---|---|
| **G** glass system | `Aero/Styles.Aero.xaml` (fills Task 0's empty one), `Aero/Icons.Aero.xaml`, `Aero/Glass/*` (GlassPanel, Icon, GlassMaterial, Backdrop, WallpaperFrost), `Aero/Motion/AeroMotion.cs` (+ `Press`), `Aero/Controls/*` (RollingNumber, PulseDot, GlassSwitch, ColourWheel), `Theme/Palette.Aero.Dark.xaml` and `Palette.Aero.Light.xaml` (modified: every value), `Fonts/Geist/*`, `PowerLedger.App.csproj` (modified: the font resources), `THIRD-PARTY-NOTICES.md` (modified), `tests/.../Aero/AeroHost.cs` (modified), `tests/.../ContrastTests.cs` (modified: Aero's pairs) |
| **D** shell and Dashboard | `Aero/AeroWindow.xaml` and `.xaml.cs` (modified: the whole shell), `Aero/AeroPageHost.cs`, `Aero/Shell/*` (banner, search, bell, service and household menus, toasts, modals, update card), `Aero/Charts/LiveChart.cs`, `DailyChart.cs`, `PieChart3D.cs`, `Aero/Pages/Dashboard/*`, `Midnight/Dashboard/DashboardViewModel.cs` and `DashboardMaths.cs` (modified, additive only: what Aero's Dashboard and Parts pages need, `DashboardPart.Last7DaysWh` filled), `tests/.../Aero/AeroWindowTests.cs` (modified) |
| **P** pages | `Aero/Pages/History/*`, `Aero/Pages/Parts/*`, `Aero/Pages/Reports/*`, `Aero/Pages/Household/*` (the page and glass versions of its dialogs' content), `Aero/Styles.Aero.Pages.xaml` (page-only styles, merged after G's) |
| **S** settings and overlay | `Aero/Pages/Settings/*` (Glass and Overlay sections included), `Aero/Charts/Sparkline.cs`, `Aero/Overlay/*` (OverlayWindow, OverlayPlacement), `Tray/TrayIcon.cs` (modified: the overlay item), `App.xaml.cs` (modified: the overlay and its tray item only), `Settings/SettingsViewModel.cs` (modified, additive: what the Glass and Overlay sections bind to beyond `Glass` and `Overlay`) |
| **I** insights | `Insights/*` but `InsightsModels.cs` (Task 0's contract; ask the lead for a change): `BillForecast.cs`, `UsageAnomalies.cs`, `IdleHabits.cs`, `Carbon.cs`, `GridFactors.cs`, `Insights.cs` (the `IInsights`), `InsightsViewModel.cs` (modified: fills Task 0's), `Aero/Charts/Heatmap.cs`, `Aero/Pages/Insights/*`, `docs/privacy` or `PRIVACY.md` (modified: Insights run on the PC only) |
| **Lead** (Wave 2) | `App.xaml.cs` (the Insights wiring), `Shell/ShellViewModel.cs`, `Looks/*`, `Preferences/*`, `Directory.Build.props`, `Updates/WhatsNew.cs`, `installer/*`, `README.md`, release notes |

---

## Task 0: the contract (lead, `plan-s/contract`, done)

Everything two agents both touch is fixed here first, so they can build in parallel against it. All of it is on
`plan-s/contract` with its tests; read the code, not only this summary.

### 0.1 The look

- `Looks/Look.cs`: `enum Look { Classic = 0, Midnight = 1, Aero = 2 }`.
- `Looks/LookRules.cs`: `PaletteFor(Look.Aero, theme)` → `Theme/Palette.Aero.{Dark,Light}.xaml`.
- `Preferences/UiPreferences.cs`:
  - `Look` defaults to `Aero`; an unknown name reads as `Aero`.
  - `AeroIntroduced` (init-only, `= true`, so a new install's preferences have nothing to move while a ui.json from
    before it, which lacks the field, reads false). `UiPreferencesStore.Load` runs `Introduced()`: a file not yet moved
    is moved to Aero once, with `LookIntroduced = false` (the banner shows) and `LookBeforeAero` = the look it left
    (null when it had none or was already on Aero). Whatever the App saves next carries the move.
  - `LookBeforeAero` (`Look?`): where the banner's **Switch back** goes; Midnight when null.
- `App.OpenWindow`: `Look.Aero` → `new AeroWindow(shell, looks, theme, updates, OpenFeedbackWindow)`.
- At start, a saved Aero whose window won't open or show opens Classic instead and saves it (`LookSwitcher.Show`, as
  for Midnight). There is no Midnight step in the fallback: Classic is the one look that has no palette or window of
  its own to fail.
- Settings: the Look choice offers Classic, Midnight and Aero, in Classic's and Midnight's Settings views.
- `ShellViewModel.SwitchLook` is unchanged: Classic ↔ Midnight, and Classic from Aero. Aero's own Switch look is D's
  menu (D1).

### 0.2 Pages

- `Page` gains `Parts` and `Insights`, appended.
- `ShellViewModel(…, DashboardViewModel? dashboard = null, InsightsViewModel? insights = null)`; `Insights` property.
  `Current`: Parts → `Dashboard` (the Parts page is a view over `DashboardViewModel`); Insights → `Insights`, else the
  Dashboard, else Now. The Dashboard reads while Dashboard or Parts shows; `Insights.Show()`/`Hide()` follow the page.
- `EndSetup` lands on the Dashboard in any look but Classic.
- Each window's `OwnPage(Page)`: Classic maps Dashboard, Parts and Insights to Now; Midnight maps Now, Parts and
  Insights to the Dashboard; Aero maps Now to the Dashboard and keeps every other page.
- Usage counting names the pages "parts" and "insights".

### 0.3 Palettes

`Theme/Palette.Aero.Dark.xaml` and `Palette.Aero.Light.xaml` define every key Classic's and Midnight's palettes define
(`Brush.*`, `M.*`), with values of the same type (for now Midnight's dark and light values), and Aero's own tokens
under `A.`: the prototype's `Tokens.xaml` with each key prefixed (`C.Text` → `A.C.Text`, `B.GlassTint` →
`A.B.GlassTint`, `Glass.Frost` → `A.Glass.Frost`, `R.Panel` → `A.R.Panel`, `T.Big` → `A.T.Big`, `F.Ui` → `A.F.Ui`),
the light palette carrying the dark values until G sets its own. `ContrastTests` checks both key sets and types, and
that both Aero palettes define the same keys. G owns every value from here on; the key set only grows.

### 0.4 Glass and overlay settings

`Preferences/GlassSettings.cs`, both records properties of `UiPreferences` (`Glass`, `Overlay`), every property with a
setter and a default, loaded tolerantly (missing or null → default; unknown enum names, numbers or null → that field's
default through `NamedEnumJsonConverter<T>`; a bad colour → `#7466D8`, a good one upper-cased; sliders clamped), `Sanitised()` on every load:

```csharp
record GlassSettings { GlassStyle Style = Tinted; string TintColor = "#7466D8"; double TintStrength = 0.5, Frost = 0.6,
    EdgeLight = 0.6; GlassAccent Accent = Lime; GlassBackdrop Backdrop = Desktop; bool ReduceTransparency, IncreaseContrast;
    bool? ReduceMotion /* null follows Windows */; bool Parallax = true }   // no carbon factor: Insights read UiPreferences.Co2KgPerKwh
enum GlassStyle { Clear, Tinted, Dark, Colour }     enum GlassAccent { Lime, Ice, Indigo, Amber, Rose }
enum GlassBackdrop { Desktop, Wallpaper, Plain }    enum OverlayPosition { TopLeft, TopRight, BottomLeft, BottomRight, Free }
record OverlaySettings { bool Enabled; OverlayPosition Position = TopRight; double? Left, Top; double Opacity = 1 /* 0.55 to 1 */;
    bool Sparkline = true }
```

- `IUiSettings.SetGlass(GlassSettings)` and `SetOverlay(OverlaySettings)` save in range (`AppPreferences`, and
  `FakeUiSettings` for tests, which records "glass {Style}" and "overlay {on|off} {Position}").
- `SettingsViewModel.Glass` and `.Overlay`: get from the saved preferences; set saves (the same value again saves
  nothing), puts any problem on `AppMessage`, and raises the property. **This is the one live channel:** G's
  `GlassMaterial` and S's overlay listen to `SettingsViewModel.PropertyChanged` for `Glass` and `Overlay`; S's Settings
  sections, the overlay's menu and the tray item set them.

### 0.5 Insights contract

`Insights/InsightsModels.cs` (no maths):

```csharp
interface IInsights { InsightsReport Read(DateTimeOffset now, TimeZoneInfo zone); }   // off the UI thread; never throws for want of data
record InsightsReport(BillForecast Forecast, IReadOnlyList<UsageAnomaly> Anomalies, IdleHabits? Habits, CarbonEstimate Carbon);
record BillForecast(decimal ProjectedCost, decimal Low, decimal High, string? Currency, int DaysOfData, bool Ready)
    { const int DaysNeeded = 7; static BillForecast NotReady(int daysOfData, string? currency); }
record UsageAnomaly(DateTimeOffset Hour, double Kwh, double NormalKwh, double Times);
record IdleHabits(double[,] Heatmap /* [(int)DayOfWeek, hour] Wh */, int WorstWindowStart, int WorstWindowHours,
    decimal SavingPerMonthCost, double SavingPerMonthKwh, string? Currency);
record CarbonEstimate(double MonthKg, double SinceStartKg, double GramsPerKwh, string Source);
```

`Insights/InsightsViewModel(IInsights, UiThreads, TimeProvider, TimeZoneInfo)`: `Report` (null until the first read),
`Show()` reads off the UI thread and every `RefreshEvery` (15 minutes) while shown, `Hide()` stops; a read a newer one
overtook is dropped. I adds to it without changing that surface. `tests/.../FakeInsights.cs` answers a fixed report.

### 0.6 The Aero window

`Aero/AeroWindow.xaml(.cs)`: `AeroWindow(ShellViewModel shell, LookSwitcher looks, ThemeManager theme, Updater updates,
Action feedback)`, an `IShellWindow` with Midnight's members (`Bounds`, `State`, `Page` through `OwnPage`, `Window`,
`CloseForSwitch`, `FitTo` and fit-to-screen), moving the shell off Now once shown, and a placeholder showing the page's
name. It merges `Aero/Styles.Aero.xaml` (empty, G fills it) through `SharedDictionary`. The App's Closing handler hides
it to the tray, as for the other looks. `tests/.../Aero/AeroHost.cs`: `Dressed(window, theme)` (Aero palette and
styles on the window) and `Window(shell, theme, updates, feedback)` (an off-screen AeroWindow, as
`MidnightFixtures.Window`).

### 0.7 The Dashboard's parts

`DashboardPart.Last7DaysWh` (`IReadOnlyList<double>`, oldest first, empty by default): the Parts page's trend. D fills
it; P draws it.

### 0.8 Style contract (G1 delivers every key; the others use only these)

Every Aero view uses only these keys from `Aero/Styles.Aero.xaml` (P's page-only styles go in its own
`Styles.Aero.Pages.xaml`). Names follow the prototype's `Controls.xaml` and `Icons.xaml`, prefixed `A.`.

- Text: `A.Text.Big` (40, the big watts), `A.Text.BigMonth` (34), `A.Text.Mid` (22), `A.Text.Title` (16),
  `A.Text.Body` (14), `A.Text.Label` (13), `A.Text.Small` (12.5), `A.Text.Secondary` (`A.B.Text2`), `A.Text.Muted`
  (`A.B.Text3`), `A.Text.Axis` (11), `A.Text.Number` (tabular figures), `A.PanelTitle`, `A.Label`.
- Surfaces: the `GlassPanel` control's default style (panel radius, tint, rim, sheen, shadow, frost), `A.Well`,
  `A.Menu` (ContextMenu), `A.MenuItem`, `A.Modal`, `A.Toast`, `A.Banner`.
- Navigation: `A.NavItem` (RadioButton), `A.SegItem` (segmented pills), `A.OptItem` (a menu's choice).
- Buttons: `A.GlassBtn`, `A.RoundGlassBtn`, `A.GlassToggle`, `A.OutlineBtn`, `A.AccentBtn`, `A.GhostBtn`,
  `A.WhiteRoundBtn`, `A.ExpandBtn`, `A.RowBtn`, `A.MenuBtn`, `A.TextToggle`.
- Inputs: `A.SearchBox`, `A.Field` (TextBox), `A.Tick` (CheckBox), `A.Slider`, and the `GlassSwitch` control.
- Data: `A.Chip.Measured`, `A.Chip.Calibrated`, `A.Chip.Estimated`, `A.Table.Header`, `A.Table.Row`.
- Focus: `A.Focus.Pill`, `A.Focus.Soft` (the lime ring).
- Icons: `A.I.*` geometries (`A.I.Bolt`, `A.I.Dashboard`, `A.I.History`, `A.I.Chip`, `A.I.Report`, `A.I.House`,
  `A.I.Gear`, `A.I.Search`, `A.I.Bell`, `A.I.Overlay`, … every one in the prototype's `Icons.xaml`, plus
  `A.I.Insights`) drawn by the `Icon` control, in `Icons.Aero.xaml`, merged by `Styles.Aero.xaml`.
- Implicit, window-scoped: `ScrollBar`, `ToolTip`, `ContextMenu`, `MenuItem`, `TextBox`, `CheckBox`, and a
  `FocusVisualStyle`.
- Controls (C#, namespace `PowerLedger.App.Aero`): `GlassPanel`, `Icon`, `GlassSwitch`, `RollingNumber`, `PulseDot`,
  `ColourWheel`; `AeroMotion` (`Spring`, `Glide`, durations, `Reduced`, `Move`, `Fade`) and `Press` (the attached
  press behaviour).

---

## Wave 1: five agents in parallel

Each brief below stands alone: an agent needs this file's ground rules, its ownership row, Task 0's contract, the spec,
and nothing else.

### Agent G: the glass system (`plan-s/g`, `D:\PowerLedger-q\s-g`)

You build the visual foundation every other agent draws with. Read the spec's §3 and the prototype's `Tokens.xaml`,
`Controls.xaml`, `Icons.xaml`, `Glass.cs`, `Native.cs` and `Motion.cs` first.

- [ ] **G1. Styles first (other agents wait on this commit).** Port `Controls.xaml` and `Icons.xaml` into
  `Aero/Styles.Aero.xaml` and `Aero/Icons.Aero.xaml` with every key in 0.8, reading colours from the palette's `A.*`
  tokens by `DynamicResource`; port `Glass.cs` (`GlassPanel`, `Icon`), `Motion.cs` (`AeroMotion`, `Press`) and the
  prototype's `Switch` (as `GlassSwitch`) with the namespace `PowerLedger.App.Aero`. A test reads the dictionary and
  asserts each key in 0.8 exists with the right type (as `MidnightStylesTests`). Update `AeroHost.Dressed` if the
  window merges more than `Styles.Aero.xaml`. Commit, and tell the lead.
- [ ] **G2. Palettes.** Give `Palette.Aero.Dark.xaml` Aero's values for the Classic and Midnight keys too (the dialogs
  and the wizard in glass colours: `Brush.Ground` the deep backdrop tone, `Brush.Amber` → the lime accent with
  `Brush.OnAmber` its ink, and so on), and design `Palette.Aero.Light.xaml` from the same tokens for a light
  backdrop. Keys only grow; types don't change. Extend `ContrastTests` for Aero: text on the glass over the darkest and
  the brightest backdrop sampled (4.5:1), the accent, rims and focus ring (3:1), in both themes.
- [ ] **G3. `Aero/Glass/GlassMaterial.cs`.** Turns `GlassSettings` into the `A.*` resources on a window, live: listens
  to `SettingsViewModel.PropertyChanged` for `Glass` and updates the window's resources (never Application's), so the
  change shows at once. Clear = low tint; Tinted = the demo's; Dark = black 55 %; Colour = `TintColor` at
  `TintStrength`. `Frost` scales the frost blur, `EdgeLight` the rim. Reduce transparency = an opaque frosted fill.
  Increase contrast = solid rims, `A.C.Text*` at 100 %, a darker wash. The accent swaps the lime for ice, indigo,
  amber or rose, each with an ink that reads on it. `ReduceMotion` sets `AeroMotion`'s override (null follows
  Windows). Pure mapping tests for every style and flag, plus renders of a sample panel for each.
- [ ] **G4. Backdrop.** `Aero/Glass/Backdrop.cs` from `Native.cs`: the DWM system backdrop (acrylic) when Windows'
  transparency is on and the build has it; `WallpaperFrost.cs`: the wallpaper (`SPI_GETDESKWALLPAPER`) read once, blurred
  once, and again only when Windows says it changed; Plain otherwise, or when `Backdrop` says so. Never a transparent
  window over nothing. The decision is a pure function with tests; the interop is thin.
- [ ] **G5. Performance.** No per-frame frost alignment: frost realigns on move, resize or parallax only; parallax
  throttled to 30 Hz and off under reduced motion; tilt a 2D skew (WPF has no CSS 3D). `BlurEffect` only on small
  surfaces. A test counts `CompositionTarget.Rendering` subscribers at rest (none).
- [ ] **G6. Controls.** `Aero/Controls/RollingNumber.cs`, `PulseDot.cs` (from `Widgets.cs`), `ColourWheel.cs` (hue and
  saturation, keyboard operable, AutomationProperties), each with tests and renders.
- [ ] **G7. Geist.** Static Geist TTFs under `Fonts/Geist/` as resources with the OFL licence, listed in
  `THIRD-PARTY-NOTICES.md`; `A.F.Ui` becomes Geist with Segoe UI Variable as the fallback; a font-loading test as
  `FontTests`.

### Agent D: the shell and the Dashboard (`plan-s/d`, `D:\PowerLedger-q\s-d`)

You turn Task 0's placeholder `AeroWindow` into the demo's shell and build the Dashboard. Read the spec's §1 and §2,
the prototype's `MainWindow.xaml(.cs)` and `Charts.cs`, and `Midnight/MidnightWindow.xaml(.cs)` for how a shell here
binds, fits the screen, shows the banner and reports a failed switch. Merge `plan-s/g` when the lead says G1 landed;
until then, style with local placeholders you delete on merge.

- [ ] **D1. The shell.** Custom chrome, the sidebar (brand; Dashboard, History, Parts, Insights, Reports, Household,
  Settings; "Your PCs" with live watts from `HouseholdViewModel`; Switch look as a small menu of the other two looks,
  each chosen through `SettingsViewModel.Look`; the version and service state at the foot) with the glass pill that
  morphs between items; the top bar (search the history with Ctrl K, the bell with approvals and
  `InsightsViewModel.Report.Anomalies` (at most 3 a day), the service pulse with a Restart menu, the household
  button); the update card and the blocking update panel (`UpdateCover`, as Midnight's); the page host
  (`AeroPageHost`) with DataTemplates for every page (Dashboard and Parts over `DashboardViewModel`: Parts is P's view,
  chosen by `ShellViewModel.Page`; Insights is I's view over `InsightsViewModel`; History, Reports, Household are P's;
  Settings is S's; the wizard is Classic's `WizardView`). Keep the constructor and the `IShellWindow` members.
- [ ] **D2. The banner and the intro.** The one-time glass banner while `SettingsViewModel.LookIntroduced` is false:
  "PowerLedger has a new look", **Switch back** (to `UiPreferences.LookBeforeAero`, Midnight when null, through
  `SettingsViewModel.Look`, the banner retired first as Midnight's does) and **Got it**. The intro: panels rise in on
  the spring, 75 ms apart, then the content glides, then the charts draw; the camera push-in; toasts; modals that open
  from their trigger. Reduced motion: opacity only.
- [ ] **D3. The Dashboard** (`Aero/Pages/Dashboard/`), the demo's panels on real data: Power now (watts through
  `RollingNumber`, the cost toggle, kWh today, the change against yesterday); Last minute (`LiveChart`); This month
  (cost and energy, Open report, "Day N of M", the split by PC, the forecast range from
  `InsightsViewModel.Report.Forecast`, "Needs a week of data" until `Ready`); Energy each day (`DailyChart`, a month
  picker, this month against last month); Where the power goes (`PieChart3D` with `DashboardPart.Model` under each
  part); the history table (Day, Week, Month, Year; search; Save CSV). Add to `DashboardViewModel` only what is
  missing (additive; Midnight's tests pass unchanged), and fill `DashboardPart.Last7DaysWh` from the parts range's
  series. `Aero/Charts/LiveChart.cs`, `DailyChart.cs`, `PieChart3D.cs` with pure geometry tests.
- [ ] **D4. Tests.** `AeroWindowTests` extended; renders of the shell on every page (with P's, S's and I's views once
  merged in Wave 2; with placeholders before), both themes, 960 and 1440 px wide, the short window (880 × 560), the
  banner, the bell open, a toast, a modal, reduced motion; keyboard order through the sidebar, top bar and panels; Esc
  closes menus and modals.

### Agent P: History, Parts, Reports and Household (`plan-s/p`, `D:\PowerLedger-q\s-p`)

You build Aero's views of the existing pages. Read the spec's §1, Midnight's views of the same pages
(`Midnight/History`, `Midnight/Report`, `Midnight/Household`) for how each binds, and the demo for the look. Merge
`plan-s/g` when the lead says G1 landed; views use only 0.8's keys and your `Styles.Aero.Pages.xaml`.

- [ ] **P1. History** (`Aero/Pages/History/HistoryView.xaml`) over `BreakdownViewModel`: range pills (`A.SegItem`),
  the chart, the by-part table, search, Save CSV.
- [ ] **P2. Parts** (`Aero/Pages/Parts/PartsView.xaml`) over `DashboardViewModel.Parts`: each part's model, watts now,
  energy, share, quality chip and a 7-day trend drawn from `DashboardPart.Last7DaysWh` (a small drawn control of your
  own under `Aero/Pages/Parts/`; D fills the data).
- [ ] **P3. Reports** (`Aero/Pages/Reports/ReportsView.xaml`) over `ReportViewModel`: the preview, Save as PDF and the
  other exports; keep the sheet element named `Sheet` for `SavePicture`.
- [ ] **P4. Household** (`Aero/Pages/Household/`) over `HouseholdViewModel` and its account ViewModels: pairing by
  code, approvals, sign-in, members; glass versions of what the page shows. The shared dialogs (consent, join prompt,
  Add a PC, recovery code, sign-in, feedback, the wizard) keep their XAML and take Aero's colours from the palette:
  render each under both Aero palettes and list any key whose value doesn't suit to the lead (G owns the palette).
- [ ] **P5. Tests.** Renders of each view in a window dressed by `AeroHost.Dressed`, both themes, 960 and 1440 px,
  the empty and failed states; the automation names; no clipped text.

### Agent S: Settings and the overlay (`plan-s/s`, `D:\PowerLedger-q\s-s`)

You build Aero's Settings page, with the new Glass and Overlay sections, and the watts overlay. Read the spec's §3 and
§5, the prototype's `OverlayWindow.xaml(.cs)`, and `Midnight/Settings/SettingsView.xaml` for how Settings binds. Merge
`plan-s/g` when the lead says G1 landed.

- [ ] **S1. Settings** (`Aero/Pages/Settings/SettingsView.xaml`) over `SettingsViewModel`: every existing section in
  glass (tariff, machine, sampling, calibration, preferences with Theme and Look (Classic, Midnight, Aero), privacy,
  household, about).
- [ ] **S2. The Glass section:** Style (Clear, Tinted, Dark, Colour; Colour opens a picker with 8 presets and G's
  `ColourWheel`), a live preview tile, the Tint strength, Frost and Edge light sliders, the accent, the backdrop,
  Reduce transparency, Increase contrast, Reduce motion (showing Windows' setting until changed), tilt and parallax,
  and the Preferences section's "Use <country>'s figure" for CO₂ per kWh. Each change sets `SettingsViewModel.Glass` to a new
  record (`with`), which saves it and raises it; add to `SettingsViewModel` only helpers the section needs
  (additive).
- [ ] **S3. The overlay** (`Aero/Overlay/`): a topmost tool window (not in the taskbar or Alt Tab), the glass pill with
  the rolling watts from `NowViewModel.Live` and, when `OverlaySettings.Sparkline`, a 30 s sparkline
  (`Aero/Charts/Sparkline.cs`); "No reading" with none. Its right-click menu: the four corners or Free (drag), opacity,
  sparkline, close, each saved through `SettingsViewModel.Overlay`. `OverlayPlacement` (pure, tested): a corner of
  the work area, or the saved Free place clamped to the work area of the monitor it is nearest, again on display and
  DPI changes.
- [ ] **S4. Turning it on.** The Overlay section in Settings, the tray menu's item (in `Tray/TrayIcon.cs`, shown only
  while the look is Aero), and the top bar's button (D binds a command you expose; agree its name with D through the
  lead). In `App.xaml.cs`, only the overlay's wiring: open it when Aero shows and `Overlay.Enabled`, close it when the
  look leaves Aero or it is turned off.
- [ ] **S5. Tests.** `OverlayPlacement` on 1 and 2 monitors at 100 % and 150 %; the overlay's menu saves each choice;
  the tray item shows only in Aero; renders of Settings (both themes, each Glass style chosen) and the overlay (each
  corner, with and without the sparkline, "No reading").

### Agent I: Insights (`plan-s/i`, `D:\PowerLedger-q\s-i`)

You write the four Insights as pure, tested maths and the Insights page. Read the spec's §4, `Now/MonthOutlook.cs`
(month pricing), `Report/IdleAdvice.cs` (idle wording), `History/RangeHistory.cs` (`IRangeHistory.Read` with a
one-hour bucket gives `Aggregate` rows with energy, `IdleWh`, parts and quality) and Task 0's `InsightsModels.cs`.
Merge `plan-s/g` when the lead says G1 landed (the page only; the maths needs nothing from G).

- [ ] **I1. Bill forecast** (`Insights/BillForecast.cs`): each remaining day as the median kWh of the same weekday over
  the last 8 weeks, falling back to the month-to-date daily mean; priced at the month's average price, as
  `MonthOutlook`; the range the p10 to p90 of the backtested daily errors over the last 8 weeks, summed over the
  remaining days by bootstrap (1000 draws, a fixed seed); not `Ready` under `BillForecast.DaysNeeded` days.
- [ ] **I2. Unusual use** (`Insights/UsageAnomalies.cs`): a baseline per (weekday, hour) of the median and MAD over the
  last 8 weeks; flag when kWh > median + 3.5 × 1.4826 × MAD, and more than 30 Wh over the median, and the baseline has at
  least 4 samples. At most 3 a day for the bell; expose them on `InsightsViewModel` as `Alerts` (today's, newest first).
- [ ] **I3. Habits** (`Insights/IdleHabits.cs`): the 7 × 24 idle heatmap over 4 weeks (indexed by `DayOfWeek`), the
  worst 2-hour window, and the saving a month from sleeping after 10 idle minutes, worded as `IdleAdvice`.
- [ ] **I4. Carbon** (`Insights/Carbon.cs`, `GridFactors.cs`): kWh × factor; the factor is Settings' CO₂ per kWh
  (`UiPreferences.Co2KgPerKwh`; Wave 2 decided on no second setting), which a built-in table by Windows' region (about
  40 countries, each with its source and year in a comment), else the world average, only suggests; `Source` says which. Note for the lead: the App already
  has `UiPreferences.Co2KgPerKwh` (Settings' CO₂ field, the world average by default, used by Now and the Report); say
  in your report whether Carbon should read it instead of a second setting.
- [ ] **I5. `Insights/Insights.cs`,** the `IInsights`: one pass over the hour rows (8 weeks, one read), all four
  findings from it, DST days and missing hours handled, no throw for want of data. `InsightsViewModel` gains what the
  page binds to, and refreshes every 15 minutes while shown (Task 0 already does the reads).
- [ ] **I6. The page** (`Aero/Pages/Insights/InsightsView.xaml`, `Aero/Charts/Heatmap.cs`): the forecast with its
  band, the anomaly list with a mini chart against the normal, the heatmap with the worst window and the saving, the
  carbon figures and their source; "Needs a week of data" and the other empty states.
- [ ] **I7. Tests.** Golden series: a flat week, weekday and weekend patterns, a spike hour, missing hours, DST days,
  fewer than 7 days; the forecast range covers the actual in about 80 % of synthetic backtests; no false flags on
  seasonal noise, a 3× hour flagged; the carbon lookup; renders of the page in both themes, ready and not.
- [ ] **I8. Privacy.** `PRIVACY.md` (and `docs/privacy` if present): Insights are worked out on the PC from its own
  history; nothing new leaves it.

---

## Wave 2: the lead

- [ ] **L1. Merge** G, D, P, S and I into `plan-s/base`; wire `InsightsViewModel` (over I's `IInsights`) into the
  `ShellViewModel` in `App.xaml.cs`; resolve palette keys; build with 0 warnings; every suite green three runs.
- [ ] **L2. Design review** against the demo: the HTML and prototype screenshots beside the renders, panel by panel,
  both themes, every Glass style, reduced motion, Reduce transparency and Increase contrast.
- [ ] **L3. On screen** (BitBlt): the desktop backdrop with transparency on, the wallpaper with it off, the overlay in
  all four corners on two monitors at 100 % and 150 %.
- [ ] **L4. Performance:** idle 60 s at most 2 % of a core; the intro's frame times, 95th percentile at most 16.7 ms;
  memory at most Midnight's plus 60 MB.
- [ ] **L5. One whole-branch code review** (cavecrew-reviewer); fix the findings test-first.
- [ ] **L6. Release 0.10.0:** the version, What's new lines and release notes (Aero the default, Switch look returns to
  Midnight or Classic, Insights on the PC only), Geist in the installer's notices, the installers, a Sandbox upgrade
  from 0.9.4 (Aero opens with the banner; Switch back is kept; the overlay works; "Needs a week of data" on a fresh
  install), and the signed release on the owner's go-ahead.
- [ ] **L7. Results** below, and the memory.

## Results

Filled in after the release.
