# PowerLedger: the Aero look

A third front end for the App, next to **Classic** and **Midnight**, and the default from 0.10.0. **Aero** is an
Apple-inspired Liquid Glass look: translucent panels over the user's own desktop or wallpaper, a lime accent, a sidebar
with a morphing pill, and the Dashboard, History, Parts, Insights, Reports, Household and Settings pages on the same
ViewModels the other two looks read. It brings two things the others don't have: four **Insights** worked out on the PC
from the history, and a small glass **watts overlay** that floats over other windows.

Approved by the owner on 2026-09-28: the Liquid Glass demo (`Downloads\PowerLedger Aero test.html`), the two Aero
videos and the WPF prototype on `plan-s/aero-preview` (`tools/AeroPreview`), "exactly as shown". Decided with the owner
the same day:

- **Default.** Everyone lands on Aero once after updating; Switch look still takes them back to Midnight or Classic.
- **Premium** means the flagship look, free for everyone. There is no licence.
- **Insights:** all four: Bill forecast, Unusual use, Habits and idle tips, Carbon estimate.
- **Overlay:** Aero only.
- **Glass settings, like the iPhone's:** Clear, Tinted, Dark or a custom colour tint; tint strength; Reduce transparency;
  Increase contrast; Reduce motion; an accent colour.

## 1. What the user sees

- **Choosing.** Settings → Preferences → **Look** offers Classic, Midnight and Aero, in every look. Switching replaces
  the window in place, on the same page, at the same size and position, as it does between Classic and Midnight
  (Midnight look design §2). The choice is kept in `ui.json` (`Look`).
- **Aero is the default.** A new install opens in Aero. So does every PC updating from 0.9.x or earlier, **once**,
  whatever look it had: `ui.json` gains `AeroIntroduced`, and a file without it is moved to Aero as it loads, with
  `LookIntroduced` cleared so the banner shows, and the look it left kept as `LookBeforeAero`. After that the user's
  choice sticks: switching back to Midnight or Classic is kept across starts.
- **The banner.** The first time Aero shows after the move, a glass banner under the top bar says "PowerLedger has a
  new look" with **Switch back** and **Got it**. Switch back returns to `LookBeforeAero` (Midnight when there is none);
  either button, or any look switch, retires it for good (`LookIntroduced`), as Midnight's banner did.
- **Opening.** The demo's intro: the panels rise in on a spring, 75 ms apart; then the content glides in; then the charts
  draw. The user's desktop shows blurred behind the glass when Windows' transparency effects are on; otherwise their own
  wallpaper, frosted once.
- **The sidebar,** as in the demo: the brand; Dashboard, History, Parts, **Insights** (new), Reports, Household,
  Settings; then "Your PCs" with each one's live watts; then Switch look. The version (vX.Y.Z) and the service's state
  sit at the foot. The current page is marked by a glass pill that morphs from item to item.
- **The top bar:** search the history (Ctrl K); a bell holding approvals and unusual-use alerts; a service pulse with a
  Restart menu; the household button.
- **Dashboard,** the demo's layout on real data:
  - Power now: big watts, a cost toggle, kWh today, and the change against yesterday.
  - Last minute: a live chart.
  - This month: cost and energy, Open report, a bar with "Day N of M", the split by PC, and the forecast range from
    Insights (§4).
  - Energy each day: a month picker, this month against last month.
  - Where the power goes: a 3D pie with each part's model under its name (`HardwareNames`).
  - History table: Day, Week, Month and Year, with search and Save CSV.
- **Parts:** each part's model, watts now, energy, share, quality, and a 7-day trend.
- **Insights:**
  - Bill forecast: the likely month-end cost with a range; before a week of data, "Needs a week of data".
  - Unusual use: the flagged hours, each with a small chart against the normal level for that hour.
  - Habits: a 7 × 24 heatmap of idle energy, the worst window, and the saving a month from sleeping sooner.
  - Carbon: kg of CO₂ this month and since the start, with the grid factor and where it came from.
- **Reports and Household:** glass versions of the existing pages and dialogs: the report preview, Save as PDF,
  pairing by code, approvals, sign-in.
