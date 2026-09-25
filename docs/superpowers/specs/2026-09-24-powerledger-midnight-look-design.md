# PowerLedger: the Midnight look

A second front end for the App, chosen by the user, next to the one that exists. The existing one is now called
**Classic**; the new one is **Midnight**. Both show the same data from the same ViewModels. Midnight is modelled on a
dark-navy dashboard: a grouped sidebar, a page header with a status pill, three KPI cards with hatched progress bars,
one large area chart with time-range pills, and a table with share bars.

Decided with the owner on 2026-09-24: dashboard-first pages; a dark and a light variant; bundled open fonts (Manrope
and JetBrains Mono); the switch in Settings plus a title-bar toggle.

## 1. What the user sees

- **Choosing.** Settings → Preferences gains **Look: Classic / Midnight**. Each look's title bar also has a small
  "Switch look" button. Switching is immediate: the window is replaced in place, on the same page, at the same size
  and position. The choice is remembered in `ui.json` (`Look`).
- **Midnight is the default** (owner's decision, 2026-09-25): new installs, and every PC updating from 0.7.x or earlier
  (whose `ui.json` has no `Look`), open in Midnight. A PC whose user chose Classic keeps Classic.
- **Saying so once.** The first time Midnight shows, a banner under the page header says "This is PowerLedger's new
  look" and "Prefer the classic one? Switch back any time here, or in Settings → Preferences.", with **Switch back**
  and **Got it**. It is not a dialog and blocks nothing. Either button, or any look switch, retires it for good
  (`LookIntroduced` in `ui.json`). What's new for 0.8.0 says the same.
- **Midnight's window.**
  - A 232 px sidebar: the brand mark and "PowerLedger" at the top; then groups with small eyebrow labels:
    - OVERVIEW: Dashboard, History;
    - INSIGHTS: Report, Household;
    - SYSTEM: Settings, Support.

    The current page is a filled indigo pill with white text. Household shows a badge with the number of PCs waiting
    for approval; Support opens the feedback window. The foot holds the service status (dot and state), the update
    card and the bug button, restyled.
  - A top bar across the content: the page title on the left; on the right the theme toggle (sun/moon, Midnight's own
    dark/light), the Switch look button, a Settings gear, and this PC's name with a laptop/desktop glyph (the signed-in
    e-mail under it when there is one). No search box: there is nothing to search, and a box that does nothing would be
    a lie.
  - A page header row: the title again in a heavier weight, a status pill (**Recording** green, **Estimating** amber,
    **Service not running** red, from the same status the Classic status bar shows), and on the right the household's
    members as a stack of initials circles that opens the Household page.
- **Dashboard** (the page that replaces Now as the landing page):
  - Three KPI cards in a row. Each has a small label, a big number in the mono font, a trend (arrow and percentage,
    green or red) and a progress bar whose remainder is diagonally hatched:
    1. **Power now:** live watts; the quality badge (Measured, Calibrated, Estimated) as the trend slot; the bar is
       live watts over the machine's power budget.
    2. **Today:** kWh so far, cost under it; trend against the average day of the last 30; the bar is today over that
       average day.
    3. **Idle waste this month:** kWh spent on while the PC sat idle, its cost under it; trend against last month's
       idle waste; the bar is idle waste over the month's total.
  - **Power over time:** one area chart, the total watts as an indigo line over a soft gradient, a dashed "now" line,
    and hover crosshair with a tooltip ("12:00 PM · 92 W"). Range pills at the top right: 1H · 1D · 1W · 1M · 1Y · All.
    Below the line, the parts (CPU, GPU, display, rest) show as thin stacked bands in their own colours, so the
    breakdown is there without cluttering the line. Sleep is hatched, as in Classic.
  - **Where the power went:** a table with a range chooser (Today, 7 days, 30 days): one row per part, with a small
    glyph and name, watts now, Wh in the range, a share bar in the part's colour, a quality chip (olive Measured, gold
    Calibrated, blue Estimated, in the style of the reference's stage chips), and the trend against the previous
    range.
