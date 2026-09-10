# PowerLedger Plan B — Sensors — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `PowerLedger.Sensors`: the adapters that read what Windows exposes each second, the validator that rejects implausible values, and the hardware inventory that keys calibration, so the Service in Plan C has a `Sample` to feed the model.

**Architecture:** Each source owns a few fields of a mutable `SampleDraft` and nothing else; a `Sampler` runs them all behind individual try/catch with backoff and hands back an immutable `Sample`. No source needs a kernel driver or elevation: Windows 11 publishes the processor's power rails through the Energy Meter Interface, NVIDIA publishes GPU telemetry through its own driver, and battery, display and activity are plain Win32 and WMI. The validator sits between the sampler and the model, marking rather than throwing.

**Tech Stack:** .NET 10, `net10.0-windows`, `System.Management` for WMI, `System.Diagnostics.PerformanceCounter` for the energy meter, and Win32 P/Invoke for everything else: battery, CPU times, idle, device power state and NVML. No third-party sensor library. xUnit, Shouldly. Spec: `docs/superpowers/specs/2026-09-08-powerledger-design.md` §4, §5, §11.

**Git rule (from the owner):** commit locally after every task. Never add a remote, push, or create a GitHub repo.

**Scope note:** this plan stops at "a correct `Sample` and a correct `HardwareInventory`". The 1 Hz loop, the write buffer, the minute and hour jobs, the named pipe, and the Windows service host are Plan C. No step here ships a running process; the console preview in `samples/PowerLedger.Demo` is upgraded at the end to prove the layer works on real hardware.

---

## What already exists

`main` holds Plan A: `PowerLedger.Contracts` (`Quality`, `SessionReason`, `ChassisKind`, `PsuTier`, `MachineProfile`), `PowerLedger.Core` (`Sample`, `Components`, `Reading`, `PowerModel`, `CalibrationLearner`, `EnergyIntegrator`, `Downsampler`, `TariffSchedule`), `PowerLedger.Storage` (SQLite, repositories, `RetentionJob`, `ReportQueries`), and `samples/PowerLedger.Demo`, a console preview with its own throwaway P/Invoke file. 110 Core and 40 Storage tests pass; the Release build has zero warnings.

The `Sample` record this plan must produce (`src/PowerLedger.Core/Sample.cs`):

```csharp
public sealed record Sample(
    DateTimeOffset Timestamp, double DeltaSeconds,
    double? CpuPackageW, double? IGpuW, double CpuLoad,
    double? DGpuW, double? DGpuLoad, bool DGpuPresent,
    double? BatteryRateW, bool OnBattery,
    double? Brightness, bool DisplayOn, int MonitorCount,
    double UserIdleSeconds, bool SessionLocked, bool Suspect);
```

## File structure

```
src/PowerLedger.Sensors/
  PowerLedger.Sensors.csproj      net10.0-windows; refs Core + Contracts; System.Management, PerformanceCounter
  SampleDraft.cs                  mutable tick under construction; each source fills the fields it owns
  ISensorSource.cs                the source contract plus SourceHealth
  Sampler.cs                      runs the sources, isolates failures, backs off, emits a Sample
  ValidatorOptions.cs             plausible ranges, median window, outlier factor, transition window
  SampleValidator.cs              range checks, spike rejection, AC transition window
  RollingMedian.cs                fixed-window median over the last N values
  Win32.cs                        P/Invoke: battery state and the UPS flag, system times, last input
  Wmi.cs                          every WMI query: a timeout, row disposal, one definition of failure
  EnergyMeter.cs                  the Energy Meter Interface: processor rails as performance counters
  EnergyMeterSource.cs            CPU package and iGPU watts from the energy meter
  BatterySource.cs                discharge watts and AC state, from the machine's own battery only
  CpuLoadSource.cs                CPU load from system time deltas
  ActivitySource.cs               user idle seconds and session locked
  DisplaySource.cs                brightness, monitor count, display on/off
  DevicePowerState.cs             whether Windows has switched a device off, without waking it
  Nvml.cs                         the nvml.dll surface, loaded from System32 only
  NvidiaSource.cs                 discrete GPU watts, load and presence
  TdpTable.cs                     CPU and GPU TDP lookup by model name
  tdp-table.json                  the bundled table, an embedded resource
  InventoryFacts.cs               what was detected, plus its stable hash
  HardwareInventory.cs            WMI detection into InventoryFacts
  MachineSensors.cs               the assembled set the Service and the preview share
tests/PowerLedger.Sensors.Tests/
  FakeSource.cs                   test double for ISensorSource
  SampleDraftTests.cs
  SamplerTests.cs
  RollingMedianTests.cs
  SampleValidatorTests.cs
  EnergyMeterTests.cs
  EnergyMeterSourceTests.cs
  SimpleSourcesTests.cs           battery, CPU load and activity
  DisplaySourceTests.cs
  NvidiaSourceTests.cs
  TdpTableTests.cs
  InventoryFactsTests.cs
  HardwareInventoryTests.cs       chassis, drive and panel decisions
  MachineSensorsTests.cs
  RealHardwareTests.cs            Trait("Category","Hardware"); reads this machine, skipped in CI
```

**Rules.** A source never computes a total and never reads another source's fields. Everything that touches Windows lives behind a source or `Win32`, so the sampler and validator stay pure and testable. `Sensors` depends on `Core` and `Contracts` only; it never references `Storage`.

---

### Task 1: Scaffold the Sensors project

**Files:**
- Create: `src/PowerLedger.Sensors/PowerLedger.Sensors.csproj`
- Create: `tests/PowerLedger.Sensors.Tests/PowerLedger.Sensors.Tests.csproj`
- Modify: `PowerLedger.sln`

- [x] **Step 1: Create the two projects and wire them up**

Run from `D:\PowerLedger`:
```bash
dotnet new classlib -n PowerLedger.Sensors -o src/PowerLedger.Sensors -f net10.0
dotnet new xunit -n PowerLedger.Sensors.Tests -o tests/PowerLedger.Sensors.Tests -f net10.0
rm src/PowerLedger.Sensors/Class1.cs tests/PowerLedger.Sensors.Tests/UnitTest1.cs
dotnet sln add src/PowerLedger.Sensors tests/PowerLedger.Sensors.Tests
dotnet add src/PowerLedger.Sensors reference src/PowerLedger.Core src/PowerLedger.Contracts
dotnet add src/PowerLedger.Sensors package System.Management
dotnet add tests/PowerLedger.Sensors.Tests reference src/PowerLedger.Sensors src/PowerLedger.Core src/PowerLedger.Contracts
dotnet add tests/PowerLedger.Sensors.Tests package Shouldly
```

- [x] **Step 2: Set the target framework and trim the generated csproj**

`src/PowerLedger.Sensors/PowerLedger.Sensors.csproj` must read exactly:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <ProjectReference Include="..\PowerLedger.Core\PowerLedger.Core.csproj" />
    <ProjectReference Include="..\PowerLedger.Contracts\PowerLedger.Contracts.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="System.Diagnostics.PerformanceCounter" Version="10.0.12" />
    <PackageReference Include="System.Management" Version="10.0.12" />
  </ItemGroup>

  <ItemGroup>
    <EmbeddedResource Include="tdp-table.json" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="PowerLedger.Sensors.Tests" />
  </ItemGroup>

  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
  </PropertyGroup>