- **Settings:** the existing sections in glass, plus a new **Glass** section, which also replaces the demo's "Try the
  look" tuner:
  - Style: Clear, Tinted, Dark or Colour. Colour opens a picker with 8 presets and a hue and saturation wheel. A live
    preview tile shows the choice.
  - Tint strength, Frost (blur) and Edge light sliders.
  - Accent: lime (the default), ice, indigo, amber, rose.
  - Backdrop: Desktop (see-through), My wallpaper, or Plain.
  - Reduce transparency, Increase contrast, Reduce motion. Reduce motion follows Windows until the user changes it.
  - Light, Dark or Follow Windows (the existing Theme choice).
  - Tilt and parallax, on or off.
  - No carbon setting of its own: the Carbon insight uses the Preferences section's CO₂ per kWh, which Now and the
    Report use too, and whose "Use <country>'s figure" button fills in Windows' region's grid figure.
  - An **Overlay** section: on or off, its corner, opacity and sparkline.
- **The overlay (Aero only):** §5.
- **The wizard, the consent dialog, the household prompts, Add a PC, the recovery code, sign-in and the feedback
  window** keep their layouts and take Aero's colours through the palette (§3), as they took Midnight's.

## 2. Architecture: a third shell over the same ViewModels

Aero is a third `IShellWindow` beside `MainWindow` (Classic) and `MidnightWindow`, chosen by the same `LookSwitcher`
(Midnight look design §2): the palette first, then the new window at the old one's bounds, state and page, shown before
the old one closes. Nothing about switching changes; `Look` gains a third value and the App's window factory a third arm.

```
src/PowerLedger.App/
  Looks/Look.cs                  enum Look { Classic = 0, Midnight = 1, Aero = 2 }
  Looks/LookRules.cs             PaletteFor(Aero, theme) → Theme/Palette.Aero.{Dark,Light}.xaml
  Aero/
    AeroWindow.xaml(.cs)         the shell: IShellWindow, sidebar, top bar, page host, intro, banner, toasts, modals
    AeroPageHost.cs              the page transition
    Styles.Aero.xaml             Aero's keyed and implicit styles, merged at window level (never Application level)
    Icons.Aero.xaml              the prototype's icons
    Glass/GlassPanel.cs          the glass surface: tint, rim, sheen, frost
    Glass/GlassMaterial.cs       GlassSettings → brushes and numbers, updated live through DynamicResource
    Glass/Backdrop.cs            DWM system backdrop, the transparency check, the wallpaper fallback (Native.cs)
    Glass/WallpaperFrost.cs      the wallpaper read once (SPI_GETDESKWALLPAPER) and blurred once per change
    Motion/AeroMotion.cs         spring and glide curves, durations, press, reduced motion
    Charts/                      LiveChart, DailyChart, PieChart3D, Heatmap, Sparkline
    Controls/                    RollingNumber, PulseDot, GlassSwitch, ColourWheel
    Pages/                       Dashboard, History, Parts, Insights, Reports, Household, Settings views
    Overlay/                     OverlayWindow.xaml(.cs), OverlayPlacement.cs
  Insights/
    InsightsModels.cs            IInsights and its records (§4)
    InsightsViewModel.cs         the page's ViewModel: reads IInsights off the UI thread, every 15 minutes while shown
    BillForecast.cs, UsageAnomalies.cs, IdleHabits.cs, Carbon.cs, GridFactors.cs, Insights.cs (the IInsights)
  Preferences/GlassSettings.cs   GlassSettings and OverlaySettings, kept in ui.json
  Fonts/Geist/                   Geist, SIL OFL, with its licence; Segoe UI Variable is the fallback
```

- **ViewModels are shared.** `ShellViewModel`, `NowViewModel`, `DashboardViewModel`, `BreakdownViewModel`,
  `ReportViewModel`, `HouseholdViewModel` and its account ViewModels, `SettingsViewModel`, `WizardViewModel` and
  `Updater` are used as they are. `InsightsViewModel` is new.
- **Pages.** `Page` gains `Parts` and `Insights`, appended. `ShellViewModel.Current` shows the Dashboard for Parts (the
  Parts page draws `DashboardViewModel.Parts`, so the Dashboard reads while either shows) and the `InsightsViewModel`
  for Insights (the Dashboard, or Now, when there is none). The page mapping each window applies:

  | Page | Classic | Midnight | Aero |
  |---|---|---|---|
  | Now | Now | Dashboard | Dashboard |
  | Dashboard | Now | Dashboard | Dashboard |
  | Parts, Insights | Now | Dashboard | as it is |
  | Breakdown, Report, Household, Settings | as it is | as it is | as it is |

  Each window's `OwnPage(Page)` is the rule, and the look switcher's tests use it. Aero lands on the Dashboard, as
  Midnight does: `EndSetup` goes to the Dashboard in any look but Classic.