- **History:** the Breakdown page's content in Midnight's clothes: the range pills (Today, 7 days, 30 days, dates),
  the W/Wh switch, the stacked area chart in a card, and the by-part table.
- **Report:** the export buttons as a row of outline buttons; the sheet's ledgers, equivalents, idle advice, quality
  bar and daily bars as a grid of cards.
- **Household:** the explainer or the ledgers, and members as table rows with an initials circle, name, kind, last
  synced, and a share bar. Manage and Sign in as cards.
- **Settings:** each section as a card with its eyebrow title; the Look choice added to Preferences (in both looks).
- **The wizard, the consent dialog, the household prompts, Add a PC, the recovery code, sign-in and the feedback
  window** keep their layouts. They take Midnight's colours when Midnight is on, through the palette described in §3.
- **Theme.** Midnight has a dark and a light variant. The Theme setting (System, Dark, Light) applies to whichever
  look is on; Midnight's top-bar toggle sets it too.

## 2. Architecture: two shells over one set of ViewModels

Three ways were weighed:

1. **Resource dictionaries only.** Swap colours and styles under the same views. Not enough: the sidebar groups, the
   top bar, the dashboard and the tables are different layouts, not colours.
2. **Two shell windows, shared ViewModels.** `MainWindow` (Classic) stays as it is. A new `MidnightWindow` has its own
   XAML, its own view for each page, and its own DataTemplates. Switching closes one window and opens the other.
   Chosen: Classic stays untouched, Midnight has full freedom, and each look is tested on its own.
3. **One window, a shell template per look.** Saves the window swap, but WindowChrome, the tray's window reference and
   the page templates all become conditional. More entangled for no gain the user can see.

**Code layout.**

```
src/PowerLedger.App/
  Looks/
    Look.cs                    enum Look { Classic, Midnight }; LookRules (which window, which palette)
    LookSwitcher.cs            replaces the window: saves the choice, opens the other look, closes this one
    IShellWindow.cs            what a shell window offers: Bounds, State, Page, Show, CloseForSwitch
  Midnight/
    MidnightWindow.xaml(.cs)   the shell: sidebar, top bar, page host, caption buttons, fit-to-screen (WindowFit)
    Styles.Midnight.xaml       Midnight's keyed styles and its implicit overrides, merged at window level
    Dashboard/DashboardView.xaml, DashboardViewModel.cs, KpiCard.cs, RangeChart.cs
    History/HistoryView.xaml   (over BreakdownViewModel)
    Report/ReportView.xaml     (over ReportViewModel)
    Household/HouseholdView.xaml (over HouseholdViewModel)
    Settings/SettingsView.xaml (over SettingsViewModel)
    Controls/AreaChart.cs, HatchBar.cs, ShareBar.cs, StatusPill.cs, Initials.cs
  Theme/
    Palette.Dark.xaml, Palette.Light.xaml            Classic, as today
    Palette.Midnight.Dark.xaml, Palette.Midnight.Light.xaml
    ThemeManager.cs            resolves (look, theme choice, Windows) → one palette
```

- **ViewModels are shared.** `ShellViewModel` (its `Page` enum gains `Dashboard`; `Now` stays for Classic), `NowViewModel`,
  `BreakdownViewModel`, `ReportViewModel`, `HouseholdViewModel`, `SettingsViewModel`, `Updater`, the wizard and the
  dialogs' ViewModels are used as they are. `DashboardViewModel` is new and composes: `NowViewModel` for the live
  readout, today, the month and the status; a `RangeChart` for the big chart; and the KPI maths.
- **Page mapping.** In Midnight, `Page.Now` and `Page.Dashboard` both show the Dashboard; in Classic, both show Now.
  The rail and the sidebar each bind to the same `Page` property, so switching looks keeps the page.
- **`LookSwitcher.Switch(Look target)`:**
  1. saves `Look` through `IUiSettings`;
  2. asks `ThemeManager` for the palette of (target, theme choice);
  3. creates the target window through a factory (`Func<Look, IShellWindow>`), copies bounds, state and page;
  4. shows it, then closes the old one with `CloseForSwitch()`, which skips the hide-to-tray behaviour;
  5. points the tray at the new window.

  The factory is injected, so the switcher is tested with fake windows.