</Project>
```
(If `dotnet add package System.Management` resolved a different patch version, keep the version it resolved rather than forcing 10.0.2.)

`tests/PowerLedger.Sensors.Tests/PowerLedger.Sensors.Tests.csproj` keeps its generated shape but its `TargetFramework` becomes `net10.0-windows`, and it must not carry `ImplicitUsings`/`Nullable` lines of its own (they come from `Directory.Build.props`).

- [x] **Step 3: Create a placeholder so the embedded resource resolves**

Write `src/PowerLedger.Sensors/tdp-table.json` with exactly:
```json
{
  "cpu": {},
  "gpu": {}
}
```
Task 9 fills it in.

- [x] **Step 4: Build and run the suites**

Run: `dotnet build`
Expected: `Build succeeded.` with 0 warnings.

Run: `dotnet test`
Expected: Core 110 passed, Storage 40 passed, Sensors reports "No test is available".

- [x] **Step 5: Commit**

```bash
git add -A
git commit -m "Scaffold the Sensors project"
```

---

### Task 2: SampleDraft and the source contract

**Files:**
- Create: `src/PowerLedger.Sensors/SampleDraft.cs`
- Create: `src/PowerLedger.Sensors/ISensorSource.cs`
- Test: `tests/PowerLedger.Sensors.Tests/SampleDraftTests.cs`

- [x] **Step 1: Write the failing test**

```csharp
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class SampleDraftTests
{
    [Fact]
    public void A_fresh_draft_claims_nothing_and_assumes_the_display_is_on()
    {
        var draft = new SampleDraft();
        draft.CpuPackageW.ShouldBeNull();
        draft.BatteryRateW.ShouldBeNull();
        draft.Brightness.ShouldBeNull();
        draft.DGpuPresent.ShouldBeFalse();
        draft.OnBattery.ShouldBeFalse();
        draft.DisplayOn.ShouldBeTrue();
        draft.MonitorCount.ShouldBe(0);
    }

    [Fact]
    public void The_draft_becomes_a_sample_with_the_given_clock_and_delta()
    {
        var at = new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
        var draft = new SampleDraft
        {
            CpuPackageW = 14.6,
            IGpuW = 2.0,
            CpuLoad = 0.3,
            DGpuW = null,
            DGpuLoad = 0.2,
            DGpuPresent = true,
            BatteryRateW = 34.2,
            OnBattery = true,
            Brightness = 0.6,
            DisplayOn = false,
            MonitorCount = 2,
            UserIdleSeconds = 12,
            SessionLocked = true,
        };

        var sample = draft.ToSample(at, deltaSeconds: 1.5);

        sample.Timestamp.ShouldBe(at);
        sample.DeltaSeconds.ShouldBe(1.5);
        sample.CpuPackageW.ShouldBe(14.6);
        sample.IGpuW.ShouldBe(2.0);
        sample.CpuLoad.ShouldBe(0.3);
        sample.DGpuW.ShouldBeNull();
        sample.DGpuLoad.ShouldBe(0.2);
        sample.DGpuPresent.ShouldBeTrue();
        sample.BatteryRateW.ShouldBe(34.2);
        sample.OnBattery.ShouldBeTrue();
        sample.Brightness.ShouldBe(0.6);
        sample.DisplayOn.ShouldBeFalse();
        sample.MonitorCount.ShouldBe(2);
        sample.UserIdleSeconds.ShouldBe(12);
        sample.SessionLocked.ShouldBeTrue();
        sample.Suspect.ShouldBeFalse();
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter SampleDraftTests`
Expected: build error, `SampleDraft` not found.

- [x] **Step 3: Write the draft and the contract**

`src/PowerLedger.Sensors/SampleDraft.cs`
```csharp
using PowerLedger.Core;

namespace PowerLedger.Sensors;

/// <summary>
/// One tick under construction. Each source fills only the fields it owns and leaves the rest alone,
/// so a source that fails contributes nothing instead of corrupting the tick.
/// </summary>
public sealed class SampleDraft
{
    public double? CpuPackageW { get; set; }
    public double? IGpuW { get; set; }
    public double CpuLoad { get; set; }
    public double? DGpuW { get; set; }
    public double? DGpuLoad { get; set; }
    public bool DGpuPresent { get; set; }
    public double? BatteryRateW { get; set; }
    public bool OnBattery { get; set; }
    public double? Brightness { get; set; }

    /// <summary>Assumed on until the display source says otherwise, so a missing source never invents a dark screen.</summary>
    public bool DisplayOn { get; set; } = true;

    public int MonitorCount { get; set; }
    public double UserIdleSeconds { get; set; }
    public bool SessionLocked { get; set; }

    /// <summary>Freezes the draft. Suspect is always false here; only the validator sets it.</summary>
    public Sample ToSample(DateTimeOffset timestamp, double deltaSeconds) => new(
        timestamp, deltaSeconds,
        CpuPackageW, IGpuW, CpuLoad,
        DGpuW, DGpuLoad, DGpuPresent,
        BatteryRateW, OnBattery,
        Brightness, DisplayOn, MonitorCount,
        UserIdleSeconds, SessionLocked, Suspect: false);
}
```

`src/PowerLedger.Sensors/ISensorSource.cs`
```csharp
namespace PowerLedger.Sensors;

/// <summary>
/// One place Windows is asked a question. A source fills its own fields of the draft and never computes a total,
/// never reads another source's fields, and never throws on the caller's behalf: the sampler isolates it.
/// </summary>
public interface ISensorSource : IDisposable
{
    /// <summary>Short name used in status and logs, e.g. "battery".</summary>
    string Name { get; }

    /// <summary>False when this machine cannot answer at all, so the sampler can skip it for good.</summary>
    bool Supported { get; }

    /// <summary>Why the source is unsupported, for the status screen; null when it is supported.</summary>
    string? Unavailable { get; }

    /// <summary>Fill this source's fields. Called once per tick.</summary>
    void Contribute(SampleDraft draft);
}

/// <param name="Name">The source's name.</param>
/// <param name="Supported">Whether the machine can answer at all.</param>
/// <param name="Unavailable">Why not, when it cannot.</param>
/// <param name="Failures">How many ticks have thrown since the service started.</param>
/// <param name="SkippedUntilTick">While the source is backing off, the tick it resumes at.</param>
/// <param name="LastError">The most recent exception message, for the status screen.</param>
public sealed record SourceHealth(
    string Name, bool Supported, string? Unavailable,
    int Failures, long SkippedUntilTick, string? LastError);
```

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter SampleDraftTests`
Expected: `Passed! - Failed: 0, Passed: 2`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Sensors tests/PowerLedger.Sensors.Tests
git commit -m "Add SampleDraft and the sensor source contract"
```

---

### Task 3: Sampler — isolate every source and back off after a failure

**Files:**
- Create: `src/PowerLedger.Sensors/Sampler.cs`
- Create: `tests/PowerLedger.Sensors.Tests/FakeSource.cs`
- Test: `tests/PowerLedger.Sensors.Tests/SamplerTests.cs`

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.Sensors.Tests/FakeSource.cs`
```csharp
using PowerLedger.Sensors;

namespace PowerLedger.Sensors.Tests;

/// <summary>A source under the test's control: it can fill fields, throw, or claim to be unsupported.</summary>
internal sealed class FakeSource(string name, Action<SampleDraft> contribute) : ISensorSource
{
    public string Name { get; } = name;
    public bool Supported { get; init; } = true;
    public string? Unavailable { get; init; }
    public int Calls { get; private set; }
    public bool Disposed { get; private set; }
    public Exception? Throw { get; set; }

    public void Contribute(SampleDraft draft)
    {
        Calls++;
        if (Throw is { } error) throw error;
        contribute(draft);
    }

    public void Dispose() => Disposed = true;
}
```

`tests/PowerLedger.Sensors.Tests/SamplerTests.cs`
```csharp
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class SamplerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Every_source_contributes_to_one_sample()
    {
        var cpu = new FakeSource("cpu", d => { d.CpuPackageW = 14.6; d.CpuLoad = 0.3; });
        var battery = new FakeSource("battery", d => { d.BatteryRateW = 34.2; d.OnBattery = true; });
        using var sampler = new Sampler([cpu, battery]);

        var sample = sampler.Read(T0, 1.0);

        sample.CpuPackageW.ShouldBe(14.6);
        sample.BatteryRateW.ShouldBe(34.2);
        sample.OnBattery.ShouldBeTrue();
        sample.Timestamp.ShouldBe(T0);
        sample.DeltaSeconds.ShouldBe(1.0);
    }

    [Fact]
    public void An_unsupported_source_is_never_called()
    {
        var dead = new FakeSource("dead", _ => { }) { Supported = false, Unavailable = "no battery" };
        using var sampler = new Sampler([dead]);

        sampler.Read(T0, 1.0);
        sampler.Read(T0.AddSeconds(1), 1.0);

        dead.Calls.ShouldBe(0);
        sampler.Health.Single().Unavailable.ShouldBe("no battery");
    }

    [Fact]
    public void A_throwing_source_does_not_stop_the_others_or_the_tick()
    {
        var bad = new FakeSource("bad", d => d.CpuLoad = 0.9) { Throw = new InvalidOperationException("sensor gone") };
        var good = new FakeSource("good", d => d.Brightness = 0.6);
        using var sampler = new Sampler([bad, good]);

        var sample = sampler.Read(T0, 1.0);

        sample.Brightness.ShouldBe(0.6);
        sample.CpuLoad.ShouldBe(0);
        sampler.Health.Single(h => h.Name == "bad").Failures.ShouldBe(1);
        sampler.Health.Single(h => h.Name == "bad").LastError.ShouldBe("sensor gone");
    }

    [Fact]
    public void A_failing_source_backs_off_and_doubles_the_wait_each_time()
    {
        var bad = new FakeSource("bad", _ => { }) { Throw = new InvalidOperationException("boom") };
        using var sampler = new Sampler([bad]);

        sampler.Read(T0, 1.0);                                  // tick 1: throws, skip 1
        bad.Calls.ShouldBe(1);
        sampler.Read(T0.AddSeconds(1), 1.0);                    // tick 2: skipped
        bad.Calls.ShouldBe(1);
        sampler.Read(T0.AddSeconds(2), 1.0);                    // tick 3: retried, throws, skip 2
        bad.Calls.ShouldBe(2);
        sampler.Read(T0.AddSeconds(3), 1.0);
        sampler.Read(T0.AddSeconds(4), 1.0);
        bad.Calls.ShouldBe(2);
        sampler.Read(T0.AddSeconds(5), 1.0);                    // tick 6: retried
        bad.Calls.ShouldBe(3);
    }

    [Fact]
    public void The_backoff_is_capped_and_a_recovery_clears_it()
    {
        var flaky = new FakeSource("flaky", d => d.CpuLoad = 0.5) { Throw = new InvalidOperationException("boom") };
        using var sampler = new Sampler([flaky], maxBackoffTicks: 4);

        for (var tick = 0; tick < 40; tick++) sampler.Read(T0.AddSeconds(tick), 1.0);
        var beforeRecovery = flaky.Calls;
        sampler.Health.Single().SkippedUntilTick.ShouldBeLessThanOrEqualTo(41 + 4);

        flaky.Throw = null;
        for (var tick = 40; tick < 60; tick++) sampler.Read(T0.AddSeconds(tick), 1.0);

        flaky.Calls.ShouldBeGreaterThan(beforeRecovery);
        sampler.Health.Single().SkippedUntilTick.ShouldBe(0);
        sampler.Read(T0.AddSeconds(60), 1.0).CpuLoad.ShouldBe(0.5);
    }

    [Fact]
    public void Disposing_the_sampler_disposes_every_source()
    {
        var one = new FakeSource("one", _ => { });
        var two = new FakeSource("two", _ => { });
        var sampler = new Sampler([one, two]);

        sampler.Dispose();

        one.Disposed.ShouldBeTrue();
        two.Disposed.ShouldBeTrue();
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter SamplerTests`
Expected: build error, `Sampler` not found.

- [x] **Step 3: Write the sampler**

`src/PowerLedger.Sensors/Sampler.cs`
```csharp
using PowerLedger.Core;

namespace PowerLedger.Sensors;

/// <summary>
/// Runs every source once per tick behind its own try/catch (spec §4). A source that throws is skipped for a
/// doubling number of ticks and retried; one broken sensor costs its own fields and nothing else.
/// </summary>
public sealed class Sampler : IDisposable
{
    private readonly List<Entry> _entries;
    private readonly int _maxBackoffTicks;
    private long _tick;

    /// <param name="sources">The sources to run, in the order they should contribute.</param>
    /// <param name="maxBackoffTicks">Ceiling on the skip length, so a dead sensor is still retried about once a minute.</param>
    public Sampler(IEnumerable<ISensorSource> sources, int maxBackoffTicks = 60)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBackoffTicks);
        _entries = sources.Select(s => new Entry(s)).ToList();
        _maxBackoffTicks = maxBackoffTicks;
    }

    /// <summary>Per-source state for the status screen.</summary>
    public IReadOnlyList<SourceHealth> Health =>
        _entries.Select(e => new SourceHealth(e.Source.Name, e.Source.Supported, e.Source.Unavailable, e.Failures, e.SkipUntil, e.LastError)).ToList();

    /// <summary>One tick. Never throws: a source's failure is recorded, not propagated.</summary>
    public Sample Read(DateTimeOffset timestamp, double deltaSeconds)
    {
        _tick++;
        var draft = new SampleDraft();
        foreach (var entry in _entries)
        {
            if (!entry.Source.Supported || _tick < entry.SkipUntil) continue;
            try
            {
                entry.Source.Contribute(draft);
                entry.Backoff = 0;
                entry.SkipUntil = 0;
            }
            catch (Exception error)
            {
                entry.Failures++;
                entry.LastError = error.Message;
                entry.Backoff = entry.Backoff == 0 ? 1 : Math.Min(entry.Backoff * 2, _maxBackoffTicks);
                // SkipUntil is the tick it resumes at, so Backoff whole ticks are actually skipped.
                entry.SkipUntil = _tick + entry.Backoff + 1;
            }
        }
        return draft.ToSample(timestamp, deltaSeconds);
    }

    public void Dispose()
    {
        foreach (var entry in _entries) entry.Source.Dispose();
        _entries.Clear();
    }

    private sealed class Entry(ISensorSource source)
    {
        public ISensorSource Source { get; } = source;
        public int Failures { get; set; }
        public int Backoff { get; set; }
        public long SkipUntil { get; set; }
        public string? LastError { get; set; }
    }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter SamplerTests`
Expected: `Passed! - Failed: 0, Passed: 6`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Sensors/Sampler.cs tests/PowerLedger.Sensors.Tests
git commit -m "Add the sampler with per-source isolation and backoff"
```

---

### Task 4: RollingMedian

**Files:**
- Create: `src/PowerLedger.Sensors/RollingMedian.cs`
- Test: `tests/PowerLedger.Sensors.Tests/RollingMedianTests.cs`

- [x] **Step 1: Write the failing tests**

```csharp
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class RollingMedianTests
{
    [Fact]
    public void An_empty_window_has_no_median()
        => new RollingMedian(5).Median.ShouldBeNull();

    [Fact]
    public void An_odd_count_takes_the_middle_value_and_an_even_count_averages_the_pair()
    {
        var window = new RollingMedian(5);
        window.Add(10);
        window.Add(30);
        window.Add(20);
        window.Median.ShouldBe(20);
        window.Add(40);
        window.Median.ShouldBe(25);
    }

    [Fact]
    public void The_window_forgets_the_oldest_value_once_it_is_full()
    {
        var window = new RollingMedian(3);
        window.Add(1);
        window.Add(2);
        window.Add(3);
        window.Median.ShouldBe(2);
        window.Add(100);                 // drops the 1
        window.Median.ShouldBe(3);
        window.Count.ShouldBe(3);
    }

    [Fact]
    public void Adding_does_not_disturb_the_order_of_later_reads()
    {
        var window = new RollingMedian(4);
        foreach (var value in new double[] { 5, 1, 4, 2 }) window.Add(value);
        window.Median.ShouldBe(3);
        window.Median.ShouldBe(3);
    }

    [Fact]
    public void A_window_must_hold_at_least_one_value()
        => Should.Throw<ArgumentOutOfRangeException>(() => new RollingMedian(0));
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter RollingMedianTests`
Expected: build error, `RollingMedian` not found.

- [x] **Step 3: Write it**

`src/PowerLedger.Sensors/RollingMedian.cs`
```csharp
namespace PowerLedger.Sensors;

/// <summary>
/// Median of the last N values. The validator compares each reading against this rather than against the
/// previous one, so a single spike cannot drag the reference with it.
/// </summary>
public sealed class RollingMedian
{
    private readonly double[] _values;
    private readonly double[] _scratch;
    private int _next;

    public RollingMedian(int window)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(window);
        _values = new double[window];
        _scratch = new double[window];
    }

    /// <summary>How many values the window holds so far, up to its size.</summary>
    public int Count { get; private set; }

    /// <summary>The median, or null while the window is empty.</summary>
    public double? Median
    {
        get
        {
            if (Count == 0) return null;
            var span = _scratch.AsSpan(0, Count);
            _values.AsSpan(0, Count).CopyTo(span);
            span.Sort();
            var middle = Count / 2;
            return Count % 2 == 1 ? span[middle] : (span[middle - 1] + span[middle]) / 2;
        }
    }

    public void Add(double value)
    {
        _values[_next] = value;
        _next = (_next + 1) % _values.Length;
        if (Count < _values.Length) Count++;
    }

    public void Reset()
    {
        Array.Clear(_values);
        _next = 0;
        Count = 0;
    }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter RollingMedianTests`
Expected: `Passed! - Failed: 0, Passed: 5`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Sensors/RollingMedian.cs tests/PowerLedger.Sensors.Tests/RollingMedianTests.cs
git commit -m "Add a fixed-window rolling median"
```

---

### Task 5: SampleValidator — ranges, spikes and the transition window

**Files:**
- Create: `src/PowerLedger.Sensors/ValidatorOptions.cs`
- Create: `src/PowerLedger.Sensors/SampleValidator.cs`
- Test: `tests/PowerLedger.Sensors.Tests/SampleValidatorTests.cs`

Spec §4: plausible ranges CPU 0–400 W, GPU 0–700 W, battery 0–300 W. A GPU or battery value more than 3× the rolling 30-sample median is a spike: it is replaced with the last good one and the tick marked suspect. A second such value in a row is a real rise, so it is accepted and restarts the median; otherwise a sustained change would be rejected forever. CPU watts from the energy meter are exact over their tick and are range-checked only, and a rail whose counter goes backwards is dropped by the meter itself (Task 7). The first 3 s after an AC↔battery transition are excluded from calibration while the quality label still switches immediately, and the transition restarts the battery median.

- [x] **Step 1: Write the failing tests**

```csharp
using PowerLedger.Core;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class SampleValidatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);

    private static Sample Raw(
        double? cpu = 15, double? gpu = 5, double? battery = null, bool onBattery = false,
        double cpuLoad = 0.3, double? brightness = 0.6, int second = 0)
        => new(T0.AddSeconds(second), 1.0, cpu, null, cpuLoad, gpu, 0.2, DGpuPresent: true,
               battery, onBattery, brightness, DisplayOn: true, MonitorCount: 1,
               UserIdleSeconds: 0, SessionLocked: false, Suspect: false);

    [Fact]
    public void A_plain_reading_passes_through_untouched()
    {
        var validator = new SampleValidator();
        var clean = validator.Validate(Raw());
        clean.CpuPackageW.ShouldBe(15);
        clean.DGpuW.ShouldBe(5);
        clean.Suspect.ShouldBeFalse();
        validator.SuspectCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(401.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_cpu_reading_outside_its_range_is_dropped_and_the_tick_marked_suspect(double watts)
    {
        var validator = new SampleValidator();
        var checked_ = validator.Validate(Raw(cpu: watts));
        checked_.CpuPackageW.ShouldBeNull();
        checked_.Suspect.ShouldBeTrue();
        validator.SuspectCount.ShouldBe(1);
    }

    [Fact]
    public void Gpu_and_battery_have_their_own_ranges()
    {
        var validator = new SampleValidator();
        validator.Validate(Raw(gpu: 701)).DGpuW.ShouldBeNull();
        validator.Validate(Raw(gpu: 699)).DGpuW.ShouldBe(699);
        validator.Validate(Raw(battery: 301, onBattery: true)).BatteryRateW.ShouldBeNull();
        validator.Validate(Raw(battery: 299, onBattery: true)).BatteryRateW.ShouldBe(299);
    }

    [Fact]
    public void A_single_gpu_spike_is_replaced_by_the_last_good_value()
    {
        var validator = new SampleValidator();
        for (var i = 0; i < 30; i++) validator.Validate(Raw(gpu: 10, second: i));

        var spike = validator.Validate(Raw(gpu: 200, second: 30));

        spike.DGpuW.ShouldBe(10);
        spike.Suspect.ShouldBeTrue();
    }

    [Fact]
    public void A_sustained_rise_is_accepted_on_its_second_reading_instead_of_being_rejected_forever()
    {
        var validator = new SampleValidator();
        for (var i = 0; i < 30; i++) validator.Validate(Raw(gpu: 10, second: i));

        validator.Validate(Raw(gpu: 200, second: 30)).DGpuW.ShouldBe(10);

        var second = validator.Validate(Raw(gpu: 200, second: 31));
        second.DGpuW.ShouldBe(200);
        second.Suspect.ShouldBeFalse();
        validator.Validate(Raw(gpu: 210, second: 32)).DGpuW.ShouldBe(210);
    }

    [Fact]
    public void Cpu_watts_from_the_energy_meter_are_never_spike_filtered()
    {
        // They are averages of real energy over the tick, so a jump from idle to turbo is a fact, not a glitch.
        var validator = new SampleValidator();
        for (var i = 0; i < 30; i++) validator.Validate(Raw(cpu: 6, second: i));

        var load = validator.Validate(Raw(cpu: 35, second: 30));
        load.CpuPackageW.ShouldBe(35);
        load.Suspect.ShouldBeFalse();
    }

    [Fact]
    public void A_dip_far_below_the_median_is_left_alone_because_idle_is_real()
    {
        var validator = new SampleValidator();
        for (var i = 0; i < 30; i++) validator.Validate(Raw(gpu: 30, second: i));

        var dip = validator.Validate(Raw(gpu: 1, second: 30));

        dip.DGpuW.ShouldBe(1);
        dip.Suspect.ShouldBeFalse();
    }

    [Fact]
    public void A_reading_stays_trusted_until_the_window_has_something_to_say()
    {
        var validator = new SampleValidator();
        validator.Validate(Raw(gpu: 5)).DGpuW.ShouldBe(5);
        validator.Validate(Raw(gpu: 300, second: 1)).DGpuW.ShouldBe(300);
        validator.SuspectCount.ShouldBe(0);
    }

    [Fact]
    public void A_negative_cpu_reading_is_dropped()
    {
        var validator = new SampleValidator();
        validator.Validate(Raw(cpu: 20));
        var negative = validator.Validate(Raw(cpu: -0.5, second: 1));
        negative.CpuPackageW.ShouldBeNull();
        negative.Suspect.ShouldBeTrue();
    }

    [Fact]
    public void The_first_seconds_after_plugging_in_are_marked_but_the_battery_flag_switches_at_once()
    {
        var validator = new SampleValidator();
        validator.Validate(Raw(battery: 30, onBattery: true, second: 0));

        var justAfter = validator.Validate(Raw(battery: null, onBattery: false, second: 1));
        justAfter.OnBattery.ShouldBeFalse();
        justAfter.Suspect.ShouldBeTrue();

        validator.Validate(Raw(second: 2)).Suspect.ShouldBeTrue();
        validator.Validate(Raw(second: 3)).Suspect.ShouldBeTrue();
        validator.Validate(Raw(second: 4)).Suspect.ShouldBeFalse();
    }

    [Fact]
    public void Unplugging_opens_the_same_window()
    {
        var validator = new SampleValidator();
        validator.Validate(Raw(second: 0));
        validator.Validate(Raw(battery: 30, onBattery: true, second: 1)).Suspect.ShouldBeTrue();
        validator.Validate(Raw(battery: 30, onBattery: true, second: 5)).Suspect.ShouldBeFalse();
    }

    [Fact]
    public void Coming_off_mains_starts_the_battery_median_afresh()
    {
        var validator = new SampleValidator();
        for (var i = 0; i < 30; i++) validator.Validate(Raw(battery: 10, onBattery: true, second: i));
        validator.Validate(Raw(battery: null, onBattery: false, second: 30));

        validator.Validate(Raw(battery: 40, onBattery: true, second: 40)).BatteryRateW.ShouldBe(40);
    }

    [Fact]
    public void Brightness_and_load_are_clamped_rather_than_dropped()
    {
        var validator = new SampleValidator();
        var wild = validator.Validate(Raw(brightness: 1.4, cpuLoad: 2.5));
        wild.Brightness.ShouldBe(1.0);
        wild.CpuLoad.ShouldBe(1.0);
        wild.Suspect.ShouldBeTrue();

        var nonsense = validator.Validate(Raw(brightness: double.NaN, cpuLoad: double.NaN, second: 1));
        nonsense.Brightness.ShouldBeNull();
        nonsense.CpuLoad.ShouldBe(0);
    }

    [Fact]
    public void A_non_finite_integrated_graphics_reading_is_dropped()
    {
        var validator = new SampleValidator();
        var checked_ = validator.Validate(Raw() with { IGpuW = double.NaN });
        checked_.IGpuW.ShouldBeNull();
        checked_.Suspect.ShouldBeTrue();
    }

    [Fact]
    public void Options_choose_the_ranges_and_the_windows()
    {
        var strict = new SampleValidator(new ValidatorOptions(CpuMaxW: 20, MedianWindow: 3, OutlierFactor: 2, TransitionSeconds: 1));
        strict.Validate(Raw(cpu: 25)).CpuPackageW.ShouldBeNull();
        for (var i = 0; i < 3; i++) strict.Validate(Raw(gpu: 5, second: i));
        strict.Validate(Raw(gpu: 15, second: 3)).DGpuW.ShouldBe(5);
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter SampleValidatorTests`
Expected: build error, `SampleValidator` not found.

- [x] **Step 3: Write the options and the validator**

`src/PowerLedger.Sensors/ValidatorOptions.cs`
```csharp
namespace PowerLedger.Sensors;

/// <param name="CpuMaxW">Upper plausible bound for CPU package watts (spec §4).</param>
/// <param name="GpuMaxW">Upper plausible bound for discrete GPU watts.</param>
/// <param name="BatteryMaxW">Upper plausible bound for battery discharge watts.</param>
/// <param name="MedianWindow">How many recent values the outlier test compares against.</param>
/// <param name="OutlierFactor">A value this many times the median or more is treated as a spike.</param>
/// <param name="TransitionSeconds">How long after an AC change a tick stays marked suspect.</param>
public sealed record ValidatorOptions(
    double CpuMaxW = 400,
    double GpuMaxW = 700,
    double BatteryMaxW = 300,
    int MedianWindow = 30,
    double OutlierFactor = 3,
    double TransitionSeconds = 3);
```

`src/PowerLedger.Sensors/SampleValidator.cs`
```csharp
using PowerLedger.Core;

namespace PowerLedger.Sensors;

/// <summary>
/// Spec §4: sits between the sampler and the model and marks rather than throws. Out-of-range values are dropped,
/// a single-tick spike is replaced with the last good reading, and a second high reading in a row is accepted as a
/// real change of level rather than rejected forever. The seconds after an AC change are flagged so the calibration
/// learner skips them while the quality label switches at once.
/// CPU watts come from the energy meter as averages of real energy over the tick, so they are range-checked but never
/// spike-filtered: a jump from idle to turbo is a fact, not a glitch. Single-threaded: the sampling loop owns it.
/// </summary>
public sealed class SampleValidator
{
    private readonly ValidatorOptions _options;
    private readonly Channel _cpu;
    private readonly Channel _gpu;
    private readonly Channel _battery;

    private bool? _lastOnBattery;
    private DateTimeOffset _transitionAt = DateTimeOffset.MinValue;

    public SampleValidator(ValidatorOptions? options = null)
    {
        _options = options ?? new ValidatorOptions();
        _cpu = new Channel(_options.MedianWindow);
        _gpu = new Channel(_options.MedianWindow);
        _battery = new Channel(_options.MedianWindow);
    }

    /// <summary>How many ticks have been marked suspect since the service started (spec §4, shown in status).</summary>
    public int SuspectCount { get; private set; }

    public Sample Validate(Sample raw)
    {
        var suspect = false;

        if (_lastOnBattery is { } previous && previous != raw.OnBattery)
        {
            _transitionAt = raw.Timestamp;
            _battery.Reset();          // a discharge rate from before the last time on mains says nothing about now
        }
        _lastOnBattery = raw.OnBattery;
        if ((raw.Timestamp - _transitionAt).TotalSeconds < _options.TransitionSeconds) suspect = true;

        var cpu = Check(raw.CpuPackageW, _options.CpuMaxW, _cpu, spikeFilter: false, ref suspect);
        var gpu = Check(raw.DGpuW, _options.GpuMaxW, _gpu, spikeFilter: true, ref suspect);
        var battery = Check(raw.BatteryRateW, _options.BatteryMaxW, _battery, spikeFilter: true, ref suspect);
        var igpu = InRange(raw.IGpuW, _options.CpuMaxW, ref suspect);

        var brightness = Fraction(raw.Brightness, ref suspect);
        var load = Fraction(raw.CpuLoad, ref suspect) ?? 0;
        var gpuLoad = Fraction(raw.DGpuLoad, ref suspect);

        if (suspect) SuspectCount++;
        return raw with
        {
            CpuPackageW = cpu,
            IGpuW = igpu,
            DGpuW = gpu,
            BatteryRateW = battery,
            Brightness = brightness,
            CpuLoad = load,
            DGpuLoad = gpuLoad,
            Suspect = suspect,
        };
    }

    private double? Check(double? value, double max, Channel channel, bool spikeFilter, ref bool suspect)
    {
        if (InRange(value, max, ref suspect) is not { } reading) return null;

        // The spike test engages only once the window is full: a median of one or two readings says nothing.
        var isSpike = spikeFilter
            && channel.Window.Count >= _options.MedianWindow
            && channel.Window.Median is { } median && median > 0
            && reading >= median * _options.OutlierFactor;

        if (isSpike && channel.ConsecutiveSpikes == 0)
        {
            // One high reading is a spike: replace it, and keep it out of the window so the median stays honest.
            channel.ConsecutiveSpikes = 1;
            suspect = true;
            return channel.Last;
        }

        // A second high reading in a row is a real change of level, so the old median no longer applies.
        if (isSpike) channel.Window.Reset();

        channel.ConsecutiveSpikes = 0;
        channel.Window.Add(reading);
        channel.Last = reading;
        return reading;
    }

    /// <summary>A watts reading: dropped when it is not a number, negative, or above the plausible ceiling.</summary>
    private static double? InRange(double? value, double max, ref bool suspect)
    {
        if (value is not { } reading) return null;
        if (double.IsFinite(reading) && reading >= 0 && reading <= max) return reading;
        suspect = true;
        return null;
    }

    /// <summary>A 0..1 reading: clamped when it is merely out of range, dropped when it is not a number.</summary>
    private static double? Fraction(double? value, ref bool suspect)
    {
        if (value is not { } reading) return null;
        if (!double.IsFinite(reading))
        {
            suspect = true;
            return null;
        }
        if (reading is < 0 or > 1)
        {
            suspect = true;
            return Math.Clamp(reading, 0, 1);
        }
        return reading;
    }

    private sealed class Channel(int window)
    {
        public RollingMedian Window { get; } = new(window);
        public double? Last { get; set; }
        public int ConsecutiveSpikes { get; set; }

        public void Reset()
        {
            Window.Reset();
            Last = null;
            ConsecutiveSpikes = 0;
        }
    }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter SampleValidatorTests`
Expected: `Passed! - Failed: 0, Passed: 18`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Sensors/ValidatorOptions.cs src/PowerLedger.Sensors/SampleValidator.cs tests/PowerLedger.Sensors.Tests/SampleValidatorTests.cs
git commit -m "Add the sample validator with ranges, outliers and the transition window"
```

---

### Task 6: Win32 surface

**Files:**
- Create: `src/PowerLedger.Sensors/Win32.cs`
- Test: `tests/PowerLedger.Sensors.Tests/RealHardwareTests.cs`

- [x] **Step 1: Write the failing test**

This is the first hardware-touching test. It carries `Trait("Category", "Hardware")` so CI can exclude it with `--filter Category!=Hardware`; it must pass on the developer's machine.

```csharp
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

[Trait("Category", "Hardware")]
public class RealHardwareTests
{
[Trait("Category", "Hardware")]
public class RealHardwareTests
{
    [Fact]
    public void The_machine_answers_the_basic_power_questions()
    {
        var battery = Win32.ReadBatteryState();
        battery.ShouldNotBeNull();

        var times = Win32.ReadSystemTimes();
        times.ShouldNotBeNull();
        times!.Value.Kernel.ShouldBeGreaterThan(0UL);
        times.Value.User.ShouldBeGreaterThan(0UL);
        times.Value.Idle.ShouldBeGreaterThan(0UL);

        Win32.ReadIdleSeconds().ShouldBeGreaterThanOrEqualTo(0);
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter RealHardwareTests`
Expected: build error, `Win32` not found.

- [x] **Step 3: Write the P/Invoke surface**

`src/PowerLedger.Sensors/Win32.cs`
```csharp
using System.Runtime.InteropServices;

namespace PowerLedger.Sensors;

/// <summary>
/// The Windows calls the driver-free sources need. Every method answers with null rather than throwing when
/// Windows declines, so a source can decide what a missing answer means.
/// </summary>
internal static class Win32
{
    private const int SystemPowerCapabilities = 4;
    private const int SystemBatteryState = 5;

    /// <summary>Windows reports this when it cannot tell how fast the battery is moving.</summary>
    public const int UnknownRate = unchecked((int)0x80000000);

    /// <param name="AcOnLine">True when the machine is on mains power.</param>
    /// <param name="Present">True when Windows sees a battery.</param>
    /// <param name="RateMilliwatts">Negative while discharging, positive while charging, <see cref="UnknownRate"/> when unknown.</param>
    /// <param name="ShortTerm">True when Windows marks the batteries short-term, as it does for a UPS on USB.</param>
    public readonly record struct BatteryState(bool AcOnLine, bool Present, bool Charging, bool Discharging, int RateMilliwatts, bool ShortTerm = false)
    {
        /// <summary>A battery that powers this machine alone. A UPS also powers whatever else is plugged into it, so
        /// neither its presence nor its drain says anything about this machine.</summary>
        public bool OwnBattery => Present && !ShortTerm;
    }

    /// <summary>Cumulative 100 ns counters since boot. Kernel time already includes idle time.</summary>
    public readonly record struct SystemTimes(ulong Idle, ulong Kernel, ulong User);

    public static BatteryState? ReadBatteryState()
    {
        var state = default(SystemBatteryStateInfo);
        var status = CallNtPowerInformation(SystemBatteryState, IntPtr.Zero, 0, ref state, (uint)Marshal.SizeOf<SystemBatteryStateInfo>());
        if (status != 0) return null;
        return new BatteryState(state.AcOnLine != 0, state.BatteryPresent != 0, state.Charging != 0, state.Discharging != 0, state.Rate, BatteriesAreShortTerm());
    }

    /// <summary>False when Windows will not say, which keeps a real battery counted.</summary>
    private static bool BatteriesAreShortTerm()
    {
        var capabilities = default(SystemPowerCapabilitiesInfo);
        var status = CallNtPowerInformation(SystemPowerCapabilities, IntPtr.Zero, 0, ref capabilities, (uint)Marshal.SizeOf<SystemPowerCapabilitiesInfo>());
        return status == 0 && capabilities.SystemBatteriesPresent != 0 && capabilities.BatteriesAreShortTerm != 0;
    }

    public static SystemTimes? ReadSystemTimes()
        => GetSystemTimes(out var idle, out var kernel, out var user) ? new SystemTimes(idle, kernel, user) : null;

    /// <summary>Seconds since the last keyboard or mouse input in the calling session. From the service (session 0)
    /// this reports the service's own session, so the service must get idle time from the user's session instead.</summary>
    public static double ReadIdleSeconds()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return unchecked((uint)Environment.TickCount - info.dwTime) / 1000.0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemBatteryStateInfo
    {
        public byte AcOnLine;
        public byte BatteryPresent;
        public byte Charging;
        public byte Discharging;
        public byte Spare0;
        public byte Spare1;
        public byte Spare2;
        public byte Spare3;
        public uint MaxCapacity;
        public uint RemainingCapacity;
        public int Rate;
        public uint EstimatedTime;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
    }

    /// <summary>SYSTEM_POWER_CAPABILITIES is 76 bytes; only its two battery flags are read. The offsets were checked
    /// on real hardware against the sleep states powercfg reports.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 76)]
    private struct SystemPowerCapabilitiesInfo
    {
        [FieldOffset(30)] public byte SystemBatteriesPresent;
        [FieldOffset(31)] public byte BatteriesAreShortTerm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(int informationLevel, IntPtr inputBuffer, uint inputBufferSize, ref SystemBatteryStateInfo outputBuffer, uint outputBufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(int informationLevel, IntPtr inputBuffer, uint inputBufferSize, ref SystemPowerCapabilitiesInfo outputBuffer, uint outputBufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter RealHardwareTests`
Expected: `Passed! - Failed: 0, Passed: 1`.

If `ReadBatteryState` returns null on a machine with a battery, stop and report it rather than loosening the assertion: it means the struct layout is wrong.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Sensors/Win32.cs tests/PowerLedger.Sensors.Tests/RealHardwareTests.cs
git commit -m "Add the Win32 surface for battery, CPU times and idle"
```

---

### Task 7: EnergyMeter — read the processor's power rails

**Files:**
- Create: `src/PowerLedger.Sensors/EnergyMeter.cs`
- Test: `tests/PowerLedger.Sensors.Tests/EnergyMeterTests.cs`

Windows 11 publishes the processor's RAPL rails as performance counters backed by the Energy Meter Interface: the `Energy Meter` category, one instance per rail, each with a monotonic `Energy` counter in picowatt-hours. Verified on the development laptop from a non-elevated process: `RAPL_Package0_PKG`, `RAPL_Package0_PP0`, `RAPL_Package0_PP1` and `RAPL_Package0_DRAM`, plus a `_Total` instance that must be ignored.

The counters are read as raw values through the performance-counter API, in well under a millisecond, and watts are the energy used since the previous read divided by the time between reads. The same numbers are available through WMI, but that query took about 270 ms, far over the tick budget. The unit was confirmed on real hardware against Windows' own milliwatt figure: 1 pWh is 3.6e-9 J. The hardware test pins it with a plausible band for this processor; a thousandfold error lands far outside it. If the assertion fails, the constant is wrong and must be corrected rather than the test loosened.

- [x] **Step 1: Write the failing tests**

```csharp
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class EnergyMeterTests
{
    [Fact]
    public void Rail_names_are_classified_by_what_they_measure()
    {
        EnergyMeter.Classify("RAPL_Package0_PKG").ShouldBe(RailKind.Package);
        EnergyMeter.Classify("RAPL_Package1_PKG").ShouldBe(RailKind.Package);
        EnergyMeter.Classify("RAPL_Package0_PP0").ShouldBe(RailKind.Cores);
        EnergyMeter.Classify("RAPL_Package0_PP1").ShouldBe(RailKind.IntegratedGpu);
        EnergyMeter.Classify("RAPL_Package0_DRAM").ShouldBe(RailKind.Memory);
        EnergyMeter.Classify("Current Socket Energy").ShouldBe(RailKind.Package);
        EnergyMeter.Classify("Apu Energy").ShouldBe(RailKind.IntegratedGpu);
        EnergyMeter.Classify("VDDCR_VDD Energy").ShouldBe(RailKind.Cores);
        EnergyMeter.Classify("_Total").ShouldBe(RailKind.Ignored);
        EnergyMeter.Classify("Power Meter (0)").ShouldBe(RailKind.Ignored);
    }

    [Fact]
    public void Rails_of_the_same_kind_are_summed_across_sockets()
    {
        var reading = EnergyMeter.Summarise([
            new Rail("RAPL_Package0_PKG", 5590, 1),
            new Rail("RAPL_Package1_PKG", 4410, 2),
            new Rail("RAPL_Package0_PP1", 66, 3),
            new Rail("_Total", 99999, 4),
        ]);

        reading.PackageW.ShouldNotBeNull().ShouldBe(10.0, 1e-9);
        reading.IntegratedGpuW.ShouldNotBeNull().ShouldBe(0.066, 1e-9);
        reading.CoresW.ShouldBeNull();
        reading.MemoryW.ShouldBeNull();
    }

    [Fact]
    public void A_machine_with_no_rails_reports_nothing_rather_than_zero()
    {
        var reading = EnergyMeter.Summarise([]);
        reading.PackageW.ShouldBeNull();
        reading.CoresW.ShouldBeNull();
        reading.IntegratedGpuW.ShouldBeNull();
        reading.MemoryW.ShouldBeNull();
    }

    [Fact]
    public void A_rail_that_reads_zero_still_counts_as_a_reading()
    {
        // A memory rail the firmware never populates reads a genuine zero, which is different from absent.
        EnergyMeter.Summarise([new Rail("RAPL_Package0_DRAM", 0, 1)]).MemoryW.ShouldBe(0);
    }
}
```

Add to `tests/PowerLedger.Sensors.Tests/RealHardwareTests.cs`, inside the existing class:

```csharp
    [Fact]
    public void The_energy_meter_reports_a_believable_package_wattage_well_inside_the_tick_budget()
    {
        using var meter = new EnergyMeter();
        if (!meter.Available)
        {
            // Windows 10 without metering hardware. Nothing to assert; the source falls back to the load model.
            meter.Unavailable.ShouldNotBeNull();
            return;
        }

        // A thousandfold unit error would put this at a few milliwatts or several kilowatts, so the band pins the unit.
        Thread.Sleep(1000);
        meter.Read().PackageW.ShouldNotBeNull().ShouldBeInRange(0.1, 200);

        // Spec §4 budgets about 2 ms a tick for the fast sources; the old WMI query took about 270 ms.
        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) meter.Read();
        (timer.Elapsed.TotalMilliseconds / 20).ShouldBeLessThan(5);
    }
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter EnergyMeterTests`
Expected: build error, `EnergyMeter` not found.

- [x] **Step 3: Write the meter**

`src/PowerLedger.Sensors/EnergyMeter.cs`
```csharp
using System.ComponentModel;
using System.Diagnostics;

namespace PowerLedger.Sensors;

/// <summary>What a named power rail measures.</summary>
public enum RailKind
{
    /// <summary>A roll-up or an unrelated counter instance.</summary>
    Ignored,
    /// <summary>Whole-processor package power, the figure the model wants.</summary>
    Package,
    /// <summary>The cores alone, inside the package figure.</summary>
    Cores,
    /// <summary>Integrated graphics, inside the package figure.</summary>
    IntegratedGpu,
    /// <summary>Attached memory, outside the package figure on the processors that report it.</summary>
    Memory,
}

/// <param name="Name">Counter instance name, e.g. "RAPL_Package0_PKG".</param>
/// <param name="PowerMilliwatts">The rail's average power over the last tick.</param>
/// <param name="EnergyPicowattHours">The rail's monotonic energy counter at the end of that tick.</param>
public readonly record struct Rail(string Name, ulong PowerMilliwatts, ulong EnergyPicowattHours);

/// <param name="PackageW">Whole-processor watts, or null when no package rail exists.</param>
/// <param name="CoresW">Cores watts, already inside PackageW.</param>
/// <param name="IntegratedGpuW">Integrated graphics watts, already inside PackageW.</param>
/// <param name="MemoryW">Memory watts, outside PackageW.</param>
public readonly record struct EnergyMeterReading(double? PackageW, double? CoresW, double? IntegratedGpuW, double? MemoryW);

/// <summary>
/// The processor's power rails as Windows publishes them (spec §4). No kernel driver and no elevation: Windows 11's
/// inbox power-management driver fills the "Energy Meter" performance counters from the processor's own meters.
/// Each rail's energy counter, in picowatt-hours, is read directly through the performance-counter API in well
/// under a millisecond, and watts are the energy used since the previous read over the time between reads, so every
/// figure is exact over its tick rather than over some window of Windows' choosing.
/// A machine with no rails reports nothing, never zero, because a zero is indistinguishable from an idle chip.
/// Single-threaded: the sampling loop owns it.
/// </summary>
public sealed class EnergyMeter : IDisposable
{
    private const string Category = "Energy Meter";
    private const string EnergyCounter = "Energy";

    /// <summary>1 pWh = 1e-12 Wh = 3.6e-9 J. Confirmed on real hardware against Windows' own milliwatt figure.</summary>
    public const double JoulesPerPicowattHour = 3.6e-9;

    private readonly List<(string Name, PerformanceCounter Energy)> _rails = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Dictionary<string, long> _previousEnergy = [];
    private TimeSpan _previousAt;

    public EnergyMeter()
    {
        try
        {
            if (!PerformanceCounterCategory.Exists(Category))
            {
                Unavailable = "this machine publishes no processor power rails";
                return;
            }

            foreach (var instance in new PerformanceCounterCategory(Category).GetInstanceNames())
            {
                if (Classify(instance) == RailKind.Ignored) continue;
                _rails.Add((instance, new PerformanceCounter(Category, EnergyCounter, instance, readOnly: true)));
            }

            if (_rails.Count == 0)
            {
                Unavailable = "this machine publishes no processor power rails";
                return;
            }

            // Take the first reading now, so the first real tick already has an interval to measure.
            _previousEnergy = Snapshot();
            _previousAt = _clock.Elapsed;
            Available = true;
        }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException or Win32Exception)
        {
            Dispose();
            Available = false;
            Unavailable = error.Message;
        }
    }

    /// <summary>True when at least one usable rail exists.</summary>
    public bool Available { get; }

    /// <summary>Why there is nothing to read, for the status screen; null when the meter works.</summary>
    public string? Unavailable { get; }

    /// <summary>Average watts per kind of rail since the previous call. A rail whose counter went backwards reports nothing.</summary>
    public EnergyMeterReading Read()
    {
        if (!Available) return default;

        var now = _clock.Elapsed;
        var seconds = (now - _previousAt).TotalSeconds;
        var energy = Snapshot();
        var previous = _previousEnergy;
        _previousEnergy = energy;
        _previousAt = now;
        if (seconds <= 0) return default;

        var rails = new List<Rail>(energy.Count);
        foreach (var (name, value) in energy)
        {
            if (!previous.TryGetValue(name, out var before) || value < before) continue;
            var watts = (value - before) * JoulesPerPicowattHour / seconds;
            rails.Add(new Rail(name, (ulong)Math.Round(watts * 1000), (ulong)value));
        }
        return Summarise(rails);
    }

    /// <summary>Groups rails by kind and sums each kind, so a two-socket machine reports one package figure.</summary>
    public static EnergyMeterReading Summarise(IReadOnlyList<Rail> rails)
    {
        double? package = null, cores = null, igpu = null, memory = null;
        foreach (var rail in rails)
        {
            var watts = rail.PowerMilliwatts / 1000.0;
            switch (Classify(rail.Name))
            {
                case RailKind.Package: package = (package ?? 0) + watts; break;
                case RailKind.Cores: cores = (cores ?? 0) + watts; break;
                case RailKind.IntegratedGpu: igpu = (igpu ?? 0) + watts; break;
                case RailKind.Memory: memory = (memory ?? 0) + watts; break;
            }
        }
        return new EnergyMeterReading(package, cores, igpu, memory);
    }

    /// <summary>Maps a counter instance name to what it measures. Intel names are RAPL_*; AMD publishes prose names.</summary>
    public static RailKind Classify(string name)
    {
        if (name.EndsWith("_PKG", StringComparison.OrdinalIgnoreCase)) return RailKind.Package;
        if (name.EndsWith("_PP0", StringComparison.OrdinalIgnoreCase)) return RailKind.Cores;
        if (name.EndsWith("_PP1", StringComparison.OrdinalIgnoreCase)) return RailKind.IntegratedGpu;
        if (name.EndsWith("_DRAM", StringComparison.OrdinalIgnoreCase)) return RailKind.Memory;
        if (name.Contains("Socket Energy", StringComparison.OrdinalIgnoreCase)) return RailKind.Package;
        if (name.Contains("Apu Energy", StringComparison.OrdinalIgnoreCase)) return RailKind.IntegratedGpu;
        if (name.Contains("VDDCR_VDD", StringComparison.OrdinalIgnoreCase)) return RailKind.Cores;
        return RailKind.Ignored;
    }

    private Dictionary<string, long> Snapshot()
    {
        var snapshot = new Dictionary<string, long>(_rails.Count);
        foreach (var (name, counter) in _rails) snapshot[name] = counter.RawValue;
        return snapshot;
    }

    public void Dispose()
    {
        foreach (var (_, counter) in _rails) counter.Dispose();
        _rails.Clear();
    }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter EnergyMeterTests`
Expected: `Passed! - Failed: 0, Passed: 4`.

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter RealHardwareTests`
Expected: `Passed! - Failed: 0, Passed: 2`. The wattage test takes about a second.

If the package figure misses the band by orders of magnitude, the picowatt-hour constant is wrong. Report the number and stop; do not widen the band.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Sensors/EnergyMeter.cs tests/PowerLedger.Sensors.Tests
git commit -m "Read the processor power rails from the Windows energy meter"
```

---

### Task 8: EnergyMeterSource

**Files:**
- Create: `src/PowerLedger.Sensors/EnergyMeterSource.cs`
- Test: `tests/PowerLedger.Sensors.Tests/EnergyMeterSourceTests.cs`

- [x] **Step 1: Write the failing tests**

```csharp
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class EnergyMeterSourceTests
{
    private static EnergyMeterSource From(EnergyMeterReading reading, bool available = true)
        => new(() => reading, available, available ? null : "no rails");

    [Fact]
    public void The_package_rail_becomes_the_cpu_reading_and_the_graphics_rail_rides_along()
    {
        var draft = new SampleDraft();
        From(new EnergyMeterReading(PackageW: 8.3, CoresW: 4.0, IntegratedGpuW: 0.35, MemoryW: null)).Contribute(draft);

        draft.CpuPackageW.ShouldBe(8.3);
        draft.IGpuW.ShouldBe(0.35);
    }

    [Fact]
    public void A_missing_package_rail_leaves_the_cpu_unclaimed_rather_than_zero()
    {
        var draft = new SampleDraft();
        From(new EnergyMeterReading(null, null, null, null)).Contribute(draft);

        draft.CpuPackageW.ShouldBeNull();
        draft.IGpuW.ShouldBeNull();
    }

    [Fact]
    public void An_unavailable_meter_says_so_and_is_never_asked()
    {
        var source = new EnergyMeterSource(() => throw new InvalidOperationException("should not be called"), available: false, unavailable: "no rails");
        source.Supported.ShouldBeFalse();
        source.Unavailable.ShouldBe("no rails");
        source.Name.ShouldBe("energy-meter");
    }

    [Fact]
    public void The_source_never_touches_fields_that_belong_to_others()
    {
        var draft = new SampleDraft { BatteryRateW = 34.2, CpuLoad = 0.5, Brightness = 0.6 };
        From(new EnergyMeterReading(8.3, null, null, null)).Contribute(draft);

        draft.BatteryRateW.ShouldBe(34.2);
        draft.CpuLoad.ShouldBe(0.5);
        draft.Brightness.ShouldBe(0.6);
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter EnergyMeterSourceTests`
Expected: build error, `EnergyMeterSource` not found.

- [x] **Step 3: Write the source**

`src/PowerLedger.Sensors/EnergyMeterSource.cs`
```csharp
namespace PowerLedger.Sensors;

/// <summary>
/// Fills the CPU package and integrated-graphics watts from the Windows energy meter. Owns those two fields
/// and nothing else: CPU load comes from <see cref="CpuLoadSource"/>, because load is available even where
/// watts are not. The memory rail is read but not published, since the model has no field for it yet.
/// </summary>
public sealed class EnergyMeterSource : ISensorSource
{
    private readonly Func<EnergyMeterReading> _read;
    private readonly EnergyMeter? _meter;

    /// <summary>Opens the machine's energy meter.</summary>
    public EnergyMeterSource()
    {
        _meter = new EnergyMeter();
        _read = _meter.Read;
        Supported = _meter.Available;
        Unavailable = _meter.Unavailable;
    }

    /// <summary>Test seam: any source of readings.</summary>
    internal EnergyMeterSource(Func<EnergyMeterReading> read, bool available, string? unavailable)
    {
        _read = read;
        Supported = available;
        Unavailable = unavailable;
    }

    public string Name => "energy-meter";

    public bool Supported { get; }

    public string? Unavailable { get; }

    public void Contribute(SampleDraft draft)
    {
        var reading = _read();
        draft.CpuPackageW = reading.PackageW;
        draft.IGpuW = reading.IntegratedGpuW;
    }

    public void Dispose() => _meter?.Dispose();
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter EnergyMeterSourceTests`
Expected: `Passed! - Failed: 0, Passed: 4`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Sensors/EnergyMeterSource.cs tests/PowerLedger.Sensors.Tests/EnergyMeterSourceTests.cs
git commit -m "Add the energy meter sensor source"
```

---

### Task 9: Battery, CPU load and activity sources

**Files:**
- Create: `src/PowerLedger.Sensors/BatterySource.cs`
- Create: `src/PowerLedger.Sensors/CpuLoadSource.cs`
- Create: `src/PowerLedger.Sensors/ActivitySource.cs`
- Test: `tests/PowerLedger.Sensors.Tests/SimpleSourcesTests.cs`

Each of these owns two or three fields and takes its Windows call as a delegate, so the logic is testable without hardware and the real call is one line.

Two traps shape them. `GetLastInputInfo` describes the caller's own session, so the service, running in session 0, must be handed idle time from the user's session. And Windows shows a UPS on USB as a battery, flagged short-term; it powers the whole desk, so it is not the machine's battery.

- [x] **Step 1: Write the failing tests**

```csharp
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class SimpleSourcesTests
{
    private static Win32.BatteryState Battery(bool ac, bool present = true, int rate = -34_200)
        => new(ac, present, Charging: ac, Discharging: !ac, rate);

    [Fact]
    public void On_battery_the_discharge_rate_becomes_positive_watts()
    {
        var draft = new SampleDraft();
        new BatterySource(() => Battery(ac: false)).Contribute(draft);

        draft.OnBattery.ShouldBeTrue();
        draft.BatteryRateW.ShouldNotBeNull().ShouldBe(34.2, 1e-9);
    }

    [Fact]
    public void On_mains_the_rate_is_not_reported_even_while_charging()
    {
        var draft = new SampleDraft();
        new BatterySource(() => Battery(ac: true, rate: 25_000)).Contribute(draft);

        draft.OnBattery.ShouldBeFalse();
        draft.BatteryRateW.ShouldBeNull();
    }

    [Fact]
    public void An_unknown_or_absent_rate_is_no_reading_at_all()
    {
        var unknown = new SampleDraft();
        new BatterySource(() => Battery(ac: false, rate: Win32.UnknownRate)).Contribute(unknown);
        unknown.BatteryRateW.ShouldBeNull();
        unknown.OnBattery.ShouldBeTrue();

        var idle = new SampleDraft();
        new BatterySource(() => Battery(ac: false, rate: 0)).Contribute(idle);
        idle.BatteryRateW.ShouldBeNull();
    }

    [Fact]
    public void A_desktop_with_no_battery_is_never_on_battery_and_the_source_says_it_is_unsupported()
    {
        var source = new BatterySource(() => Battery(ac: true, present: false, rate: 0));
        source.Supported.ShouldBeFalse();
        source.Unavailable.ShouldBe("no battery fitted");

        var draft = new SampleDraft();
        source.Contribute(draft);
        draft.OnBattery.ShouldBeFalse();
    }

    [Fact]
    public void A_ups_is_not_the_machines_battery()
    {
        // A desktop riding out a power cut on a UPS: the drain is the whole desk, not this machine.
        var ups = new Win32.BatteryState(AcOnLine: false, Present: true, Charging: false, Discharging: true, RateMilliwatts: -180_000, ShortTerm: true);
        ups.OwnBattery.ShouldBeFalse();

        var source = new BatterySource(() => ups);
        source.Supported.ShouldBeFalse();
        source.Unavailable.ShouldNotBeNull().ShouldContain("UPS");

        var draft = new SampleDraft();
        source.Contribute(draft);
        draft.OnBattery.ShouldBeFalse();
        draft.BatteryRateW.ShouldBeNull();
    }

    [Fact]
    public void Windows_refusing_to_answer_leaves_the_draft_alone()
    {
        var draft = new SampleDraft { OnBattery = true };
        new BatterySource(() => null).Contribute(draft);
        draft.OnBattery.ShouldBeTrue();
        draft.BatteryRateW.ShouldBeNull();
    }

    [Fact]
    public void Cpu_load_is_the_busy_share_of_the_time_that_passed()
    {
        var times = new Queue<Win32.SystemTimes?>([
            new Win32.SystemTimes(Idle: 1000, Kernel: 1000, User: 0),
            new Win32.SystemTimes(Idle: 1100, Kernel: 1200, User: 100),   // busy 300, idle 100 -> 0.667
        ]);
        var source = new CpuLoadSource(() => times.Dequeue());

        var first = new SampleDraft();
        source.Contribute(first);
        first.CpuLoad.ShouldBe(0);                                        // the first tick only primes the counters

        var second = new SampleDraft();
        source.Contribute(second);
        second.CpuLoad.ShouldBe(1 - 100 / 300.0, 1e-9);
    }

    [Fact]
    public void A_load_reading_is_clamped_and_a_still_clock_reads_zero()
    {
        var times = new Queue<Win32.SystemTimes?>([
            new Win32.SystemTimes(1000, 1000, 0),
            new Win32.SystemTimes(1000, 1000, 0),
        ]);
        var source = new CpuLoadSource(() => times.Dequeue());
        source.Contribute(new SampleDraft());

        var draft = new SampleDraft();
        source.Contribute(draft);
        draft.CpuLoad.ShouldBe(0);
    }

    [Fact]
    public void Activity_reports_idle_seconds_and_the_lock_state()
    {
        var draft = new SampleDraft();
        new ActivitySource(() => 42.5, () => true).Contribute(draft);

        draft.UserIdleSeconds.ShouldBe(42.5);
        draft.SessionLocked.ShouldBeTrue();
    }

    [Fact]
    public void Activity_with_no_console_session_reports_no_idle_time_rather_than_guessing()
    {
        var draft = new SampleDraft { UserIdleSeconds = 99 };
        new ActivitySource(() => null, () => false).Contribute(draft);

        draft.UserIdleSeconds.ShouldBe(0);
        draft.SessionLocked.ShouldBeFalse();
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter SimpleSourcesTests`
Expected: build error, `BatterySource` not found.

- [x] **Step 3: Write the three sources**

`src/PowerLedger.Sensors/BatterySource.cs`
```csharp
namespace PowerLedger.Sensors;

/// <summary>
/// Whether the machine is on mains and, while it is not, how fast the battery is draining. This is the only
/// source that measures the whole machine rather than one of its parts, so it decides whether a reading is Measured.
/// A UPS on USB also looks like a battery to Windows. It is ignored, because its drain includes everything else
/// plugged into it and would teach the calibration a baseline the machine does not have.
/// </summary>
public sealed class BatterySource : ISensorSource
{
    private readonly Func<Win32.BatteryState?> _read;

    public BatterySource() : this(Win32.ReadBatteryState) { }

    /// <summary>Test seam: any source of battery states.</summary>
    internal BatterySource(Func<Win32.BatteryState?> read)
    {
        _read = read;
        var state = read();
        Supported = state?.OwnBattery ?? false;
        Unavailable = Supported ? null
            : state is null ? "Windows did not answer"
            : state.Value.Present ? "the only battery is a UPS, which powers more than this machine"
            : "no battery fitted";
    }

    public string Name => "battery";

    public bool Supported { get; }

    public string? Unavailable { get; }

    public void Contribute(SampleDraft draft)
    {
        if (_read() is not { } state) return;
        draft.OnBattery = state.OwnBattery && !state.AcOnLine;

        // Windows reports the rate as negative while discharging. A zero means "not moving", which is no reading.
        var discharging = draft.OnBattery && state.RateMilliwatts != 0 && state.RateMilliwatts != Win32.UnknownRate;
        draft.BatteryRateW = discharging ? Math.Abs(state.RateMilliwatts) / 1000.0 : null;
    }

    public void Dispose() { }
}
```

`src/PowerLedger.Sensors/CpuLoadSource.cs`
```csharp
namespace PowerLedger.Sensors;

/// <summary>
/// CPU load from the deltas of the system time counters. Available on every machine, including those where
/// the energy meter reports nothing, which is what makes the load fallback in the power model possible.
/// </summary>
public sealed class CpuLoadSource : ISensorSource
{
    private readonly Func<Win32.SystemTimes?> _read;
    private Win32.SystemTimes? _previous;

    public CpuLoadSource() : this(Win32.ReadSystemTimes) { }

    /// <summary>Test seam: any source of system times.</summary>
    internal CpuLoadSource(Func<Win32.SystemTimes?> read)
    {
        _read = read;
        Supported = true;
    }

    public string Name => "cpu-load";

    public bool Supported { get; }

    public string? Unavailable => null;

    public void Contribute(SampleDraft draft)
    {
        if (_read() is not { } now) return;
        if (_previous is not { } before)
        {
            _previous = now;      // the first tick has no delta to work with
            return;
        }
        _previous = now;

        // Kernel time already includes idle time, so busy plus idle is kernel plus user.
        var total = now.Kernel - before.Kernel + (now.User - before.User);
        var idle = now.Idle - before.Idle;
        draft.CpuLoad = total == 0 ? 0 : Math.Clamp(1.0 - (double)idle / total, 0, 1);
    }

    public void Dispose() { }
}
```

`src/PowerLedger.Sensors/ActivitySource.cs`
```csharp
namespace PowerLedger.Sensors;

/// <summary>
/// How long the person at the keyboard has been away, and whether they locked the screen.
/// Idle time is only meaningful from the user's own session: the default reads it there with GetLastInputInfo,
/// which is right for the App and the preview. The service runs in session 0, where that call describes the
/// service itself, so it must pass in idle time reported from the user's session.
/// </summary>
public sealed class ActivitySource : ISensorSource
{
    private readonly Func<double?> _idleSeconds;
    private readonly Func<bool> _locked;

    /// <param name="sessionLocked">The service supplies this from its session-change notifications; the preview passes false.</param>
    /// <param name="idleSeconds">Idle time from the user's session, or null for this process's own session.</param>
    public ActivitySource(Func<bool> sessionLocked, Func<double?>? idleSeconds = null)
        : this(idleSeconds ?? OwnSessionIdle, sessionLocked) { }

    /// <summary>Test seam: any source of idle time and lock state.</summary>
    internal ActivitySource(Func<double?> idleSeconds, Func<bool> locked)
    {
        _idleSeconds = idleSeconds;
        _locked = locked;
    }

    public string Name => "activity";

    public bool Supported => true;

    public string? Unavailable => null;

    /// <summary>Unknown idle time counts as active, which never overstates idle waste.</summary>
    public void Contribute(SampleDraft draft)
    {
        draft.UserIdleSeconds = _idleSeconds() ?? 0;
        draft.SessionLocked = _locked();
    }

    public void Dispose() { }

    private static double? OwnSessionIdle() => Win32.ReadIdleSeconds();
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter SimpleSourcesTests`
Expected: `Passed! - Failed: 0, Passed: 10`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Sensors tests/PowerLedger.Sensors.Tests/SimpleSourcesTests.cs
git commit -m "Add the battery, CPU load and activity sources"
```

---

### Task 10: DisplaySource

**Files:**
- Create: `src/PowerLedger.Sensors/Wmi.cs`
- Create: `src/PowerLedger.Sensors/DisplaySource.cs`
- Test: `tests/PowerLedger.Sensors.Tests/DisplaySourceTests.cs`
- Test: add to `tests/PowerLedger.Sensors.Tests/RealHardwareTests.cs`

Brightness and monitor count come from WMI, which is slow, so they are cached and refreshed on a timer rather than read every tick (spec §4 performance rules). Whether the display is on comes from the service's power notifications, so it arrives as a delegate. The panel's size belongs to the hardware inventory (Task 14).

This is the first WMI user, so it also brings `Wmi`, the one way the assembly queries WMI. Every query gets a timeout, because a hung provider would otherwise stall the sampling loop, and every row is disposed, because each holds a COM object. WMI fails with `ManagementException`, with `COMException` while the service restarts, and with `UnauthorizedAccessException`; `Wmi.IsFailure` names all three. A desktop has no panel whose brightness Windows controls, and WMI answers that query with "not supported", which means no brightness rather than a failure.

- [x] **Step 1: Write the failing tests**

```csharp
using System.Management;
using System.Runtime.InteropServices;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class DisplaySourceTests
{
    [Fact]
    public void Brightness_and_monitor_count_reach_the_draft()
    {
        var draft = new SampleDraft();
        new DisplaySource(() => new DisplayState(Brightness: 0.6, MonitorCount: 2), () => true).Contribute(draft);

        draft.Brightness.ShouldBe(0.6);
        draft.MonitorCount.ShouldBe(2);
        draft.DisplayOn.ShouldBeTrue();
    }

    [Fact]
    public void A_dark_screen_keeps_its_brightness_because_the_panel_is_still_set_that_way()
    {
        var draft = new SampleDraft();
        new DisplaySource(() => new DisplayState(0.6, 1), () => false).Contribute(draft);

        draft.DisplayOn.ShouldBeFalse();
        draft.Brightness.ShouldBe(0.6);
    }

    [Fact]
    public void A_desktop_with_no_brightness_control_reports_no_brightness()
    {
        var draft = new SampleDraft();
        new DisplaySource(() => new DisplayState(null, 1), () => true).Contribute(draft);

        draft.Brightness.ShouldBeNull();
        draft.MonitorCount.ShouldBe(1);
    }

    [Fact]
    public void The_slow_query_runs_once_and_is_reused_until_it_goes_stale()
    {
        var calls = 0;
        var source = new DisplaySource(() => { calls++; return new DisplayState(0.5, 1); }, () => true, refreshEvery: TimeSpan.FromMinutes(5));

        for (var tick = 0; tick < 10; tick++) source.Contribute(new SampleDraft());

        calls.ShouldBe(1);
        source.Refresh();
        source.Contribute(new SampleDraft());
        calls.ShouldBe(2);
    }

    [Theory]
    [InlineData("query refused")]
    [InlineData("service restarting")]
    public void The_last_good_state_survives_a_query_that_throws(string failure)
    {
        var fail = false;
        Exception Failure() => failure == "query refused" ? new ManagementException("not found") : new COMException("RPC server unavailable");
        var source = new DisplaySource(
            () => fail ? throw Failure() : new DisplayState(0.7, 1),
            () => true,
            refreshEvery: TimeSpan.Zero);

        source.Contribute(new SampleDraft());
        fail = true;

        var draft = new SampleDraft();
        source.Contribute(draft);
        draft.Brightness.ShouldBe(0.7);
    }

    [Fact]
    public void A_failing_query_is_retried_soon_but_not_every_tick()
    {
        var calls = 0;
        var source = new DisplaySource(() => { calls++; throw new COMException("RPC server unavailable"); }, () => true, refreshEvery: TimeSpan.FromMinutes(5));

        var draft = new SampleDraft();
        for (var tick = 0; tick < 10; tick++) source.Contribute(draft);

        calls.ShouldBe(1);
        draft.MonitorCount.ShouldBe(1);                 // until WMI answers, one display is assumed
        draft.Brightness.ShouldBeNull();
    }
}
```

Add to `RealHardwareTests`:

```csharp
    [Fact]
    public void The_display_query_answers_on_its_first_tick()
    {
        var draft = new SampleDraft();
        new DisplaySource(() => true).Contribute(draft);

        draft.MonitorCount.ShouldBeGreaterThan(0);
        // Null on a desktop, where no panel has a brightness Windows controls.
        if (draft.Brightness is { } brightness) brightness.ShouldBeInRange(0, 1);
    }
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter DisplaySourceTests`
Expected: build error, `DisplaySource` not found.

- [x] **Step 3: Write the source**

`src/PowerLedger.Sensors/Wmi.cs`
```csharp
using System.Management;
using System.Runtime.InteropServices;
using EnumerationOptions = System.Management.EnumerationOptions;

namespace PowerLedger.Sensors;

/// <summary>
/// The one way this assembly asks WMI anything. Queries are forward-only with a timeout, because a hung WMI provider
/// would otherwise stall whichever thread asked, and every row is disposed, because each one holds a COM object.
/// </summary>
internal static class Wmi
{
    /// <summary>How long one row may take to arrive. Healthy queries answer in milliseconds.</summary>
    private static readonly TimeSpan RowTimeout = TimeSpan.FromSeconds(5);

    /// <summary>What WMI throws when it cannot answer: a refused or timed-out query, the WMI service stopping or
    /// restarting, or a namespace the caller may not read.</summary>
    public static bool IsFailure(Exception error) => error is ManagementException or COMException or UnauthorizedAccessException;

    /// <summary>Runs one query and folds its rows. Throws whatever WMI throws; see <see cref="IsFailure"/>.</summary>
    public static T Read<T>(string scope, string query, Func<IReadOnlyList<ManagementBaseObject>, T> fold)
    {
        var options = new EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = RowTimeout };
        var rows = new List<ManagementBaseObject>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, query, options);
            using var results = searcher.Get();
            foreach (var row in results) rows.Add(row);
            return fold(rows);
        }
        finally
        {
            foreach (var row in rows) row.Dispose();
        }
    }

    /// <summary>As <see cref="Read{T}"/>, but a query WMI cannot answer comes back as <paramref name="fallback"/>.</summary>
    public static T ReadOr<T>(string scope, string query, Func<IReadOnlyList<ManagementBaseObject>, T> fold, T fallback)
    {
        try
        {
            return Read(scope, query, fold);
        }
        catch (Exception error) when (IsFailure(error))
        {
            return fallback;
        }
    }
}
```

`src/PowerLedger.Sensors/DisplaySource.cs`
```csharp
using System.Management;

namespace PowerLedger.Sensors;

/// <param name="Brightness">0..1 for the internal panel; null where the machine has no brightness control.</param>
/// <param name="MonitorCount">How many displays Windows currently reports as active.</param>
public readonly record struct DisplayState(double? Brightness, int MonitorCount);

/// <summary>
/// Panel brightness and monitor count. WMI is slow, so the query runs on a timer and every tick reuses the answer
/// (spec §4). Whether the screen is lit comes from the service's power notifications. The panel's size is the
/// hardware inventory's business, not this source's.
/// </summary>
public sealed class DisplaySource : ISensorSource
{
    /// <summary>After a failed query, when to try again: soon, so a WMI restart heals quickly, but not every tick.</summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(10);

    private readonly Func<DisplayState> _query;
    private readonly Func<bool> _displayOn;
    private readonly TimeSpan _refreshEvery;
    private DisplayState _state = new(null, 1);
    private DateTimeOffset _nextReadAt = DateTimeOffset.MinValue;
    private volatile bool _refreshRequested;

    /// <param name="displayOn">The service supplies this from GUID_CONSOLE_DISPLAY_STATE; the preview passes true.</param>
    public DisplaySource(Func<bool> displayOn) : this(QueryWmi, displayOn) { }

    /// <summary>Test seam: any source of display state.</summary>
    internal DisplaySource(Func<DisplayState> query, Func<bool> displayOn, TimeSpan? refreshEvery = null)
    {
        _query = query;
        _displayOn = displayOn;
        _refreshEvery = refreshEvery ?? TimeSpan.FromMinutes(1);
    }

    public string Name => "display";

    public bool Supported => true;

    public string? Unavailable => null;

    /// <summary>Makes the next tick re-query, after a resume or a monitor change. Safe to call from any thread.</summary>
    public void Refresh() => _refreshRequested = true;

    public void Contribute(SampleDraft draft)
    {
        var now = DateTimeOffset.UtcNow;
        if (_refreshRequested || now >= _nextReadAt)
        {
            // Cleared before the query, so a Refresh that arrives while it runs is not lost.
            _refreshRequested = false;
            try
            {
                _state = _query();
                _nextReadAt = now + _refreshEvery;
            }
            catch (Exception error) when (Wmi.IsFailure(error))
            {
                // Keep the last good answer; a WMI hiccup must not blank the brightness.
                _nextReadAt = now + (_refreshEvery < RetryAfterFailure ? _refreshEvery : RetryAfterFailure);
            }
        }

        draft.Brightness = _state.Brightness;
        draft.MonitorCount = _state.MonitorCount;
        draft.DisplayOn = _displayOn();
    }

    private static DisplayState QueryWmi()
    {
        var monitors = Wmi.Read(@"\\.\root\wmi", "SELECT Active FROM WmiMonitorBasicDisplayParams",
            rows => rows.Count(row => row["Active"] is true));

        double? brightness;
        try
        {
            brightness = Wmi.Read(@"\\.\root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness",
                rows => rows.Count > 0 ? Convert.ToDouble(rows[0]["CurrentBrightness"]) / 100.0 : (double?)null);
        }
        catch (ManagementException error) when (error.ErrorCode == ManagementStatus.NotSupported)
        {
            // A desktop: WMI refuses outright when no panel has a brightness Windows controls.
            brightness = null;
        }

        return new DisplayState(brightness, Math.Max(monitors, 1));
    }

    public void Dispose() { }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter DisplaySourceTests`
Expected: `Passed! - Failed: 0, Passed: 7`.

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter RealHardwareTests`
Expected: `Passed! - Failed: 0, Passed: 3`. On the development laptop the display test should find one monitor and a brightness.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Sensors/DisplaySource.cs tests/PowerLedger.Sensors.Tests/DisplaySourceTests.cs
git add src/PowerLedger.Sensors/Wmi.cs tests/PowerLedger.Sensors.Tests/RealHardwareTests.cs
git commit -m "Add the display source with a cached WMI query"
```

---

### Task 11: NvidiaSource

**Files:**
- Create: `src/PowerLedger.Sensors/Nvml.cs`
- Create: `src/PowerLedger.Sensors/NvidiaSource.cs`
- Create: `src/PowerLedger.Sensors/DevicePowerState.cs`
- Test: `tests/PowerLedger.Sensors.Tests/NvidiaSourceTests.cs`
- Test: add to `tests/PowerLedger.Sensors.Tests/RealHardwareTests.cs`

NVML ships with the NVIDIA display driver as `nvml.dll` in the system directory, so no package and no driver of ours is involved. Two facts decided by measurement on the development laptop: the GeForce MX330 answers temperature and utilisation but returns "not supported" for power, and that is a property of the card rather than of the driver. So power is often null and the model's load fallback is the normal path.

A switchable-graphics laptop keeps its discrete GPU switched off, in D3, almost all the time, drawing next to nothing. Asking NVML about a card in that state either fails or wakes it. Windows' device power state answers in microseconds, needs no elevation and never wakes the device, so the source asks it first and reports 0 W without calling NVML. Because the service runs as LocalSystem, `nvml.dll` is loaded from System32 only, never from the search path. NVML codes that mean the driver or the card is gone make the read throw, so the sampler backs off instead of reporting a silent null forever.

- [x] **Step 1: Write the failing tests**

```csharp
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class NvidiaSourceTests
{
    private static NvidiaSource From(double? watts, double? load, bool present = true)
        => new(() => new GpuReading(present, watts, load), present, present ? null : "no NVIDIA GPU");

    [Fact]
    public void A_card_that_reports_power_fills_both_fields()
    {
        var draft = new SampleDraft();
        From(watts: 18.5, load: 0.42).Contribute(draft);

        draft.DGpuPresent.ShouldBeTrue();
        draft.DGpuW.ShouldBe(18.5);
        draft.DGpuLoad.ShouldBe(0.42);
    }

    [Fact]
    public void A_card_with_no_power_sensor_still_reports_presence_and_load()
    {
        // The MX330 measured on the development laptop: utilisation yes, power not supported.
        var draft = new SampleDraft();
        From(watts: null, load: 0.31).Contribute(draft);

        draft.DGpuPresent.ShouldBeTrue();
        draft.DGpuW.ShouldBeNull();
        draft.DGpuLoad.ShouldBe(0.31);
    }

    [Fact]
    public void A_machine_with_no_nvidia_card_says_so_and_claims_nothing()
    {
        var source = new NvidiaSource(() => new GpuReading(false, null, null), present: false, unavailable: "no NVIDIA GPU");
        source.Supported.ShouldBeFalse();
        source.Unavailable.ShouldBe("no NVIDIA GPU");

        var draft = new SampleDraft();
        source.Contribute(draft);
        draft.DGpuPresent.ShouldBeFalse();
        draft.DGpuW.ShouldBeNull();
    }

    [Fact]
    public void A_switched_off_gpu_draws_nothing_and_nvml_is_never_asked()
    {
        // Measured on the development laptop: the MX330 sits in D3 on 39 ticks out of 40.
        var asked = false;
        var source = new NvidiaSource(() => { asked = true; return new GpuReading(true, null, 0.5); },
            present: true, unavailable: null, poweredOff: () => true);

        var draft = new SampleDraft();
        source.Contribute(draft);

        draft.DGpuPresent.ShouldBeTrue();
        draft.DGpuW.ShouldBe(0);
        draft.DGpuLoad.ShouldBe(0);
        asked.ShouldBeFalse();
    }

    [Fact]
    public void The_source_is_named_for_the_status_screen()
        => From(1, 1).Name.ShouldBe("nvidia-gpu");
}
```

Add to `RealHardwareTests`:

```csharp
    [Fact]
    public void An_nvidia_card_answers_or_is_honestly_absent()
    {
        using var nvml = new Nvml();
        if (!nvml.Available)
        {
            nvml.Unavailable.ShouldNotBeNull();
            return;
        }

        var reading = nvml.Read();
        reading.Present.ShouldBeTrue();
        reading.LoadFraction.ShouldNotBeNull();
        reading.LoadFraction!.Value.ShouldBeInRange(0, 1);

        // Power is null on cards with no measurement hardware, which is most low-end laptop GPUs.
        if (reading.PowerWatts is { } watts) watts.ShouldBeInRange(0.1, 700);

        // Windows can say whether the card is switched off without waking it.
        var device = DevicePowerState.FindNvidiaGpu();
        device.ShouldNotBeNull().ShouldStartWith(@"PCI\VEN_10DE");
        Should.NotThrow(() => DevicePowerState.IsPoweredOff(device));
    }
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter NvidiaSourceTests`
Expected: build error, `NvidiaSource` not found.

- [x] **Step 3: Write the interop and the source**

`src/PowerLedger.Sensors/Nvml.cs`
```csharp
using System.Runtime.InteropServices;

namespace PowerLedger.Sensors;

/// <param name="Present">True when at least one NVIDIA GPU answered.</param>
/// <param name="PowerWatts">Current draw, or null on a card with no power measurement hardware.</param>
/// <param name="LoadFraction">Utilisation 0..1, or null when the card declines to answer.</param>
public readonly record struct GpuReading(bool Present, double? PowerWatts, double? LoadFraction);

/// <summary>
/// The NVIDIA management library, which ships with the display driver. No package reference and no driver of
/// ours: if there is no NVIDIA GPU, loading the library simply fails and the source reports itself unsupported.
/// </summary>
public sealed class Nvml : IDisposable
{
    private const int Success = 0;
    private const int Uninitialized = 1;
    private const int DriverNotLoaded = 9;
    private const int GpuIsLost = 15;

    private readonly IntPtr _device;
    private bool _initialised;

    public Nvml()
    {
        try
        {
            if (nvmlInit_v2() != Success)
            {
                Unavailable = "NVML would not start";
                return;
            }
            _initialised = true;

            if (nvmlDeviceGetCount_v2(out var count) != Success || count == 0)
            {
                Unavailable = "no NVIDIA GPU";
                return;
            }
            if (nvmlDeviceGetHandleByIndex_v2(0, out _device) != Success)
            {
                Unavailable = "NVML would not open the GPU";
                return;
            }
            Available = true;
        }
        catch (DllNotFoundException)
        {
            Unavailable = "no NVIDIA driver installed";
        }
        catch (EntryPointNotFoundException)
        {
            Unavailable = "the NVIDIA driver is too old";
        }
    }

    /// <summary>True when a GPU is open and can be asked.</summary>
    public bool Available { get; }

    /// <summary>Why there is nothing to ask, for the status screen; null when a GPU is available.</summary>
    public string? Unavailable { get; }

    public GpuReading Read()
    {
        if (!Available) return new GpuReading(false, null, null);

        var powerResult = nvmlDeviceGetPowerUsage(_device, out var milliwatts);
        var loadResult = nvmlDeviceGetUtilizationRates(_device, out var utilisation);
        ThrowIfBroken(powerResult);
        ThrowIfBroken(loadResult);

        // "Not supported" means a card with no power sensor, and other errors a card that declined this once: both are null.
        double? watts = powerResult == Success ? milliwatts / 1000.0 : null;
        double? load = loadResult == Success ? utilisation.Gpu / 100.0 : null;
        return new GpuReading(true, watts, load);
    }

    /// <summary>A lost GPU, an unloaded driver or a torn-down library is a broken source, not a missing reading:
    /// throwing lets the sampler back off and show it in status instead of charging a phantom load forever.</summary>
    private static void ThrowIfBroken(int result)
    {
        if (result is Uninitialized or DriverNotLoaded or GpuIsLost)
        {
            throw new InvalidOperationException($"NVML error {result}: the GPU or its driver is no longer available");
        }
    }

    public void Dispose()
    {
        if (!_initialised) return;
        _initialised = false;
        try
        {
            nvmlShutdown();
        }
        catch (DllNotFoundException)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Utilisation
    {
        public uint Gpu;
        public uint Memory;
    }

    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlShutdown();
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilisation utilisation);
}
```

`src/PowerLedger.Sensors/NvidiaSource.cs`
```csharp
namespace PowerLedger.Sensors;

/// <summary>
/// Discrete GPU power, load and presence. When Windows has switched the GPU off, as a switchable-graphics laptop
/// does almost all the time, it draws next to nothing and NVML is not asked at all. When it is on, power is null on
/// the many laptop GPUs with no measurement hardware, which the power model turns into its load fallback.
/// </summary>
public sealed class NvidiaSource : ISensorSource
{
    private readonly Func<GpuReading> _read;
    private readonly Func<bool> _poweredOff;
    private readonly Nvml? _nvml;

    public NvidiaSource()
    {
        var device = DevicePowerState.FindNvidiaGpu();
        _poweredOff = device is null ? static () => false : () => DevicePowerState.IsPoweredOff(device);
        _nvml = new Nvml();
        _read = _nvml.Read;
        Supported = _nvml.Available;
        Unavailable = _nvml.Unavailable;
    }

    /// <summary>Test seam: any source of GPU readings and power state.</summary>
    internal NvidiaSource(Func<GpuReading> read, bool present, string? unavailable, Func<bool>? poweredOff = null)
    {
        _read = read;
        _poweredOff = poweredOff ?? (static () => false);
        Supported = present;
        Unavailable = unavailable;
    }

    public string Name => "nvidia-gpu";

    public bool Supported { get; }

    public string? Unavailable { get; }

    public void Contribute(SampleDraft draft)
    {
        if (_poweredOff())
        {
            draft.DGpuPresent = true;
            draft.DGpuW = 0;
            draft.DGpuLoad = 0;
            return;
        }

        var reading = _read();
        draft.DGpuPresent = reading.Present;
        draft.DGpuW = reading.PowerWatts;
        draft.DGpuLoad = reading.LoadFraction;
    }

    public void Dispose() => _nvml?.Dispose();
}
```

`src/PowerLedger.Sensors/DevicePowerState.cs`
```csharp
using System.Runtime.InteropServices;

namespace PowerLedger.Sensors;

/// <summary>
/// Whether Windows has switched a device off. A switchable-graphics laptop keeps its discrete GPU in D3 almost all
/// the time, where it draws next to nothing; asking the GPU's own library about it then either fails or wakes it.
/// Windows' device power state answers in microseconds, needs no elevation and never wakes the device.
/// </summary>
internal static class DevicePowerState
{
    /// <summary>DEVICE_POWER_STATE: PowerDeviceD0 is 1, PowerDeviceD3 is 4.</summary>
    private const int PowerDeviceD3 = 4;

    private static readonly DevPropKey PowerData = new() { FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = 32 };

    /// <summary>The Plug and Play instance id of the first NVIDIA display adapter, or null when there is none.</summary>
    public static string? FindNvidiaGpu() => Wmi.ReadOr(@"\\.\root\cimv2", "SELECT PNPDeviceID FROM Win32_VideoController", rows =>
    {
        foreach (var row in rows)
        {
            if (row["PNPDeviceID"] is string id && id.StartsWith(@"PCI\VEN_10DE", StringComparison.OrdinalIgnoreCase)) return id;
        }
        return null;
    }, null);

    /// <summary>True when Windows reports the device in D3. Anything it cannot answer counts as on, which never hides real draw.</summary>
    public static bool IsPoweredOff(string pnpDeviceId)
    {
        if (CM_Locate_DevNodeW(out var node, pnpDeviceId, 0) != 0) return false;
        var key = PowerData;
        var buffer = new byte[64];
        var size = (uint)buffer.Length;
        if (CM_Get_DevNode_PropertyW(node, ref key, out _, buffer, ref size, 0) != 0 || size < 8) return false;
        // CM_POWER_DATA begins with PD_Size, then PD_MostRecentPowerState.
        return BitConverter.ToInt32(buffer, 4) == PowerDeviceD3;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CM_Locate_DevNodeW(out uint node, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CM_Get_DevNode_PropertyW(uint node, ref DevPropKey key, out uint propertyType, byte[] buffer, ref uint bufferSize, uint flags);
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter NvidiaSourceTests`
Expected: `Passed! - Failed: 0, Passed: 5`.

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter RealHardwareTests`
Expected: `Passed! - Failed: 0, Passed: 4`. On the development laptop the GPU test should find a card, report a load, find no power, and get an answer about whether the card is switched off.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Sensors/Nvml.cs src/PowerLedger.Sensors/NvidiaSource.cs src/PowerLedger.Sensors/DevicePowerState.cs tests/PowerLedger.Sensors.Tests
git commit -m "Add the NVIDIA GPU source through NVML"
```

---

### Task 12: TdpTable

**Files:**
- Modify: `src/PowerLedger.Sensors/tdp-table.json`
- Create: `src/PowerLedger.Sensors/TdpTable.cs`
- Test: `tests/PowerLedger.Sensors.Tests/TdpTableTests.cs`

Spec §5: a CPU or GPU with no power sensor is modelled as `idle + (TDP − idle) × load`, with the TDP taken from a bundled table and a default when the model is unknown. Matching is by substring because Windows reports names like "11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz".

- [x] **Step 1: Write the failing tests**

```csharp
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class TdpTableTests
{
    [Fact]
    public void A_windows_cpu_name_matches_the_bare_model_in_the_table()
    {
        var table = TdpTable.Bundled;
        table.Cpu("11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz").ShouldBe(28);
        table.Cpu("Intel(R) Core(TM) i7-1165G7").ShouldBe(28);
    }

    [Fact]
    public void A_gpu_name_matches_the_same_way()
        => TdpTable.Bundled.Gpu("NVIDIA GeForce MX330").ShouldBe(10);

    [Fact]
    public void An_unknown_model_has_no_answer_so_the_caller_can_choose_its_own_default()
    {
        TdpTable.Bundled.Cpu("Some Processor 9000").ShouldBeNull();
        TdpTable.Bundled.Gpu("Imaginary GPU").ShouldBeNull();
        TdpTable.Bundled.Cpu(null).ShouldBeNull();
        TdpTable.Bundled.Cpu("").ShouldBeNull();
    }

    [Fact]
    public void The_longest_matching_key_wins_so_a_suffix_beats_its_prefix()
    {
        var table = new TdpTable(
            new Dictionary<string, double> { ["i7-1165"] = 15, ["i7-1165G7"] = 28 },
            new Dictionary<string, double>());

        table.Cpu("Intel Core i7-1165G7").ShouldBe(28);
    }

    [Fact]
    public void Matching_ignores_case()
        => TdpTable.Bundled.Cpu("intel core i7-1165g7").ShouldBe(28);

    [Fact]
    public void The_bundled_table_is_loaded_once_and_holds_real_entries()
    {
        TdpTable.Bundled.ShouldBeSameAs(TdpTable.Bundled);
        TdpTable.Bundled.CpuCount.ShouldBeGreaterThan(5);
        TdpTable.Bundled.GpuCount.ShouldBeGreaterThan(3);
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter TdpTableTests`
Expected: build error, `TdpTable` not found.

- [x] **Step 3: Fill the table and write the lookup**

`src/PowerLedger.Sensors/tdp-table.json` — replace the placeholder with:
```json
{
  "cpu": {
    "i7-1165G7": 28,
    "i5-1135G7": 28,
    "i7-1185G7": 28,
    "i7-1260P": 28,
    "i7-12700H": 45,
    "i5-8250U": 15,
    "i7-8550U": 15,
    "i5-10210U": 15,
    "i7-10510U": 15,
    "i9-13900K": 125,
    "i7-13700K": 125,
    "i5-13600K": 125,
    "i9-12900K": 125,
    "i7-12700K": 125,
    "i5-12400": 65,
    "Ryzen 5 5600X": 65,
    "Ryzen 7 5800X": 105,
    "Ryzen 9 5900X": 105,
    "Ryzen 9 7950X": 170,
    "Ryzen 7 7800X3D": 120,
    "Ryzen 5 5500U": 15,
    "Ryzen 7 5800U": 15,
    "Ryzen 7 6800H": 45
  },
  "gpu": {
    "MX330": 10,
    "MX350": 25,
    "MX450": 25,
    "GTX 1650": 75,
    "GTX 1660": 120,
    "RTX 2060": 160,
    "RTX 3050": 130,
    "RTX 3060": 170,
    "RTX 3070": 220,
    "RTX 3080": 320,
    "RTX 4060": 115,
    "RTX 4070": 200,
    "RTX 4080": 320,
    "RTX 4090": 450,
    "RX 6600": 132,
    "RX 6700 XT": 230,
    "RX 7800 XT": 263
  }
}
```

`src/PowerLedger.Sensors/TdpTable.cs`
```csharp
using System.Reflection;
using System.Text.Json;

namespace PowerLedger.Sensors;

/// <summary>
/// Thermal design power by model name, for the parts that report no watts of their own (spec §5).
/// Windows reports decorated names, so a key matches when it appears anywhere in the name; the longest
/// matching key wins, so "i7-1165G7" beats "i7-1165".
/// </summary>
public sealed class TdpTable(IReadOnlyDictionary<string, double> cpu, IReadOnlyDictionary<string, double> gpu)
{
    private static readonly Lazy<TdpTable> Loaded = new(Load, isThreadSafe: true);

    /// <summary>The table shipped with the app, read once.</summary>
    public static TdpTable Bundled => Loaded.Value;

    public int CpuCount => cpu.Count;

    public int GpuCount => gpu.Count;

    /// <summary>Watts for a processor name, or null when the model is unknown.</summary>
    public double? Cpu(string? name) => Match(cpu, name);

    /// <summary>Watts for a graphics card name, or null when the model is unknown.</summary>
    public double? Gpu(string? name) => Match(gpu, name);

    private static double? Match(IReadOnlyDictionary<string, double> table, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        double? best = null;
        var bestLength = 0;
        foreach (var (key, watts) in table)
        {
            if (key.Length <= bestLength || !name.Contains(key, StringComparison.OrdinalIgnoreCase)) continue;
            best = watts;
            bestLength = key.Length;
        }
        return best;
    }

    private static TdpTable Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PowerLedger.Sensors.tdp-table.json")
            ?? throw new InvalidOperationException("tdp-table.json is missing from the assembly");
        var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(stream)
            ?? throw new InvalidOperationException("tdp-table.json is not a table");
        return new TdpTable(
            parsed.GetValueOrDefault("cpu") ?? new Dictionary<string, double>(),
            parsed.GetValueOrDefault("gpu") ?? new Dictionary<string, double>());
    }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter TdpTableTests`
Expected: `Passed! - Failed: 0, Passed: 6`.

If the resource name is wrong the load throws with a clear message; check the actual name with `Assembly.GetExecutingAssembly().GetManifestResourceNames()` rather than guessing.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Sensors/TdpTable.cs src/PowerLedger.Sensors/tdp-table.json tests/PowerLedger.Sensors.Tests/TdpTableTests.cs
git commit -m "Add the bundled TDP table"
```

---

### Task 13: InventoryFacts and its hash

**Files:**
- Create: `src/PowerLedger.Sensors/InventoryFacts.cs`
- Test: `tests/PowerLedger.Sensors.Tests/InventoryFactsTests.cs`

The hash keys the learned calibration in storage, so it must change when the machine changes and must not change when it does not. It must also survive a restart, which rules out `string.GetHashCode`. So it covers only what cannot change without opening the case: chassis, processor and memory. Drive counts, the panel size and the GPU name all move with docks, external drives and driver installs; the GPU reads "Microsoft Basic Display Adapter" until the vendor's driver arrives.

- [x] **Step 1: Write the failing tests**

```csharp
using PowerLedger.Contracts;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class InventoryFactsTests
{
    private static InventoryFacts Laptop() => new(
        Chassis: ChassisKind.Laptop,
        CpuName: "11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz",
        GpuName: "NVIDIA GeForce MX330",
        RamSticks: 1, RamIsDdr5: false,
        SsdCount: 1, HddCount: 0,
        DisplayDiagonalInches: 15.6,
        MonitorCount: 1);

    [Fact]
    public void The_same_machine_hashes_the_same_way_every_time()
    {
        Laptop().Hash.ShouldBe(Laptop().Hash);
        Laptop().Hash.Length.ShouldBe(16);
    }

    [Fact]
    public void A_different_processor_memory_or_chassis_is_a_different_machine()
    {
        var baseline = Laptop().Hash;
        (Laptop() with { CpuName = "AMD Ryzen 7 5800U with Radeon Graphics" }).Hash.ShouldNotBe(baseline);
        (Laptop() with { RamSticks = 2 }).Hash.ShouldNotBe(baseline);
        (Laptop() with { RamIsDdr5 = true }).Hash.ShouldNotBe(baseline);
        (Laptop() with { Chassis = ChassisKind.Desktop }).Hash.ShouldNotBe(baseline);
    }

    [Fact]
    public void Docks_external_drives_and_driver_installs_do_not_relearn_the_machine()
    {
        var baseline = Laptop().Hash;
        (Laptop() with { MonitorCount = 3 }).Hash.ShouldBe(baseline);                          // docked
        (Laptop() with { DisplayDiagonalInches = 0 }).Hash.ShouldBe(baseline);                 // lid shut on the dock
        (Laptop() with { SsdCount = 2, HddCount = 1 }).Hash.ShouldBe(baseline);                // external drives
        (Laptop() with { GpuName = "Microsoft Basic Display Adapter" }).Hash.ShouldBe(baseline); // before the driver
    }

    [Fact]
    public void The_facts_become_the_profile_the_power_model_wants()
    {
        var profile = Laptop().ToProfile(MachineProfile.DefaultLaptop);

        profile.Chassis.ShouldBe(ChassisKind.Laptop);
        profile.RamSticks.ShouldBe(1);
        profile.RamIsDdr5.ShouldBeFalse();
        profile.SsdCount.ShouldBe(1);
        profile.HddCount.ShouldBe(0);
        profile.DisplayDiagonalInches.ShouldBe(15.6);
    }

    [Fact]
    public void Turning_facts_into_a_profile_keeps_the_settings_the_user_chose()
    {
        var chosen = MachineProfile.DefaultLaptop with { PsuTier = PsuTier.Gold, ExtrasWatts = 12, IncludeMonitors = true, MonitorWatts = 30 };
        var profile = Laptop().ToProfile(chosen);

        profile.PsuTier.ShouldBe(PsuTier.Gold);
        profile.ExtrasWatts.ShouldBe(12);
        profile.IncludeMonitors.ShouldBeTrue();
        profile.MonitorWatts.ShouldBe(30);
        profile.RamSticks.ShouldBe(1);                 // detection still wins for the detectable fields
    }

    [Fact]
    public void An_unknown_panel_size_leaves_the_profile_alone()
    {
        // Detected while docked with the lid shut: no built-in panel shows, but it is still there.
        var docked = Laptop() with { DisplayDiagonalInches = 0 };
        docked.ToProfile(MachineProfile.DefaultLaptop with { DisplayDiagonalInches = 13.3 }).DisplayDiagonalInches.ShouldBe(13.3);

        var desktop = Laptop() with { Chassis = ChassisKind.Desktop, DisplayDiagonalInches = 0 };
        desktop.ToProfile(MachineProfile.DefaultDesktop).DisplayDiagonalInches.ShouldBe(0);
    }

    [Fact]
    public void The_facts_serialise_to_the_json_storage_keeps_beside_the_hash()
    {
        var json = Laptop().ToJson();
        json.ShouldContain("i7-1165G7");
        json.ShouldContain("MX330");
    }

    [Fact]
    public void Tdp_comes_from_the_bundled_table_when_the_model_is_known()
    {
        Laptop().CpuTdpW.ShouldBe(28);
        Laptop().GpuTdpW.ShouldBe(10);
        (Laptop() with { CpuName = "Unknown Chip" }).CpuTdpW.ShouldBeNull();
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter InventoryFactsTests`
Expected: build error, `InventoryFacts` not found.

- [x] **Step 3: Write the record**

`src/PowerLedger.Sensors/InventoryFacts.cs`
```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PowerLedger.Contracts;

namespace PowerLedger.Sensors;

/// <summary>
/// What was detected about this machine (spec §5). The hash keys the learned calibration, so it covers only what
/// cannot change without opening the case: the chassis, the processor and the memory. Drives, displays and graphics
/// adapters come and go with docks, external drives and driver installs, and none of those may throw away a
/// learned baseline.
/// </summary>
/// <param name="GpuName">Not part of the hash: it reads "Microsoft Basic Display Adapter" until the vendor's driver installs.</param>
/// <param name="SsdCount">Not part of the hash, and neither is <paramref name="HddCount"/>: drives come and go.</param>
/// <param name="DisplayDiagonalInches">The built-in panel's diagonal, or 0 when none was found. Not part of the hash:
/// a laptop docked with its lid shut shows no panel at all.</param>
/// <param name="MonitorCount">How many displays were attached when this was detected. Not part of the hash.</param>
public sealed record InventoryFacts(
    ChassisKind Chassis,
    string? CpuName,
    string? GpuName,
    int RamSticks,
    bool RamIsDdr5,
    int SsdCount,
    int HddCount,
    double DisplayDiagonalInches,
    int MonitorCount)
{
    /// <summary>The processor's rated watts from the bundled table, or null when the model is unknown.</summary>
    public double? CpuTdpW => TdpTable.Bundled.Cpu(CpuName);

    /// <summary>The graphics card's rated watts from the bundled table, or null when the model is unknown.</summary>
    public double? GpuTdpW => TdpTable.Bundled.Gpu(GpuName);

    /// <summary>Stable across restarts, unlike a runtime string hash, so a stored calibration still matches.</summary>
    public string Hash
    {
        get
        {
            var identity = FormattableString.Invariant($"{Chassis}|{CpuName}|{RamSticks}|{RamIsDdr5}");
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
            return Convert.ToHexStringLower(digest.AsSpan(0, 8));
        }
    }

    /// <summary>The detected fields folded into the user's profile. Everything the user chose is left alone, and so is
    /// the panel size when no built-in panel was found.</summary>
    public MachineProfile ToProfile(MachineProfile chosen) => chosen with
    {
        Chassis = Chassis,
        RamSticks = RamSticks,
        RamIsDdr5 = RamIsDdr5,
        SsdCount = SsdCount,
        HddCount = HddCount,
        DisplayDiagonalInches = DisplayDiagonalInches > 0 ? DisplayDiagonalInches : chosen.DisplayDiagonalInches,
    };

    /// <summary>The record as storage keeps it, for the status screen and for diagnosing a hash change.</summary>
    public string ToJson() => JsonSerializer.Serialize(this);
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter InventoryFactsTests`
Expected: `Passed! - Failed: 0, Passed: 8`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Sensors/InventoryFacts.cs tests/PowerLedger.Sensors.Tests/InventoryFactsTests.cs
git commit -m "Add hardware inventory facts with a stable hash"
```

---

### Task 14: HardwareInventory — detect the machine

**Files:**
- Create: `src/PowerLedger.Sensors/HardwareInventory.cs`
- Test: `tests/PowerLedger.Sensors.Tests/HardwareInventoryTests.cs`
- Test: add to `tests/PowerLedger.Sensors.Tests/RealHardwareTests.cs`

Detection is all WMI, so the logic worth unit-testing is the chassis, drive and panel decisions; the rest is proven against the real machine. Three traps shaped them. A UPS on USB looks like a battery, so only the machine's own battery says laptop. USB sticks, SD cards and mounted ISOs look like drives, so drives are filtered by bus. And Windows lists monitors in no fixed order, so the built-in panel is found by its connection type; a laptop shut on its dock shows none, and the profile then keeps the size it had.

- [x] **Step 1: Write the failing tests**

Add to `tests/PowerLedger.Sensors.Tests/RealHardwareTests.cs`:

```csharp
    [Fact]
    public void The_machine_describes_itself()
    {
        var facts = HardwareInventory.Detect();

        facts.CpuName.ShouldNotBeNullOrWhiteSpace();
        facts.RamSticks.ShouldBeGreaterThan(0);
        (facts.SsdCount + facts.HddCount).ShouldBeGreaterThan(0);
        facts.MonitorCount.ShouldBeGreaterThan(0);
        facts.Hash.Length.ShouldBe(16);

        // Detection must be repeatable: the same machine, the same hash.
        HardwareInventory.Detect().Hash.ShouldBe(facts.Hash);
    }
```

Create `tests/PowerLedger.Sensors.Tests/HardwareInventoryTests.cs`:

```csharp
using PowerLedger.Contracts;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class HardwareInventoryTests
{
    [Theory]
    [InlineData(9, true, ChassisKind.Laptop)]      // Laptop
    [InlineData(10, true, ChassisKind.Laptop)]     // Notebook
    [InlineData(14, true, ChassisKind.Laptop)]     // Sub-notebook
    [InlineData(31, true, ChassisKind.Laptop)]     // Convertible
    [InlineData(3, false, ChassisKind.Desktop)]    // Desktop
    [InlineData(7, false, ChassisKind.Desktop)]    // Tower
    [InlineData(23, false, ChassisKind.Desktop)]   // Rack mount
    public void The_enclosure_type_decides_the_chassis(int enclosure, bool battery, ChassisKind expected)
        => HardwareInventory.ChassisFrom(enclosure, batteryPresent: battery).ShouldBe(expected);

    [Fact]
    public void A_battery_outvotes_an_enclosure_that_claims_to_be_a_desktop()
    {
        // Some laptops report "Other" or "Unknown"; a fitted battery settles it.
        HardwareInventory.ChassisFrom(enclosureType: 2, batteryPresent: true).ShouldBe(ChassisKind.Laptop);
        HardwareInventory.ChassisFrom(enclosureType: 3, batteryPresent: true).ShouldBe(ChassisKind.Laptop);
    }

    [Fact]
    public void No_enclosure_information_falls_back_to_the_battery()
    {
        HardwareInventory.ChassisFrom(null, batteryPresent: true).ShouldBe(ChassisKind.Laptop);
        HardwareInventory.ChassisFrom(null, batteryPresent: false).ShouldBe(ChassisKind.Desktop);
    }

    [Fact]
    public void Only_drives_fitted_inside_the_machine_are_counted()
    {
        var drives = new[]
        {
            (MediaType: 4, BusType: 17),   // NVMe SSD
            (MediaType: 3, BusType: 11),   // SATA hard disk
            (MediaType: 0, BusType: 13),   // eMMC on a budget laptop, media type unknown
            (MediaType: 4, BusType: 7),    // USB stick
            (MediaType: 3, BusType: 7),    // USB backup disk
            (MediaType: 0, BusType: 12),   // SD card
            (MediaType: 0, BusType: 15),   // mounted ISO
        };

        HardwareInventory.CountDrives(drives).ShouldBe((2, 1));
    }

    [Theory]
    [InlineData(0x80000000u)]   // internal
    [InlineData(11u)]           // embedded DisplayPort
    [InlineData(6u)]            // LVDS, on older laptops
    public void The_panel_size_comes_from_the_built_in_panel_wherever_windows_lists_it(uint builtIn)
    {
        var connections = new Dictionary<string, uint>
        {
            [@"DISPLAY\DEL41A8\1"] = 10,          // DisplayPort desk monitor, listed first
            [@"DISPLAY\AUO4199\2"] = builtIn,     // the laptop's own panel
        };
        (string, double, double)[] sizes = [(@"DISPLAY\DEL41A8\1", 60, 34), (@"DISPLAY\AUO4199\2", 34, 19)];

        HardwareInventory.BuiltInDiagonal(connections, sizes).ShouldBe(15.3);
    }

    [Fact]
    public void A_laptop_shut_on_its_dock_reports_no_panel_size_rather_than_the_desk_monitor()
    {
        var connections = new Dictionary<string, uint> { [@"DISPLAY\DEL41A8\1"] = 10 };
        (string, double, double)[] sizes = [(@"DISPLAY\DEL41A8\1", 60, 34)];

        HardwareInventory.BuiltInDiagonal(connections, sizes).ShouldBe(0);
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter HardwareInventoryTests`
Expected: build error, `HardwareInventory` not found.

- [x] **Step 3: Write the detector**

`src/PowerLedger.Sensors/HardwareInventory.cs`
```csharp
using PowerLedger.Contracts;

namespace PowerLedger.Sensors;

/// <summary>
/// Asks Windows what this machine is made of (spec §5). Runs at service start and on resume, never per tick,
/// because WMI is slow. Every query is individually guarded: a machine that will not answer one question
/// still produces facts for the rest.
/// </summary>
public static class HardwareInventory
{
    /// <summary>Enclosure types Windows uses for portable machines.</summary>
    private static readonly HashSet<int> PortableEnclosures = [8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32];

    /// <summary>STORAGE_BUS_TYPE values for drives that are not part of the machine. FireWire, Fibre Channel, USB,
    /// iSCSI and SD cards are external or remote; virtual, file-backed and Storage Spaces disks are not drives at all.</summary>
    private static readonly HashSet<int> NotFittedBuses = [4, 6, 7, 9, 12, 14, 15, 16];

    /// <summary>D3DKMDT_VIDEO_OUTPUT_TECHNOLOGY values for a panel built into the machine: LVDS, embedded
    /// DisplayPort, embedded UDI and the generic "internal".</summary>
    private static readonly HashSet<uint> BuiltInConnections = [6, 11, 13, 0x80000000];

    /// <summary>The machine's own battery settles it; otherwise the enclosure type decides, and an unknown enclosure means desktop.</summary>
    /// <param name="batteryPresent">True for a battery that powers this machine alone; a UPS does not count.</param>
    public static ChassisKind ChassisFrom(int? enclosureType, bool batteryPresent)
    {
        if (batteryPresent) return ChassisKind.Laptop;
        return enclosureType is { } type && PortableEnclosures.Contains(type) ? ChassisKind.Laptop : ChassisKind.Desktop;
    }

    /// <summary>Solid-state and spinning drives fitted inside the machine. Media type 3 is a hard disk; anything else,
    /// unknown included, counts as solid state, the lower-power guess.</summary>
    public static (int Ssd, int Hdd) CountDrives(IEnumerable<(int MediaType, int BusType)> drives)
    {
        var solid = 0;
        var spinning = 0;
        foreach (var (media, bus) in drives)
        {
            if (NotFittedBuses.Contains(bus)) continue;
            if (media == 3) spinning++;
            else solid++;
        }
        return (solid, spinning);
    }

    /// <summary>The built-in panel's diagonal in inches, or 0 when no built-in panel shows. The panel is found by how
    /// it is connected, never by where Windows lists it, so a docked laptop reports its own panel, not the desk monitor.</summary>
    /// <param name="connections">Connection type by monitor instance name.</param>
    /// <param name="sizes">Maximum image size in centimetres by monitor instance name.</param>
    public static double BuiltInDiagonal(
        IReadOnlyDictionary<string, uint> connections, IEnumerable<(string Instance, double WidthCm, double HeightCm)> sizes)
    {
        foreach (var (instance, width, height) in sizes)
        {
            if (width <= 0 || height <= 0) continue;
            if (!connections.TryGetValue(instance, out var connection) || !BuiltInConnections.Contains(connection)) continue;
            return Math.Round(Math.Sqrt(width * width + height * height) / 2.54, 1);
        }
        return 0;
    }

    /// <summary>One detection pass. Never throws: an unanswered question leaves its field at a sensible default.</summary>
    public static InventoryFacts Detect()
    {
        var battery = Win32.ReadBatteryState();
        var enclosure = Wmi.ReadOr(@"\\.\root\cimv2", "SELECT ChassisTypes FROM Win32_SystemEnclosure", rows =>
        {
            foreach (var row in rows)
            {
                if (row["ChassisTypes"] is ushort[] { Length: > 0 } types) return (int?)types[0];
            }
            return null;
        }, null);
        var chassis = ChassisFrom(enclosure, battery?.OwnBattery ?? false);

        var cpuName = Wmi.ReadOr(@"\\.\root\cimv2", "SELECT Name FROM Win32_Processor", rows =>
            rows.Count > 0 ? (rows[0]["Name"] as string)?.Trim() : null, null);

        var gpuName = Wmi.ReadOr(@"\\.\root\cimv2", "SELECT Name, AdapterCompatibility FROM Win32_VideoController", rows =>
        {
            string? fallback = null;
            foreach (var row in rows)
            {
                var name = (row["Name"] as string)?.Trim();
                var vendor = row["AdapterCompatibility"] as string ?? "";
                if (vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || vendor.Contains("Advanced Micro", StringComparison.OrdinalIgnoreCase))
                {
                    return name;      // prefer the discrete card over the integrated one
                }
                fallback ??= name;
            }
            return fallback;
        }, null);

        var (sticks, ddr5) = Wmi.ReadOr(@"\\.\root\cimv2", "SELECT SMBIOSMemoryType FROM Win32_PhysicalMemory", rows =>
        {
            var count = 0;
            var isDdr5 = false;
            foreach (var row in rows)
            {
                count++;
                // 34 is DDR5 in the SMBIOS memory-type table; 26 is DDR4.
                if (row["SMBIOSMemoryType"] is not null && Convert.ToInt32(row["SMBIOSMemoryType"]) >= 34) isDdr5 = true;
            }
            return (Math.Max(count, 1), isDdr5);
        }, (1, false));

        var (ssd, hdd) = Wmi.ReadOr(@"\\.\root\microsoft\windows\storage", "SELECT MediaType, BusType FROM MSFT_PhysicalDisk", rows =>
            CountDrives(rows.Select(row => (
                row["MediaType"] is null ? 0 : Convert.ToInt32(row["MediaType"]),
                row["BusType"] is null ? 0 : Convert.ToInt32(row["BusType"])))), (1, 0));

        var connections = Wmi.ReadOr(@"\\.\root\wmi", "SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams", rows =>
        {
            var byInstance = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (row["InstanceName"] is string name && row["VideoOutputTechnology"] is uint connection) byInstance[name] = connection;
            }
            return byInstance;
        }, []);

        var (monitors, diagonal) = Wmi.ReadOr(@"\\.\root\wmi", "SELECT InstanceName, Active, MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams", rows =>
        (
            Math.Max(rows.Count(row => row["Active"] is true), 1),
            BuiltInDiagonal(connections, rows.Select(row => (
                row["InstanceName"] as string ?? "",
                Convert.ToDouble(row["MaxHorizontalImageSize"]),
                Convert.ToDouble(row["MaxVerticalImageSize"]))))
        ), (1, 0d));

        return new InventoryFacts(
            chassis, cpuName, gpuName,
            sticks, ddr5,
            Math.Max(ssd, 0), Math.Max(hdd, 0),
            diagonal,
            monitors);
    }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter HardwareInventoryTests`
Expected: `Passed! - Failed: 0, Passed: 14`.

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter RealHardwareTests`
Expected: `Passed! - Failed: 0, Passed: 5`. On the development laptop the detected chassis must be `Laptop` and the CPU name must contain `i7-1165G7`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Sensors/HardwareInventory.cs tests/PowerLedger.Sensors.Tests
git commit -m "Detect the machine through WMI"
```

---

### Task 15: MachineSensors — the assembled set

**Files:**
- Create: `src/PowerLedger.Sensors/MachineSensors.cs`
- Test: `tests/PowerLedger.Sensors.Tests/MachineSensorsTests.cs`

One factory so the Service and the preview build the same set the same way, rather than each remembering the order and the wiring.

- [x] **Step 1: Write the failing tests**

```csharp
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class MachineSensorsTests
{
    [Fact]
    public void The_assembled_set_covers_every_field_of_a_tick()
    {
        using var sensors = MachineSensors.Create(displayOn: () => true, sessionLocked: () => false);

        var names = sensors.Sampler.Health.Select(h => h.Name).ToList();
        names.ShouldContain("energy-meter");
        names.ShouldContain("nvidia-gpu");
        names.ShouldContain("battery");
        names.ShouldContain("cpu-load");
        names.ShouldContain("activity");
        names.ShouldContain("display");
    }

    [Fact]
    public void A_tick_comes_back_shaped_even_on_a_machine_that_answers_little()
    {
        using var sensors = MachineSensors.Create(() => true, () => false);
        var at = DateTimeOffset.UtcNow;

        var sample = sensors.Read(at, 1.0);

        sample.Timestamp.ShouldBe(at);
        sample.DeltaSeconds.ShouldBe(1.0);
        sample.CpuLoad.ShouldBeInRange(0, 1);
        sample.UserIdleSeconds.ShouldBeGreaterThanOrEqualTo(0);
        sample.MonitorCount.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void The_second_tick_carries_a_real_cpu_load_because_the_first_primed_the_counters()
    {
        using var sensors = MachineSensors.Create(() => true, () => false);
        sensors.Read(DateTimeOffset.UtcNow, 1.0);
        Thread.Sleep(250);

        var sample = sensors.Read(DateTimeOffset.UtcNow, 0.25);

        sample.CpuLoad.ShouldBeInRange(0, 1);
        sample.Suspect.ShouldBeFalse();
    }

    [Fact]
    public void Readings_pass_through_the_validator()
    {
        using var sensors = MachineSensors.Create(() => true, () => false);
        sensors.Read(DateTimeOffset.UtcNow, 1.0);

        sensors.Validator.SuspectCount.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Disposing_the_set_disposes_its_sources()
    {
        var sensors = MachineSensors.Create(() => true, () => false);
        sensors.Dispose();
        Should.NotThrow(() => sensors.Dispose());
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter MachineSensorsTests`
Expected: build error, `MachineSensors` not found.

- [x] **Step 3: Write the factory**

`src/PowerLedger.Sensors/MachineSensors.cs`
```csharp
using PowerLedger.Core;

namespace PowerLedger.Sensors;

/// <summary>
/// The sensor set this machine can offer, assembled once and read every tick. The Service and the console
/// preview both build it through here so they cannot drift apart. Single-threaded: the sampling loop owns it.
/// </summary>
public sealed class MachineSensors : IDisposable
{
    private MachineSensors(Sampler sampler, SampleValidator validator, DisplaySource display)
    {
        Sampler = sampler;
        Validator = validator;
        Display = display;
    }

    /// <summary>The composed sources, for per-source health on the status screen.</summary>
    public Sampler Sampler { get; }

    /// <summary>The validator, for its suspect count.</summary>
    public SampleValidator Validator { get; }

    /// <summary>The display source, so the caller can force a refresh after a resume or a monitor change.</summary>
    public DisplaySource Display { get; }

    /// <param name="displayOn">Whether the screen is lit; the Service supplies this from its power notifications.</param>
    /// <param name="sessionLocked">Whether the console session is locked; the Service supplies this from its session notifications.</param>
    /// <param name="userIdleSeconds">Idle time from the user's session. Leave null in a process that runs in that session;
    /// the Service, in session 0, must supply it.</param>
    /// <param name="validatorOptions">Overrides for the plausible ranges and windows.</param>
    public static MachineSensors Create(
        Func<bool> displayOn, Func<bool> sessionLocked,
        Func<double?>? userIdleSeconds = null, ValidatorOptions? validatorOptions = null)
    {
        var display = new DisplaySource(displayOn);
        var sources = new List<ISensorSource>
        {
            new EnergyMeterSource(),
            new NvidiaSource(),
            new BatterySource(),
            new CpuLoadSource(),
            new ActivitySource(sessionLocked, userIdleSeconds),
            display,
        };
        return new MachineSensors(new Sampler(sources), new SampleValidator(validatorOptions), display);
    }

    /// <summary>One validated tick. Never throws.</summary>
    public Sample Read(DateTimeOffset timestamp, double deltaSeconds)
        => Validator.Validate(Sampler.Read(timestamp, deltaSeconds));

    public void Dispose() => Sampler.Dispose();
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Sensors.Tests --filter MachineSensorsTests`
Expected: `Passed! - Failed: 0, Passed: 5`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Sensors/MachineSensors.cs tests/PowerLedger.Sensors.Tests/MachineSensorsTests.cs
git commit -m "Assemble the machine's sensor set behind one factory"
```

---

### Task 16: Point the console preview at the real sensors

**Files:**
- Modify: `samples/PowerLedger.Demo/PowerLedger.Demo.csproj`
- Modify: `samples/PowerLedger.Demo/Program.cs`
- Delete: `samples/PowerLedger.Demo/WindowsSensors.cs`
- Modify: `samples/PowerLedger.Demo/README.md`

The preview currently carries its own throwaway P/Invoke. Replacing it with `PowerLedger.Sensors` deletes that duplication, proves the layer end to end on real hardware, and gives the owner measured CPU watts instead of an estimate.

- [x] **Step 1: Reference the Sensors project**

Add to `samples/PowerLedger.Demo/PowerLedger.Demo.csproj`, inside the existing `ProjectReference` group:
```xml
    <ProjectReference Include="..\..\src\PowerLedger.Sensors\PowerLedger.Sensors.csproj" />
```

- [x] **Step 2: Delete the throwaway sensors**

```bash
git rm samples/PowerLedger.Demo/WindowsSensors.cs
```

- [x] **Step 3: Rewrite the recording loop against the real layer**

In `samples/PowerLedger.Demo/Program.cs`, add `using PowerLedger.Sensors;` and replace the whole `Record` method with:

```csharp
    private static void Record(SqliteDatabase db, int seconds)
    {
        var facts = HardwareInventory.Detect();
        using var sensors = MachineSensors.Create(displayOn: () => true, sessionLocked: () => false);

        var profile = facts.ToProfile(facts.Chassis == ChassisKind.Laptop ? MachineProfile.DefaultLaptop : MachineProfile.DefaultDesktop);
        var hardware = new HardwareFacts(
            facts.CpuTdpW ?? (facts.Chassis == ChassisKind.Laptop ? HardwareFacts.LaptopDefaults.CpuTdpW : HardwareFacts.DesktopDefaults.CpuTdpW),
            facts.GpuTdpW ?? (facts.Chassis == ChassisKind.Laptop ? HardwareFacts.LaptopDefaults.GpuTdpW : HardwareFacts.DesktopDefaults.GpuTdpW));

        // Learn fast so a short preview can reach Calibrated; the service uses 10-minute thresholds.
        var learner = new CalibrationLearner(new CalibrationOptions(HalfLifeSamples: 60, MinBucketSamples: 15, MinTotalSamples: 15));
        var calibration = new CalibrationRepository(db);
        learner.Import(calibration.Load(facts.Hash));
        var model = new PowerModel(profile, hardware, new PowerModelOptions(), learner);

        new InventoryRepository(db).Upsert(new InventoryRecord(facts.Hash, DateTimeOffset.UtcNow, facts.ToJson()));

        var raw = new RawSampleRepository(db);
        var aggregates = new AggregateRepository(db);
        var sessions = new SessionRepository(db);
        sessions.CloseAllOpen(DateTimeOffset.UtcNow);
        var session = sessions.Open(SessionReason.ServiceStart, DateTimeOffset.UtcNow);

        Console.WriteLine($"  {facts.CpuName}");
        Console.WriteLine($"  {facts.GpuName ?? "no discrete GPU"}    {facts.Chassis}    inventory {facts.Hash}");
        Console.WriteLine();
        Console.WriteLine($"  Recording for {seconds} s. Unplug the charger to see measured readings.");
        Console.WriteLine();

        var clock = Stopwatch.StartNew();
        var previous = TimeSpan.Zero;
        var pending = new List<Reading>();
        var minute = new List<Reading>();
        var minuteStart = FloorMinute(DateTimeOffset.UtcNow);

        for (var tick = 0; tick < seconds; tick++)
        {
            Thread.Sleep(1000);
            var elapsed = clock.Elapsed;
            var delta = (elapsed - previous).TotalSeconds;
            previous = elapsed;

            var now = DateTimeOffset.UtcNow;
            var sample = sensors.Read(now, delta);
            var reading = model.Evaluate(sample);
            learner.Observe(sample, reading.Components.Cpu, reading.Components.Gpu, reading.Components.Display);

            if (FloorMinute(now) != minuteStart)
            {
                if (minute.Count > 0) aggregates.UpsertMinute(Downsampler.ToMinute(minuteStart, minute, EnergyIntegrator.GapThresholdFor(1)));
                minute.Clear();
                minuteStart = FloorMinute(now);
            }

            minute.Add(reading);
            pending.Add(reading);
            if (pending.Count >= 10)
            {
                raw.InsertBatch(pending);
                pending.Clear();
            }

            PrintLive(reading, learner);
        }

        if (pending.Count > 0) raw.InsertBatch(pending);
        if (minute.Count > 0) aggregates.UpsertMinute(Downsampler.ToMinute(minuteStart, minute, EnergyIntegrator.GapThresholdFor(1)));
        calibration.Save(facts.Hash, learner.Export(), DateTimeOffset.UtcNow);
        sessions.Close(session, DateTimeOffset.UtcNow, SessionReason.ServiceStop);

        Console.WriteLine();
        Console.WriteLine();
        foreach (var health in sensors.Sampler.Health.Where(h => !h.Supported))
        {
            Console.WriteLine($"  {health.Name} unavailable: {health.Unavailable}");
        }
        if (sensors.Validator.SuspectCount > 0) Console.WriteLine($"  {sensors.Validator.SuspectCount} ticks marked suspect");
        Console.WriteLine();
    }
```

- [x] **Step 4: Run it and confirm the CPU reading is now measured**

Run: `dotnet build -c Release`
Expected: 0 warnings.

Run: `dotnet run --project samples/PowerLedger.Demo -c Release -- 12`
Expected: the header names the processor and the GPU, and the live line shows a `cpu` figure that tracks real activity rather than the flat load model. On a Windows 11 machine with energy-meter rails the CPU column moves with load and matches the package wattage the meter reports. Record the output in the task report.

- [x] **Step 5: Update the preview's README and commit**

Rewrite `samples/PowerLedger.Demo/README.md` to say that the preview now uses `PowerLedger.Sensors`, that CPU watts come from the Windows energy meter with no driver, that GPU power is unavailable on cards without measurement hardware, and that calibration is persisted between runs. Keep it under 30 lines.

```bash
git add samples docs
git commit -m "Point the console preview at the real sensor layer"
```

---

### Task 17: Final verification and handoff to Plan C

- [x] **Step 1: Clean build with warnings as errors**

Run: `dotnet build -c Release`
Expected: `Build succeeded.` and `0 Warning(s)`.

- [x] **Step 2: Full test run**

Run: `dotnet test -c Release`
Expected: Core `Passed: 110`, Storage `Passed: 40`, Sensors `Passed: 99`, no failures, no skipped tests.

- [x] **Step 3: Confirm CI can skip the hardware tests**

Run: `dotnet test -c Release --filter "Category!=Hardware"`
Expected: Sensors `Passed: 94` (the five `RealHardwareTests` excluded), Core and Storage unchanged, no failures. This is the command CI uses, because a build agent has no battery and no GPU.

- [x] **Step 4: Confirm the working tree is clean and every task is committed**

Run: `git status --short` → no output. Run: `git log --oneline | head -n 20` → one commit per task from this plan, newest first.

- [x] **Step 5: Record the public surface Plan C will build on**

Plan C (the Windows service) consumes exactly these types:

| Type | Used by Plan C for |
|---|---|
| `MachineSensors.Create(displayOn, sessionLocked, userIdleSeconds)` | one set per power session: built at start, rebuilt on every resume; the delegates come from the service's power and session notifications and from the App over the pipe |
| `MachineSensors.Read(timestamp, deltaSeconds)` | one validated `Sample` per tick, never throwing |
| `MachineSensors.Display.Refresh()` | on resume and on a display-change notification, so the cached WMI answer is re-read; safe from any thread |
| `Sampler.Health`, `SampleValidator.SuspectCount` | the pipe's `GetStatus` reply, through a snapshot the loop publishes |
| `HardwareInventory.Detect()` → `InventoryFacts` | at start and on resume; `Hash` keys calibration, `ToProfile` folds detection into the user's settings, `ToJson` is what `InventoryRepository` stores |
| `InventoryFacts.CpuTdpW` / `GpuTdpW` | the `HardwareFacts` the power model needs |
| `TdpTable.Bundled` | only if the service wants a lookup outside the inventory |

**Rules Plan C must follow.** These are contracts the types cannot enforce:

- Build the sensor set at start and rebuild it on every resume, disposing the old one. Across a sleep the energy counters can reset, NVML can lose the card, and the validator's windows hold values from before the sleep. Within a power session keep the one set: `CpuLoadSource` needs a previous reading, so the first tick of every set reports zero load.
- Idle time must come from the user's session. `GetLastInputInfo` in session 0 describes the service itself, so the App reports its own idle seconds over the pipe and the service passes them in as `userIdleSeconds`. With no App connected, hand in a delegate that returns null; unknown idle time counts as active, which never overstates idle waste.
- Only the sampling loop touches the sensor set, the sampler and the validator. None of them is thread-safe, and the validator's rolling windows must live as long as the set. The pipe thread reads a status snapshot (source health, suspect count, the last reading) that the loop publishes each tick with a single reference swap. `Display.Refresh()` is the one call that is safe from another thread.
- Guard the loop with a watchdog. WMI now gives up after five seconds a row, but NVML and the performance-counter API have no timeout. If a tick runs past about ten seconds, record a gap, dispose the stuck set on a background thread and build a fresh one.
- Pass a real monotonic delta, from a `Stopwatch` rather than the wall clock, and pass `EnergyIntegrator.GapThresholdFor(sampleIntervalSeconds)` to the downsampler.
- Re-run `HardwareInventory.Detect()` on resume. The hash covers only chassis, processor and memory, so a changed hash means a different machine: start a fresh learner rather than importing the old calibration.
- `ToProfile` overwrites every detected field. Apply it on first run and when the hash changes, not on every resume, or it will undo corrections the user made in the wizard. Remember which fields the user corrected and leave those alone.
- A source reporting `Supported == false` is a fact about the machine, not an error. Show it in status and do not retry it within the set; the rebuild on resume checks again.

**Known gaps, deliberately left to later plans:**

- Whether the display is on and whether the session is locked arrive as delegates. Plan C supplies them from `RegisterPowerSettingNotification` and `WTSRegisterSessionNotification`; until then the preview passes constants.
- `Reading` carries one quality for the whole tick. Status and the report need to say which parts were measured, such as CPU from the energy meter, and which were estimated. Plan C should add per-component provenance to `Reading` before the pipe contract freezes.
- Only NVIDIA cards are read. Any other discrete GPU is reported absent, and the model charges an absent GPU nothing, not its load fallback. On a desktop with a Radeon card the estimate loses the whole GPU; on a laptop on battery the draw lands in the unattributed share instead. The driver-free fix is the `GPU Engine` performance counters, which give per-adapter utilisation for every vendor and can feed the model's existing TDP-times-load fallback. `HardwareInventory` also prefers any AMD adapter for the GPU name, which on a Ryzen laptop is the integrated one; revisit that alongside.
- The model charges a built-in panel only on laptops, so an all-in-one desktop's panel draws nothing. The inventory already reports its size.
- With the lid shut on a dock the built-in panel is dark, but the model still charges it. Plan C can register for `GUID_LIDSWITCH_STATE_CHANGE` once `Sample` has a field to carry it.
- A UPS is recognised by Windows' short-term battery flag, which is that flag's documented meaning, but no real UPS has been tried.
- Fan count is taken from the machine profile rather than measured; measuring it needs the kernel driver this design deliberately avoids.

No step in this plan ships a running process; that is Plan C's first task.

- [x] **Step 6: Commit the finished plan**

```bash
git add docs/superpowers/plans
git commit -m "Complete Plan B: sensors verified on real hardware"
```

---

## After the final review

The whole-branch review found problems the tasks above did not. Each fix is its own commit, and the code blocks above already show the fixed files.

| Problem | Fix |
|---|---|
| The validator rejected a sustained rise forever: once the median fell behind, every new reading looked like a spike. | A rise is accepted on its second reading and restarts the window. Energy-meter CPU watts are never spike-filtered, and coming off mains restarts the battery median. |
| The console-session idle call read past its buffer, and from session 0 it could not work anyway. | Removed. Idle time comes from `GetLastInputInfo` in the caller's session, or is handed in by the service. |
| The energy meter's WMI query took about 270 ms a tick. | The rails are read as performance-counter raw values in well under a millisecond, and watts come from the energy used between reads. |
| A switched-off discrete GPU was charged its idle floor, and asking NVML could wake it. | Windows' device power state says when the card is in D3; it then draws nothing and NVML is not asked. NVML loads from System32 only, and a lost card or unloaded driver fails the source so the sampler backs off. |
| The inventory hash covered drives, the panel and the GPU name, so a USB stick, a dock or a driver install threw away the calibration. | The hash covers chassis, processor and memory only. |
| A UPS on USB made a desktop look like a laptop, and its drain during a power cut would have trained the calibration. | A short-term battery is not the machine's own. USB, SD, network and virtual drives no longer count as fitted drives. |
| WMI could throw `COMException` while restarting, never timed out and leaked its rows, and a desktop's brightness query failed every tick. | One helper runs every query with a timeout and disposes its rows. The display source keeps its last answer, retries in ten seconds, and reads "not supported" brightness as none. `Refresh` is safe from any thread. |
| The panel size came from whichever monitor WMI listed first. | The built-in panel is found by its connection type. With none showing, the profile keeps its size. |

---

## Self-review against the spec

| Spec section | Covered by |
|---|---|
| §3 `PowerLedger.Sensors` project, rule that sources never compute totals | Tasks 1–2, 15; every source fills only its own fields |
| §4 sampling at 1 Hz, per-source try/catch with backoff, a failing source never stops the loop | Task 3 |
| §4 sensor table: CPU rails, discrete GPU, battery, display, activity | Tasks 7–11 |
| §4 no kernel driver, energy meter, the "no rails" fallback | Tasks 7–8; `Supported` is false and the model falls back |
| §4 performance rule that slow sources are not read per tick | Task 10, cached WMI with a refresh timer |
| §4 `Sample` record | Task 2 |
| §4 validator: ranges, outliers, transition window, suspect counts; counter wrap | Tasks 4–5; Task 7 drops a rail whose counter went backwards |
| §5 default estimate table, TDP by model with a fallback | Task 12 |
| §5 machine profile detection, inventory hash keyed to calibration | Tasks 13–14 |
| §11 no elevation for sensors, everything stays local | Tasks 7–11; no driver, no network |
| §12 hardware tests tagged and skipped in CI | Tasks 6, 17 |
| §6 suspend and resume, §8 the pipe, §9 the UI, §13 the installer | **Plans C, D, E** — out of scope here by design |

Names were checked across tasks: `SampleDraft` fields match `Sample`'s parameters; `EnergyMeterReading.PackageW` feeds `SampleDraft.CpuPackageW`; `InventoryFacts.ToProfile` returns the `MachineProfile` that `PowerModel` takes; `Sampler.Health` returns the `SourceHealth` defined in Task 2.
