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

(Filled in after wave 3.)