- **`AeroWindow(ShellViewModel shell, LookSwitcher looks, ThemeManager theme, Updater updates, Action feedback)`**, the
  same constructor as `MidnightWindow`'s. A close hides it to the tray through the App's Closing handler, as for the
  other two; `CloseForSwitch` closes it for good.
- **Switch look.** Classic's and Midnight's title-bar buttons keep `ShellViewModel.SwitchLook` (Classic ↔ Midnight;
  from Aero it offers Classic). Aero's sidebar item opens a small menu of the other two looks, each chosen through
  `SettingsViewModel.Look`, so the choice is saved and a failure lands on Settings' message line.
- **At start,** a saved Aero whose window won't open or show opens Classic instead, which is then saved, as a failing
  Midnight does (Midnight look design §5).
- **Usage counting** names the two new pages "parts" and "insights" (data-sharing design §3).

## 3. Theming and the glass settings

- **One palette at a time, at Application level,** as before (Midnight look design §3). Aero's two palettes,
  `Palette.Aero.Dark.xaml` and `Palette.Aero.Light.xaml`, define **every key** Classic's and Midnight's define
  (`Brush.*`, `M.*`), with values of the same type, so the shared dialogs, the wizard and anything drawn with a Midnight
  key take Aero's colours; and Aero's own tokens under `A.`, as the prototype's `Tokens.xaml` names them (`A.C.*`
  colours, `A.B.*` brushes, `A.Glass.*` numbers, `A.R.*` radii, `A.T.*` type sizes, `A.F.Ui`). A test checks the key
  sets and the types.
- **Aero's styles live at window level** (`Styles.Aero.xaml` merged into `AeroWindow.Resources`), so its implicit
  styles never reach Classic, Midnight or the dialogs.
- **Tokens.** Colours, radii, type and glass numbers come from the approved HTML through the prototype's `Tokens.xaml`:
  text #F3F4F6 at 100, 70 and 44 %; the accent lime #D3F03F with its ink #1B2004; the violet #7466D8; panels of radius
  26, wells 20, menus 18, modals 28; type from 40 (the big watts) down to 11 (axes). The prototype is dark only; the
  light palette is set by agent G to the same design, checked by the contrast tests.
- **`GlassSettings`** (`ui.json` → `Glass`), all with setters so a file from before a field keeps its default:

  | Field | Values | Default |
  |---|---|---|
  | `Style` | Clear, Tinted, Dark, Colour | Tinted |
  | `TintColor` | #RRGGBB | #7466D8 |
  | `TintStrength` | 0 to 1 | 0.5 |
  | `Frost` | 0 to 1 | 0.6 |
  | `EdgeLight` | 0 to 1 | 0.6 |
  | `Accent` | Lime, Ice, Indigo, Amber, Rose | Lime |
  | `Backdrop` | Desktop, Wallpaper, Plain | Desktop |
  | `ReduceTransparency` | bool | false |
  | `IncreaseContrast` | bool | false |
  | `ReduceMotion` | bool, or null to follow Windows | null |
  | `Parallax` | bool | true |

  Loading is tolerant: a missing or null object or field takes its default; a name this version doesn't know, a number
  or null for an enum takes that field's default; a colour that isn't #RRGGBB takes the default and a good one is kept
  upper-case; a slider out of range is clamped; a carbon factor an earlier build wrote there is ignored. None of these fails
  the rest of the file.
- **`GlassMaterial`** turns the settings into the glass: Clear is a low tint; Tinted the demo's; Dark black at 55 %;
  Colour the user's hue at the chosen strength. Reduce transparency is an opaque frosted fill. Increase contrast is
  solid rims, full-strength text and a darker wash. It listens to `SettingsViewModel.Glass`, which Settings raises
  after `IUiSettings.SetGlass` has saved the settings in range, and updates the `A.*` resources on the window, so the
  change shows at once and nothing is rebuilt.
- **Reduce motion** is `GlassSettings.ReduceMotion ?? !SystemParameters.ClientAreaAnimation`. Reduced, the large
  spatial movements (rise, glide, tilt, parallax, camera push-in) become short opacity and colour changes; nothing is
  lost but the travel.