- **The title-bar toggle** calls the same switcher. So does Settings.

## 3. Theming

- **One palette at a time, at Application level,** as today: `ThemeManager` puts exactly one dictionary at
  `MergedDictionaries[0]`. Its resolve rule becomes `Resolve(look, choice, windowsUsesLight)`, giving one of four
  palettes. Midnight's palettes define **both key sets**: the Classic keys (`Brush.Ground`, `Brush.Ink`, `Brush.Amber`
  and the rest) with Midnight's values, so the shared dialogs, the wizard and the update card take Midnight's colours
  without a change; and Midnight's own keys (`M.*`) for what only Midnight uses.
- **Midnight's styles live at window level.** `Styles.Midnight.xaml` is merged into `MidnightWindow.Resources`, so its
  implicit styles (ScrollBar, ToolTip, TextBox, CheckBox) win inside that window and never leak into Classic or the
  dialogs.
- **Tokens (dark / light).**

  | Key | Dark | Light | Used for |
  |---|---|---|---|
  | `M.Ground` | #0B0E1C | #F3F4FA | the window |
  | `M.Panel` | #10142A | #FFFFFF | sidebar, top bar, cards |
  | `M.Raised` | #161B36 | #F7F8FD | table header, hover rows, pills' track |
  | `M.Line` | #1F2547 | #E2E5F1 | hairlines |
  | `M.LineStrong` | #2B3260 | #C9CEE3 | card borders, focus ring base |
  | `M.Ink` | #ECEEF9 | #151A33 | primary text |
  | `M.Ink2` | #A9AFCC | #5C6386 | secondary text |
  | `M.Ink3` | #6B7196 | #8A90AE | muted text, eyebrows |
  | `M.Accent` | #4C6FFF | #3A5BFF | active pill, chart line, buttons |
  | `M.AccentSoft` | #4C6FFF at 22 % | #3A5BFF at 14 % | chart fill top, badges' ground |
  | `M.OnAccent` | #FFFFFF | #FFFFFF | text on the accent |
  | `M.Good` | #34D399 | #1E9E6C | up trends, Recording |
  | `M.Bad` | #F87171 | #D64545 | down trends, Service not running |
  | `M.Warn` | #F5B942 | #C98A12 | Estimating |
  | `M.PartCpu` | #6D8CFF | #3A5BFF | CPU band, share bar |
  | `M.PartGpu` | #A78BFA | #7C5CE6 | GPU |
  | `M.PartDisplay` | #38BDF8 | #0891B2 | display |
  | `M.PartRest` | #7C86A8 | #8A90AE | rest |
  | `M.ChipMeasured` | #2F3A1E / #D9E27F | #EEF3D6 / #4B5A12 | ground / text |
  | `M.ChipCalibrated` | #3A3418 / #F1D57A | #FBF1CF / #7A5A08 | |
  | `M.ChipEstimated` | #1A2F44 / #7CC4F5 | #DDEFFA / #0E5A85 | |

  Every text-on-ground pair above meets 4.5:1; every pill, bar and line against its ground meets 3:1. The
  implementation checks the pairs in a test (contrast maths in `Theme/Contrast.cs`).
- **The brand mark stays amber** in both looks. Amber is not used as Midnight's accent.
- **Fonts.** Midnight uses `Font.Ui2` (Manrope, then Segoe UI) and `Font.Numbers2` (JetBrains Mono, then Cascadia
  Mono). Classic keeps Archivo and Martian Mono, whose files are bundled now (they were listed in the project but
  never added, so Classic has been rendering in Bahnschrift). All four are OFL-licensed static TTFs under
  `src/PowerLedger.App/Fonts/<Family>/` with their licences, listed in `THIRD-PARTY-NOTICES.md`. Static instances,
  because WPF ignores variable-font axes.

## 4. The Dashboard's data

- **KPI 1, Power now:** `Live.Watts`, `Live.Quality`, and `Live.Budget` totals from `NowViewModel`. The bar is
  `Watts / Live.Meter.Max` (the live meter's scale, which Classic's Now page draws the same reading against), capped
  at 1.
