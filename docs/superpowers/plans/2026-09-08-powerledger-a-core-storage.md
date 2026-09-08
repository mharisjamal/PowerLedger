# PowerLedger Plan A — Scaffold, Core, Contracts, Storage — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the hardware-free half of PowerLedger: the solution scaffold, the shared contracts, the pure domain library (power model, calibration learner, energy integrator, downsampler, tariff and cost math), and the SQLite storage layer, all fully unit-tested.

**Architecture:** `PowerLedger.Core` converts sensor `Sample`s into `Reading`s with a quality label, integrates energy, and aggregates it. `PowerLedger.Storage` persists readings, aggregates, sessions, tariffs, calibration, and settings in one SQLite file and answers report queries. `PowerLedger.Contracts` holds enums and settings records shared with the later Service and App. Nothing in this plan touches hardware; Plans B (Sensors + Service), C (App), and D (Installer) follow.

**Tech Stack:** .NET 10 SDK, C# 14, xUnit, Shouldly, FsCheck.Xunit, Microsoft.Data.Sqlite. Spec: `docs/superpowers/specs/2026-09-08-powerledger-design.md`.

**Git rule (from the owner):** commit locally after every task. Never add a remote, push, or create a GitHub repo; the owner will supply the destination repo later.

---

## File structure

```
PowerLedger.sln
Directory.Build.props                              shared build settings (nullable, warnings as errors, version)
.editorconfig                                      formatting rules
.gitattributes                                     text=auto; .sln checked out CRLF
global.json                                        pins SDK 10.0.400, rollForward latestFeature
src/PowerLedger.Contracts/
  Quality.cs                                       enum Quality
  SessionReason.cs                                 enum SessionReason
  MachineProfile.cs                                ChassisKind, PsuTier, MachineProfile record + defaults
src/PowerLedger.Core/
  Sample.cs                                        Sample record (raw sensor values for one tick)
  Components.cs                                    Components record (per-part watts)
  Reading.cs                                       Reading record (model output)
  HardwareFacts.cs                                 HardwareFacts record (TDPs from inventory)
  PowerModelOptions.cs                             idle threshold, adapter efficiency
  IBaselineProvider.cs                             learned-baseline lookup interface
  DisplayModel.cs                                  internal panel + external monitor watts
  PsuEfficiency.cs                                 tier → efficiency
  PowerModel.cs                                    Sample → Reading
  CalibrationLearner.cs                            per-bucket EMA baselines, IBaselineProvider
  CalibrationState.cs                              serialisable snapshot of the learner
  EnergySlice.cs                                   energy of one tick
  EnergyIntegrator.cs                              Reading → EnergySlice with Δt/gap rules
  Aggregate.cs                                     minute/hour aggregate record
  Downsampler.cs                                   readings → minute, minutes → hour
  Tariff.cs                                        Tariff record
  TariffSchedule.cs                                tariff-at-time and cost
  Co2.cs                                           kWh → kg
  Comparisons.cs                                   everyday equivalents
src/PowerLedger.Storage/
  SqliteDatabase.cs                                open, pragmas, migrate, connection factory
  Migrator.cs                                      schema_version + ordered migrations
  Schema.cs                                        V1 DDL
  RawSampleRepository.cs                           batched insert, read range, purge
  AggregateRepository.cs                           upsert/read minute and hour rows, purge
  SessionRepository.cs                             open/close/list sessions
  TariffRepository.cs                              add/list tariffs
  SettingsRepository.cs                            key/value settings
  CalibrationRepository.cs                         save/load CalibrationState per inventory hash
  InventoryRepository.cs                           hardware inventory rows
  RetentionJob.cs                                  purge by retention settings
  RangeTotals.cs                                   report totals record
  ReportQueries.cs                                 totals, daily buckets, idle waste
tests/PowerLedger.Core.Tests/                      one test class per Core file
tests/PowerLedger.Storage.Tests/                   one test class per repository + TestDatabase helper
```

---

### Task 0: Install the .NET 10 SDK

The machine has the .NET 10 Desktop Runtime but no SDK (`dotnet --list-sdks` prints nothing). This is a system install, so ask the owner to run it or get explicit approval first.

- [x] **Step 1: Install the SDK**

Run (PowerShell, as the user):
```powershell
winget install Microsoft.DotNet.SDK.10 --silent --accept-package-agreements --accept-source-agreements
```
Expected: `Successfully installed`.

- [x] **Step 2: Verify**

Run: `dotnet --list-sdks`
Expected: one line starting with `10.0.` (open a new shell if the PATH did not refresh).

---

### Task 1: Solution scaffold

**Files:**
- Create: `PowerLedger.sln`, `Directory.Build.props`, `.editorconfig`
- Create: `src/PowerLedger.Contracts/PowerLedger.Contracts.csproj`, `src/PowerLedger.Core/PowerLedger.Core.csproj`, `src/PowerLedger.Storage/PowerLedger.Storage.csproj`
- Create: `tests/PowerLedger.Core.Tests/PowerLedger.Core.Tests.csproj`, `tests/PowerLedger.Storage.Tests/PowerLedger.Storage.Tests.csproj`

- [x] **Step 1: Create solution and projects**

Run from `D:\PowerLedger`:
```bash
dotnet new sln -n PowerLedger --format sln
dotnet new classlib -n PowerLedger.Contracts -o src/PowerLedger.Contracts -f net10.0
dotnet new classlib -n PowerLedger.Core -o src/PowerLedger.Core -f net10.0
dotnet new classlib -n PowerLedger.Storage -o src/PowerLedger.Storage -f net10.0
dotnet new xunit -n PowerLedger.Core.Tests -o tests/PowerLedger.Core.Tests -f net10.0
dotnet new xunit -n PowerLedger.Storage.Tests -o tests/PowerLedger.Storage.Tests -f net10.0
rm src/PowerLedger.Contracts/Class1.cs src/PowerLedger.Core/Class1.cs src/PowerLedger.Storage/Class1.cs
rm tests/PowerLedger.Core.Tests/UnitTest1.cs tests/PowerLedger.Storage.Tests/UnitTest1.cs
dotnet sln add src/PowerLedger.Contracts src/PowerLedger.Core src/PowerLedger.Storage tests/PowerLedger.Core.Tests tests/PowerLedger.Storage.Tests
```

- [x] **Step 2: Wire references and packages**

```bash
dotnet add src/PowerLedger.Core reference src/PowerLedger.Contracts
dotnet add src/PowerLedger.Storage reference src/PowerLedger.Core src/PowerLedger.Contracts
dotnet add src/PowerLedger.Storage package Microsoft.Data.Sqlite
dotnet add tests/PowerLedger.Core.Tests reference src/PowerLedger.Core
dotnet add tests/PowerLedger.Core.Tests package Shouldly
dotnet add tests/PowerLedger.Core.Tests package FsCheck.Xunit
dotnet add tests/PowerLedger.Storage.Tests reference src/PowerLedger.Storage src/PowerLedger.Core
dotnet add tests/PowerLedger.Storage.Tests package Shouldly
```

- [x] **Step 3: Write `Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <WarningsNotAsErrors>NU1901;NU1902;NU1903;NU1904</WarningsNotAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <Version>0.1.0</Version>
    <Authors>PowerLedger</Authors>
    <RootNamespace>$(MSBuildProjectName)</RootNamespace>
  </PropertyGroup>
</Project>
```

- [x] **Step 4: Write `.editorconfig`**

```ini
root = true
[*]
indent_style = space
indent_size = 4
insert_final_newline = true
charset = utf-8
[*.{json,yml,yaml,xml,csproj,props}]
indent_size = 2
[*.sln]
indent_style = tab
[*.{csproj,sln}]
charset = utf-8-bom
[*.cs]
csharp_style_namespace_declarations = file_scoped:warning
dotnet_sort_system_directives_first = true
```


- [x] **Step 4b: Pin the SDK band and the line-ending policy**

Run: `dotnet new globaljson --sdk-version 10.0.400 --roll-forward latestFeature`

Write `.gitattributes`:
```
* text=auto
*.sln text eol=crlf
```
Git owns line endings (index is always LF); `.editorconfig` no longer sets `end_of_line`.

- [x] **Step 5: Build and run the (empty) test suites**

Run: `dotnet build`
Expected: `Build succeeded.` with 0 warnings.

Run: `dotnet test`
Expected: both test projects report `Passed! - Failed: 0, Passed: 0` (or "No test is available" — both are fine at this point).

- [x] **Step 6: Commit**

```bash
git add -A
git commit -m "Scaffold solution with Contracts, Core, Storage and test projects"
```

---

### Task 2: Contracts — enums and MachineProfile

**Files:**
- Create: `src/PowerLedger.Contracts/Quality.cs`
- Create: `src/PowerLedger.Contracts/SessionReason.cs`
- Create: `src/PowerLedger.Contracts/MachineProfile.cs`
- Test: `tests/PowerLedger.Core.Tests/MachineProfileTests.cs`

- [x] **Step 1: Write the failing test**

```csharp
using System.Text.Json;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class MachineProfileTests
{
    [Fact]
    public void DefaultLaptop_is_a_laptop_with_a_panel_and_no_external_monitors()
    {
        var p = MachineProfile.DefaultLaptop;
        p.Chassis.ShouldBe(ChassisKind.Laptop);
        p.DisplayDiagonalInches.ShouldBe(15.6);
        p.IncludeMonitors.ShouldBeFalse();
        p.PsuTier.ShouldBe(PsuTier.Bronze);
    }

    [Fact]
    public void DefaultDesktop_has_no_internal_panel()
    {
        MachineProfile.DefaultDesktop.Chassis.ShouldBe(ChassisKind.Desktop);
        MachineProfile.DefaultDesktop.DisplayDiagonalInches.ShouldBe(0);
    }

    [Fact]
    public void Quality_integer_values_are_pinned_because_they_are_stored_in_sqlite()
    {
        ((int)Quality.Estimated).ShouldBe(0);
        ((int)Quality.Calibrated).ShouldBe(1);
        ((int)Quality.Measured).ShouldBe(2);
    }

    [Fact]
    public void Json_missing_properties_fall_back_to_documented_defaults_not_zero()
    {
        var p = JsonSerializer.Deserialize<MachineProfile>("""{"Chassis":0,"DisplayDiagonalInches":0}""")!;
        p.Chassis.ShouldBe(ChassisKind.Desktop);
        p.PsuTier.ShouldBe(PsuTier.Bronze);
        p.MonitorWatts.ShouldBe(25);
        p.RamSticks.ShouldBe(1);
        p.CpuTdpOverrideW.ShouldBeNull();
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PowerLedger.Core.Tests --filter MachineProfileTests`
Expected: build error `The type or namespace name 'MachineProfile' could not be found`.

- [x] **Step 3: Write the contracts**

`src/PowerLedger.Contracts/Quality.cs`
```csharp
namespace PowerLedger.Contracts;

/// <summary>
/// How trustworthy a total-power reading is. Higher is better.
/// Persisted in SQLite as the integer value: append new members, never renumber.
/// </summary>
public enum Quality
{
    /// <summary>Component sum with default baselines (about ±20 %).</summary>
    Estimated = 0,
    /// <summary>Component sum with a baseline learned from battery data (about ±10 %).</summary>
    Calibrated = 1,
    /// <summary>Whole-system battery discharge rate (about ±3 %).</summary>
    Measured = 2,
}
```

`src/PowerLedger.Contracts/SessionReason.cs`
```csharp
namespace PowerLedger.Contracts;

/// <summary>
/// Why a power session started or ended. Persisted in SQLite by name (Enum.Parse):
/// append new members, never rename existing ones.
/// </summary>
public enum SessionReason
{
    /// <summary>Started: the machine booted and the service came up with it.</summary>
    Boot = 0,
    /// <summary>Started: the service was (re)started on a machine that was already running.</summary>
    ServiceStart = 1,
    /// <summary>Started: the machine woke from sleep or hibernation.</summary>
    Resume = 2,
    /// <summary>Ended: the machine is going to sleep or hibernation.</summary>
    Suspend = 3,
    /// <summary>Ended: the machine is shutting down or restarting.</summary>
    Shutdown = 4,
    /// <summary>Ended: the service was stopped while the machine kept running.</summary>
    ServiceStop = 5,
    /// <summary>The previous session never wrote an end (crash or power loss): it is closed with this reason, and the new session starts with it too.</summary>
    CrashRecovered = 6,
}
```

`src/PowerLedger.Contracts/MachineProfile.cs`
```csharp
namespace PowerLedger.Contracts;

/// <summary>Persisted and piped as an integer: append new members, never renumber.</summary>
public enum ChassisKind
{
    Desktop = 0,
    Laptop = 1,
}

/// <summary>80 PLUS tier of a desktop power supply. Persisted as an integer: append new members, never renumber.</summary>
public enum PsuTier
{
    White = 0,
    Bronze = 1,
    Silver = 2,
    Gold = 3,
    Platinum = 4,
    Titanium = 5,
}

/// <summary>
/// What the machine is made of, as detected at install time and corrected by the user.
/// Init-only properties with initialisers: a JSON blob that lacks a property (written by an older version)
/// deserialises to the documented default instead of zero, and adding a property later is not a breaking change.
/// </summary>
public sealed record MachineProfile
{
    public ChassisKind Chassis { get; init; } = ChassisKind.Laptop;
    public int RamSticks { get; init; } = 1;
    public bool RamIsDdr5 { get; init; }
    public int SsdCount { get; init; } = 1;
    public int HddCount { get; init; }
    public int FanCount { get; init; } = 1;
    /// <summary>Only used for desktops; laptops use adapter efficiency instead.</summary>
    public PsuTier PsuTier { get; init; } = PsuTier.Bronze;
    /// <summary>User-declared extras (RGB, pumps, USB devices) in watts.</summary>
    public double ExtrasWatts { get; init; }
    public int ExternalMonitors { get; init; }
    /// <summary>External monitors are self-powered; they count only when the user opts in.</summary>
    public bool IncludeMonitors { get; init; }
    /// <summary>Watts per external monitor while the display is on.</summary>
    public double MonitorWatts { get; init; } = 25;
    /// <summary>Internal panel diagonal. 0 means unknown or no internal panel (desktops).</summary>
    public double DisplayDiagonalInches { get; init; } = 15.6;
    public double? CpuTdpOverrideW { get; init; }
    public double? GpuTdpOverrideW { get; init; }

    public static MachineProfile DefaultLaptop { get; } = new();

    public static MachineProfile DefaultDesktop { get; } = new()
    {
        Chassis = ChassisKind.Desktop,
        RamSticks = 2,
        FanCount = 3,
        ExternalMonitors = 1,
        DisplayDiagonalInches = 0,
    };
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PowerLedger.Core.Tests --filter MachineProfileTests`
Expected: `Passed! - Failed: 0, Passed: 4`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Contracts tests/PowerLedger.Core.Tests/MachineProfileTests.cs
git commit -m "Add Contracts: Quality, SessionReason, MachineProfile"
```

---

### Task 3: Core records — Sample, Components, Reading, HardwareFacts, options

**Files:**
- Create: `src/PowerLedger.Core/Sample.cs`
- Create: `src/PowerLedger.Core/Components.cs`
- Create: `src/PowerLedger.Core/Reading.cs`
- Create: `src/PowerLedger.Core/HardwareFacts.cs`
- Create: `src/PowerLedger.Core/PowerModelOptions.cs`
- Create: `src/PowerLedger.Core/IBaselineProvider.cs`
- Create: `src/PowerLedger.Core/CalibrationBuckets.cs`
- Create: `tests/PowerLedger.Core.Tests/TestData.cs`
- Test: `tests/PowerLedger.Core.Tests/ComponentsTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/PowerLedger.Core.Tests/ComponentsTests.cs`
```csharp
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class ComponentsTests
{
    [Fact]
    public void Sum_adds_every_part()
    {
        var c = new Components(Cpu: 1, Gpu: 2, Display: 3, Ram: 4, Storage: 5, Board: 6, Extras: 7, Monitors: 8, PsuLoss: 9, Rest: 10);
        c.Sum.ShouldBe(55);
    }

    [Theory]
    [InlineData(0.0, true, 0)]
    [InlineData(0.55, true, 6)]
    [InlineData(1.0, true, 10)]
    [InlineData(null, true, 5)]
    [InlineData(0.9, false, -1)]
    public void Buckets_follow_brightness_in_ten_percent_steps_and_display_off_is_minus_one(double? brightness, bool displayOn, int expected)
    {
        CalibrationBuckets.For(brightness, displayOn).ShouldBe(expected);
    }
}
```

`tests/PowerLedger.Core.Tests/TestData.cs`
```csharp
using PowerLedger.Core;