- **Performance.** Blur is GPU-expensive in WPF (`CLAUDE.md`): the system backdrop does the desktop's blur; the
  wallpaper is frosted once, and again only when it changes; `BlurEffect` is used only on small surfaces (menus,
  tooltips, the top bar), never on page content. Frost realigns on move, resize and parallax only, never per frame;
  parallax is throttled to 30 Hz. Idle, the App uses at most 2 % of one core; animation runs only on interaction or the
  1 s live tick. The intro drops no frames (95th percentile at most 16.7 ms on the test PC). Memory stays within
  Midnight's plus 60 MB.

## 4. Insights

Pure maths in `src/PowerLedger.App/Insights/`, fed by the hour rows `IRangeHistory.Read` returns with a one-hour bucket
(`Aggregate`: energy, idle energy, parts, quality). Worked out on the PC; nothing is sent anywhere, and `docs/privacy`
says so.

- **The contract.** `IInsights.Read(DateTimeOffset now, TimeZoneInfo zone)` returns an `InsightsReport`
  `(BillForecast Forecast, IReadOnlyList<UsageAnomaly> Anomalies, IdleHabits? Habits, CarbonEstimate Carbon)`. It reads
  history, so it is called off the UI thread, and it never throws for want of data: a finding it can't make yet says so.
  `InsightsViewModel(IInsights, UiThreads, TimeProvider, TimeZoneInfo)` reads it as the page shows and every 15 minutes
  while it does; the Dashboard's forecast slot and the bell read the same report.
- **Bill forecast** (`BillForecast(ProjectedCost, Low, High, Currency, DaysOfData, Ready)`):
  - each remaining day of the month is predicted as the median kWh of the same weekday over the last 8 weeks, falling
    back to the month-to-date daily mean; the cost is at the month's tariff (its average price, as `MonthOutlook`);
  - the range is the 10th to 90th percentile of the backtested daily errors over the last 8 weeks, summed over the
    remaining days by bootstrap (1000 draws, a fixed seed, so the same data always gives the same range);
  - shown from 7 days of history (`BillForecast.DaysNeeded`); before that, "Needs a week of data".
- **Unusual use** (`UsageAnomaly(Hour, Kwh, NormalKwh, Times)`):
  - the baseline per (weekday, hour) is the median and the MAD over the last 8 weeks of hour rows;
  - an hour is flagged when its kWh is over the median plus 3.5 × 1.4826 × MAD, **and** more than 30 Wh over the
    median, **and** the baseline has at least 4 samples;
  - the bell gets at most 3 alerts a day. A Windows toast is optional and off by default.
- **Habits and idle tips** (`IdleHabits(Heatmap, WorstWindowStart, WorstWindowHours, SavingPerMonthCost,
  SavingPerMonthKwh, Currency)`):
  - a 7 × 24 heatmap of idle Wh, indexed by `DayOfWeek` and hour, from `IdleWh` over the last 4 weeks, averaged per week;
  - the worst 2-hour window across the week;
  - the saving a month if the PC slept after 10 idle minutes: the idle-on kWh past the first 10 minutes, at the tariff.
    The wording reuses `IdleAdvice`'s.
- **Carbon** (`CarbonEstimate(MonthKg, SinceStartKg, GramsPerKwh, Source)`): kWh × the grid factor. The factor is
  Settings' one CO₂ per kWh (`UiPreferences.Co2KgPerKwh`, 0.40 kg by default), which Now and the Report use too, so
  every screen agrees. A built-in table by Windows' region (about 40 countries, each with its source and year cited in
  `GridFactors.cs`), with the world average after it, only suggests a figure: Settings' "Use <country>'s figure"
  button. `Source` says in the page's words where the figure in use came from: the default, the region's or the
  world's table entry, or the user's own.

## 5. The overlay

- **What it shows:** a glass pill with the rolling watts and, optionally, a 30 s sparkline under them; "No reading"
  when there is none. It is a topmost tool window, not in the taskbar or Alt Tab, bound to `NowViewModel.Live`.
