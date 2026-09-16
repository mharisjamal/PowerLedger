# Plan J — Monitors that know themselves

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** PowerLedger detects each external monitor, knows or estimates its power, scales it by the brightness the
monitor reports, and never asks what Windows can already tell it.

**Architecture:** The service detects monitors over WMI (maker, model, size, native resolution) every minute, looks each
one up in a shipped Energy Star table or estimates it from size and resolution, and keeps them on a thread-safe
`MonitorBoard` that the power model reads every tick and the status reports to the App. Brightness can only be read in the
user's session, so the App reads it over DDC/CI (read-only, capability-gated) and reports it to the service through a new
pipe message. The user's choices per monitor (count it, a typed figure) live in the machine profile; old settings migrate.
Design: `docs/superpowers/specs/2026-09-16-powerledger-monitors-design.md`.

**Tech Stack:** .NET 10, WMI (`System.Management`), Dxva2/User32 P/Invoke, System.Text.Json source generation, xUnit +
Shouldly, WPF.

---

## Facts this plan relies on (from the code map of 2026-09-16)

- `DisplaySource` (`src/PowerLedger.Sensors/DisplaySource.cs`) runs on the service's sensor thread every tick and
  re-queries WMI every minute (`WmiMonitorBasicDisplayParams` active count, `WmiMonitorBrightness` first row).
- `HardwareInventory` (`src/PowerLedger.Sensors/HardwareInventory.cs`) tells internal panels apart with
  `BuiltInConnections = [6, 11, 13, 0x80000000]` on `WmiMonitorConnectionParams.VideoOutputTechnology`, and computes the
  built-in diagonal with `BuiltInDiagonal` (rounds integer-cm EDID sizes, so 15.6" reads 15.3").
- `DisplayModel.MonitorWatts(profile, displayOn)` (`src/PowerLedger.Core/DisplayModel.cs:37-41`) is the only place
  monitors reach the model: `ExternalMonitors × (displayOn ? MonitorWatts : 0.5)` when `IncludeMonitors`. `PowerModel`
  adds monitors after the supply-efficiency division, and on top of a measured battery rate.
- `MachineProfile` (`src/PowerLedger.Contracts/MachineProfile.cs`) holds `ExternalMonitors` (0, desktop 1),
  `IncludeMonitors` (false), `MonitorWatts` (25). Stored settings load by reflection so missing properties keep their
  defaults; the pipe's source-generated JSON does not, so every new pipe property needs a safe zero value.
- `ServiceStatus` (`src/PowerLedger.Contracts/ServiceStatus.cs`) carries no hardware; the App reads detection from the
  database. Pipe message kinds may be added, never renamed; the pipe name is the protocol version.
- The service runs in session 0, so DDC/CI (`Dxva2.GetPhysicalMonitorsFromHMONITOR`) cannot reach the user's monitors
  there. The App runs in the user's session all the time (tray).
- The wizard's machine step, as a desktop, must fit an 880×560 window without scrolling (`RenderingTests`).

## Execution

| Wave | Agent | Branch (worktree) | Tasks |
|---|---|---|---|
| 0 | data agent (done first) | `plan-j/data` | the Energy Star table: `assets/monitors/energy-star-monitors.csv`, its script and README |
| 1 | A | `plan-j/contracts` | 1–3: shared types, pipe message, power model |
| 1 | B | `plan-j/catalogue` (from `plan-j/data`) | 4–5: catalogue and estimate |
| 1 | C | `plan-j/inventory` | 6: WMI monitor inventory, diagonal snapping |
| 1 | D | `plan-j/ddc` | 7: the App's DDC/CI brightness reader |
| 2 | E | `plan-j/service` (from the merged wave 1) | 8–10: board, detection loop, migration, status |
| 2 | F | `plan-j/app` (from the merged wave 1) | 11–13: reporter, wizard, Settings |
| 3 | lead | `plan-j/monitors` | docs, review, tests, Sandbox, CI, release 0.4.0 |

Rules as before: commit per task with the given message as Haris, stage by explicit path, no attribution lines, never
push, touch only your task's files. `dotnet build -c Release` 0 warnings. Unit tests for a project:
`dotnet test tests/<Project>.Tests -c Release --filter "Category!=Hardware&Category!=UI&Category!=Installed"`.

---

## Wave 1

### Task 1: Shared monitor types (Agent A)