namespace PowerLedger.Core.Tests;

internal static class TestData
{
    public static readonly DateTimeOffset T0 = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A laptop tick. Defaults: on AC, CPU 14.6 W, dGPU 4.1 W, brightness 60 %, display on, user active.</summary>
    public static Sample Laptop(
        double? cpu = 14.6, double? gpu = 4.1, bool gpuPresent = true,
        double? battery = null, bool onBattery = false,
        double? brightness = 0.6, bool displayOn = true,
        double idleSeconds = 0, double cpuLoad = 0.3, double? gpuLoad = 0.3,
        double delta = 1.0, DateTimeOffset? ts = null, bool suspect = false, bool locked = false)
        => new(ts ?? T0, delta, cpu, 2.0, cpuLoad, gpu, gpuLoad, gpuPresent, battery, onBattery,
               brightness, displayOn, 0, idleSeconds, locked, suspect);
}

internal sealed class FixedBaseline(double? value) : IBaselineProvider
{
    public double? GetBaseline(int bucket) => value;
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Core.Tests --filter ComponentsTests`
Expected: build error, `Components` and `CalibrationBuckets` not found.

- [ ] **Step 3: Write the records**

`src/PowerLedger.Core/Sample.cs`
```csharp
namespace PowerLedger.Core;

/// <summary>Raw sensor values for one tick. Null means the source had no value this tick.</summary>
/// <param name="DeltaSeconds">Seconds since the previous tick, from a monotonic clock.</param>
/// <param name="CpuLoad">0..1.</param>
/// <param name="BatteryRateW">Discharge watts (positive) while on battery; null when unknown.</param>
/// <param name="Brightness">0..1 for the internal panel; null when unavailable.</param>
/// <param name="Suspect">Set by the validator when a value was replaced or looks implausible.</param>
public sealed record Sample(
    DateTimeOffset Timestamp,
    double DeltaSeconds,
    double? CpuPackageW,
    double? IGpuW,
    double CpuLoad,
    double? DGpuW,
    double? DGpuLoad,
    bool DGpuPresent,
    double? BatteryRateW,
    bool OnBattery,
    double? Brightness,
    bool DisplayOn,
    int MonitorCount,
    double UserIdleSeconds,
    bool SessionLocked,
    bool Suspect);
```

`src/PowerLedger.Core/Components.cs`
```csharp
namespace PowerLedger.Core;

/// <summary>Watts attributed to each part of the machine for one tick.</summary>
/// <param name="Rest">Measured remainder (battery mode) or learned baseline (calibrated mode); 0 in estimated mode.</param>
public sealed record Components(
    double Cpu, double Gpu, double Display, double Ram, double Storage,
    double Board, double Extras, double Monitors, double PsuLoss, double Rest)
{
    public double Sum => Cpu + Gpu + Display + Ram + Storage + Board + Extras + Monitors + PsuLoss + Rest;

    public static Components Zero { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}
```

`src/PowerLedger.Core/Reading.cs`
```csharp
using PowerLedger.Contracts;

namespace PowerLedger.Core;

/// <summary>One tick after the power model: whole-system watts plus how much to trust them.</summary>
public sealed record Reading(
    DateTimeOffset Timestamp,
    double DeltaSeconds,
    double TotalW,
    Quality Quality,
    Components Components,
    bool OnBattery,
    bool DisplayOn,
    bool UserIdle,
    bool SessionLocked,
    double CpuLoad,
    double? GpuLoad,
    double? Brightness,
    bool Suspect);
```

`src/PowerLedger.Core/HardwareFacts.cs`
```csharp
namespace PowerLedger.Core;

/// <summary>Numbers from the hardware inventory that the model needs for sensorless fallbacks.</summary>
public sealed record HardwareFacts(double CpuTdpW, double GpuTdpW)
{
    public static HardwareFacts LaptopDefaults { get; } = new(CpuTdpW: 15, GpuTdpW: 25);
    public static HardwareFacts DesktopDefaults { get; } = new(CpuTdpW: 65, GpuTdpW: 75);
}
```

`src/PowerLedger.Core/PowerModelOptions.cs`
```csharp
namespace PowerLedger.Core;

public sealed record PowerModelOptions(
    double IdleThresholdSeconds = 300,
    double LaptopAdapterEfficiency = 0.90);
```

`src/PowerLedger.Core/IBaselineProvider.cs`
```csharp
namespace PowerLedger.Core;

/// <summary>Learned "rest of system" watts for a calibration bucket, or null when nothing was learned yet.</summary>
public interface IBaselineProvider
{
    double? GetBaseline(int bucket);
}
```

`src/PowerLedger.Core/CalibrationBuckets.cs`
```csharp
namespace PowerLedger.Core;

/// <summary>Buckets baselines by panel brightness in 10 % steps (0..10); -1 means the display is off.</summary>
public static class CalibrationBuckets
{
    public const int DisplayOff = -1;
    public const int Count = 12;

    public static int For(double? brightness, bool displayOn)
    {
        if (!displayOn) return DisplayOff;
        var b = Math.Clamp(brightness ?? 0.5, 0, 1);
        return (int)Math.Round(b * 10, MidpointRounding.AwayFromZero);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Core.Tests --filter ComponentsTests`
Expected: `Passed! - Failed: 0, Passed: 6`.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.Core tests/PowerLedger.Core.Tests
git commit -m "Add Core records: Sample, Components, Reading, HardwareFacts, calibration buckets"
```

---

### Task 4: DisplayModel and PsuEfficiency

**Files:**
- Create: `src/PowerLedger.Core/DisplayModel.cs`
- Create: `src/PowerLedger.Core/PsuEfficiency.cs`
- Test: `tests/PowerLedger.Core.Tests/DisplayModelTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class DisplayModelTests
{
    [Fact]
    public void Laptop_panel_at_sixty_percent_on_a_15_inch_screen_is_4_2_watts()
        => DisplayModel.PanelWatts(MachineProfile.DefaultLaptop, 0.6, displayOn: true).ShouldBe(4.2, 0.001);

    [Fact]
    public void Small_panels_scale_down_and_large_panels_scale_up()
    {
        var small = MachineProfile.DefaultLaptop with { DisplayDiagonalInches = 13.3 };
        var large = MachineProfile.DefaultLaptop with { DisplayDiagonalInches = 17.3 };
        DisplayModel.PanelWatts(small, 1.0, true).ShouldBe(6.0 * 0.8, 0.001);
        DisplayModel.PanelWatts(large, 1.0, true).ShouldBe(6.0 * 1.3, 0.001);
    }

    [Fact]
    public void Panel_is_zero_when_display_is_off_or_machine_is_a_desktop()
    {
        DisplayModel.PanelWatts(MachineProfile.DefaultLaptop, 0.6, displayOn: false).ShouldBe(0);
        DisplayModel.PanelWatts(MachineProfile.DefaultDesktop, 0.6, displayOn: true).ShouldBe(0);
    }

    [Fact]
    public void Unknown_brightness_assumes_fifty_percent()
        => DisplayModel.PanelWatts(MachineProfile.DefaultLaptop, null, true).ShouldBe(1.5 + 4.5 * 0.5, 0.001);

    [Fact]
    public void External_monitors_count_only_when_opted_in()
    {
        var two = MachineProfile.DefaultDesktop with { ExternalMonitors = 2, IncludeMonitors = true, MonitorWatts = 25 };
        DisplayModel.MonitorWatts(two, displayOn: true).ShouldBe(50);
        DisplayModel.MonitorWatts(two, displayOn: false).ShouldBe(1.0, 0.001);
        DisplayModel.MonitorWatts(MachineProfile.DefaultDesktop, displayOn: true).ShouldBe(0);
    }

    [Theory]
    [InlineData(PsuTier.White, 0.82)]
    [InlineData(PsuTier.Bronze, 0.85)]
    [InlineData(PsuTier.Gold, 0.90)]
    [InlineData(PsuTier.Titanium, 0.94)]
    public void Psu_efficiency_by_tier(PsuTier tier, double expected)
        => PsuEfficiency.For(tier).ShouldBe(expected);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Core.Tests --filter DisplayModelTests`
Expected: build error, `DisplayModel` not found.

- [ ] **Step 3: Write the implementation**

`src/PowerLedger.Core/PsuEfficiency.cs`
```csharp
using PowerLedger.Contracts;

namespace PowerLedger.Core;

public static class PsuEfficiency
{
    public static double For(PsuTier tier) => tier switch
    {
        PsuTier.White => 0.82,
        PsuTier.Bronze => 0.85,
        PsuTier.Silver => 0.87,
        PsuTier.Gold => 0.90,
        PsuTier.Platinum => 0.92,
        PsuTier.Titanium => 0.94,
        _ => 0.85,
    };
}
```

`src/PowerLedger.Core/DisplayModel.cs`
```csharp
using PowerLedger.Contracts;

namespace PowerLedger.Core;

/// <summary>Watts drawn by the internal panel and by opted-in external monitors.</summary>
public static class DisplayModel
{
    public const double PanelBaseW = 1.5;
    public const double PanelRangeW = 4.5;
    public const double MonitorSleepW = 0.5;

    public static double PanelWatts(MachineProfile profile, double? brightness, bool displayOn)
    {
        if (profile.Chassis != ChassisKind.Laptop || !displayOn) return 0;
        var b = Math.Clamp(brightness ?? 0.5, 0, 1);
        return (PanelBaseW + PanelRangeW * b) * SizeFactor(profile.DisplayDiagonalInches);
    }

    public static double SizeFactor(double diagonalInches) => diagonalInches switch
    {
        <= 0 => 1.0,
        <= 14.0 => 0.8,
        < 17.0 => 1.0,
        _ => 1.3,
    };

    public static double MonitorWatts(MachineProfile profile, bool displayOn)
    {
        if (!profile.IncludeMonitors || profile.ExternalMonitors <= 0) return 0;
        return profile.ExternalMonitors * (displayOn ? profile.MonitorWatts : MonitorSleepW);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Core.Tests --filter DisplayModelTests`
Expected: `Passed! - Failed: 0, Passed: 9`.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.Core/DisplayModel.cs src/PowerLedger.Core/PsuEfficiency.cs tests/PowerLedger.Core.Tests/DisplayModelTests.cs
git commit -m "Add display and PSU efficiency models"
```

---

### Task 5: PowerModel — Sample → Reading

**Files:**
- Create: `src/PowerLedger.Core/PowerModel.cs`
- Test: `tests/PowerLedger.Core.Tests/PowerModelTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class PowerModelTests
{
    private static PowerModel Laptop(double? baseline = null, PowerModelOptions? options = null)
        => new(MachineProfile.DefaultLaptop, HardwareFacts.LaptopDefaults, options ?? new PowerModelOptions(), new FixedBaseline(baseline));

    private static PowerModel Desktop()
        => new(MachineProfile.DefaultDesktop, HardwareFacts.DesktopDefaults, new PowerModelOptions(), new FixedBaseline(null));

    [Fact]
    public void On_battery_the_discharge_rate_is_the_total_and_the_remainder_becomes_rest()
    {
        var r = Laptop().Evaluate(TestData.Laptop(battery: 34.2, onBattery: true));
        r.TotalW.ShouldBe(34.2, 0.001);
        r.Quality.ShouldBe(Quality.Measured);
        r.Components.Cpu.ShouldBe(14.6);
        r.Components.Gpu.ShouldBe(4.1);
        r.Components.Display.ShouldBe(4.2, 0.001);
        r.Components.Rest.ShouldBe(34.2 - 14.6 - 4.1 - 4.2, 0.001);
        r.Components.PsuLoss.ShouldBe(0);
    }

    [Fact]
    public void On_ac_without_calibration_the_laptop_uses_the_default_baseline_and_adapter_efficiency()
    {
        var r = Laptop().Evaluate(TestData.Laptop());
        var beforePsu = 14.6 + 4.1 + 4.2 + 5.0;
        r.Quality.ShouldBe(Quality.Estimated);
        r.TotalW.ShouldBe(beforePsu / 0.9, 0.001);
        r.Components.Board.ShouldBe(5.0);
        r.Components.PsuLoss.ShouldBe(beforePsu / 0.9 - beforePsu, 0.001);
        r.Components.Sum.ShouldBe(r.TotalW, 0.001);
    }

    [Fact]
    public void On_ac_with_a_learned_baseline_the_reading_is_calibrated()
    {
        var r = Laptop(baseline: 9.0).Evaluate(TestData.Laptop());
        r.Quality.ShouldBe(Quality.Calibrated);
        r.Components.Rest.ShouldBe(9.0);
        r.Components.Board.ShouldBe(0);
        r.TotalW.ShouldBe((14.6 + 4.1 + 4.2 + 9.0) / 0.9, 0.001);
    }

    [Fact]
    public void Battery_flag_without_a_rate_falls_back_to_estimate_at_full_efficiency()
    {
        var r = Laptop().Evaluate(TestData.Laptop(onBattery: true, battery: null));
        r.Quality.ShouldBe(Quality.Estimated);
        r.TotalW.ShouldBe(14.6 + 4.1 + 4.2 + 5.0, 0.001);
        r.OnBattery.ShouldBeTrue();
    }

    [Fact]
    public void Desktop_estimate_adds_ram_storage_board_fans_and_divides_by_psu_efficiency()
    {
        var r = Desktop().Evaluate(TestData.Laptop(cpu: 50, gpu: 120, brightness: null));
        var beforePsu = 50 + 120 + 0 + 2 * 2.5 + 2 + (12 + 3 * 1);
        r.Components.Display.ShouldBe(0);
        r.Components.Ram.ShouldBe(5.0);
        r.Components.Storage.ShouldBe(2.0);
        r.Components.Board.ShouldBe(15.0);
        r.TotalW.ShouldBe(beforePsu / 0.85, 0.001);
    }

    [Fact]
    public void Missing_cpu_sensor_uses_idle_plus_tdp_times_load()
        => Laptop().Evaluate(TestData.Laptop(cpu: null, cpuLoad: 0.5)).Components.Cpu.ShouldBe(2 + (15 - 2) * 0.5, 0.001);

    [Fact]
    public void Cpu_tdp_override_wins_over_inventory_facts()
    {
        var profile = MachineProfile.DefaultLaptop with { CpuTdpOverrideW = 28 };
        var model = new PowerModel(profile, HardwareFacts.LaptopDefaults, new PowerModelOptions(), new FixedBaseline(null));
        model.Evaluate(TestData.Laptop(cpu: null, cpuLoad: 1.0)).Components.Cpu.ShouldBe(28, 0.001);
    }

    [Fact]
    public void Missing_gpu_sensor_uses_idle_plus_tdp_times_load_and_absent_gpu_is_zero()
    {
        Laptop().Evaluate(TestData.Laptop(gpu: null, gpuLoad: 0.4)).Components.Gpu.ShouldBe(3 + (25 - 3) * 0.4, 0.001);
        Laptop().Evaluate(TestData.Laptop(gpu: null, gpuPresent: false)).Components.Gpu.ShouldBe(0);
    }

    [Fact]
    public void User_is_idle_at_or_beyond_the_threshold()
    {
        Laptop().Evaluate(TestData.Laptop(idleSeconds: 299)).UserIdle.ShouldBeFalse();
        Laptop().Evaluate(TestData.Laptop(idleSeconds: 300)).UserIdle.ShouldBeTrue();
        Laptop(options: new PowerModelOptions(IdleThresholdSeconds: 60)).Evaluate(TestData.Laptop(idleSeconds: 61)).UserIdle.ShouldBeTrue();
    }

    [Fact]
    public void Suspect_and_lock_flags_pass_through()
    {
        var r = Laptop().Evaluate(TestData.Laptop(suspect: true, locked: true));
        r.Suspect.ShouldBeTrue();
        r.SessionLocked.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Core.Tests --filter PowerModelTests`
Expected: build error, `PowerModel` not found.

- [ ] **Step 3: Write the model**

`src/PowerLedger.Core/PowerModel.cs`
```csharp
using PowerLedger.Contracts;

namespace PowerLedger.Core;

/// <summary>
/// Turns one <see cref="Sample"/> into a <see cref="Reading"/> (spec §5).
/// Battery discharge is the truth when available; otherwise the parts are summed
/// with a learned or default "rest of system" baseline and divided by supply efficiency.
/// </summary>
public sealed class PowerModel
{
    public const double LaptopBaselineW = 5;
    public const double DesktopBoardW = 12;
    public const double FanW = 1;
    public const double Ddr4StickW = 2.5;
    public const double Ddr5StickW = 1.5;
    public const double SsdW = 2;
    public const double HddW = 6;
    public const double CpuIdleLaptopW = 2;
    public const double CpuIdleDesktopW = 8;
    public const double GpuIdleW = 3;

    private readonly MachineProfile _profile;
    private readonly HardwareFacts _facts;
    private readonly PowerModelOptions _options;
    private readonly IBaselineProvider _baselines;

    public PowerModel(MachineProfile profile, HardwareFacts facts, PowerModelOptions options, IBaselineProvider baselines)
    {
        _profile = profile;
        _facts = facts;
        _options = options;
        _baselines = baselines;
    }

    public Reading Evaluate(Sample s)
    {
        var cpu = CpuWatts(s);
        var gpu = GpuWatts(s);
        var display = DisplayModel.PanelWatts(_profile, s.Brightness, s.DisplayOn);
        var monitors = DisplayModel.MonitorWatts(_profile, s.DisplayOn);
        var userIdle = s.UserIdleSeconds >= _options.IdleThresholdSeconds;

        if (s.OnBattery && s.BatteryRateW is { } measured && measured >= 0)
        {
            var rest = Math.Max(0, measured - cpu - gpu - display);
            var measuredParts = new Components(cpu, gpu, display, 0, 0, 0, 0, 0, 0, rest);
            return Build(s, measured, Quality.Measured, measuredParts, userIdle);
        }

        var isLaptop = _profile.Chassis == ChassisKind.Laptop;
        Components parts;
        Quality quality;
        if (_baselines.GetBaseline(CalibrationBuckets.For(s.Brightness, s.DisplayOn)) is { } learned)
        {
            parts = new Components(cpu, gpu, display, 0, 0, 0, 0, monitors, 0, learned);
            quality = Quality.Calibrated;
        }
        else if (isLaptop)
        {
            parts = new Components(cpu, gpu, display, 0, 0, LaptopBaselineW, _profile.ExtrasWatts, monitors, 0, 0);
            quality = Quality.Estimated;
        }
        else
        {
            var board = DesktopBoardW + _profile.FanCount * FanW;
            parts = new Components(cpu, gpu, display, RamWatts(), StorageWatts(), board, _profile.ExtrasWatts, monitors, 0, 0);
            quality = Quality.Estimated;
        }

        var beforePsu = parts.Sum;
        var efficiency = isLaptop
            ? (s.OnBattery ? 1.0 : _options.LaptopAdapterEfficiency)
            : PsuEfficiency.For(_profile.PsuTier);
        var total = beforePsu / efficiency;
        return Build(s, total, quality, parts with { PsuLoss = total - beforePsu }, userIdle);
    }

    private static Reading Build(Sample s, double total, Quality quality, Components parts, bool userIdle)
        => new(s.Timestamp, s.DeltaSeconds, total, quality, parts, s.OnBattery, s.DisplayOn, userIdle,
               s.SessionLocked, s.CpuLoad, s.DGpuLoad, s.Brightness, s.Suspect);

    private double CpuWatts(Sample s)
    {
        if (s.CpuPackageW is { } w) return Math.Max(0, w);
        var idle = _profile.Chassis == ChassisKind.Laptop ? CpuIdleLaptopW : CpuIdleDesktopW;
        var tdp = Math.Max(idle + 1, _profile.CpuTdpOverrideW ?? _facts.CpuTdpW);
        return idle + (tdp - idle) * Math.Clamp(s.CpuLoad, 0, 1);
    }

    private double GpuWatts(Sample s)
    {
        if (!s.DGpuPresent) return 0;
        if (s.DGpuW is { } w) return Math.Max(0, w);
        var tdp = Math.Max(GpuIdleW + 1, _profile.GpuTdpOverrideW ?? _facts.GpuTdpW);
        return GpuIdleW + (tdp - GpuIdleW) * Math.Clamp(s.DGpuLoad ?? 0, 0, 1);
    }

    private double RamWatts() => _profile.RamSticks * (_profile.RamIsDdr5 ? Ddr5StickW : Ddr4StickW);

    private double StorageWatts() => _profile.SsdCount * SsdW + _profile.HddCount * HddW;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Core.Tests --filter PowerModelTests`
Expected: `Passed! - Failed: 0, Passed: 10`.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.Core/PowerModel.cs tests/PowerLedger.Core.Tests/PowerModelTests.cs
git commit -m "Add PowerModel: measured, calibrated and estimated readings"
```

---

### Task 6: CalibrationLearner — learn the laptop baseline from battery data

**Files:**
- Create: `src/PowerLedger.Core/CalibrationOptions.cs`
- Create: `src/PowerLedger.Core/CalibrationState.cs`
- Create: `src/PowerLedger.Core/CalibrationLearner.cs`
- Test: `tests/PowerLedger.Core.Tests/CalibrationLearnerTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class CalibrationLearnerTests
{
    // Small thresholds so tests stay fast; production defaults are 600 / 300 / 1800.
    private static readonly CalibrationOptions Fast = new(HalfLifeSamples: 10, MinBucketSamples: 5, MinTotalSamples: 5);

    private static void Feed(CalibrationLearner learner, int ticks, double batteryW, double brightness = 0.6, bool displayOn = true)
    {
        for (var i = 0; i < ticks; i++)
        {
            var s = TestData.Laptop(cpu: 10, gpu: 2, battery: batteryW, onBattery: true, brightness: brightness, displayOn: displayOn);
            learner.Observe(s, cpuW: 10, gpuW: 2, displayW: 4);
        }
    }

    [Fact]
    public void Nothing_is_reported_before_the_minimum_sample_counts()
    {
        var learner = new CalibrationLearner(new CalibrationOptions(HalfLifeSamples: 10, MinBucketSamples: 50, MinTotalSamples: 100));
        Feed(learner, 40, batteryW: 25);
        learner.GetBaseline(CalibrationBuckets.For(0.6, true)).ShouldBeNull();
    }

    [Fact]
    public void A_constant_observation_is_learned_exactly()
    {
        var learner = new CalibrationLearner(Fast);
        Feed(learner, 10, batteryW: 25);                       // 25 - 10 - 2 - 4 = 9 W of "rest"
        learner.GetBaseline(CalibrationBuckets.For(0.6, true)).ShouldBe(9.0, 0.0001);
    }

    [Fact]
    public void The_average_moves_with_the_configured_half_life()
    {
        var learner = new CalibrationLearner(Fast);
        Feed(learner, 1, batteryW: 26);                        // seeds bucket at 10 W
        Feed(learner, 30, batteryW: 36);                       // 20 W for three half-lives
        learner.GetBaseline(CalibrationBuckets.For(0.6, true)).ShouldBe(20 - 10 * 0.125, 0.05);
    }

    [Fact]
    public void Buckets_are_independent()
    {
        var learner = new CalibrationLearner(Fast);
        Feed(learner, 10, batteryW: 25, brightness: 0.3);
        learner.GetBaseline(CalibrationBuckets.For(0.3, true)).ShouldNotBeNull();
        learner.GetBaseline(CalibrationBuckets.For(0.9, true)).ShouldBeNull();
        learner.GetBaseline(CalibrationBuckets.DisplayOff).ShouldBeNull();
    }

    [Fact]
    public void Ticks_on_ac_or_marked_suspect_are_ignored()
    {
        var learner = new CalibrationLearner(Fast);
        learner.Observe(TestData.Laptop(battery: 25, onBattery: false), 10, 2, 4);
        learner.Observe(TestData.Laptop(battery: 25, onBattery: true, suspect: true), 10, 2, 4);
        learner.Observe(TestData.Laptop(battery: null, onBattery: true), 10, 2, 4);
        learner.TotalSamples.ShouldBe(0);
    }

    [Fact]
    public void Export_and_import_round_trip()
    {
        var learner = new CalibrationLearner(Fast);
        Feed(learner, 10, batteryW: 25, brightness: 0.6);
        Feed(learner, 10, batteryW: 21, displayOn: false);
        var state = learner.Export();
        state.Buckets.Count.ShouldBe(2);

        var restored = new CalibrationLearner(Fast);
        restored.Import(state);
        restored.TotalSamples.ShouldBe(20);
        restored.GetBaseline(CalibrationBuckets.For(0.6, true)).ShouldBe(9.0, 0.0001);
        restored.GetBaseline(CalibrationBuckets.DisplayOff).ShouldBe(21 - 10 - 2 - 4, 0.0001);
    }

    [Fact]
    public void Reset_forgets_everything()
    {
        var learner = new CalibrationLearner(Fast);
        Feed(learner, 10, batteryW: 25);
        learner.Reset();
        learner.TotalSamples.ShouldBe(0);
        learner.GetBaseline(CalibrationBuckets.For(0.6, true)).ShouldBeNull();
    }

    [Fact]
    public void A_learned_baseline_turns_ac_readings_calibrated()
    {
        var learner = new CalibrationLearner(Fast);
        Feed(learner, 10, batteryW: 25);
        var model = new PowerModel(MachineProfile.DefaultLaptop, HardwareFacts.LaptopDefaults, new PowerModelOptions(), learner);
        var r = model.Evaluate(TestData.Laptop(cpu: 10, gpu: 2, brightness: 0.6));
        r.Quality.ShouldBe(Quality.Calibrated);
        r.Components.Rest.ShouldBe(9.0, 0.0001);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Core.Tests --filter CalibrationLearnerTests`
Expected: build error, `CalibrationLearner` not found.

- [ ] **Step 3: Write the learner**

`src/PowerLedger.Core/CalibrationOptions.cs`
```csharp
namespace PowerLedger.Core;

/// <param name="HalfLifeSamples">Samples after which an old observation carries half its weight (600 = 10 min at 1 Hz).</param>
/// <param name="MinBucketSamples">Samples a bucket needs before it is trusted (300 = 5 min).</param>
/// <param name="MinTotalSamples">Battery samples the machine needs in total before any bucket is trusted (1800 = 30 min).</param>
public sealed record CalibrationOptions(
    int HalfLifeSamples = 600,
    int MinBucketSamples = 300,
    int MinTotalSamples = 1800);
```

`src/PowerLedger.Core/CalibrationState.cs`
```csharp
namespace PowerLedger.Core;

public sealed record BucketState(int Bucket, double BaselineW, int Samples);

/// <summary>Serialisable snapshot of a <see cref="CalibrationLearner"/>, keyed by hardware inventory hash in storage.</summary>
public sealed record CalibrationState(IReadOnlyList<BucketState> Buckets)
{
    public static CalibrationState Empty { get; } = new([]);
}
```

`src/PowerLedger.Core/CalibrationLearner.cs`
```csharp
namespace PowerLedger.Core;

/// <summary>
/// Learns the laptop's "rest of system" watts per brightness bucket from battery ticks:
/// observed = measured − cpu − gpu − display, smoothed with an exponential moving average.
/// </summary>
public sealed class CalibrationLearner : IBaselineProvider
{
    private readonly CalibrationOptions _options;
    private readonly double _alpha;
    private readonly double[] _baseline = new double[CalibrationBuckets.Count];
    private readonly int[] _samples = new int[CalibrationBuckets.Count];

    public CalibrationLearner(CalibrationOptions? options = null)
    {
        _options = options ?? new CalibrationOptions();
        _alpha = 1 - Math.Pow(2, -1.0 / _options.HalfLifeSamples);
    }

    public int TotalSamples => _samples.Sum();

    /// <summary>Feed one tick. Ignored unless the sample is on battery, has a rate, and is not suspect.
    /// The caller must also skip the 3 s after an AC transition.</summary>
    public void Observe(Sample s, double cpuW, double gpuW, double displayW)
    {
        if (!s.OnBattery || s.Suspect || s.BatteryRateW is not { } measured) return;
        var observed = Math.Max(0, measured - cpuW - gpuW - displayW);
        var i = Index(CalibrationBuckets.For(s.Brightness, s.DisplayOn));
        _baseline[i] = _samples[i] == 0 ? observed : _baseline[i] + _alpha * (observed - _baseline[i]);
        _samples[i]++;
    }

    public double? GetBaseline(int bucket)
    {
        var i = Index(bucket);
        if (TotalSamples < _options.MinTotalSamples || _samples[i] < _options.MinBucketSamples) return null;
        return _baseline[i];
    }

    public CalibrationState Export() => new(
        Enumerable.Range(0, CalibrationBuckets.Count)
            .Where(i => _samples[i] > 0)
            .Select(i => new BucketState(BucketOf(i), _baseline[i], _samples[i]))
            .ToList());

    public void Import(CalibrationState state)
    {
        Reset();
        foreach (var b in state.Buckets)
        {
            var i = Index(b.Bucket);
            _baseline[i] = b.BaselineW;
            _samples[i] = b.Samples;
        }
    }

    public void Reset()
    {
        Array.Clear(_baseline);
        Array.Clear(_samples);
    }

    private static int Index(int bucket) => Math.Clamp(bucket, CalibrationBuckets.DisplayOff, 10) + 1;

    private static int BucketOf(int index) => index - 1;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Core.Tests --filter CalibrationLearnerTests`
Expected: `Passed! - Failed: 0, Passed: 8`.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.Core/Calibration*.cs tests/PowerLedger.Core.Tests/CalibrationLearnerTests.cs
git commit -m "Add CalibrationLearner with per-bucket EMA baselines"
```

---

### Task 7: EnergyIntegrator — Reading → energy with the Δt and gap rules

**Files:**
- Create: `src/PowerLedger.Core/EnergySlice.cs`
- Create: `src/PowerLedger.Core/EnergyIntegrator.cs`
- Test: `tests/PowerLedger.Core.Tests/EnergyIntegratorTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class EnergyIntegratorTests
{
    private static Reading Reading(double totalW = 34.2, double delta = 1.0, bool idle = false, bool displayOn = true, bool onBattery = false)
        => new(TestData.T0, delta, totalW, Quality.Measured,
               new Components(Cpu: 14.6, Gpu: 4.1, Display: 4.2, 0, 0, 0, 0, 0, 0, Rest: 11.3),
               onBattery, displayOn, idle, false, 0.3, 0.3, 0.6, false);

    [Fact]
    public void One_second_at_34_2_watts_is_0_0095_watt_hours_split_by_component()
    {
        var e = EnergyIntegrator.Integrate(Reading());
        e.Wh.ShouldBe(34.2 / 3600, 1e-9);
        e.CpuWh.ShouldBe(14.6 / 3600, 1e-9);
        e.GpuWh.ShouldBe(4.1 / 3600, 1e-9);
        e.DisplayWh.ShouldBe(4.2 / 3600, 1e-9);
        e.RestWh.ShouldBe(11.3 / 3600, 1e-9);
        e.OnSeconds.ShouldBe(1.0);
        e.Gap.ShouldBeFalse();
    }

    [Fact]
    public void Ticks_up_to_five_seconds_count_in_full()
    {
        var e = EnergyIntegrator.Integrate(Reading(delta: 5.0));
        e.Wh.ShouldBe(34.2 * 5 / 3600, 1e-9);
        e.OnSeconds.ShouldBe(5.0);
    }

    [Fact]
    public void Ticks_longer_than_five_seconds_are_gaps_with_zero_energy()
    {
        var e = EnergyIntegrator.Integrate(Reading(delta: 5.01));
        e.Gap.ShouldBeTrue();
        e.Wh.ShouldBe(0);
        e.OnSeconds.ShouldBe(0);
        e.GapSeconds.ShouldBe(5.01);
    }

    [Fact]
    public void Non_positive_deltas_contribute_nothing_and_are_not_gaps()
    {
        var e = EnergyIntegrator.Integrate(Reading(delta: 0));
        e.Wh.ShouldBe(0);
        e.Gap.ShouldBeFalse();
        e.GapSeconds.ShouldBe(0);
    }

    [Fact]
    public void Idle_energy_is_split_by_display_state()
    {
        EnergyIntegrator.Integrate(Reading(idle: true, displayOn: true)).IdleOnWh.ShouldBe(34.2 / 3600, 1e-9);
        EnergyIntegrator.Integrate(Reading(idle: true, displayOn: true)).IdleOnSeconds.ShouldBe(1.0);
        EnergyIntegrator.Integrate(Reading(idle: true, displayOn: false)).IdleOffWh.ShouldBe(34.2 / 3600, 1e-9);
        EnergyIntegrator.Integrate(Reading(idle: true, displayOn: false)).IdleOffSeconds.ShouldBe(1.0);
        EnergyIntegrator.Integrate(Reading(idle: false)).IdleOnWh.ShouldBe(0);
        EnergyIntegrator.Integrate(Reading(idle: false)).IdleOnSeconds.ShouldBe(0);
    }

    [Fact]
    public void Battery_seconds_are_counted_only_on_battery()
    {
        EnergyIntegrator.Integrate(Reading(onBattery: true)).BatterySeconds.ShouldBe(1.0);
        EnergyIntegrator.Integrate(Reading(onBattery: false)).BatterySeconds.ShouldBe(0);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Core.Tests --filter EnergyIntegratorTests`
Expected: build error, `EnergyIntegrator` not found.

- [ ] **Step 3: Write the integrator**

`src/PowerLedger.Core/EnergySlice.cs`
```csharp
namespace PowerLedger.Core;

/// <summary>Energy contributed by one tick. RestWh is everything that is not CPU, GPU or display.</summary>
public sealed record EnergySlice(
    double Wh, double CpuWh, double GpuWh, double DisplayWh, double RestWh,
    double IdleOnWh, double IdleOffWh,
    double IdleOnSeconds, double IdleOffSeconds,
    double OnSeconds, double BatterySeconds, double GapSeconds, bool Gap)
{
    public static EnergySlice Nothing { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false);

    public static EnergySlice GapOf(double seconds) => Nothing with { GapSeconds = seconds, Gap = true };
}
```

`src/PowerLedger.Core/EnergyIntegrator.cs`
```csharp
namespace PowerLedger.Core;

/// <summary>Spec §6: a tick of at most <see cref="MaxDeltaSeconds"/> counts in full; anything longer is a gap.</summary>
public static class EnergyIntegrator
{
    public const double MaxDeltaSeconds = 5;

    public static EnergySlice Integrate(Reading r, double maxDeltaSeconds = MaxDeltaSeconds)
    {
        if (r.DeltaSeconds <= 0) return EnergySlice.Nothing;
        if (r.DeltaSeconds > maxDeltaSeconds) return EnergySlice.GapOf(r.DeltaSeconds);

        var hours = r.DeltaSeconds / 3600.0;
        var wh = r.TotalW * hours;
        var cpu = r.Components.Cpu * hours;
        var gpu = r.Components.Gpu * hours;
        var display = r.Components.Display * hours;
        var rest = wh - cpu - gpu - display;
        var idleOn = r.UserIdle && r.DisplayOn ? wh : 0;
        var idleOff = r.UserIdle && !r.DisplayOn ? wh : 0;
        return new EnergySlice(wh, cpu, gpu, display, rest, idleOn, idleOff,
            IdleOnSeconds: r.UserIdle && r.DisplayOn ? r.DeltaSeconds : 0,
            IdleOffSeconds: r.UserIdle && !r.DisplayOn ? r.DeltaSeconds : 0,
            OnSeconds: r.DeltaSeconds,
            BatterySeconds: r.OnBattery ? r.DeltaSeconds : 0,
            GapSeconds: 0, Gap: false);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Core.Tests --filter EnergyIntegratorTests`
Expected: `Passed! - Failed: 0, Passed: 6`.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.Core/Energy*.cs tests/PowerLedger.Core.Tests/EnergyIntegratorTests.cs
git commit -m "Add EnergyIntegrator with delta cap and gap rule"
```

---

### Task 8: Aggregate and Downsampler — readings → minute, minutes → hour

**Files:**
- Create: `src/PowerLedger.Core/Aggregate.cs`
- Create: `src/PowerLedger.Core/Downsampler.cs`
- Test: `tests/PowerLedger.Core.Tests/DownsamplerTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class DownsamplerTests
{
    private static Reading At(int second, double totalW, double delta = 1.0, Quality q = Quality.Measured, bool idle = false)
        => new(TestData.T0.AddSeconds(second), delta, totalW, q,
               new Components(Cpu: totalW * 0.4, Gpu: totalW * 0.1, Display: 4, 0, 0, 0, 0, 0, 0, Rest: totalW * 0.5 - 4),
               OnBattery: q == Quality.Measured, DisplayOn: true, UserIdle: idle, SessionLocked: false, 0.3, 0.3, 0.6, false);

    [Fact]
    public void Sixty_seconds_at_thirty_watts_is_half_a_watt_hour()
    {
        var readings = Enumerable.Range(0, 60).Select(i => At(i, 30)).ToList();
        var m = Downsampler.ToMinute(TestData.T0, readings);
        m.Start.ShouldBe(TestData.T0);
        m.EnergyWh.ShouldBe(0.5, 1e-9);
        m.AvgW.ShouldBe(30, 1e-9);
        m.MaxW.ShouldBe(30);
        m.OnSeconds.ShouldBe(60);
        m.SampleCount.ShouldBe(60);
        m.GapSeconds.ShouldBe(0);
    }

    [Fact]
    public void Minute_energy_equals_the_sum_of_per_tick_integration_even_with_gaps()
    {
        var rng = new Random(7);
        var readings = Enumerable.Range(0, 60).Select(i => At(i, 20 + rng.NextDouble() * 40, delta: i == 30 ? 12.0 : 1.0)).ToList();
        var m = Downsampler.ToMinute(TestData.T0, readings);
        var expected = readings.Sum(r => EnergyIntegrator.Integrate(r).Wh);
        m.EnergyWh.ShouldBe(expected, 1e-9);
        m.GapSeconds.ShouldBe(12.0);
        m.OnSeconds.ShouldBe(59);
    }

    [Fact]
    public void Gap_ticks_do_not_affect_max_or_average()
    {
        var readings = new List<Reading> { At(0, 30), At(1, 30), At(2, 999, delta: 60) };
        var m = Downsampler.ToMinute(TestData.T0, readings);
        m.MaxW.ShouldBe(30);
        m.AvgW.ShouldBe(30, 1e-9);
    }

    [Fact]
    public void Quality_seconds_are_tallied_and_the_dominant_one_reported()
    {
        var readings = Enumerable.Range(0, 40).Select(i => At(i, 30))
            .Concat(Enumerable.Range(40, 20).Select(i => At(i, 30, q: Quality.Estimated))).ToList();
        var m = Downsampler.ToMinute(TestData.T0, readings);
        m.MeasuredSeconds.ShouldBe(40);
        m.EstimatedSeconds.ShouldBe(20);
        m.CalibratedSeconds.ShouldBe(0);
        m.DominantQuality.ShouldBe(Quality.Measured);
        m.BatterySeconds.ShouldBe(40);
    }

    [Fact]
    public void Idle_energy_carries_into_the_minute()
    {
        var readings = Enumerable.Range(0, 60).Select(i => At(i, 30, idle: i >= 30)).ToList();
        var m = Downsampler.ToMinute(TestData.T0, readings);
        m.IdleOnWh.ShouldBe(0.25, 1e-9);
        m.IdleOnSeconds.ShouldBe(30);
        m.IdleOffSeconds.ShouldBe(0);
    }

    [Fact]
    public void An_empty_minute_is_all_zeros()
    {
        var m = Downsampler.ToMinute(TestData.T0, []);
        m.EnergyWh.ShouldBe(0);
        m.AvgW.ShouldBe(0);
        m.SampleCount.ShouldBe(0);
        m.DominantQuality.ShouldBe(Quality.Estimated);
    }

    [Fact]
    public void Hours_sum_minutes_and_average_by_on_time()
    {
        var minutes = Enumerable.Range(0, 60).Select(i =>
            Downsampler.ToMinute(TestData.T0.AddMinutes(i), Enumerable.Range(0, 60).Select(s => At(i * 60 + s, i < 30 ? 20 : 40)).ToList())).ToList();
        var h = Downsampler.ToHour(TestData.T0, minutes);
        h.EnergyWh.ShouldBe(minutes.Sum(m => m.EnergyWh), 1e-9);
        h.EnergyWh.ShouldBe(30.0, 1e-6);
        h.AvgW.ShouldBe(30, 1e-6);
        h.MaxW.ShouldBe(40);
        h.OnSeconds.ShouldBe(3600);
        h.SampleCount.ShouldBe(3600);
        h.DominantQuality.ShouldBe(Quality.Measured);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Core.Tests --filter DownsamplerTests`
Expected: build error, `Downsampler` not found.

- [ ] **Step 3: Write the aggregate and downsampler**

`src/PowerLedger.Core/Aggregate.cs`
```csharp
using PowerLedger.Contracts;

namespace PowerLedger.Core;

/// <summary>One minute or one hour of energy. AvgW is energy-weighted over on-time, so gaps do not drag it down.</summary>
public sealed record Aggregate(
    DateTimeOffset Start,
    double AvgW, double MaxW,
    double EnergyWh, double CpuWh, double GpuWh, double DisplayWh, double RestWh,
    double IdleOnWh, double IdleOffWh,
    double IdleOnSeconds, double IdleOffSeconds,
    double OnSeconds, double BatterySeconds, double GapSeconds,
    int SampleCount,
    double MeasuredSeconds, double CalibratedSeconds, double EstimatedSeconds)
{
    public Quality DominantQuality =>
        MeasuredSeconds >= CalibratedSeconds && MeasuredSeconds >= EstimatedSeconds && MeasuredSeconds > 0 ? Quality.Measured
        : CalibratedSeconds >= EstimatedSeconds && CalibratedSeconds > 0 ? Quality.Calibrated
        : Quality.Estimated;

    public static Aggregate Empty(DateTimeOffset start) => new(start, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}
```

`src/PowerLedger.Core/Downsampler.cs`
```csharp
using PowerLedger.Contracts;

namespace PowerLedger.Core;

public static class Downsampler
{
    public static Aggregate ToMinute(DateTimeOffset minuteStart, IReadOnlyList<Reading> readings, double maxDeltaSeconds = EnergyIntegrator.MaxDeltaSeconds)
    {
        var a = Aggregate.Empty(minuteStart);
        foreach (var r in readings)
        {
            var e = EnergyIntegrator.Integrate(r, maxDeltaSeconds);
            a = a with
            {
                EnergyWh = a.EnergyWh + e.Wh,
                CpuWh = a.CpuWh + e.CpuWh,
                GpuWh = a.GpuWh + e.GpuWh,
                DisplayWh = a.DisplayWh + e.DisplayWh,
                RestWh = a.RestWh + e.RestWh,
                IdleOnWh = a.IdleOnWh + e.IdleOnWh,
                IdleOffWh = a.IdleOffWh + e.IdleOffWh,
                IdleOnSeconds = a.IdleOnSeconds + e.IdleOnSeconds,
                IdleOffSeconds = a.IdleOffSeconds + e.IdleOffSeconds,
                OnSeconds = a.OnSeconds + e.OnSeconds,
                BatterySeconds = a.BatterySeconds + e.BatterySeconds,
                GapSeconds = a.GapSeconds + e.GapSeconds,
                SampleCount = a.SampleCount + 1,
                MaxW = e.Gap ? a.MaxW : Math.Max(a.MaxW, r.TotalW),
                MeasuredSeconds = a.MeasuredSeconds + (r.Quality == Quality.Measured ? e.OnSeconds : 0),
                CalibratedSeconds = a.CalibratedSeconds + (r.Quality == Quality.Calibrated ? e.OnSeconds : 0),
                EstimatedSeconds = a.EstimatedSeconds + (r.Quality == Quality.Estimated ? e.OnSeconds : 0),
            };
        }
        return a with { AvgW = Average(a.EnergyWh, a.OnSeconds) };
    }

    public static Aggregate ToHour(DateTimeOffset hourStart, IReadOnlyList<Aggregate> minutes)
    {
        var a = Aggregate.Empty(hourStart);
        foreach (var m in minutes)
        {
            a = a with
            {
                EnergyWh = a.EnergyWh + m.EnergyWh,
                CpuWh = a.CpuWh + m.CpuWh,
                GpuWh = a.GpuWh + m.GpuWh,
                DisplayWh = a.DisplayWh + m.DisplayWh,
                RestWh = a.RestWh + m.RestWh,
                IdleOnWh = a.IdleOnWh + m.IdleOnWh,
                IdleOffWh = a.IdleOffWh + m.IdleOffWh,
                IdleOnSeconds = a.IdleOnSeconds + m.IdleOnSeconds,
                IdleOffSeconds = a.IdleOffSeconds + m.IdleOffSeconds,
                OnSeconds = a.OnSeconds + m.OnSeconds,
                BatterySeconds = a.BatterySeconds + m.BatterySeconds,
                GapSeconds = a.GapSeconds + m.GapSeconds,
                SampleCount = a.SampleCount + m.SampleCount,
                MaxW = Math.Max(a.MaxW, m.MaxW),
                MeasuredSeconds = a.MeasuredSeconds + m.MeasuredSeconds,
                CalibratedSeconds = a.CalibratedSeconds + m.CalibratedSeconds,
                EstimatedSeconds = a.EstimatedSeconds + m.EstimatedSeconds,
            };
        }
        return a with { AvgW = Average(a.EnergyWh, a.OnSeconds) };
    }

    private static double Average(double wh, double onSeconds) => onSeconds > 0 ? wh / (onSeconds / 3600.0) : 0;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Core.Tests --filter DownsamplerTests`
Expected: `Passed! - Failed: 0, Passed: 7`.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.Core/Aggregate.cs src/PowerLedger.Core/Downsampler.cs tests/PowerLedger.Core.Tests/DownsamplerTests.cs
git commit -m "Add Aggregate and Downsampler with energy conservation"
```

---

### Task 9: Tariffs, cost, CO₂ and everyday comparisons

**Files:**
- Create: `src/PowerLedger.Core/Tariff.cs`
- Create: `src/PowerLedger.Core/TariffSchedule.cs`
- Create: `src/PowerLedger.Core/Co2.cs`
- Create: `src/PowerLedger.Core/Comparisons.cs`
- Test: `tests/PowerLedger.Core.Tests/TariffScheduleTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class TariffScheduleTests
{
    private static readonly DateTimeOffset Jan = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Jun = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TariffSchedule Schedule = new([
        new Tariff(Jun, 0.20m, "USD"),
        new Tariff(Jan, 0.17m, "USD"),
    ]);

    [Fact]
    public void At_picks_the_latest_tariff_effective_on_or_before_the_instant()
    {
        Schedule.At(Jun.AddDays(10))!.PricePerKwh.ShouldBe(0.20m);
        Schedule.At(Jun)!.PricePerKwh.ShouldBe(0.20m);
        Schedule.At(Jun.AddSeconds(-1))!.PricePerKwh.ShouldBe(0.17m);
    }

    [Fact]
    public void Energy_before_the_first_tariff_uses_the_first_tariff()
        => Schedule.At(Jan.AddYears(-1))!.PricePerKwh.ShouldBe(0.17m);

    [Fact]
    public void An_empty_schedule_has_no_tariff_and_zero_cost()
    {
        var empty = new TariffSchedule([]);
        empty.At(Jun).ShouldBeNull();
        empty.Cost([(Jun, 1000)]).ShouldBe(0m);
        empty.Currency.ShouldBeNull();
    }

    [Fact]
    public void Cost_applies_the_rate_in_force_for_each_slice()
    {
        var cost = Schedule.Cost([(Jan.AddDays(3), 500), (Jun.AddDays(3), 500)]);
        cost.ShouldBe(0.5m * 0.17m + 0.5m * 0.20m);
        Schedule.Currency.ShouldBe("USD");
    }

    [Fact]
    public void Cost_is_exact_decimal_arithmetic()
        => Schedule.Cost([(Jan, 1234)]).ShouldBe(0.20978m);

    [Fact]
    public void Co2_and_comparisons()
    {
        Co2.Kg(0.284, 0.38).ShouldBe(0.10792, 1e-9);
        Co2.DefaultKgPerKwh.ShouldBe(0.40);
        Comparisons.LedBulbHours(1.0).ShouldBe(100, 1e-9);
        Comparisons.PhoneCharges(1.0).ShouldBe(1000.0 / 15, 1e-9);
        Comparisons.EvKm(1.0).ShouldBe(1 / 0.18, 1e-9);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Core.Tests --filter TariffScheduleTests`
Expected: build error, `TariffSchedule` not found.

- [ ] **Step 3: Write the implementation**

`src/PowerLedger.Core/Tariff.cs`
```csharp
namespace PowerLedger.Core;

/// <param name="Currency">ISO 4217 code, e.g. "USD".</param>
public sealed record Tariff(DateTimeOffset EffectiveFrom, decimal PricePerKwh, string Currency);
```

`src/PowerLedger.Core/TariffSchedule.cs`
```csharp
namespace PowerLedger.Core;

/// <summary>Spec §7: cost is computed at query time from the tariff in force when the energy was used.</summary>
public sealed class TariffSchedule
{
    private readonly List<Tariff> _tariffs;

    public TariffSchedule(IEnumerable<Tariff> tariffs)
        => _tariffs = tariffs.OrderBy(t => t.EffectiveFrom).ToList();

    public string? Currency => _tariffs.Count == 0 ? null : _tariffs[^1].Currency;

    /// <summary>The latest tariff effective on or before <paramref name="at"/>; energy before the first tariff uses the first one.</summary>
    public Tariff? At(DateTimeOffset at)
    {
        Tariff? best = null;
        foreach (var t in _tariffs)
        {
            if (t.EffectiveFrom <= at) best = t;
            else break;
        }
        return best ?? (_tariffs.Count > 0 ? _tariffs[0] : null);
    }

    public decimal Cost(IEnumerable<(DateTimeOffset Start, double Wh)> energy)
    {
        decimal total = 0;
        foreach (var (start, wh) in energy)
        {
            if (At(start) is not { } tariff) continue;
            total += (decimal)wh / 1000m * tariff.PricePerKwh;
        }
        return total;
    }
}
```

`src/PowerLedger.Core/Co2.cs`
```csharp
namespace PowerLedger.Core;

public static class Co2
{
    /// <summary>World-average grid intensity, used when no country factor is chosen.</summary>
    public const double DefaultKgPerKwh = 0.40;

    public static double Kg(double kwh, double kgPerKwh) => kwh * kgPerKwh;
}
```

`src/PowerLedger.Core/Comparisons.cs`
```csharp
namespace PowerLedger.Core;

/// <summary>Everyday equivalents for a kWh figure (spec §9).</summary>
public static class Comparisons
{
    public const double LedBulbW = 10;
    public const double PhoneChargeWh = 15;
    public const double EvKwhPerKm = 0.18;

    public static double LedBulbHours(double kwh) => kwh * 1000 / LedBulbW;
    public static double PhoneCharges(double kwh) => kwh * 1000 / PhoneChargeWh;
    public static double EvKm(double kwh) => kwh / EvKwhPerKm;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Core.Tests --filter TariffScheduleTests`
Expected: `Passed! - Failed: 0, Passed: 6`.

- [ ] **Step 5: Run the whole Core suite and commit**

Run: `dotnet test tests/PowerLedger.Core.Tests`
Expected: `Passed! - Failed: 0, Passed: 56`.

```bash
git add src/PowerLedger.Core tests/PowerLedger.Core.Tests
git commit -m "Add tariff schedule, cost, CO2 and comparison math"
```

---

### Task 10: Storage foundation — SqliteDatabase, Migrator, Schema V1

**Files:**
- Create: `src/PowerLedger.Storage/Schema.cs`
- Create: `src/PowerLedger.Storage/Migrator.cs`
- Create: `src/PowerLedger.Storage/SqliteDatabase.cs`
- Create: `src/PowerLedger.Storage/Rows.cs`
- Create: `tests/PowerLedger.Storage.Tests/TestDatabase.cs`
- Test: `tests/PowerLedger.Storage.Tests/SqliteDatabaseTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/PowerLedger.Storage.Tests/TestDatabase.cs`
```csharp
using PowerLedger.Storage;

namespace PowerLedger.Storage.Tests;

/// <summary>A fresh migrated database in a temp file, deleted on dispose (WAL needs a real file).</summary>
internal sealed class TestDatabase : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"powerledger-test-{Guid.NewGuid():N}.db");
    public SqliteDatabase Db { get; }

    public TestDatabase() => Db = SqliteDatabase.OpenAndMigrate(Path);

    public void Dispose()
    {
        Db.Dispose();
        foreach (var f in new[] { Path, Path + "-wal", Path + "-shm", Path + ".bak" })
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }
}
```

`tests/PowerLedger.Storage.Tests/SqliteDatabaseTests.cs`
```csharp
using Microsoft.Data.Sqlite;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Storage.Tests;

public class SqliteDatabaseTests
{
    [Fact]
    public void A_new_database_is_migrated_to_the_latest_version_in_wal_mode()
    {
        using var t = new TestDatabase();
        using var c = t.Db.Open();
        Migrator.CurrentVersion(c).ShouldBe(Migrator.LatestVersion);
        Scalar<string>(c, "PRAGMA journal_mode").ShouldBe("wal");
        Scalar<long>(c, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('samples_raw','samples_1m','samples_1h','sessions','tariffs','calibration','hardware_inventory','settings')").ShouldBe(8);
    }

    [Fact]
    public void Opening_twice_does_not_re_run_migrations()
    {
        using var t = new TestDatabase();
        using var again = SqliteDatabase.OpenAndMigrate(t.Path);
        using var c = again.Open();
        Scalar<long>(c, "SELECT COUNT(*) FROM schema_version").ShouldBe(Migrator.LatestVersion);
    }

    [Fact]
    public void A_read_only_handle_can_read_while_a_writer_holds_a_transaction()
    {
        using var t = new TestDatabase();
        using var writer = t.Db.Open();
        using var tx = writer.BeginTransaction();
        Exec(writer, "INSERT INTO settings(key, value) VALUES ('a', '1')");

        using var readerDb = new SqliteDatabase(t.Path, readOnly: true);
        using var reader = readerDb.Open();
        Scalar<long>(reader, "SELECT COUNT(*) FROM settings").ShouldBe(0);
        tx.Commit();
        Scalar<long>(reader, "SELECT COUNT(*) FROM settings").ShouldBe(1);
    }

    private static T Scalar<T>(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T));
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Storage.Tests --filter SqliteDatabaseTests`
Expected: build error, `SqliteDatabase` not found.

- [ ] **Step 3: Write the schema, migrator and database**

`src/PowerLedger.Storage/Schema.cs`
```csharp
namespace PowerLedger.Storage;

/// <summary>DDL per schema version. Never edit a shipped version; add a new one.</summary>
internal static class Schema
{
    public const string V1 = """
        CREATE TABLE samples_raw (
            ts_ms        INTEGER PRIMARY KEY,
            delta_s      REAL    NOT NULL,
            total_w      REAL    NOT NULL,
            quality      INTEGER NOT NULL,
            cpu_w        REAL    NOT NULL,
            gpu_w        REAL    NOT NULL,
            display_w    REAL    NOT NULL,
            ram_w        REAL    NOT NULL,
            storage_w    REAL    NOT NULL,
            board_w      REAL    NOT NULL,
            extras_w     REAL    NOT NULL,
            monitors_w   REAL    NOT NULL,
            psu_loss_w   REAL    NOT NULL,
            rest_w       REAL    NOT NULL,
            on_battery   INTEGER NOT NULL,
            display_on   INTEGER NOT NULL,
            user_idle    INTEGER NOT NULL,
            locked       INTEGER NOT NULL,
            cpu_load     REAL    NOT NULL,
            gpu_load     REAL,
            brightness   REAL,
            suspect      INTEGER NOT NULL
        );

        CREATE TABLE samples_1m (
            start_ms     INTEGER PRIMARY KEY,
            avg_w        REAL NOT NULL, max_w REAL NOT NULL,
            energy_wh    REAL NOT NULL, cpu_wh REAL NOT NULL, gpu_wh REAL NOT NULL, display_wh REAL NOT NULL, rest_wh REAL NOT NULL,
            idle_on_wh   REAL NOT NULL, idle_off_wh REAL NOT NULL,
            idle_on_s    REAL NOT NULL, idle_off_s REAL NOT NULL,
            on_s         REAL NOT NULL, battery_s REAL NOT NULL, gap_s REAL NOT NULL,
            sample_count INTEGER NOT NULL,
            measured_s   REAL NOT NULL, calibrated_s REAL NOT NULL, estimated_s REAL NOT NULL
        );

        CREATE TABLE samples_1h (
            start_ms     INTEGER PRIMARY KEY,
            avg_w        REAL NOT NULL, max_w REAL NOT NULL,
            energy_wh    REAL NOT NULL, cpu_wh REAL NOT NULL, gpu_wh REAL NOT NULL, display_wh REAL NOT NULL, rest_wh REAL NOT NULL,
            idle_on_wh   REAL NOT NULL, idle_off_wh REAL NOT NULL,
            idle_on_s    REAL NOT NULL, idle_off_s REAL NOT NULL,
            on_s         REAL NOT NULL, battery_s REAL NOT NULL, gap_s REAL NOT NULL,
            sample_count INTEGER NOT NULL,
            measured_s   REAL NOT NULL, calibrated_s REAL NOT NULL, estimated_s REAL NOT NULL
        );

        CREATE TABLE sessions (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            start_ms   INTEGER NOT NULL,
            end_ms     INTEGER,
            reason     TEXT    NOT NULL,
            end_reason TEXT
        );
        CREATE INDEX ix_sessions_start ON sessions(start_ms);

        CREATE TABLE tariffs (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            effective_from_ms INTEGER NOT NULL,
            price_micro       INTEGER NOT NULL,
            currency          TEXT    NOT NULL
        );

        CREATE TABLE calibration (
            inventory_hash TEXT    NOT NULL,
            bucket         INTEGER NOT NULL,
            baseline_w     REAL    NOT NULL,
            samples        INTEGER NOT NULL,
            updated_ms     INTEGER NOT NULL,
            PRIMARY KEY (inventory_hash, bucket)
        );

        CREATE TABLE hardware_inventory (
            hash        TEXT PRIMARY KEY,
            detected_ms INTEGER NOT NULL,
            json        TEXT    NOT NULL
        );

        CREATE TABLE settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;
}
```

`src/PowerLedger.Storage/Migrator.cs`
```csharp
using Microsoft.Data.Sqlite;

namespace PowerLedger.Storage;

public static class Migrator
{
    private static readonly IReadOnlyList<(int Version, string Sql)> Migrations = [(1, Schema.V1)];

    public static int LatestVersion => Migrations[^1].Version;

    public static int CurrentVersion(SqliteConnection c)
    {
        Exec(c, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL, applied_ms INTEGER NOT NULL)");
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Applies every migration newer than the stored version, each in its own transaction.</summary>
    public static void Apply(SqliteConnection c)
    {
        var current = CurrentVersion(c);
        foreach (var (version, sql) in Migrations)
        {
            if (version <= current) continue;
            using var tx = c.BeginTransaction();
            Exec(c, sql);
            Exec(c, $"INSERT INTO schema_version(version, applied_ms) VALUES ({version}, {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()})");
            tx.Commit();
        }
    }

    internal static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
```

`src/PowerLedger.Storage/SqliteDatabase.cs`
```csharp
using Microsoft.Data.Sqlite;

namespace PowerLedger.Storage;

/// <summary>Connection factory for one database file. WAL mode lets the App read while the Service writes.</summary>
public sealed class SqliteDatabase : IDisposable
{
    private readonly string _connectionString;
    private readonly bool _readOnly;

    public string Path { get; }

    public SqliteDatabase(string path, bool readOnly = false)
    {
        Path = path;
        _readOnly = readOnly;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    /// <summary>Opens (creating if needed), backs up before a schema upgrade, sets pragmas and migrates.</summary>
    public static SqliteDatabase OpenAndMigrate(string path)
    {
        var existedBefore = File.Exists(path) && new FileInfo(path).Length > 0;
        var db = new SqliteDatabase(path);
        using var c = db.Open();
        Migrator.Exec(c, "PRAGMA auto_vacuum = INCREMENTAL");   // only takes effect on a brand-new file; harmless otherwise
        if (existedBefore && Migrator.CurrentVersion(c) < Migrator.LatestVersion)
        {
            File.Copy(path, path + ".bak", overwrite: true);   // spec §7: backup once per schema version bump
        }
        Migrator.Apply(c);
        return db;
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        Migrator.Exec(c, "PRAGMA busy_timeout = 5000");
        if (!_readOnly)
        {
            Migrator.Exec(c, "PRAGMA journal_mode = WAL");
            Migrator.Exec(c, "PRAGMA synchronous = NORMAL");
            Migrator.Exec(c, "PRAGMA foreign_keys = ON");
        }
        return c;
    }

    public void Dispose() => SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
}
```

`src/PowerLedger.Storage/Rows.cs`
```csharp
using Microsoft.Data.Sqlite;

namespace PowerLedger.Storage;

/// <summary>Small helpers shared by the repositories.</summary>
internal static class Rows
{
    public static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();

    public static DateTimeOffset Time(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    public static long Micro(decimal price) => (long)decimal.Round(price * 1_000_000m);

    public static decimal Price(long micro) => micro / 1_000_000m;

    public static double? NullableDouble(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    public static void Add(SqliteCommand cmd, string name, object? value) => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Storage.Tests --filter SqliteDatabaseTests`
Expected: `Passed! - Failed: 0, Passed: 3`.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.Storage tests/PowerLedger.Storage.Tests
git commit -m "Add SQLite database, migrator and schema v1"
```

---

### Task 11: RawSampleRepository — batched insert, range read, purge

**Files:**
- Create: `src/PowerLedger.Storage/RawSampleRepository.cs`
- Create: `tests/PowerLedger.Storage.Tests/Fixtures.cs`
- Test: `tests/PowerLedger.Storage.Tests/RawSampleRepositoryTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/PowerLedger.Storage.Tests/Fixtures.cs`
```csharp
using PowerLedger.Contracts;
using PowerLedger.Core;

namespace PowerLedger.Storage.Tests;

internal static class Fixtures
{
    public static readonly DateTimeOffset T0 = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    public static Reading Reading(int second, double totalW = 30, Quality q = Quality.Measured, bool idle = false, bool displayOn = true, double delta = 1.0)
        => new(T0.AddSeconds(second), delta, totalW, q,
               new Components(Cpu: totalW * 0.4, Gpu: totalW * 0.1, Display: 4, Ram: 0, Storage: 0, Board: 0, Extras: 0, Monitors: 0, PsuLoss: 0, Rest: totalW * 0.5 - 4),
               OnBattery: q == Quality.Measured, DisplayOn: displayOn, UserIdle: idle, SessionLocked: false,
               CpuLoad: 0.3, GpuLoad: 0.2, Brightness: 0.6, Suspect: false);

    public static List<Reading> Minute(int startSecond, double totalW = 30, Quality q = Quality.Measured)
        => Enumerable.Range(startSecond, 60).Select(s => Reading(s, totalW, q)).ToList();
}
```

`tests/PowerLedger.Storage.Tests/RawSampleRepositoryTests.cs`
```csharp
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Storage.Tests;

public class RawSampleRepositoryTests
{
    [Fact]
    public void A_batch_round_trips_every_field()
    {
        using var t = new TestDatabase();
        var repo = new RawSampleRepository(t.Db);
        var batch = Fixtures.Minute(0);
        repo.InsertBatch(batch);

        var back = repo.Read(Fixtures.T0, Fixtures.T0.AddMinutes(1));
        back.Count.ShouldBe(60);
        back[0].ShouldBe(batch[0]);
        back[59].ShouldBe(batch[59]);
        repo.Count().ShouldBe(60);
    }

    [Fact]
    public void Read_is_half_open_and_ordered_by_time()
    {
        using var t = new TestDatabase();
        var repo = new RawSampleRepository(t.Db);
        repo.InsertBatch(Fixtures.Minute(0));
        var back = repo.Read(Fixtures.T0.AddSeconds(10), Fixtures.T0.AddSeconds(20));
        back.Count.ShouldBe(10);
        back.First().Timestamp.ShouldBe(Fixtures.T0.AddSeconds(10));
        back.Last().Timestamp.ShouldBe(Fixtures.T0.AddSeconds(19));
    }

    [Fact]
    public void Inserting_the_same_timestamp_twice_keeps_the_latest_row()
    {
        using var t = new TestDatabase();
        var repo = new RawSampleRepository(t.Db);
        repo.InsertBatch([Fixtures.Reading(0, totalW: 30)]);
        repo.InsertBatch([Fixtures.Reading(0, totalW: 31)]);
        repo.Read(Fixtures.T0, Fixtures.T0.AddSeconds(1)).Single().TotalW.ShouldBe(31);
    }

    [Fact]
    public void Purge_removes_rows_older_than_the_cutoff()
    {
        using var t = new TestDatabase();
        var repo = new RawSampleRepository(t.Db);
        repo.InsertBatch(Fixtures.Minute(0));
        repo.PurgeBefore(Fixtures.T0.AddSeconds(45)).ShouldBe(45);
        repo.Count().ShouldBe(15);
    }

    [Fact]
    public void An_empty_batch_is_a_no_op()
    {
        using var t = new TestDatabase();
        new RawSampleRepository(t.Db).InsertBatch([]);
        new RawSampleRepository(t.Db).Count().ShouldBe(0);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Storage.Tests --filter RawSampleRepositoryTests`
Expected: build error, `RawSampleRepository` not found.

- [ ] **Step 3: Write the repository**

`src/PowerLedger.Storage/RawSampleRepository.cs`
```csharp
using Microsoft.Data.Sqlite;
using PowerLedger.Contracts;
using PowerLedger.Core;

namespace PowerLedger.Storage;

public sealed class RawSampleRepository(SqliteDatabase db)
{
    private const string Columns =
        "ts_ms, delta_s, total_w, quality, cpu_w, gpu_w, display_w, ram_w, storage_w, board_w, extras_w, monitors_w, psu_loss_w, rest_w, " +
        "on_battery, display_on, user_idle, locked, cpu_load, gpu_load, brightness, suspect";

    /// <summary>Writes all readings in one transaction. Same timestamp replaces the earlier row.</summary>
    public void InsertBatch(IReadOnlyList<Reading> readings)
    {
        if (readings.Count == 0) return;
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"INSERT OR REPLACE INTO samples_raw ({Columns}) VALUES " +
            "($ts, $delta, $total, $quality, $cpu, $gpu, $display, $ram, $storage, $board, $extras, $monitors, $psu, $rest, " +
            "$onBattery, $displayOn, $userIdle, $locked, $cpuLoad, $gpuLoad, $brightness, $suspect)";
        var names = new[] { "$ts", "$delta", "$total", "$quality", "$cpu", "$gpu", "$display", "$ram", "$storage", "$board", "$extras", "$monitors", "$psu", "$rest", "$onBattery", "$displayOn", "$userIdle", "$locked", "$cpuLoad", "$gpuLoad", "$brightness", "$suspect" };
        foreach (var n in names) cmd.Parameters.Add(new SqliteParameter(n, null));

        foreach (var r in readings)
        {
            var p = r.Components;
            object?[] values = [Rows.Ms(r.Timestamp), r.DeltaSeconds, r.TotalW, (int)r.Quality, p.Cpu, p.Gpu, p.Display, p.Ram, p.Storage, p.Board, p.Extras, p.Monitors, p.PsuLoss, p.Rest,
                r.OnBattery ? 1 : 0, r.DisplayOn ? 1 : 0, r.UserIdle ? 1 : 0, r.SessionLocked ? 1 : 0, r.CpuLoad, r.GpuLoad, r.Brightness, r.Suspect ? 1 : 0];
            for (var i = 0; i < values.Length; i++) cmd.Parameters[i].Value = values[i] ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Readings with from ≤ timestamp &lt; to, oldest first.</summary>
    public List<Reading> Read(DateTimeOffset from, DateTimeOffset to)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM samples_raw WHERE ts_ms >= $from AND ts_ms < $to ORDER BY ts_ms";
        Rows.Add(cmd, "$from", Rows.Ms(from));
        Rows.Add(cmd, "$to", Rows.Ms(to));
        using var r = cmd.ExecuteReader();
        var list = new List<Reading>();
        while (r.Read())
        {
            var parts = new Components(r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), r.GetDouble(7), r.GetDouble(8), r.GetDouble(9), r.GetDouble(10), r.GetDouble(11), r.GetDouble(12), r.GetDouble(13));
            list.Add(new Reading(Rows.Time(r.GetInt64(0)), r.GetDouble(1), r.GetDouble(2), (Quality)r.GetInt32(3), parts,
                r.GetInt32(14) == 1, r.GetInt32(15) == 1, r.GetInt32(16) == 1, r.GetInt32(17) == 1,
                r.GetDouble(18), Rows.NullableDouble(r, 19), Rows.NullableDouble(r, 20), r.GetInt32(21) == 1));
        }
        return list;
    }

    public int PurgeBefore(DateTimeOffset cutoff)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM samples_raw WHERE ts_ms < $cutoff";
        Rows.Add(cmd, "$cutoff", Rows.Ms(cutoff));
        return cmd.ExecuteNonQuery();
    }

    public long Count()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM samples_raw";
        return (long)cmd.ExecuteScalar()!;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Storage.Tests --filter RawSampleRepositoryTests`
Expected: `Passed! - Failed: 0, Passed: 5`.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.Storage/RawSampleRepository.cs tests/PowerLedger.Storage.Tests
git commit -m "Add RawSampleRepository with batched writes"
```

---

### Task 12: AggregateRepository — minute and hour rows

**Files:**
- Create: `src/PowerLedger.Storage/AggregateRepository.cs`
- Test: `tests/PowerLedger.Storage.Tests/AggregateRepositoryTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Storage.Tests;

public class AggregateRepositoryTests
{
    private static Aggregate Minute(int index, double watts = 30, Quality q = Quality.Measured)
        => Downsampler.ToMinute(Fixtures.T0.AddMinutes(index), Fixtures.Minute(index * 60, watts, q));

    [Fact]
    public void Minute_rows_round_trip_every_field()
    {
        using var t = new TestDatabase();
        var repo = new AggregateRepository(t.Db);
        var m = Minute(0);
        repo.UpsertMinute(m);
        repo.ReadMinutes(Fixtures.T0, Fixtures.T0.AddMinutes(1)).Single().ShouldBe(m);
    }

    [Fact]
    public void Reads_are_half_open_ordered_and_per_table()
    {
        using var t = new TestDatabase();
        var repo = new AggregateRepository(t.Db);
        for (var i = 0; i < 5; i++) repo.UpsertMinute(Minute(i));
        repo.UpsertHour(Downsampler.ToHour(Fixtures.T0, [Minute(0), Minute(1)]));

        var minutes = repo.ReadMinutes(Fixtures.T0.AddMinutes(1), Fixtures.T0.AddMinutes(4));
        minutes.Select(m => m.Start).ShouldBe([Fixtures.T0.AddMinutes(1), Fixtures.T0.AddMinutes(2), Fixtures.T0.AddMinutes(3)]);
        repo.ReadHours(Fixtures.T0, Fixtures.T0.AddHours(1)).Single().EnergyWh.ShouldBe(1.0, 1e-9);
    }

    [Fact]
    public void Upsert_replaces_an_existing_row()
    {
        using var t = new TestDatabase();
        var repo = new AggregateRepository(t.Db);
        repo.UpsertMinute(Minute(0, watts: 30));
        repo.UpsertMinute(Minute(0, watts: 60));
        repo.ReadMinutes(Fixtures.T0, Fixtures.T0.AddMinutes(1)).Single().AvgW.ShouldBe(60, 1e-9);
    }

    [Fact]
    public void Last_starts_are_null_when_empty_and_the_newest_row_otherwise()
    {
        using var t = new TestDatabase();
        var repo = new AggregateRepository(t.Db);
        repo.LastMinuteStart().ShouldBeNull();
        repo.LastHourStart().ShouldBeNull();
        repo.UpsertMinute(Minute(3));
        repo.UpsertMinute(Minute(1));
        repo.LastMinuteStart().ShouldBe(Fixtures.T0.AddMinutes(3));
    }

    [Fact]
    public void Purge_removes_old_minutes_only()
    {
        using var t = new TestDatabase();
        var repo = new AggregateRepository(t.Db);
        for (var i = 0; i < 5; i++) repo.UpsertMinute(Minute(i));
        repo.PurgeMinutesBefore(Fixtures.T0.AddMinutes(3)).ShouldBe(3);
        repo.ReadMinutes(Fixtures.T0, Fixtures.T0.AddHours(1)).Count.ShouldBe(2);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Storage.Tests --filter AggregateRepositoryTests`
Expected: build error, `AggregateRepository` not found.

- [ ] **Step 3: Write the repository**

`src/PowerLedger.Storage/AggregateRepository.cs`
```csharp
using Microsoft.Data.Sqlite;
using PowerLedger.Core;

namespace PowerLedger.Storage;

public sealed class AggregateRepository(SqliteDatabase db)
{
    private const string MinuteTable = "samples_1m";
    private const string HourTable = "samples_1h";
    private const string Columns =
        "start_ms, avg_w, max_w, energy_wh, cpu_wh, gpu_wh, display_wh, rest_wh, idle_on_wh, idle_off_wh, " +
        "idle_on_s, idle_off_s, on_s, battery_s, gap_s, sample_count, measured_s, calibrated_s, estimated_s";

    public void UpsertMinute(Aggregate a) => Upsert(MinuteTable, a);

    public void UpsertHour(Aggregate a) => Upsert(HourTable, a);

    public List<Aggregate> ReadMinutes(DateTimeOffset from, DateTimeOffset to) => Read(MinuteTable, from, to);

    public List<Aggregate> ReadHours(DateTimeOffset from, DateTimeOffset to) => Read(HourTable, from, to);

    public DateTimeOffset? LastMinuteStart() => LastStart(MinuteTable);

    public DateTimeOffset? LastHourStart() => LastStart(HourTable);

    public int PurgeMinutesBefore(DateTimeOffset cutoff)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"DELETE FROM {MinuteTable} WHERE start_ms < $cutoff";
        Rows.Add(cmd, "$cutoff", Rows.Ms(cutoff));
        return cmd.ExecuteNonQuery();
    }

    private void Upsert(string table, Aggregate a)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"INSERT OR REPLACE INTO {table} ({Columns}) VALUES " +
            "($start, $avg, $max, $energy, $cpu, $gpu, $display, $rest, $idleOn, $idleOff, $idleOnS, $idleOffS, $on, $battery, $gap, $count, $measured, $calibrated, $estimated)";
        Rows.Add(cmd, "$start", Rows.Ms(a.Start));
        Rows.Add(cmd, "$avg", a.AvgW);
        Rows.Add(cmd, "$max", a.MaxW);
        Rows.Add(cmd, "$energy", a.EnergyWh);
        Rows.Add(cmd, "$cpu", a.CpuWh);
        Rows.Add(cmd, "$gpu", a.GpuWh);
        Rows.Add(cmd, "$display", a.DisplayWh);
        Rows.Add(cmd, "$rest", a.RestWh);
        Rows.Add(cmd, "$idleOn", a.IdleOnWh);
        Rows.Add(cmd, "$idleOff", a.IdleOffWh);
        Rows.Add(cmd, "$idleOnS", a.IdleOnSeconds);
        Rows.Add(cmd, "$idleOffS", a.IdleOffSeconds);
        Rows.Add(cmd, "$on", a.OnSeconds);
        Rows.Add(cmd, "$battery", a.BatterySeconds);
        Rows.Add(cmd, "$gap", a.GapSeconds);
        Rows.Add(cmd, "$count", a.SampleCount);
        Rows.Add(cmd, "$measured", a.MeasuredSeconds);
        Rows.Add(cmd, "$calibrated", a.CalibratedSeconds);
        Rows.Add(cmd, "$estimated", a.EstimatedSeconds);
        cmd.ExecuteNonQuery();
    }

    private List<Aggregate> Read(string table, DateTimeOffset from, DateTimeOffset to)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM {table} WHERE start_ms >= $from AND start_ms < $to ORDER BY start_ms";
        Rows.Add(cmd, "$from", Rows.Ms(from));
        Rows.Add(cmd, "$to", Rows.Ms(to));
        using var r = cmd.ExecuteReader();
        var list = new List<Aggregate>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    private DateTimeOffset? LastStart(string table)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT MAX(start_ms) FROM {table}";
        var v = cmd.ExecuteScalar();
        return v is long ms ? Rows.Time(ms) : null;
    }

    internal static Aggregate Map(SqliteDataReader r) => new(
        Rows.Time(r.GetInt64(0)), r.GetDouble(1), r.GetDouble(2),
        r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), r.GetDouble(7),
        r.GetDouble(8), r.GetDouble(9),
        r.GetDouble(10), r.GetDouble(11),
        r.GetDouble(12), r.GetDouble(13), r.GetDouble(14),
        r.GetInt32(15),
        r.GetDouble(16), r.GetDouble(17), r.GetDouble(18));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Storage.Tests --filter AggregateRepositoryTests`
Expected: `Passed! - Failed: 0, Passed: 5`.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.Storage/AggregateRepository.cs tests/PowerLedger.Storage.Tests/AggregateRepositoryTests.cs
git commit -m "Add AggregateRepository for minute and hour rows"
```

---

### Task 13: Sessions, tariffs and settings repositories

**Files:**
- Create: `src/PowerLedger.Storage/SessionRepository.cs`
- Create: `src/PowerLedger.Storage/TariffRepository.cs`
- Create: `src/PowerLedger.Storage/SettingsRepository.cs`
- Test: `tests/PowerLedger.Storage.Tests/SmallRepositoriesTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Storage.Tests;

public class SmallRepositoriesTests
{
    [Fact]
    public void Sessions_open_close_and_list_by_overlap()
    {
        using var t = new TestDatabase();
        var repo = new SessionRepository(t.Db);
        var id = repo.Open(SessionReason.Boot, Fixtures.T0);
        repo.OpenSession()!.Id.ShouldBe(id);
        repo.OpenSession()!.Reason.ShouldBe(SessionReason.Boot);

        repo.Close(id, Fixtures.T0.AddHours(2), SessionReason.Suspend);
        repo.OpenSession().ShouldBeNull();
        var second = repo.Open(SessionReason.Resume, Fixtures.T0.AddHours(5));

        var overlapping = repo.List(Fixtures.T0.AddHours(1), Fixtures.T0.AddHours(6));
        overlapping.Select(s => s.Id).ShouldBe([id, second]);
        repo.List(Fixtures.T0.AddHours(2), Fixtures.T0.AddHours(5)).ShouldBeEmpty();
        overlapping[0].End.ShouldBe(Fixtures.T0.AddHours(2));
        overlapping[0].EndReason.ShouldBe(SessionReason.Suspend);
        overlapping[1].End.ShouldBeNull();
        overlapping[1].EndReason.ShouldBeNull();
    }

    [Fact]
    public void Dangling_sessions_from_a_crash_can_be_closed_in_one_call()
    {
        using var t = new TestDatabase();
        var repo = new SessionRepository(t.Db);
        repo.Open(SessionReason.ServiceStart, Fixtures.T0);
        repo.CloseAllOpen(Fixtures.T0.AddMinutes(10)).ShouldBe(1);
        repo.CloseAllOpen(Fixtures.T0.AddMinutes(11)).ShouldBe(0);
        var closed = repo.List(Fixtures.T0, Fixtures.T0.AddHours(1)).Single();
        closed.End.ShouldBe(Fixtures.T0.AddMinutes(10));
        closed.EndReason.ShouldBe(SessionReason.CrashRecovered);
    }

    [Fact]
    public void Tariffs_keep_six_decimals_and_come_back_sorted()
    {
        using var t = new TestDatabase();
        var repo = new TariffRepository(t.Db);
        repo.Add(new Tariff(Fixtures.T0.AddMonths(1), 0.20m, "USD"));
        repo.Add(new Tariff(Fixtures.T0, 0.123456m, "USD"));
        var all = repo.All();
        all.Select(x => x.PricePerKwh).ShouldBe([0.123456m, 0.20m]);
        repo.Schedule().At(Fixtures.T0.AddDays(1))!.PricePerKwh.ShouldBe(0.123456m);
    }

    [Fact]
    public void Settings_are_a_simple_key_value_store()
    {
        using var t = new TestDatabase();
        var repo = new SettingsRepository(t.Db);
        repo.Get("tariff.currency").ShouldBeNull();
        repo.Set("tariff.currency", "USD");
        repo.Set("tariff.currency", "EUR");
        repo.Get("tariff.currency").ShouldBe("EUR");
        repo.Set("idle.threshold", "300");
        repo.All().ShouldBe(new Dictionary<string, string> { ["tariff.currency"] = "EUR", ["idle.threshold"] = "300" });
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Storage.Tests --filter SmallRepositoriesTests`
Expected: build error, `SessionRepository` not found.

- [ ] **Step 3: Write the repositories**

`src/PowerLedger.Storage/SessionRepository.cs`
```csharp
using PowerLedger.Contracts;

namespace PowerLedger.Storage;

/// <param name="Reason">Why the session started.</param>
/// <param name="EndReason">Why it ended; null while the session is open.</param>
public sealed record Session(long Id, DateTimeOffset Start, DateTimeOffset? End, SessionReason Reason, SessionReason? EndReason);

/// <summary>Power-state timeline. A session is open from boot/resume until suspend/shutdown/stop.</summary>
public sealed class SessionRepository(SqliteDatabase db)
{
    public long Open(SessionReason reason, DateTimeOffset start)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO sessions(start_ms, end_ms, reason) VALUES ($start, NULL, $reason); SELECT last_insert_rowid()";
        Rows.Add(cmd, "$start", Rows.Ms(start));
        Rows.Add(cmd, "$reason", reason.ToString());
        return (long)cmd.ExecuteScalar()!;
    }

    public void Close(long id, DateTimeOffset end, SessionReason reason)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET end_ms = $end, end_reason = $reason WHERE id = $id";
        Rows.Add(cmd, "$end", Rows.Ms(end));
        Rows.Add(cmd, "$reason", reason.ToString());
        Rows.Add(cmd, "$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Closes every session still open (after a crash) with reason CrashRecovered and returns how many there were.</summary>
    public int CloseAllOpen(DateTimeOffset end)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET end_ms = $end, end_reason = $reason WHERE end_ms IS NULL";
        Rows.Add(cmd, "$end", Rows.Ms(end));
        Rows.Add(cmd, "$reason", SessionReason.CrashRecovered.ToString());
        return cmd.ExecuteNonQuery();
    }

    public Session? OpenSession()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, start_ms, end_ms, reason, end_reason FROM sessions WHERE end_ms IS NULL ORDER BY start_ms DESC LIMIT 1";
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    /// <summary>Sessions overlapping [from, to).</summary>
    public List<Session> List(DateTimeOffset from, DateTimeOffset to)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, start_ms, end_ms, reason, end_reason FROM sessions WHERE start_ms < $to AND (end_ms IS NULL OR end_ms > $from) ORDER BY start_ms";
        Rows.Add(cmd, "$from", Rows.Ms(from));
        Rows.Add(cmd, "$to", Rows.Ms(to));
        using var r = cmd.ExecuteReader();
        var list = new List<Session>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    private static Session Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        r.GetInt64(0), Rows.Time(r.GetInt64(1)),
        r.IsDBNull(2) ? null : Rows.Time(r.GetInt64(2)),
        Enum.Parse<SessionReason>(r.GetString(3)),
        r.IsDBNull(4) ? null : Enum.Parse<SessionReason>(r.GetString(4)));
}
```

`src/PowerLedger.Storage/TariffRepository.cs`
```csharp
using PowerLedger.Core;

namespace PowerLedger.Storage;

public sealed class TariffRepository(SqliteDatabase db)
{
    public void Add(Tariff tariff)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO tariffs(effective_from_ms, price_micro, currency) VALUES ($from, $price, $currency)";
        Rows.Add(cmd, "$from", Rows.Ms(tariff.EffectiveFrom));
        Rows.Add(cmd, "$price", Rows.Micro(tariff.PricePerKwh));
        Rows.Add(cmd, "$currency", tariff.Currency);
        cmd.ExecuteNonQuery();
    }

    public List<Tariff> All()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT effective_from_ms, price_micro, currency FROM tariffs ORDER BY effective_from_ms, id";
        using var r = cmd.ExecuteReader();
        var list = new List<Tariff>();
        while (r.Read()) list.Add(new Tariff(Rows.Time(r.GetInt64(0)), Rows.Price(r.GetInt64(1)), r.GetString(2)));
        return list;
    }

    public TariffSchedule Schedule() => new(All());
}
```

`src/PowerLedger.Storage/SettingsRepository.cs`
```csharp
namespace PowerLedger.Storage;

/// <summary>Service-owned settings. Keys are dotted names such as "tariff.currency"; values are strings.</summary>
public sealed class SettingsRepository(SqliteDatabase db)
{
    public string? Get(string key)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key";
        Rows.Add(cmd, "$key", key);
        return cmd.ExecuteScalar() as string;
    }

    public void Set(string key, string value)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO settings(key, value) VALUES ($key, $value)";
        Rows.Add(cmd, "$key", key);
        Rows.Add(cmd, "$value", value);
        cmd.ExecuteNonQuery();
    }

    public Dictionary<string, string> All()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM settings";
        using var r = cmd.ExecuteReader();
        var dict = new Dictionary<string, string>();
        while (r.Read()) dict[r.GetString(0)] = r.GetString(1);
        return dict;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Storage.Tests --filter SmallRepositoriesTests`
Expected: `Passed! - Failed: 0, Passed: 4`.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.Storage tests/PowerLedger.Storage.Tests/SmallRepositoriesTests.cs
git commit -m "Add session, tariff and settings repositories"
```

---

### Task 14: Calibration, inventory and retention

**Files:**
- Create: `src/PowerLedger.Storage/CalibrationRepository.cs`
- Create: `src/PowerLedger.Storage/InventoryRepository.cs`
- Create: `src/PowerLedger.Storage/RetentionJob.cs`
- Test: `tests/PowerLedger.Storage.Tests/CalibrationInventoryRetentionTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using PowerLedger.Core;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Storage.Tests;

public class CalibrationInventoryRetentionTests
{
    [Fact]
    public void Calibration_state_round_trips_per_inventory_hash_and_save_replaces()
    {
        using var t = new TestDatabase();
        var repo = new CalibrationRepository(t.Db);
        repo.Load("abc").Buckets.ShouldBeEmpty();

        repo.Save("abc", new CalibrationState([new BucketState(6, 9.0, 400), new BucketState(-1, 7.5, 320)]), Fixtures.T0);
        repo.Save("other", new CalibrationState([new BucketState(6, 20.0, 5)]), Fixtures.T0);
        var back = repo.Load("abc");
        back.Buckets.Count.ShouldBe(2);
        back.Buckets.Single(b => b.Bucket == 6).ShouldBe(new BucketState(6, 9.0, 400));

        repo.Save("abc", new CalibrationState([new BucketState(6, 9.5, 401)]), Fixtures.T0.AddMinutes(1));
        repo.Load("abc").Buckets.ShouldBe([new BucketState(6, 9.5, 401)]);

        repo.Clear("abc");
        repo.Load("abc").Buckets.ShouldBeEmpty();
        repo.Load("other").Buckets.Count.ShouldBe(1);
    }

    [Fact]
    public void Inventory_keeps_the_first_detection_time_per_hash_and_reports_the_latest()
    {
        using var t = new TestDatabase();
        var repo = new InventoryRepository(t.Db);
        repo.Latest().ShouldBeNull();
        repo.Upsert(new InventoryRecord("h1", Fixtures.T0, "{\"cpu\":\"i7\"}"));
        repo.Upsert(new InventoryRecord("h1", Fixtures.T0.AddDays(1), "{\"cpu\":\"i7\"}"));
        repo.Upsert(new InventoryRecord("h2", Fixtures.T0.AddHours(1), "{\"cpu\":\"i9\"}"));
        repo.All().Count.ShouldBe(2);
        repo.All().Single(r => r.Hash == "h1").DetectedAt.ShouldBe(Fixtures.T0);
        repo.Latest()!.Hash.ShouldBe("h2");
    }

    [Fact]
    public void Retention_purges_raw_and_minute_rows_by_age()
    {
        using var t = new TestDatabase();
        var raw = new RawSampleRepository(t.Db);
        var agg = new AggregateRepository(t.Db);
        var now = Fixtures.T0;
        raw.InsertBatch([Fixtures.Reading(0) with { Timestamp = now.AddHours(-49) }, Fixtures.Reading(0) with { Timestamp = now.AddHours(-47) }]);
        agg.UpsertMinute(Aggregate.Empty(now.AddYears(-3)));
        agg.UpsertMinute(Aggregate.Empty(now.AddDays(-1)));
        agg.UpsertHour(Aggregate.Empty(now.AddYears(-3)));

        var result = new RetentionJob(t.Db).Run(now, new RetentionOptions());
        result.RawDeleted.ShouldBe(1);
        result.MinutesDeleted.ShouldBe(1);
        raw.Count().ShouldBe(1);
        agg.ReadHours(now.AddYears(-4), now).Count.ShouldBe(1);
    }

    [Fact]
    public void Retention_options_are_clamped_to_the_spec_bounds()
    {
        RetentionOptions.Clamped(rawHours: 1, historyYears: 99).ShouldBe(new RetentionOptions(24, 5));
        RetentionOptions.Clamped(rawHours: 500, historyYears: 0).ShouldBe(new RetentionOptions(168, 1));
        RetentionOptions.Clamped(72, 3).ShouldBe(new RetentionOptions(72, 3));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Storage.Tests --filter CalibrationInventoryRetentionTests`
Expected: build error, `CalibrationRepository` not found.

- [ ] **Step 3: Write the implementation**

`src/PowerLedger.Storage/CalibrationRepository.cs`
```csharp
using PowerLedger.Core;

namespace PowerLedger.Storage;

/// <summary>Learned baselines, keyed by hardware inventory hash so a hardware change never reuses stale numbers.</summary>
public sealed class CalibrationRepository(SqliteDatabase db)
{
    public void Save(string inventoryHash, CalibrationState state, DateTimeOffset now)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using (var del = c.CreateCommand())
        {
            del.CommandText = "DELETE FROM calibration WHERE inventory_hash = $hash";
            Rows.Add(del, "$hash", inventoryHash);
            del.ExecuteNonQuery();
        }
        using (var ins = c.CreateCommand())
        {
            ins.CommandText = "INSERT INTO calibration(inventory_hash, bucket, baseline_w, samples, updated_ms) VALUES ($hash, $bucket, $baseline, $samples, $updated)";
            var hash = ins.Parameters.Add("$hash", Microsoft.Data.Sqlite.SqliteType.Text);
            var bucket = ins.Parameters.Add("$bucket", Microsoft.Data.Sqlite.SqliteType.Integer);
            var baseline = ins.Parameters.Add("$baseline", Microsoft.Data.Sqlite.SqliteType.Real);
            var samples = ins.Parameters.Add("$samples", Microsoft.Data.Sqlite.SqliteType.Integer);
            var updated = ins.Parameters.Add("$updated", Microsoft.Data.Sqlite.SqliteType.Integer);
            foreach (var b in state.Buckets)
            {
                hash.Value = inventoryHash;
                bucket.Value = b.Bucket;
                baseline.Value = b.BaselineW;
                samples.Value = b.Samples;
                updated.Value = Rows.Ms(now);
                ins.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    public CalibrationState Load(string inventoryHash)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT bucket, baseline_w, samples FROM calibration WHERE inventory_hash = $hash ORDER BY bucket";
        Rows.Add(cmd, "$hash", inventoryHash);
        using var r = cmd.ExecuteReader();
        var buckets = new List<BucketState>();
        while (r.Read()) buckets.Add(new BucketState(r.GetInt32(0), r.GetDouble(1), r.GetInt32(2)));
        return new CalibrationState(buckets);
    }

    public void Clear(string inventoryHash)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM calibration WHERE inventory_hash = $hash";
        Rows.Add(cmd, "$hash", inventoryHash);
        cmd.ExecuteNonQuery();
    }
}
```

`src/PowerLedger.Storage/InventoryRepository.cs`
```csharp
namespace PowerLedger.Storage;

/// <param name="Hash">Stable hash of the detected hardware set (computed by the Sensors project in Plan B).</param>
/// <param name="Json">The detected inventory, serialised for display and diagnostics.</param>
public sealed record InventoryRecord(string Hash, DateTimeOffset DetectedAt, string Json);

public sealed class InventoryRepository(SqliteDatabase db)
{
    /// <summary>Inserts a new hardware set; an existing hash keeps its first detection time.</summary>
    public void Upsert(InventoryRecord record)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO hardware_inventory(hash, detected_ms, json) VALUES ($hash, $detected, $json)";
        Rows.Add(cmd, "$hash", record.Hash);
        Rows.Add(cmd, "$detected", Rows.Ms(record.DetectedAt));
        Rows.Add(cmd, "$json", record.Json);
        cmd.ExecuteNonQuery();
    }

    public InventoryRecord? Latest() => All().OrderByDescending(r => r.DetectedAt).FirstOrDefault();

    public List<InventoryRecord> All()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT hash, detected_ms, json FROM hardware_inventory ORDER BY detected_ms";
        using var r = cmd.ExecuteReader();
        var list = new List<InventoryRecord>();
        while (r.Read()) list.Add(new InventoryRecord(r.GetString(0), Rows.Time(r.GetInt64(1)), r.GetString(2)));
        return list;
    }
}
```

`src/PowerLedger.Storage/RetentionJob.cs`
```csharp
namespace PowerLedger.Storage;

/// <summary>Spec §7: raw 24–168 h (default 48), minute history 1–5 years (default 2). Hour rows are kept forever.</summary>
public sealed record RetentionOptions(int RawHours = 48, int HistoryYears = 2)
{
    public static RetentionOptions Clamped(int rawHours, int historyYears)
        => new(Math.Clamp(rawHours, 24, 168), Math.Clamp(historyYears, 1, 5));
}

public sealed record RetentionResult(int RawDeleted, int MinutesDeleted);

public sealed class RetentionJob(SqliteDatabase db)
{
    public RetentionResult Run(DateTimeOffset now, RetentionOptions options)
    {
        var raw = new RawSampleRepository(db).PurgeBefore(now.AddHours(-options.RawHours));
        var minutes = new AggregateRepository(db).PurgeMinutesBefore(now.AddYears(-options.HistoryYears));
        return new RetentionResult(raw, minutes);
    }

    /// <summary>Reclaims free pages a little at a time. Weekly is plenty; never run a full VACUUM.</summary>
    public void IncrementalVacuum(int pages = 2000)
    {
        using var c = db.Open();
        Migrator.Exec(c, $"PRAGMA incremental_vacuum({pages})");
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Storage.Tests --filter CalibrationInventoryRetentionTests`
Expected: `Passed! - Failed: 0, Passed: 4`.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.Storage tests/PowerLedger.Storage.Tests/CalibrationInventoryRetentionTests.cs
git commit -m "Add calibration, inventory repositories and retention job"
```

---

### Task 15: ReportQueries — range totals and daily buckets with cost at query time

**Files:**
- Create: `src/PowerLedger.Storage/RangeTotals.cs`
- Create: `src/PowerLedger.Storage/ReportQueries.cs`
- Test: `tests/PowerLedger.Storage.Tests/ReportQueriesTests.cs`

Series data for charts comes straight from `AggregateRepository.ReadMinutes` / `ReadHours`; no extra query is needed for that.

- [ ] **Step 1: Write the failing tests**

```csharp
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Storage.Tests;

public class ReportQueriesTests
{
    private static void SeedThreeHours(TestDatabase t)
    {
        var agg = new AggregateRepository(t.Db);
        for (var i = 0; i < 120; i++)
            agg.UpsertMinute(Downsampler.ToMinute(Fixtures.T0.AddMinutes(i), Fixtures.Minute(i * 60, 30, Quality.Measured)));
        for (var i = 120; i < 180; i++)
        {
            var readings = Enumerable.Range(i * 60, 60).Select(s => Fixtures.Reading(s, 60, Quality.Estimated, idle: true)).ToList();
            agg.UpsertMinute(Downsampler.ToMinute(Fixtures.T0.AddMinutes(i), readings));
        }
        new TariffRepository(t.Db).Add(new Tariff(Fixtures.T0, 0.20m, "USD"));
    }

    [Fact]
    public void Totals_over_a_short_range_come_from_minute_rows()
    {
        using var t = new TestDatabase();
        SeedThreeHours(t);
        var totals = new ReportQueries(t.Db).Totals(Fixtures.T0, Fixtures.T0.AddHours(4));

        totals.EnergyKwh.ShouldBe(0.12, 1e-9);            // 120 × 0.5 Wh + 60 × 1 Wh
        totals.Cost.ShouldBe(0.024m);
        totals.Currency.ShouldBe("USD");
        totals.AvgW.ShouldBe(40, 1e-9);                    // 120 Wh over 3 h on
        totals.PeakW.ShouldBe(60);
        totals.PeakAt.ShouldBe(Fixtures.T0.AddHours(2));
        totals.OnHours.ShouldBe(3, 1e-9);
        totals.IdleOnHours.ShouldBe(1, 1e-9);
        totals.IdleOffHours.ShouldBe(0);
        totals.AsleepHours.ShouldBe(1, 1e-9);              // 4 h range − 3 h on
        totals.IdleOnKwh.ShouldBe(0.06, 1e-9);
        totals.CpuKwh.ShouldBe(0.12 * 0.4, 1e-9);
        totals.DisplayKwh.ShouldBe(4.0 * 3 / 1000, 1e-9);
        totals.MeasuredShare.ShouldBe(2.0 / 3, 1e-9);
        totals.EstimatedShare.ShouldBe(1.0 / 3, 1e-9);
        totals.CalibratedShare.ShouldBe(0);
    }

    [Fact]
    public void Totals_over_a_long_range_come_from_hour_rows()
    {
        using var t = new TestDatabase();
        var agg = new AggregateRepository(t.Db);
        for (var d = 0; d < 5; d++)
            agg.UpsertHour(Downsampler.ToHour(Fixtures.T0.AddDays(d), [Downsampler.ToMinute(Fixtures.T0.AddDays(d), Fixtures.Minute(0, 100))]));
        agg.UpsertMinute(Downsampler.ToMinute(Fixtures.T0, Fixtures.Minute(0, 999)));   // must be ignored

        var totals = new ReportQueries(t.Db).Totals(Fixtures.T0, Fixtures.T0.AddDays(5));
        totals.EnergyKwh.ShouldBe(5 * 100.0 / 60 / 1000, 1e-9);
        totals.Cost.ShouldBe(0m);
        totals.Currency.ShouldBeNull();
    }

    [Fact]
    public void Cost_uses_the_tariff_in_force_when_the_energy_was_used()
    {
        using var t = new TestDatabase();
        SeedThreeHours(t);
        new TariffRepository(t.Db).Add(new Tariff(Fixtures.T0.AddHours(2), 0.50m, "USD"));
        new ReportQueries(t.Db).Totals(Fixtures.T0, Fixtures.T0.AddHours(3)).Cost.ShouldBe(0.06m * 0.20m + 0.06m * 0.50m);
    }

    [Fact]
    public void Daily_buckets_split_by_local_day()
    {
        using var t = new TestDatabase();
        SeedThreeHours(t);                                                       // 12:00–15:00 UTC on 8 Sep
        var plusTen = TimeZoneInfo.CreateCustomTimeZone("plus10", TimeSpan.FromHours(10), "plus10", "plus10");
        var days = new ReportQueries(t.Db).DailyBuckets(Fixtures.T0, Fixtures.T0.AddHours(4), plusTen);
        days.Count.ShouldBe(2);                                                  // 22:00–24:00 on 8 Sep, 00:00–01:00 on 9 Sep local
        days[0].Day.ShouldBe(new DateOnly(2026, 9, 8));
        days[0].EnergyKwh.ShouldBe(0.06, 1e-9);
        days[1].Day.ShouldBe(new DateOnly(2026, 9, 9));
        days[1].EnergyKwh.ShouldBe(0.06, 1e-9);
        days[1].Cost.ShouldBe(0.012m);
        days[1].PeakW.ShouldBe(60);
    }

    [Fact]
    public void An_empty_range_is_all_zeros()
    {
        using var t = new TestDatabase();
        var totals = new ReportQueries(t.Db).Totals(Fixtures.T0, Fixtures.T0.AddHours(1));
        totals.EnergyKwh.ShouldBe(0);
        totals.Cost.ShouldBe(0m);
        totals.PeakAt.ShouldBeNull();
        totals.AvgW.ShouldBe(0);
        totals.MeasuredShare.ShouldBe(0);
        totals.AsleepHours.ShouldBe(1, 1e-9);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Storage.Tests --filter ReportQueriesTests`
Expected: build error, `ReportQueries` not found.

- [ ] **Step 3: Write the queries**

`src/PowerLedger.Storage/RangeTotals.cs`
```csharp
namespace PowerLedger.Storage;

/// <summary>Everything the Report screen shows for one range. Asleep = range length minus on-time.</summary>
public sealed record RangeTotals(
    DateTimeOffset From, DateTimeOffset To,
    double EnergyKwh, decimal Cost, string? Currency,
    double AvgW, double PeakW, DateTimeOffset? PeakAt,
    double OnHours, double IdleOnHours, double IdleOffHours, double AsleepHours,
    double CpuKwh, double GpuKwh, double DisplayKwh, double RestKwh,
    double IdleOnKwh, double IdleOffKwh,
    double MeasuredShare, double CalibratedShare, double EstimatedShare);

public sealed record DayTotals(DateOnly Day, double EnergyKwh, decimal Cost, double OnHours, double PeakW, double IdleOnKwh, double IdleOffKwh);
```

`src/PowerLedger.Storage/ReportQueries.cs`
```csharp
using PowerLedger.Core;

namespace PowerLedger.Storage;

public sealed class ReportQueries(SqliteDatabase db)
{
    /// <summary>Ranges up to this length read minute rows; longer ranges read hour rows.</summary>
    public static readonly TimeSpan MinuteResolutionLimit = TimeSpan.FromDays(3);

    public RangeTotals Totals(DateTimeOffset from, DateTimeOffset to)
    {
        var rows = Load(from, to);
        var schedule = new TariffRepository(db).Schedule();

        double energy = 0, cpu = 0, gpu = 0, display = 0, rest = 0, idleOn = 0, idleOff = 0;
        double onS = 0, idleOnS = 0, idleOffS = 0, measuredS = 0, calibratedS = 0, estimatedS = 0, peak = 0;
        DateTimeOffset? peakAt = null;
        foreach (var r in rows)
        {
            energy += r.EnergyWh; cpu += r.CpuWh; gpu += r.GpuWh; display += r.DisplayWh; rest += r.RestWh;
            idleOn += r.IdleOnWh; idleOff += r.IdleOffWh;
            onS += r.OnSeconds; idleOnS += r.IdleOnSeconds; idleOffS += r.IdleOffSeconds;
            measuredS += r.MeasuredSeconds; calibratedS += r.CalibratedSeconds; estimatedS += r.EstimatedSeconds;
            if (r.MaxW > peak) { peak = r.MaxW; peakAt = r.Start; }
        }

        var qualityS = measuredS + calibratedS + estimatedS;
        var onHours = onS / 3600.0;
        return new RangeTotals(
            from, to,
            EnergyKwh: energy / 1000,
            Cost: schedule.Cost(rows.Select(r => (r.Start, r.EnergyWh))),
            Currency: schedule.Currency,
            AvgW: onHours > 0 ? energy / onHours : 0,
            PeakW: peak, PeakAt: peakAt,
            OnHours: onHours,
            IdleOnHours: idleOnS / 3600.0,
            IdleOffHours: idleOffS / 3600.0,
            AsleepHours: Math.Max(0, (to - from).TotalHours - onHours),
            CpuKwh: cpu / 1000, GpuKwh: gpu / 1000, DisplayKwh: display / 1000, RestKwh: rest / 1000,
            IdleOnKwh: idleOn / 1000, IdleOffKwh: idleOff / 1000,
            MeasuredShare: Share(measuredS, qualityS),
            CalibratedShare: Share(calibratedS, qualityS),
            EstimatedShare: Share(estimatedS, qualityS));
    }

    /// <summary>One bucket per local calendar day that has rows, oldest first.</summary>
    public List<DayTotals> DailyBuckets(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        var schedule = new TariffRepository(db).Schedule();
        return Load(from, to)
            .GroupBy(r => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(r.Start, zone).DateTime))
            .OrderBy(g => g.Key)
            .Select(g => new DayTotals(
                g.Key,
                EnergyKwh: g.Sum(r => r.EnergyWh) / 1000,
                Cost: schedule.Cost(g.Select(r => (r.Start, r.EnergyWh))),
                OnHours: g.Sum(r => r.OnSeconds) / 3600.0,
                PeakW: g.Max(r => r.MaxW),
                IdleOnKwh: g.Sum(r => r.IdleOnWh) / 1000,
                IdleOffKwh: g.Sum(r => r.IdleOffWh) / 1000))
            .ToList();
    }

    private List<Aggregate> Load(DateTimeOffset from, DateTimeOffset to)
    {
        var repo = new AggregateRepository(db);
        return to - from <= MinuteResolutionLimit ? repo.ReadMinutes(from, to) : repo.ReadHours(from, to);
    }

    private static double Share(double part, double whole) => whole > 0 ? part / whole : 0;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Storage.Tests --filter ReportQueriesTests`
Expected: `Passed! - Failed: 0, Passed: 5`.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.Storage/RangeTotals.cs src/PowerLedger.Storage/ReportQueries.cs tests/PowerLedger.Storage.Tests/ReportQueriesTests.cs
git commit -m "Add ReportQueries with cost at query time and daily buckets"
```

---

### Task 16: Property tests — integrator and downsampler invariants

**Files:**
- Test: `tests/PowerLedger.Core.Tests/EnergyProperties.cs`

- [ ] **Step 1: Write the property tests**

```csharp
using FsCheck.Xunit;
using PowerLedger.Contracts;
using PowerLedger.Core;

namespace PowerLedger.Core.Tests;

public class EnergyProperties
{
    private static Reading Make(double totalW, double delta, bool idle, int second = 0)
    {
        var display = Math.Min(6, totalW * 0.1);
        var parts = new Components(Cpu: totalW * 0.5, Gpu: totalW * 0.2, Display: display, 0, 0, 0, 0, 0, 0, Rest: totalW * 0.3 - display);
        return new Reading(TestData.T0.AddSeconds(second), delta, totalW, Quality.Estimated, parts,
            OnBattery: false, DisplayOn: true, UserIdle: idle, SessionLocked: false, 0.5, null, 0.5, false);
    }

    [Property(MaxTest = 300)]
    public bool Energy_is_non_negative_bounded_by_five_seconds_and_gaps_match_the_rule(int w, int d)
    {
        var watts = Math.Abs(w % 1000);
        var delta = Math.Abs(d % 100) / 10.0;                       // 0.0 .. 9.9 s
        var e = EnergyIntegrator.Integrate(Make(watts, delta, idle: false));
        var bounded = e.Wh >= 0 && e.Wh <= watts * 5 / 3600.0 + 1e-9;
        var gapRule = e.Gap == (delta > 5);
        var seconds = e.Gap ? e.OnSeconds == 0 && e.GapSeconds == delta : e.GapSeconds == 0;
        return bounded && gapRule && seconds;
    }

    [Property(MaxTest = 200)]
    public bool Minute_energy_equals_the_sum_of_ticks_and_components_add_up(int[] ws)
    {
        if (ws.Length == 0) return true;
        var readings = ws.Take(60)
            .Select((w, i) => Make(Math.Abs(w % 500), delta: i % 7 == 0 ? 7.0 : 1.0, idle: i % 3 == 0, second: i))
            .ToList();
        var m = Downsampler.ToMinute(TestData.T0, readings);
        var expected = readings.Sum(r => EnergyIntegrator.Integrate(r).Wh);
        var conserved = Math.Abs(m.EnergyWh - expected) < 1e-9;
        var partsAddUp = Math.Abs(m.CpuWh + m.GpuWh + m.DisplayWh + m.RestWh - m.EnergyWh) < 1e-9;
        var idleWithin = m.IdleOnWh + m.IdleOffWh <= m.EnergyWh + 1e-9;
        return conserved && partsAddUp && idleWithin;
    }
}
```

- [ ] **Step 2: Run them**

Run: `dotnet test tests/PowerLedger.Core.Tests --filter EnergyProperties`
Expected: `Passed! - Failed: 0, Passed: 2`. If a property fails, FsCheck prints the shrunk counter-example; fix the production code, not the property.

- [ ] **Step 3: Commit**

```bash
git add tests/PowerLedger.Core.Tests/EnergyProperties.cs
git commit -m "Add property tests for energy integration invariants"
```

---

### Task 17: Final verification and handoff to Plan B

- [ ] **Step 1: Clean build with warnings as errors**

Run: `dotnet build -c Release`
Expected: `Build succeeded.` and `0 Warning(s)`.

- [ ] **Step 2: Full test run**

Run: `dotnet test -c Release`
Expected: Core `Passed: 58`, Storage `Passed: 26`, no failures, no skipped tests.

- [ ] **Step 3: Confirm the working tree is clean and every task is committed**

Run: `git status --short` → no output. Run: `git log --oneline | head -n 20` → one commit per task from this plan, newest first.

- [ ] **Step 4: Record the public surface Plan B will build on**

Plan B (Sensors + Service) consumes exactly these types; do not rename them without updating both plans:

| Type | Used by Plan B for |
|---|---|
| `Sample`, `Reading`, `Components` | sampler output, model output |
| `PowerModel`, `PowerModelOptions`, `HardwareFacts`, `IBaselineProvider` | one model instance per hardware inventory |
| `CalibrationLearner`, `CalibrationState`, `CalibrationOptions`, `CalibrationBuckets` | learning on battery ticks, persisted through `CalibrationRepository` |
| `EnergyIntegrator`, `Downsampler`, `Aggregate` | minute and hour jobs |
| `TariffSchedule`, `Tariff` | settings and pipe `SetTariff` |
| `SqliteDatabase.OpenAndMigrate`, all repositories, `RetentionJob`, `ReportQueries` | service startup, writer, jobs, and the App's read side |
| `MachineProfile`, `Quality`, `SessionReason` (Contracts) | inventory, pipe DTOs |

No step in this plan ships a running process; that is Plan B's first task.

---

## Self-review against the spec

| Spec section | Covered by |
|---|---|
| §3 projects Core / Contracts / Storage, rules (Core pure, App never writes) | Tasks 1–15; Core has no I/O dependency |
| §4 Sample record | Task 3 |
| §5 modes, quality labels, default table, fallbacks, buckets, learner thresholds | Tasks 3–6 |
| §6 Δt rule, gaps, idle split | Task 7; sessions in Task 13 |
| §7 tables, WAL, pragmas, batched writes, retention bounds, cost at query time, migrations with `.bak` | Tasks 10–15 |
| §9 comparisons constants, CO₂ default | Task 9 |
| §12 Core/Storage tests, property tests, reader-while-writer | Tasks 2–16 |
| §4 validator, §4 sensors, §6 suspend/resume, §8 pipe, §9 UI, §13 installer | **Plans B, C, D** — out of scope here by design |

Type names were checked across tasks: `Aggregate` carries `IdleOnSeconds`/`IdleOffSeconds` from Task 8 through the schema (Task 10), `AggregateRepository` (Task 12) and `ReportQueries` (Task 15).