- **Turning it on:** Settings → Overlay, the tray menu (shown only in Aero), or a button in the top bar.
- **Its menu** (right-click): the four corners or Free (drag it anywhere), opacity, sparkline, close.
- **`OverlaySettings`** (`ui.json` → `Overlay`): `Enabled` (false), `Position` (TopLeft, TopRight, BottomLeft,
  BottomRight, Free; TopRight), `Left` and `Top` (where a Free overlay was dragged to, in device-independent pixels on
  the virtual screen; null until dragged), `Opacity` (0.55 to 1; 1), `Sparkline` (true). Loaded as tolerantly as
  `GlassSettings`; saved through `IUiSettings.SetOverlay` and raised by `SettingsViewModel.Overlay`.
- **Placement** is clamped to the work area of the monitor it is on, again on display and DPI changes, so a monitor
  unplugged never strands it off screen.
- **It hides** when the look leaves Aero, and comes back with Aero if it is on.

## 6. Error handling

- A history read that fails leaves each Insights card in its empty state ("Needs a week of data", "Nothing unusual",
  "No idle time yet") and tries again on the next refresh; nothing throws to the UI thread.
- The service down: the pulse and the overlay say so; the pages keep their last figures, as in the other looks.
- A look switch that can't open Aero keeps the old window and says why on Settings' message line (Midnight look design
  §5). Aero failing at start opens Classic, which is saved.
- Windows' transparency effects off, or the system backdrop not available: the wallpaper backdrop; no wallpaper, the
  plain one. Never a transparent window over nothing.
- `ui.json` with anything unknown or out of range in `Glass` or `Overlay`: that field's default, the rest kept (§3).
- A `Look` this version doesn't know reads as Aero.

## 7. Testing

- **Pure:** the Insights maths on golden series: a flat week, weekday and weekend patterns, a spike hour, missing hours,
  DST days, fewer than 7 days. The forecast range covers the actual in about 80 % of backtests on synthetic data. No
  false flags on seasonal noise; a 3× hour is flagged. The carbon table lookup. `GlassSettings` and `OverlaySettings`
  persistence and tolerant loading. The one-time move to Aero.
- **ViewModel:** `InsightsViewModel` (reads while shown, every 15 minutes, off the UI thread), `SettingsViewModel.Glass`
  and `.Overlay`, `ShellViewModel` page mapping and landing, `LookSwitcherTests` for Aero (switching with each look both
  ways, the page mapping, the start-up fallback to Classic).
- **Render (UiHarness, the UI category):** every Aero page, dialog, menu, toast and the overlay, in both themes, every
  Glass style, and with Reduce transparency, Increase contrast and reduced motion; at 960 and 1440 px wide. PNGs go to
  `%TEMP%\powerledger-renders\aero-*`, and are checked for the glass rim, text contrast and no clipped text. The palette
  goes on the window a test draws, never the application (review 11).
- **Contrast:** an automated WCAG AA check of text on glass over the darkest and brightest sampled backdrop pixels, for
  each style.
- **Accessibility:** tab order through the sidebar, top bar and panels; a visible lime focus ring; AutomationProperties
  names on every control; Esc closes menus and dialogs.
- **On screen** (BitBlt, not RenderTargetBitmap, which misses what the DWM draws): the desktop backdrop with
  transparency on, the wallpaper with it off, and the overlay in all 4 corners on two monitors at 100 % and 150 %.
- **Performance:** idle 60 s, then the process at most 2 % of a core; the intro's frame times from
  `CompositionTarget.Rendering`, 95th percentile at most 16.7 ms; memory at most Midnight's plus 60 MB.
- **Classic and Midnight:** their tests pass, changed only where Aero joins their choices or their page mapping.

## 8. Out of scope

- Any change to the service, the pipe, the storage, or the data server.
- New data collection: every figure comes from the history and the ViewModels that exist.
- A licence, paid tier or feature gate.
- The overlay in Classic or Midnight.
- Restyling the dialogs' layouts (they take the palette only).
- True 3D tilt: WPF has no CSS 3D, so tilt is a 2D skew approximation.

## 9. Release

Aero ships as **0.10.0** from `plan-s/*`, after 0.9.4. The What's new lines and release notes say Aero is the new
default, that Switch look returns to Midnight or Classic, and that Insights run on the PC only. Geist's licence goes
into `THIRD-PARTY-NOTICES.md` and the installer. The release is tested as an upgrade from 0.9.4 in Windows Sandbox:
Aero opens with the banner, Switch back is kept, the overlay works, and Insights say "Needs a week of data" on a fresh
install. The signed release waits for the owner's go-ahead.