**Files:** Create `src/PowerLedger.Contracts/Monitors.cs`; modify `MachineProfile.cs`, `ServiceStatus.cs`,
`ServiceSettings.cs`, `PipeMessages.cs`, `PipeProtocol.cs`; tests in `tests/PowerLedger.Service.Tests`
(`PipeProtocolTests.cs`, `ServiceSettingsTests.cs`) and a new `tests/PowerLedger.Service.Tests/MonitorKeysTests.cs`
(the Contracts project's tests live in the Service test project — check, and follow where `PipeProtocolTests` lives).

`src/PowerLedger.Contracts/Monitors.cs`:

```csharp
namespace PowerLedger.Contracts;

/// <summary>Where a monitor's watts came from (spec §5). Stored as a number: append, never renumber.</summary>
public enum MonitorSource
{
    /// <summary>Energy Star's measured figure for this model.</summary>
    Model = 0,

    /// <summary>Worked out from the monitor's size and resolution.</summary>
    Estimate = 1,

    /// <summary>Typed by the user, which always wins.</summary>
    Typed = 2,
}

/// <summary>What the user said about one monitor: whether it counts, and a figure of their own. A monitor with no choice
/// counts, with the figure PowerLedger works out.</summary>
public sealed record MonitorChoice
{
    public string Key { get; init; } = "";

    public bool Counted { get; init; } = true;

    /// <summary>On-mode watts typed by the user, or null for PowerLedger's own figure.</summary>
    public double? Watts { get; init; }
}

/// <summary>One external monitor as the service sees it, for the App (spec §9).</summary>
public sealed record MonitorStatus
{
    public string Key { get; init; } = "";

    /// <summary>Windows' device instance, which the App's brightness reports name (see <see cref="MonitorKeys"/>).</summary>
    public string Instance { get; init; } = "";

    /// <summary>"DELL U2723QE", or the maker and product code when the monitor gives no name.</summary>
    public string Name { get; init; } = "";

    public double Inches { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>On-mode watts at the list's test brightness, before this monitor's own brightness.</summary>
    public double OnWatts { get; init; }

    public double SleepWatts { get; init; }

    public MonitorSource Source { get; init; }

    public bool Counted { get; init; }

    /// <summary>The brightness the App last reported, 0–1, or null when the monitor didn't answer.</summary>
    public double? Brightness { get; init; }

    /// <summary>What it draws now, as the model counts it (0 when not counted).</summary>
    public double WattsNow { get; init; }
}

/// <summary>A brightness the App read from one monitor.</summary>
public sealed record MonitorBrightness
{
    public string Instance { get; init; } = "";

    public double Brightness { get; init; }
}

/// <summary>
/// One monitor's identity across the two ways Windows names it. WMI's <c>InstanceName</c> is
/// <c>DISPLAY\DELA0B1\5&amp;2f5a1b&amp;0&amp;UID4353_0</c>; a device interface path is
/// <c>\\?\DISPLAY#DELA0B1#5&amp;2f5a1b&amp;0&amp;UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}</c>. Both become
/// <c>DISPLAY\DELA0B1\5&amp;2F5A1B&amp;0&amp;UID4353</c>.
/// </summary>
public static class MonitorKeys
{
    public static string FromInstanceName(string instanceName)
    {
        var text = instanceName.Trim();
        var underscore = text.LastIndexOf('_');
        if (underscore > 0 && text[(underscore + 1)..].All(char.IsAsciiDigit)) text = text[..underscore];
        return text.ToUpperInvariant();
    }

    public static string? FromDevicePath(string devicePath)
    {
        var text = devicePath.Trim();
        if (text.StartsWith(@"\\?\", StringComparison.Ordinal)) text = text[4..];
        var brace = text.IndexOf("#{", StringComparison.Ordinal);
        if (brace >= 0) text = text[..brace];
        var parts = text.Split('#');
        return parts.Length == 3 && parts[0].Equals("DISPLAY", StringComparison.OrdinalIgnoreCase)
            ? string.Join('\\', parts).ToUpperInvariant()
            : null;
    }
}
```

Changes:
- `MachineProfile`: add `public IReadOnlyList<MonitorChoice> Monitors { get; init; } = [];` with a doc comment saying the
  old `ExternalMonitors`, `IncludeMonitors` and `MonitorWatts` stay only so old settings can be migrated (Task 10) and no
  longer reach the model.
- `ServiceStatus`: add `IReadOnlyList<MonitorStatus>? Monitors` as a new last parameter with default `null` (check the
  record's shape; keep the pipe round trip working — a missing property must read as null/empty).
- `ServiceSettings.ValidateProfile`: at most 16 monitor choices; keys 1–200 characters; typed watts finite and 0–500
  ("A monitor must draw between 0 and 500 W."); duplicate keys refused ("Each monitor can be listed once.").
- Pipe: a new request `ReportBrightnessRequest(long Id, IReadOnlyList<MonitorBrightness> Monitors)` with kind
  `"reportBrightness"`, answered with the existing `OkReply` (or whatever the codebase uses for a plain acknowledgement —
  follow `SetSettingsRequest`'s pattern). Register the new types in the source-generated `PipeJson` context. Validation:
  at most 16 entries, brightness finite 0–1, instance 1–260 characters.

Tests: `MonitorKeys` — the two forms in the summary become the same key; a path that isn't a display gives null; the
`_0` suffix is dropped only when digits follow. Settings validation for the new rules. Pipe round trip for a status with
monitors and for a `reportBrightness` request, and a status without `monitors` reads as null. Stored settings missing
`monitors` load with an empty list.

- [ ] Write the tests, see them fail, implement, see them pass, commit:
  `git commit -m "Name monitors the same way on both sides of the pipe, and carry their choices, status and brightness"`

### Task 2: The monitors' draw in the power model (Agent A)

**Files:** Create `src/PowerLedger.Core/MonitorPower.cs`; modify `DisplayModel.cs`, `PowerModel.cs`; tests
`tests/PowerLedger.Core.Tests/MonitorPowerTests.cs`, `DisplayModelTests.cs`, `PowerModelTests.cs`.

```csharp
namespace PowerLedger.Core;

/// <summary>What the external monitors draw right now; the service's board implements it (Plan J).</summary>
public interface IMonitorDraw
{
    double Watts(bool displayOn);
}

/// <summary>No external monitors: the model's default, and what tests use unless they say otherwise.</summary>
public sealed class NoMonitors : IMonitorDraw
{
    public static NoMonitors Instance { get; } = new();

    public double Watts(bool displayOn) => 0;
}

/// <summary>
/// A monitor's draw at a brightness (spec §5). Power is close to linear in screen luminance, and a monitor's fixed
/// electronics are 28–51% of its full-brightness draw (measured, TechPowerUp 2021–2026), so the draw at brightness b is
/// <c>P_full × (0.45 + 0.55 b)</c>. Energy Star's on-mode figure is taken as the draw at 75%, and a monitor whose
/// brightness is unknown is assumed to sit there too.
/// </summary>
public static class MonitorPower
{
    public const double FixedShare = 0.45;
    public const double ListedBrightness = 0.75;
    public const double DefaultSleepW = 0.2;

    public static double At(double listedOnW, double? brightness)
    {
        var b = brightness is { } value && double.IsFinite(value) ? Math.Clamp(value, 0, 1) : ListedBrightness;
        return listedOnW * Share(b) / Share(ListedBrightness);
    }

    private static double Share(double b) => FixedShare + (1 - FixedShare) * b;
}
```

- `DisplayModel.MonitorWatts` and `MonitorSleepW` go; `PowerModel` takes an `IMonitorDraw` (constructor parameter
  defaulting to `NoMonitors.Instance`, following how the learner is passed) and uses `monitors.Watts(sample.DisplayOn)`
  where it used `DisplayModel.MonitorWatts`. Everything about where monitors are added (after the supply division, on top
  of a measured rate) stays as it is.
- Tests: `MonitorPower.At(28.3, null)` is 28.3; at full brightness it is 28.3 × 1.0 / 0.8625 ≈ 32.81; at zero ≈ 14.77;
  NaN counts as unknown; the draw's `Watts(false)` path. Rewrite the old monitor tests in `DisplayModelTests` and
  `PowerModelTests` to use a fake `IMonitorDraw` returning a fixed figure, keeping what they asserted about where the
  watts are added.

- [ ] TDD as above; commit: `git commit -m "Let the model ask what the monitors draw, and scale a monitor's figure by its brightness"`

### Task 3: Wave-1 wiring of the model (Agent A)

`ModelFactory.Build(settings, facts, learner)` (`src/PowerLedger.Service/ModelFactory.cs`) gains an `IMonitorDraw`
parameter, passed as `NoMonitors.Instance` at every existing call site for now (Task 9 replaces it). Build, run every
test project's unit filter, commit: `git commit -m "Pass the monitors' draw through the model factory"`.

### Task 4: The catalogue (Agent B)

**Files:** Create `src/PowerLedger.Sensors/Monitors/MonitorCatalogue.cs`,
`src/PowerLedger.Sensors/Monitors/MonitorMakers.cs`; embed `assets/monitors/energy-star-monitors.csv` in
`PowerLedger.Sensors.csproj` as an `EmbeddedResource` (linked, logical name `PowerLedger.Sensors.Monitors.energy-star-monitors.csv`);
tests `tests/PowerLedger.Sensors.Tests/MonitorCatalogueTests.cs`.

The CSV (from the data agent) has columns `brand,model_number,model_name,alternatives,inches,width,height,panel,on_w,
sleep_w,off_w,max_nits,hdr,certified`, RFC 4180 quoting, `alternatives` joined with `|`.

```csharp
namespace PowerLedger.Sensors;

/// <summary>A certified monitor from the shipped Energy Star table.</summary>
public sealed record CatalogueMonitor(string Brand, string ModelNumber, string ModelName, double Inches, int Width, int Height,
    string Panel, double OnW, double SleepW);

/// <summary>
/// Energy Star's certified monitors (spec §5), shipped with PowerLedger: looks a detected monitor up by maker and model
/// name. Both sides are normalised the same way — upper case, letters and digits only, the maker's name dropped from the
/// front — and matched whole, never by substring. A match must also agree with the monitor's physical size to within an
/// inch, which is what stops vague names ("LG HDR 4K") matching the wrong panel. When several rows still agree, a row
/// whose resolution matches wins, then the one closest in size, then the first in the table's order.
/// </summary>
public sealed class MonitorCatalogue
{
    public static MonitorCatalogue Shipped { get; }     // lazily loaded from the embedded resource

    public IReadOnlyList<CatalogueMonitor> Monitors { get; }

    public static MonitorCatalogue Parse(TextReader csv);

    /// <summary>The certified monitor this one is, or null.</summary>
    public CatalogueMonitor? Find(string maker, string name, double inches, int width, int height);

    internal static string Normalise(string text, string? brand);
}
```

Rules to implement and test:
- **Keys.** Index each row under its normalised `model_number`, `model_name` and each `alternatives` entry. Strip trailing
  wildcard placeholders the data uses (`*`, `?`, `#`, and a trailing run of `x`/`X` characters) before normalising; keep a
  key only if it has a letter and a digit and is at least 4 characters. A model number that ends in one extra lower-case
  revision letter compared with the model name (Dell `U2723QEt` vs `U2723QE`) is indexed without it too.
- **Maker.** `MonitorMakers.Brand(string pnpId)` maps EDID PNP maker codes to the brand the table uses: at least
  DEL→DELL, GSM→LG, SAM/SEC→SAMSUNG, ACR→ACER, AUS→ASUS, BNQ→BENQ, AOC→AOC, HWP/HPN→HP, LEN→LENOVO, PHL→PHILIPS,
  VSC→VIEWSONIC, MSI→MSI, GBT→GIGABYTE, EIZ/ENC→EIZO, NEC→NEC, SNY→SONY, IVM→IIYAMA, HKC→HKC, SPT→SCEPTRE. Add any
  other maker the CSV has more than ten monitors from whose PNP code you can confirm from a primary source. Unknown codes
  return null, and matching then ignores the brand. Check each brand string against the CSV's `brand` column and use its
  exact spelling.
- **Name.** Strip the brand's name from the start of the EDID name ("DELL U2723QE" → "U2723QE", "HP 322pb" → "322PB"),
  and the words "MONITOR"/"DISPLAY".
- **Size gate.** Only rows whose inches are within 1.0 of the detected size (sizes of 0 skip the gate, but then only an
  unambiguous single row may match).
- Tests with rows present in the real CSV (look them up first): Dell `U2723QE` matches the row whose model number is
  `U2723QEt`; Acer `B196L` matches `B196L_q`-style entries if present; HP's "HP 322pb Monitor"; BenQ `GW2480` against
  `GW2480-B`; "LG HDR 4K" with no size matches nothing; a real model at the wrong size (e.g. a 27" name reported as 24")
  matches nothing; `Normalise` cases; the shipped table loads and has more than 1,000 rows.

- [ ] TDD; commit: `git commit -m "Look a detected monitor up in Energy Star's certified list by maker, model and size"`

### Task 5: The estimate (Agent B)

**Files:** Create `src/PowerLedger.Sensors/Monitors/MonitorEstimate.cs`; tests `MonitorEstimateTests.cs`.

```csharp
/// <summary>
/// A monitor's on-mode watts from its size and resolution when the list doesn't know it (spec §5): the median of the
/// certified monitors of the same size class and resolution class when there are at least three, otherwise Energy Star
/// Displays v8's allowance formula scaled to typical draw. Measured against the list itself this lands within ±9% for half
/// of monitors.
/// </summary>
public static class MonitorEstimate
{
    public static (double OnW, double SleepW) For(double inches, int width, int height, MonitorCatalogue catalogue);
}
```

- **Classes.** Size class: round to the nearest of 19, 22, 24, 27, 32, 34, 38, 43, 49 (by absolute difference). Resolution
  class: megapixels bucketed at ≤1.3 (720p–1080p-ish), ≤2.4 (1080p/1200p), ≤4.2 (1440p/ultrawide 1080), ≤5.1 (UW 1440p),
  ≤9 (4K), else higher. The median `on_w` of rows in the same size and resolution class when there are ≥3; sleep is the
  class median `sleep_w`, else `MonitorPower.DefaultSleepW`.
- **Formula fallback.** `A` = screen area in square inches from diagonal and aspect (width/height; default 16:9),
  `MP` = megapixels. For `A < 190`: `E = 4.00·MP + 0.172·A + 1.50`; otherwise implement the v8 formula's area bands (look
  up Energy Star Displays Version 8.0 §3.3's `E_TEC_MAX` for the other bands and cite the section in a comment). On-mode
  watts ≈ `0.296 × E − 0.5`, never below 3 W.
- **Zero size or resolution.** Fall back to the median of all monitors (≈14.3 W).
- Tests: leave-one-out over the real table (for each row, estimate from the table without that row) — median absolute
  error ≤ 12% and 90th percentile ≤ 35% (the research measured 9% and 25%; the margins keep the test stable); a 24"
  1920×1080 lands 10–16 W; a 27" 3840×2160 lands 18–30 W; a 49" super-ultrawide uses the formula when the class is thin.

- [ ] TDD; commit: `git commit -m "Estimate a monitor the list doesn't know from its size and resolution"`

### Task 6: The monitors Windows reports (Agent C)

**Files:** Create `src/PowerLedger.Sensors/Monitors/MonitorInventory.cs`; modify `HardwareInventory.cs`
(`BuiltInDiagonal` snapping); tests `tests/PowerLedger.Sensors.Tests/MonitorInventoryTests.cs`,
`HardwareInventoryTests.cs`, and a `Category=Hardware` test in `RealHardwareTests.cs`.

```csharp
/// <summary>An external monitor as WMI describes it (spec §4).</summary>
public sealed record MonitorFacts(
    string Instance,      // MonitorKeys.FromInstanceName of WMI's InstanceName
    string Key,           // maker + product code + serial when the serial is meaningful, else Instance
    string Maker,         // PNP id, e.g. "DEL"
    string ProductCode,   // e.g. "A0B1"
    string Name,          // UserFriendlyName, may be ""
    double Inches,        // from MaxHorizontal/VerticalImageSize, snapped (see below)
    int Width, int Height // the preferred (native) mode
);

public static class MonitorInventory
{
    /// <summary>The active external monitors, read from WMI. Never throws; a failed class reads as nothing.</summary>
    public static IReadOnlyList<MonitorFacts> Read();

    /// <summary>The same, from rows already read — the part tests drive.</summary>
    internal static IReadOnlyList<MonitorFacts> From(
        IReadOnlyList<(string Instance, ushort[] Maker, ushort[] Product, ushort[] Serial, ushort[] Name)> ids,
        IReadOnlyDictionary<string, uint> connections,
        IReadOnlyList<(string Instance, bool Active, double WidthCm, double HeightCm)> sizes,
        IReadOnlyDictionary<string, (int Width, int Height)> nativeModes);

    /// <summary>A diagonal from EDID's whole centimetres, snapped to the nearest common panel size within 0.5".</summary>
    public static double Diagonal(double widthCm, double heightCm);
}
```

- WMI classes, all in `root\wmi`, all through `Wmi.ReadOr` as `HardwareInventory` does: `WmiMonitorID` (ushort arrays,
  zero-terminated), `WmiMonitorConnectionParams`, `WmiMonitorBasicDisplayParams` (Active, sizes) and
  `WmiMonitorListedSupportedSourceModes` (`MonitorSourceModes[]` with `HorizontalActivePixels`,
  `VerticalActivePixels`, and `PreferredMonitorSourceModeIndex`). Instances join on `MonitorKeys.FromInstanceName`.
- Keep only active monitors whose connection isn't in `BuiltInConnections` (reuse the existing array; make it `internal`
  if needed).
- Serial is "meaningful" when non-empty and not all zeros; key = `$"{Maker}{ProductCode}-{Serial}"`.
- **Snapping.** Common diagonals: 11.6, 12.5, 13.3, 13.5, 14, 15.6, 16, 17.3, 18.5, 19, 19.5, 21.5, 22, 23, 23.8, 24, 24.5,
  25, 27, 28, 29, 31.5, 32, 34, 35, 38, 40, 42, 43, 45, 48, 49. Use it in `BuiltInDiagonal` too, and update the existing
  test that expects 15.3 to expect 15.6 (a 34 × 19 cm panel).
- Tests: two external monitors and a built-in panel, only the externals come back; an inactive monitor is left out; the
  name and codes decode from ushort arrays with zero padding; a monitor without a native mode reads width/height 0; the
  key with and without a serial; `Diagonal(60, 34)` is 27; `Diagonal(34, 19)` is 15.6; a size far from any common one
  stays unsnapped (rounded to 0.1). Hardware test: `Read()` on this machine doesn't throw and returns only externals.

- [ ] TDD; commit: `git commit -m "Read each external monitor's maker, model, size and native resolution from WMI"`

### Task 7: Reading a monitor's brightness, carefully (Agent D)

**Files:** Create `src/PowerLedger.App/Monitors/DdcBrightness.cs`; tests
`tests/PowerLedger.App.Tests/DdcBrightnessTests.cs`.

```csharp
/// <summary>A brightness read from one monitor, with the device path Windows names it by.</summary>
internal sealed record DdcReading(string DevicePath, double Brightness);

internal interface IBrightnessReader
{
    /// <summary>Every monitor that answered. Slow (about 40 ms a monitor): call it off the UI thread.</summary>
    IReadOnlyList<DdcReading> Read();
}

/// <summary>
/// DDC/CI brightness (spec §5), read-only and careful, because Microsoft warns many monitors implement the commands badly:
/// each physical monitor is asked for its capabilities once, and read only if it reports brightness support; a monitor
/// that fails any call is not asked again while the App runs; nothing is ever written.
/// </summary>
internal sealed class DdcBrightness : IBrightnessReader
```

- Win32: `EnumDisplayMonitors` → for each `HMONITOR`, `GetMonitorInfoW` (MONITORINFOEX `szDevice`, e.g. `\\.\DISPLAY1`)
  → `EnumDisplayDevicesW(szDevice, i, ref DISPLAY_DEVICE, EDD_GET_DEVICE_INTERFACE_NAME = 1)` for i = 0.. gives each
  attached monitor's `DeviceID` (the device interface path) → `GetNumberOfPhysicalMonitorsFromHMONITOR` and
  `GetPhysicalMonitorsFromHMONITOR` (Dxva2) → for each physical monitor, in the same order as the display devices:
  `GetMonitorCapabilities` (skip unless `MC_CAPS_BRIGHTNESS = 0x2`) → `GetMonitorBrightness(min, current, max)` → brightness
  `(current − min) / (max − min)` when `max > min` → `DestroyPhysicalMonitors`.
- Keep the P/Invoke in one private nested static class, `SetLastError = true`, `CharSet.Unicode` where strings are
  involved, and free physical monitor handles in `finally`.
- Remember failures by device path in a set; a monitor in it is skipped. Skip device paths whose `MonitorKeys` shape isn't
  a display (the App can't reference Contracts' helper in this wave — check the prefix `\\?\DISPLAY#` locally).
- Pure parts to test: the normalisation `(current − min)/(max − min)` with edge cases (max == min → no reading; current
  outside → clamped); the failure memory (a fake native layer behind an internal interface: capabilities without
  brightness → not read; a throw → remembered and skipped next time). A `Category=Hardware` test calls `Read()` and only
  asserts it doesn't throw and every brightness is 0–1 (this laptop likely has no DDC-capable monitor).

- [ ] TDD; commit: `git commit -m "Read external monitors' brightness over DDC/CI, read-only, asking each once"`

---

## Wave 2

### Task 8: The monitor board (Agent E)

**Files:** Create `src/PowerLedger.Service/MonitorBoard.cs`; tests `tests/PowerLedger.Service.Tests/MonitorBoardTests.cs`.

```csharp
/// <summary>
/// The external monitors the service knows (Plan J): what WMI detected, each one's figure from the catalogue, the estimate
/// or the user, the user's choices, and the brightness the App last reported. The model reads it every tick and the status
/// every publish, from different threads, so every member takes the lock.
/// </summary>
internal sealed class MonitorBoard : IMonitorDraw
{
    public static readonly TimeSpan BrightnessStale = TimeSpan.FromMinutes(15);

    public MonitorBoard(MonitorCatalogue catalogue, TimeProvider clock);

    /// <summary>What WMI found; recomputes figures only for monitors that changed.</summary>
    public void Detected(IReadOnlyList<MonitorFacts> monitors);

    /// <summary>The user's choices from the settings.</summary>
    public void Choose(IReadOnlyList<MonitorChoice> choices);

    /// <summary>The App's report; brightness older than <see cref="BrightnessStale"/> counts as unknown.</summary>
    public void Report(IReadOnlyList<MonitorBrightness> readings);

    public double Watts(bool displayOn);          // Σ counted: on → MonitorPower.At(OnW, brightness), off → SleepW

    public IReadOnlyList<MonitorStatus> Status(bool displayOn);
}
```

- Figure: typed watts win (`Source = Typed`, sleep = catalogue/estimate sleep); else a catalogue hit (`Model`); else the
  estimate (`Estimate`). Name: `UserFriendlyName`, or `"{Maker} {ProductCode}"` when empty.
- A monitor with no choice counts. Reported brightness matches by `Instance`.
- Tests with a small parsed catalogue: counted vs not; typed wins; stale brightness; display off uses sleep watts; a
  monitor unplugged disappears; `Status` fields.

- [ ] TDD; commit: `git commit -m "Keep the service's monitors, their figures and the brightness the App reports on one board"`

### Task 9: Detection, reports and the model (Agent E)

- `DisplaySource`: every minute, on the sensor thread, besides its current query, call `MonitorInventory.Read()` and hand
  the list to a callback (`Action<IReadOnlyList<MonitorFacts>>?` constructor parameter, invoked only when the list differs
  from the previous one by instance, size, resolution or name). Keep the existing test seam working.
- `ServiceHost`/`MachineSensors`: create one `MonitorBoard` (with `MonitorCatalogue.Shipped` and `TimeProvider.System`) and
  pass `board.Detected` as that callback; `ModelFactory.Build` gets the board as its `IMonitorDraw`; `SamplingLoop.Apply`
  and start call `board.Choose(settings.Profile.Monitors)`.
- `PipeHandler`: `reportBrightness` validates and calls `board.Report`, replying with the plain acknowledgement.
- `SamplingLoop.Publish`: `ServiceStatus.Monitors = board.Status(displayOn)`.
- Tests: the loop harness with a board and fake facts publishes monitors in the status and adds their watts to the
  reading; a `reportBrightness` over the pipe changes a monitor's `WattsNow`; bad reports are refused with the validation
  message.

- [ ] TDD; commit: `git commit -m "Detect monitors every minute, take the App's brightness, and let the model count them"`

### Task 10: Old settings (Agent E)

In `ProfilePolicy` (or where stored settings are first applied), once monitors are detected: when the stored profile has
no monitor choices but has `ExternalMonitors > 0` or `IncludeMonitors`, write a choice for each detected external monitor
— `Counted = IncludeMonitors`, `Watts = MonitorWatts` when it isn't the old default of 25 — set `ExternalMonitors = 0` and
`IncludeMonitors = false`, and save. A profile with neither leaves new monitors counted by default. Tests for both, and for
a migration that happens only once.

- [ ] TDD; commit: `git commit -m "Carry an old monitor count and figure over to the monitors now detected"`

### Task 11: Reporting brightness (Agent F)

- `src/PowerLedger.App/Monitors/BrightnessReporter.cs`: a timer (TimeProvider) that, a minute after start and every five
  minutes after, reads `IBrightnessReader` on a background thread, maps each `DevicePath` through
  `MonitorKeys.FromDevicePath`, and sends `reportBrightness` through the service link (add `ReportBrightnessAsync` to
  `IServiceLink`/`PipeServiceLink` following `SetSettingsAsync`). Does nothing while
  `UiPreferences.ReadMonitorBrightness` (new, default true, same `{ get; set; }` note as `CheckForUpdates`) is off.
- Settings → Preferences: a tick box "Read brightness from monitors (read-only)".
- Tests: schedule, mapping, the preference, a link failure is swallowed.

- [ ] TDD; commit: `git commit -m "Report the monitors' brightness to the service every few minutes, if allowed"`

### Task 12: The wizard (Agent F)

- `WizardViewModel`: `Monitors` — rows built from `status.Monitors` (name, "27 in · 3840 × 2160", watts text, source text
  "measured for this model" / "estimated from its size" / "typed", a `Counted` bool, an editable watts string). The
  monitor block is hidden when there are none. Saving the machine step writes `Profile.Monitors` choices (Counted, and
  Watts only when the typed text differs from the figure shown); the old count/watts fields are no longer shown or sent.
- `WizardView.xaml`: replace the "External monitors / Count them, at / W each" row with an `ItemsControl` of rows; each
  row: name and size (text), watts `TextBox` (`AutomationProperties.Name="Watts for {name}"`), a "Count it" check box.
  An estimated monitor shows its source in amber-free muted text: "estimated from its size — correct it if you know
  better". The desktop machine step must still fit 880×560 with two monitors — add two monitors to the rendering test's
  wizard status and keep `A_short_window_scrolls_the_pages_that_do_not_fit_and_cuts_none_off` passing.
- Tests: no monitors → no monitor rows and nothing about monitors saved; two monitors → rows and choices saved; an unknown
  one says so; a typed figure is saved as `Watts`.

- [ ] TDD; commit: `git commit -m "Show detected monitors by name in the wizard, and ask only about the ones it had to estimate"`

### Task 13: Settings (Agent F)

- `ServiceForm` / `SettingsViewModel`: the same monitor rows as the wizard, plus brightness state ("brightness 60%, read
  from the monitor" / "brightness unknown, assumed 75%") and `WattsNow`. Rows refresh with the status (every 10 s while
  shown) without discarding a figure being typed.
- `SettingsView.xaml`: replace the monitor row; keep "Built-in panel, inches".
- `NowViewModel.DisplayDetail` and the Breakdown footnote: say "plus N monitors" using the status.
- Tests as for the wizard, plus the refresh keeping typed text.

- [ ] TDD; commit: `git commit -m "List each monitor in Settings with its figure, its source and its brightness"`

---

## Wave 3 (lead)

- Merge in order: data → catalogue; contracts; inventory; ddc; then service and app (from the merged tip). Resolve the
  `ModelFactory` call sites.
- Docs: README (how monitors are counted, the Energy Star attribution), the main spec §4/§5/§9/§11/§12, Settings About
  attribution line.
- Full tests (Hardware and UI included), one reviewer subagent (security: the new pipe message, P/Invoke handle lifetime,
  thread safety of the board; accuracy: the estimate and brightness maths against the design), installers, Sandbox
  (no external monitors there: the wizard must show no monitor block and the service no monitors), CI, release 0.4.0.
- The owner's check: on the laptop alone, the wizard (Settings → Run setup again) asks nothing about monitors; with an
  external monitor plugged in, it appears by name within a minute.

## Results

2026-09-16–17. Built by waves of parallel agents, each in a worktree of its own, as the Execution table sets out: the
data agent first, then agents A to D at once, then E and F from the merged first wave. The lead merged each branch onto
`plan-j/monitors` as it finished. The fixes went the same way: agents of their own for the decisions at merge, then five
at once for the first review's findings and three at once for the second's. The branch holds 72 commits at `8704cd6`.

**Verified**

- `dotnet build -c Release`: 0 warnings. Every test outside `Hardware` and `Installed`, run at `8704cd6`: 1,334 pass
  (Core 134, Storage 49, Sensors 412, Service 204, App 535 with the UI renders).
- The estimate, with each of the table's 1,576 monitors up to 57" estimated from all the others: within 9.04% of what it
  measured for half of them and within 25.6% for nine in ten; `MonitorEstimateTests` holds 12% and 35%.
- The catalogue before and after the first review's fixes, run over the 33,385 named monitors with a size in linuxhw's
  DigitalDisplay collection of real EDIDs: model matches went from 2,501 to 2,301, and matches whose resolution
  disagrees with their listing's from 190 to 55, all of them exact names or series words. 213 monitors went to the
  estimate, and 13 found a listing they hadn't. `MonitorCatalogue.cs` and `MonitorMakers.cs` note the limits left as
  they are.
- A flaky test fixed (`2aa4dd6`): `SensorWorkerTests.A_hung_read_abandons_the_set_and_the_next_read_starts_a_fresh_one`
  threw a `NullReferenceException` at its poll, which read the list of sets built without the lock `Add` takes, and
  `List<T>.Add` sets the count before it stores the item. Run back to back for 60 seconds while the App tests ran, it
  failed 1,057 times in 68,107 runs; with every read of the list copied under the lock, the same loop under the same
  load failed in none of 55,382.
- WMI on the development laptop refuses `WmiMonitorAnalogVideoInputParams`, a monitor class it has no instances of, with
  "Not supported", and a class that doesn't exist with "Invalid class": the two refusals now read as no monitors. The
  refusal with every monitor unplugged couldn't be seen there, since the laptop's own panel is always attached.
- Installers at full compression: universal **96.0 MB**, x64 **56.8 MB**, Arm64 **50.2 MB**.
- **Windows Sandbox, end to end, 58 of 58**, run before and again after the second review's fixes. 0.3.0 installed as a
  desktop with its old default of one uncounted monitor; a stand-in feed offered 0.4.0, the App took the x64 installer,
  and "Restart to update" brought the service and the App back as 0.4.0 with "Updated to 0.4.0". Sandbox has no
  external monitor, so the old settings stayed waiting for one; Settings showed "External monitors: none detected"; the
  service took an empty brightness report, logged no error, and the App still answered six minutes on. A silent
  uninstall then removed the service and both data folders, and a fresh 0.4.0 install's wizard never asked about
  monitors and saved no default monitor choices.
<!-- lead: CI run and both jobs' results, and the release link -->

**First review** (whole branch): 8 findings, none critical, 5 important and 3 minor, all fixed.

- **Important.** An abandoned sensor set could hand the board a stale list after its replacement had handed over the
  monitors attached, leaving them uncounted, and a WMI class that failed read as no monitors: each set's token is now
  cancelled before its replacement is built and checked under the board's lock, a read that doesn't say which monitors
  are attached leaves the list alone, and a monitor keeps the resolution last read for it.
- **Important.** A USB-C portable monitor on a laptop was counted twice: a monitor now has a plug of its own or runs off
  the PC, a laptop's monitor of 17.3" or less is taken to run off it, and one that does always counts, isn't added to a
  measured rate and is taken out of what the learner learns; the wizard and Settings ask on a laptop whether each
  monitor has its own plug.
- **Important.** A family or a name cut short could match a model of another resolution, since resolution only broke
  ties: those matches now need a listing at the monitor's resolution, while exact names and series words keep theirs.
- **Important.** Old settings that didn't count monitors lost that for a monitor attached later: the carry-over now sets
  `CountMonitorsByDefault` from them, and writes choices only for a typed figure other than the old 25 W.
- **Important.** The quality badge said nothing of the estimated monitor figures in a reading: the badge still describes
  the PC's own reading, and the Now screen's display row and live note, Breakdown's footnote and the report's quality
  legend say how the monitors were figured.
- **Minor.** Maker codes missing from the table turned off the brand check: `ACI` and `AOP` (ASUS's and AOpen's), `APP`,
  `TSB` and `HEC` were added, and a maker without a brand matches exact names only.
- **Minor.** Twin monitors that share a serial number changed key when one was unplugged: a serial number under four
  characters, or of one character repeated, keys a monitor by its instance, and monitors whose serial key was found
  shared stay keyed by instance while the service runs.
- **Minor.** A monitor that failed a brightness read was asked again after an hour: it is left alone until a display
  change or a resume, a rule the second review kept only for a monitor that hasn't given a brightness.

**Second review** (whole branch): 5 findings, 2 important, 1 between minor and important, and 2 minor, all fixed.

- **Important.** A desktop's last monitor went on counting once unplugged: with no display left, WMI refuses the monitor
  classes with "Not supported" rather than list none, and that read as WMI failing to answer, so the board kept the
  monitor. `Wmi.MeansNoInstances` now takes "Not supported" and "Invalid class" as no instances, so the display query
  still answers and the inventory finds no monitors, and the board empties at the next display query, within a minute;
  any other refusal still keeps the list.
- **Important.** One failed read left a monitor at the assumed 75% brightness until a display change or a resume, and a
  monitor switched off at its own button raises neither: on a desktop left awake with its monitor off for the evening,
  the figure ran about 40% over for a monitor really at 30%. A monitor that has given a brightness is now asked again at
  the next read after a failure, the wait doubles with each failure in a row up to an hour, and any reply ends the run;
  one that never has keeps the old rule, and a reset keeps which monitors have answered.
- **Between minor and important.** Settings or the wizard, loaded before the service carried the old monitor settings
  over, saved them back whole: a monitor the service had left out counted from then on, and a figure typed before was
  dropped. A save now reads the service's settings again when those loaded are behind, and takes from them what the form
  doesn't show, keeping what the user typed and ticked; a save that changes the chassis says each listed monitor's plug,
  and a plug the settings already say is kept while the row shows it unchanged.
- **Minor.** A display change or a resume made the App's UI thread wait for a brightness read under way, 1.7 s in the
  review's measurement, since SystemEvents raises them on the UI thread, where the reader subscribed: each now only
  marks a reset, which the read carries out before the next monitor.
- **Minor.** On a desktop, ticking "has its own plug" took the box from under the pointer, so it couldn't be unticked
  until the page was read again: `MonitorRow.ShowsPlug` keeps a box on screen once the user has ticked or unticked it.

**Decided at merge** (the design's section of that name): a figure the user typed is used as typed, never scaled by
brightness; a monitor's choice is saved only when it differs from the service's defaults or holds a typed figure, so the
choices for monitors unplugged no longer pile up until the limit of 16 refuses every save, and the ones kept run from
the most recently seen; saving a form that lists monitors clears the old count and the choice to count monitors. The
first review's fixes added the plug, the default carried over, the resolution a family needs, the list a stale read
can't wipe and the badge left to the PC's own reading; the second's, the classes with no instances read as no monitors,
how long a monitor that failed is left alone, and saves that never undo the carry-over.

**Settled along the way**: a monitor running off the PC always counts, whatever its choice says, since what it draws is
inside what the PC draws, so its Count it box stays ticked and the learner always takes its figure out (`f9a8757`,
`7e8ce08`); and a match by a series word keeps its listing at any resolution, as an exact name's does, since the
series-word matches that disagreed on resolution were the listed model giving another mode (`5de1afe`).

**Deviations from the plan's code**: the model asks the monitors for `MonitorWatts`, split between those with a plug of
their own and those running off the PC, not one figure, and `MonitorBoard.Choose` takes the whole profile, for its
default for counting and its chassis; `MonitorChoice` gains `OwnPlug`, `MonitorStatus` the plug and both defaults, and
`MachineProfile` `CountMonitorsByDefault`, which the carry-over (Task 10) sets instead of writing a choice for each
monitor; `MonitorInventory.Read` gives null when WMI doesn't say which monitors are attached and none when WMI refuses
the classes as having no instances, where Task 6 read any failed class as nothing, and it keeps the serial keys found
shared and the resolutions read; the sensor worker builds each set with a cancellation token; the estimate has a 16-inch
size class for portable monitors; a maker code without a brand matches exact names only, where Task 4 let it match any
brand, and a family or a name cut short needs the monitor's resolution; the DDC/CI reader, first built to try a failed
monitor again after an hour, leaves one that hasn't given a brightness alone as Task 7 asked, until a display change or
a resume, asks one that has again after a wait that doubles up to an hour, measured with a `TimeProvider`, and on either
event only marks a reset; `ServiceForm.SaveAsync` reads the service's settings again when those loaded are behind, and
`MonitorRow` gains `Take` and `ShowsPlug` for it and the desktop's plug box; and one constant,
`ServiceSettings.MaxMonitors`, bounds both the choices and a brightness report.

**Not covered**: no real external monitor was tried end to end, because the Sandbox runs had none and the development
laptop's only screen is its built-in panel, so DDC/CI hasn't met a real monitor either. The Arm64 build hasn't been
tried on a real Arm PC. A monitor switched off at its own button while the PC stays awake may still count as on, if
Windows keeps listing it.