- **KPI 2, Today:** `Today.EnergyWh` and `Today.Cost` from `NowViewModel`. The average day is the mean of the last 30
  complete days' energy from the history reader (days with no rows are left out). Trend = today so far against the
  average day's energy up to the same time of day (the average day is scaled by the fraction of the day elapsed); the
  bar is today over the whole average day.
- **KPI 3, Idle waste this month:** the month's idle-on energy and cost from the same source the Report's idle-waste
  section uses (`ReportData` for the month to date). Trend against the previous month's idle waste; the bar is idle
  waste over the month's total energy. With no previous month, the trend slot says "first month".
- **Power over time** (`RangeChart`): the pills map to `DateRange`s from `Ranges`: 1H = a new `LastHour`; 1D =
  `Today`; 1W = `LastDays(7)`; 1M = `LastDays(30)`; 1Y = a new `LastDays(365)`; All = a new `All`, from the first row
  in the history. Each range's `Bucket` sets the row level, kept under about 400 points: a minute for the hour, five
  minutes for today, an hour for the week and the month, a day for the year and for all. `IRangeHistory.Read` returns
  the `RangeReport`, and `ChartModel` and `Charts` build the chart as they do for Breakdown, so the stacked parts and
  the sleep hatching come free. The chart re-reads on a pill click, and every minute for 1H and 1D. The hover tooltip
  reads the model's point under the pointer.
- **Where the power went:** the parts rows for the chosen range come from `BreakdownViewModel`'s builder for that
  range (Today, 7 days, 30 days); "watts now" from `Live.Budget`. The previous range is built once more for the trend.

Every number is formatted by the formatters Classic uses (`Units`, currency by the tariff), so the two looks never
disagree on a figure.

## 5. Error handling

- A history read that fails shows the chart's empty state, "No history yet" or "Couldn't read the history", with a
  retry on the next tick; nothing throws to the UI thread.
- The service down: the status pill says so, the live card shows "—" and the last known quality, and the rest of the
  page stays, as in Classic.
- A look switch that fails to open the new window (an exception in construction) keeps the old window, reverts the
  saved choice and shows the error in Settings' message line.
- `ui.json` with an unknown `Look` value reads as Midnight, the default look.
- A saved look whose window fails to open or show at start opens Classic instead (0.8.1): the reason goes to the App's
  log and Classic is saved, so the next start doesn't fail the same way.

## 6. Testing

- **Pure:** `LookRulesTests` (window and palette per look and theme), `ThemeRulesTests` extended, `ContrastTests`
  (every token pair), `DashboardMathsTests` (trends, bars, the average day, the previous month), `RangeChartTests`
  (row level per range, point counts), `AreaChartGeometryTests` (the line, the fill, the hover hit).
- **ViewModel:** `DashboardViewModelTests` with `FakeLink`, a fake history and `FakeTimeProvider`: KPIs from fixtures,
  range pills, the service down, no history. `LookSwitcherTests` with fake windows: bounds, page and tray carried
  over; a failing factory reverts.
- **Render (UI category):** `MidnightRenderingTests`: every Midnight page in dark and light, the short window
  (880 × 560) scrolling with the sidebar and top bar fixed, the KPI cards at the minimum width, the chart tooltip,
  and the shared dialogs under Midnight's palette (consent, join prompt, feedback). PNGs go beside Classic's, as
  `midnight-{page}-{Dark|Light}.png`.
- **Classic:** its existing tests pass unchanged; that is the proof Classic is untouched.

## 7. Out of scope

- A search box, notifications bell, or people avatars: nothing in PowerLedger fills them.
- Any change to the service, the pipe, the storage or the Worker.
- New data: every figure on Midnight's pages exists in Classic's ViewModels or the history reader today.
- Restyling the dialogs' layouts (they take the palette only).

## 8. Release

Midnight ships as 0.8.0 on its own branch (`plan-o/*`), after 0.7.0. If 0.7.0 is still waiting on the owner's sign-in
setup when Midnight is done and reviewed, the two go out together.
