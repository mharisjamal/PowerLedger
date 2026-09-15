# PowerLedger Plan C — Service — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `PowerLedger.Service`: the Windows service that samples the machine every second, stores the readings, folds them into minute and hour rows, keeps the sessions timeline through sleep and shutdown, and serves live readings, status and settings to the App over a local named pipe.

**Architecture:** One hosted `SamplingLoop` owns everything stateful: the sensor set (behind a watchdog thread), the power model, the calibration learner, the write buffer, the sessions and the settings. It is the only writer of readings, rollups, sessions and settings. Other threads reach it through a command inbox (suspend, resume, settings, calibration reset) and read only what it publishes (a status snapshot and a live feed). Windows notifications arrive through the power manager's callback registrations and the service control handler. The pipe server is its own hosted service. Pipe messages and settings live in `PowerLedger.Contracts`, so the App (Plan D) shares them.

**Tech Stack:** .NET 10 worker (`Microsoft.Extensions.Hosting.WindowsServices` 10.0.12), `System.IO.Pipes` with ACLs, `System.Text.Json` source generation, Serilog file logging (`Serilog.Extensions.Hosting` 10.0.0, `Serilog.Sinks.File` 7.0.0), Plan A's `PowerLedger.Storage` and Plan B's `PowerLedger.Sensors`. Tests: xUnit, Shouldly, `Microsoft.Extensions.TimeProvider.Testing` 10.10.0. Spec: `docs/superpowers/specs/2026-09-08-powerledger-design.md` §3, §6–§8 and §10–§12.

**Git rule (from the owner):** commit locally after every task. Never add a remote, push, or create a GitHub repo.

**Scope note:** this plan ends with a service that runs from the console or under the service control manager, plus a development script that installs it. The App is Plan D and the installer is Plan E.

---

## What already exists

`main` holds Plans A and B:

- `PowerLedger.Contracts`: `Quality`, `SessionReason`, `ChassisKind`, `PsuTier`, `MachineProfile`.
- `PowerLedger.Core`: `Sample`, `Reading`, `Components`, `PowerModel`, `CalibrationLearner`, `EnergyIntegrator`, `Downsampler`, `Aggregate`, `TariffSchedule`.
- `PowerLedger.Storage`: `SqliteDatabase`, one repository per table, `RetentionJob`, `ReportQueries`.
- `PowerLedger.Sensors`: `MachineSensors`, `HardwareInventory`, `InventoryFacts`.
- `samples/PowerLedger.Demo`, the console preview.

Tests: 110 Core, 40 Storage, 99 Sensors. The Release build has zero warnings.

The service must follow the handoff rules at the end of Plan A (Task 17) and Plan B (Task 17 and "After the final review"). This plan implements them. Each rule is named where it applies.

## Decisions made while planning

Each was checked on the development laptop on 2026-09-12, unelevated.

- **No PRESHUTDOWN hook.** `ServiceBase` exposes none, so the final flush rides on SHUTDOWN. A minute of readings writes in milliseconds, well inside the shutdown allowance.
- **Suspend, resume and display state come from power-manager callbacks.** `PowerRegisterSuspendResumeNotification` and `PowerSettingRegisterNotification(GUID_CONSOLE_DISPLAY_STATE)` with `DEVICE_NOTIFY_CALLBACK` work unelevated and deliver the current display state at once. `ServiceBase.OnPowerEvent` throws away the event data, so it cannot report the display state. The callbacks also work in a console run.
- **Session lock state comes from the service control manager's session events.** A console run assumes the session is unlocked.
- **Idle time comes from the App.** The service in session 0 cannot see input, so the App sends a `reportActivity` message every few seconds. With no App reporting, idle time is unknown, which counts as active.
- **Users get read access to the data folder, not modify.** A read-only SQLite connection reads a WAL database whose `-wal` and `-shm` it cannot write (tested with SQLite 3.51). A database users could edit would feed crafted input to a SYSTEM process. The service refuses a data folder another account owns and sets aside database files another account owns. This revises spec §7.
- **Pipe ACL.** Local authenticated users with read and write can connect in both directions. A second `FirstPipeInstance` of the same name is refused, so no other process can serve the name while the service runs. A server that is not SYSTEM needs its own account granted full control to add instances, which matters for console runs and tests.

## File structure

```
src/PowerLedger.Contracts/
  ServiceSettings.cs            what the App may change, with range checks
  PipeMessages.cs               every message kind, polymorphic on "type"
  ReadingFrame.cs               one tick as the App sees it
  ServiceStatus.cs              the status screen's data
  PipeProtocol.cs               pipe name, 64 KB limit, JSON source generation, settings JSON
  MessageChannel.cs             newline-delimited framing over any stream
src/PowerLedger.Storage/
  RawSampleRepository.cs        + Latest(), for closing a crashed session at its last tick
src/PowerLedger.Service/
  PowerLedger.Service.csproj    worker SDK, net10.0-windows
  Program.cs                    entry point: exit codes and fatal logging
  ServiceHost.cs                host composition, configuration, logging
  ServicePaths.cs               where the files live; --data overrides
  DataDirectory.cs              folder ownership checks and ACL
  DatabaseOpener.cs             open, check, set aside a damaged database
  SettingsStore.cs              settings persistence plus ProfilePolicy
  ModelFactory.cs               settings + inventory → PowerModel
  ServiceSignals.cs             display, lock and idle inputs from other threads
  TickClock.cs                  Δt per tick; the wall clock across a resume
  ISensorSet.cs                 the sensor layer as the loop sees it; the real adapter
  SensorWorker.cs               dedicated sensor thread with a watchdog
  WriteBuffer.cs                batched raw writes that survive a full disk
  Rollups.cs                    minute and hour folding, catch-up at start
  Retention.cs                  03:00 purge and weekly vacuum
  SessionTracker.cs             the sessions timeline
  CalibrationKeeper.cs          the learner per hardware hash, saved on a timer
  LiveFeed.cs                   per-subscriber frame queues
  StatusBoard.cs                the latest status and settings
  Frames.cs                     Core and Sensors types → Contracts DTOs
  LoopCommands.cs               the loop's inbox
  SamplingLoop.cs               the loop itself
  PipeHandler.cs                request → reply
  PipeServer.cs                 the named pipe, its ACL and per-client tasks
  PowerNotifications.cs         suspend, resume and display callbacks
  PowerLedgerServiceLifetime.cs session lock and shutdown from the SCM
  README.md                     run modes, flags, files
tests/PowerLedger.Service.Tests/
  TestDatabase.cs, Samples.cs, Facts.cs, Readings.cs, FakeSensorSet.cs, WaitFor.cs   helpers
  one test file per unit, plus SamplingLoopTests (the spec §12 ten-minute run) and ServiceHostTests (hardware)
scripts/
  dev-service.ps1               install, remove, start and stop the service (elevated)
  pipe-status.ps1               ask a running service for its status
```

**Rules.** The loop is the only code that touches the sensor set, model, learner, buffer, sessions and settings. Other threads send it a command or read what it published. The Contracts project stays free of dependencies, because the App references it. Nothing that arrives over the pipe names a file or runs a command.

---

### Task 1: Scaffold the Service projects

**Files:**
- Create: `src/PowerLedger.Service/PowerLedger.Service.csproj`
- Create: `src/PowerLedger.Service/Program.cs`
- Create: `tests/PowerLedger.Service.Tests/PowerLedger.Service.Tests.csproj`
- Modify: `PowerLedger.sln`

- [x] **Step 1: Write the service project**

`src/PowerLedger.Service/PowerLedger.Service.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk.Worker">

  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <OutputType>Exe</OutputType>
    <InvariantGlobalization>true</InvariantGlobalization>
    <ServerGarbageCollection>false</ServerGarbageCollection>
    <ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>
    <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" Version="10.0.12" />
    <PackageReference Include="Serilog.Extensions.Hosting" Version="10.0.0" />
    <PackageReference Include="Serilog.Sinks.File" Version="7.0.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\PowerLedger.Contracts\PowerLedger.Contracts.csproj" />
    <ProjectReference Include="..\PowerLedger.Core\PowerLedger.Core.csproj" />
    <ProjectReference Include="..\PowerLedger.Sensors\PowerLedger.Sensors.csproj" />
    <ProjectReference Include="..\PowerLedger.Storage\PowerLedger.Storage.csproj" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="PowerLedger.Service.Tests" />
  </ItemGroup>

</Project>
```

Workstation GC without the background thread, invariant globalization and English-only satellite assemblies keep the service inside the spec §12 memory budget of 50 MB.

`src/PowerLedger.Service/Program.cs`
```csharp
namespace PowerLedger.Service;

/// <summary>Placeholder until Task 21 composes the host.</summary>
internal static class Program
{
    private static int Main() => 0;
}
```

- [x] **Step 2: Write the test project**

`tests/PowerLedger.Service.Tests/PowerLedger.Service.Tests.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.10.0" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="Shouldly" Version="4.3.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\PowerLedger.Service\PowerLedger.Service.csproj" />
    <ProjectReference Include="..\..\src\PowerLedger.Sensors\PowerLedger.Sensors.csproj" />
    <ProjectReference Include="..\..\src\PowerLedger.Storage\PowerLedger.Storage.csproj" />
    <ProjectReference Include="..\..\src\PowerLedger.Core\PowerLedger.Core.csproj" />
    <ProjectReference Include="..\..\src\PowerLedger.Contracts\PowerLedger.Contracts.csproj" />
  </ItemGroup>

</Project>
```

- [x] **Step 3: Add both to the solution**

```bash
dotnet sln PowerLedger.sln add src/PowerLedger.Service/PowerLedger.Service.csproj --solution-folder src
dotnet sln PowerLedger.sln add tests/PowerLedger.Service.Tests/PowerLedger.Service.Tests.csproj --solution-folder tests
```

- [x] **Step 4: Build and run the empty suite**

Run: `dotnet build -c Release`
Expected: `Build succeeded.` with 0 warnings.

Run: `dotnet test tests/PowerLedger.Service.Tests -c Release`
Expected: the Service test run reports that no test is available.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Service tests/PowerLedger.Service.Tests PowerLedger.sln
git commit -m "Add the Service project and its tests"
```

---

### Task 2: ServiceSettings — what the App may change

**Files:**
- Create: `src/PowerLedger.Contracts/ServiceSettings.cs`
- Test: `tests/PowerLedger.Service.Tests/ServiceSettingsTests.cs`

Spec §8 limits `SetSettings` to the tariff, the machine profile, the idle threshold, the sample interval and retention, each range-checked. The tariff travels on its own message (Task 3), because it appends a row rather than replacing a value.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.Service.Tests/ServiceSettingsTests.cs`
```csharp
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class ServiceSettingsTests
{
    [Fact]
    public void The_defaults_are_the_spec_values_and_pass_their_own_checks()
    {
        var settings = ServiceSettings.Default;
        settings.IdleThresholdSeconds.ShouldBe(300);
        settings.SampleIntervalSeconds.ShouldBe(1);
        settings.RawRetentionHours.ShouldBe(48);
        settings.HistoryRetentionYears.ShouldBe(2);
        settings.Validate().ShouldBeNull();
        (settings with { Profile = MachineProfile.DefaultDesktop }).Validate().ShouldBeNull();
    }

    [Theory]
    [InlineData(59, 1, 48, 2)]
    [InlineData(1801, 1, 48, 2)]
    [InlineData(300, 0, 48, 2)]
    [InlineData(300, 6, 48, 2)]
    [InlineData(300, 1, 23, 2)]
    [InlineData(300, 1, 169, 2)]
    [InlineData(300, 1, 48, 0)]
    [InlineData(300, 1, 48, 6)]
    public void Each_number_is_held_to_its_range(int idle, int interval, int rawHours, int years)
        => new ServiceSettings { IdleThresholdSeconds = idle, SampleIntervalSeconds = interval, RawRetentionHours = rawHours, HistoryRetentionYears = years }
            .Validate().ShouldNotBeNull();

    [Theory]
    [InlineData(60, 1, 24, 1)]
    [InlineData(1800, 5, 168, 5)]
    public void The_ends_of_each_range_are_accepted(int idle, int interval, int rawHours, int years)
        => new ServiceSettings { IdleThresholdSeconds = idle, SampleIntervalSeconds = interval, RawRetentionHours = rawHours, HistoryRetentionYears = years }
            .Validate().ShouldBeNull();

    [Fact]
    public void A_profile_with_an_impossible_value_is_rejected()
    {
        var laptop = MachineProfile.DefaultLaptop;
        With(laptop with { ExtrasWatts = double.NaN }).ShouldNotBeNull();
        With(laptop with { ExtrasWatts = -1 }).ShouldNotBeNull();
        With(laptop with { SsdCount = -1 }).ShouldNotBeNull();
        With(laptop with { RamSticks = 0 }).ShouldNotBeNull();
        With(laptop with { Chassis = (ChassisKind)7 }).ShouldNotBeNull();
        With(laptop with { PsuTier = (PsuTier)9 }).ShouldNotBeNull();
        With(laptop with { DisplayDiagonalInches = 5 }).ShouldNotBeNull();
        With(laptop with { CpuTdpOverrideW = 0 }).ShouldNotBeNull();
        With(laptop with { MonitorWatts = double.PositiveInfinity }).ShouldNotBeNull();
    }

    [Fact]
    public void Values_that_mean_unknown_or_none_are_accepted()
        => With(MachineProfile.DefaultLaptop with { DisplayDiagonalInches = 0, CpuTdpOverrideW = null, GpuTdpOverrideW = 75, SsdCount = 0 })
            .ShouldBeNull();

    [Fact]
    public void A_missing_profile_is_rejected_rather_than_crashing()
        => (ServiceSettings.Default with { Profile = null! }).Validate().ShouldNotBeNull();

    private static string? With(MachineProfile profile) => (ServiceSettings.Default with { Profile = profile }).Validate();
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter ServiceSettingsTests`
Expected: build error, `ServiceSettings` not found.

- [x] **Step 3: Write the settings**

`src/PowerLedger.Contracts/ServiceSettings.cs`
```csharp
namespace PowerLedger.Contracts;

/// <summary>
/// What the App may change over the pipe (spec §8): the machine profile, the idle threshold, the sample interval and
/// how long history is kept. Tariffs travel on their own message. The service accepts nothing until
/// <see cref="Validate"/> passes, and nothing here names a file or a command. Init-only properties with initialisers,
/// so settings stored by an older version take the default for anything added since.
/// </summary>
public sealed record ServiceSettings
{
    public const int MinIdleThresholdSeconds = 60;
    public const int MaxIdleThresholdSeconds = 1800;
    public const int MinSampleIntervalSeconds = 1;
    public const int MaxSampleIntervalSeconds = 5;
    public const int MinRawRetentionHours = 24;
    public const int MaxRawRetentionHours = 168;
    public const int MinHistoryRetentionYears = 1;
    public const int MaxHistoryRetentionYears = 5;

    public MachineProfile Profile { get; init; } = MachineProfile.DefaultLaptop;

    /// <summary>Seconds without input after which the user counts as idle (spec §6).</summary>
    public int IdleThresholdSeconds { get; init; } = 300;

    /// <summary>Seconds between samples (spec §4).</summary>
    public int SampleIntervalSeconds { get; init; } = 1;

    /// <summary>Hours of one-second rows to keep (spec §7).</summary>
    public int RawRetentionHours { get; init; } = 48;

    /// <summary>Years of one-minute rows to keep; hour rows are kept for ever (spec §7).</summary>
    public int HistoryRetentionYears { get; init; } = 2;

    public static ServiceSettings Default { get; } = new();

    /// <summary>Null when every value is acceptable; otherwise the first problem, in words the App can show.</summary>
    public string? Validate()
    {
        if (Profile is null) return "The machine profile is missing.";
        if (IdleThresholdSeconds is < MinIdleThresholdSeconds or > MaxIdleThresholdSeconds)
            return $"The idle threshold must be between {MinIdleThresholdSeconds} and {MaxIdleThresholdSeconds} seconds.";
        if (SampleIntervalSeconds is < MinSampleIntervalSeconds or > MaxSampleIntervalSeconds)
            return $"The sample interval must be between {MinSampleIntervalSeconds} and {MaxSampleIntervalSeconds} seconds.";
        if (RawRetentionHours is < MinRawRetentionHours or > MaxRawRetentionHours)
            return $"Second-by-second history must be kept between {MinRawRetentionHours} and {MaxRawRetentionHours} hours.";
        if (HistoryRetentionYears is < MinHistoryRetentionYears or > MaxHistoryRetentionYears)
            return $"Minute-by-minute history must be kept between {MinHistoryRetentionYears} and {MaxHistoryRetentionYears} years.";
        return ValidateProfile(Profile);
    }

    private static string? ValidateProfile(MachineProfile p)
    {
        if (!Enum.IsDefined(p.Chassis)) return "The chassis is not one PowerLedger knows.";
        if (!Enum.IsDefined(p.PsuTier)) return "The power supply rating is not one PowerLedger knows.";
        if (p.RamSticks is < 1 or > 32) return "Memory must be between 1 and 32 sticks.";
        if (p.SsdCount is < 0 or > 32 || p.HddCount is < 0 or > 32) return "Drive counts must be between 0 and 32.";
        if (p.FanCount is < 0 or > 32) return "The fan count must be between 0 and 32.";
        if (p.ExternalMonitors is < 0 or > 16) return "External monitors must be between 0 and 16.";
        if (!InRange(p.ExtrasWatts, 0, 1000)) return "Extras must be between 0 and 1000 W.";
        if (!InRange(p.MonitorWatts, 0, 500)) return "A monitor must draw between 0 and 500 W.";
        if (p.DisplayDiagonalInches != 0 && !InRange(p.DisplayDiagonalInches, 7, 50))
            return "The panel size must be 0 for none, or between 7 and 50 inches.";
        if (p.CpuTdpOverrideW is { } cpu && !InRange(cpu, 1, 1000)) return "The processor's rated power must be between 1 and 1000 W.";
        if (p.GpuTdpOverrideW is { } gpu && !InRange(gpu, 1, 1500)) return "The graphics card's rated power must be between 1 and 1500 W.";
        return null;
    }

    private static bool InRange(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter ServiceSettingsTests`
Expected: `Passed! - Failed: 0, Passed: 14`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Contracts/ServiceSettings.cs tests/PowerLedger.Service.Tests/ServiceSettingsTests.cs
git commit -m "Add the service settings the App may change, with range checks"
```

---

### Task 3: The pipe protocol

**Files:**
- Create: `src/PowerLedger.Contracts/PipeMessages.cs`
- Create: `src/PowerLedger.Contracts/ReadingFrame.cs`
- Create: `src/PowerLedger.Contracts/ServiceStatus.cs`
- Create: `src/PowerLedger.Contracts/PipeProtocol.cs`
- Create: `src/PowerLedger.Contracts/MessageChannel.cs`
- Test: `tests/PowerLedger.Service.Tests/PipeProtocolTests.cs`
- Test: `tests/PowerLedger.Service.Tests/MessageChannelTests.cs`

Spec §8: newline-delimited JSON with `System.Text.Json` source generation. Each message carries `type`, and requests carry an `id`. The limit is 64 KB a message. Everything lives in Contracts, so the App's client (Plan D) uses the same framing. Enums travel as numbers, as `MachineProfile` and `Quality` already promise. The discriminator is written first; readers rely on that. Stored settings are the one exception to source generation: found while executing this task, the generated code assigns every init-only property, so JSON from an older version that lacks a setting would read back as zero rather than the default `MachineProfile` promises. Settings storage uses reflection, which sets only what the JSON holds.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.Service.Tests/PipeProtocolTests.cs`
```csharp
using System.Text;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class PipeProtocolTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

    internal static ReadingFrame Frame(double totalW = 20) => new(
        At, 1, totalW, Quality.Measured,
        new ComponentWatts(Cpu: 6, Gpu: 1, Display: 3, Ram: 0, Storage: 0, Board: 0, Extras: 0, Monitors: 2, PsuLoss: 0, Unattributed: 8),
        CpuMeasured: true, GpuMeasured: false,
        OnBattery: true, DisplayOn: true, UserIdle: false, SessionLocked: false, CpuLoad: 0.3, GpuLoad: null, Brightness: 0.5, Suspect: false);

    public static TheoryData<PipeMessage> SimpleMessages => new()
    {
        new SubscribeRequest(1),
        new GetStatusRequest(2),
        new GetSettingsRequest(3),
        new SetTariffRequest(4, 0.31m, "EUR", At),
        new SetTariffRequest(5, 12.5m, "JPY", null),
        new ResetCalibrationRequest(6),
        new ReportActivityRequest(7, 42.5),
        new OkReply(8),
        new ErrorReply(9, "no"),
        new ErrorReply(null, "unreadable"),
        Frame(),
    };

    [Theory]
    [MemberData(nameof(SimpleMessages))]
    public void Every_simple_message_survives_the_wire(PipeMessage message)
    {
        var line = PipeProtocol.Serialize(message);
        line[^1].ShouldBe((byte)'\n');
        PipeProtocol.Deserialize(Trim(line)).ShouldBe(message);
    }

    [Fact]
    public void Status_and_settings_replies_survive_the_wire()
    {
        var status = new ServiceStatus(
            "0.1.0", At, 42,
            [new SourceStatus("battery", true, null, 0, null), new SourceStatus("nvidia-gpu", false, "no NVIDIA driver", 0, null)],
            3, 1, new CalibrationStatus(900, 1800, 2, 12), "64596c2b0f03e58f", 123456, null, null, Frame());
        var line = PipeProtocol.Serialize(new StatusReply(10, status));
        var back = PipeProtocol.Deserialize(Trim(line)).ShouldBeOfType<StatusReply>();
        back.Id.ShouldBe(10);
        back.Status.Sources.Count.ShouldBe(2);
        back.Status.Sources[1].Unavailable.ShouldBe("no NVIDIA driver");
        back.Status.Calibration.ShouldBe(status.Calibration);
        back.Status.Last.ShouldBe(status.Last);
        PipeProtocol.Serialize(back).ShouldBe(line);

        var settings = ServiceSettings.Default with { SampleIntervalSeconds = 2, Profile = MachineProfile.DefaultDesktop with { PsuTier = PsuTier.Gold } };
        PipeProtocol.Deserialize(Trim(PipeProtocol.Serialize(new SettingsReply(11, settings))))
            .ShouldBeOfType<SettingsReply>().Settings.ShouldBe(settings);
    }

    [Fact]
    public void The_type_comes_first_names_are_camel_case_and_enums_are_numbers()
    {
        Text(new GetStatusRequest(7)).ShouldBe("""{"type":"getStatus","id":7}""" + "\n");
        Text(Frame()).ShouldContain("\"quality\":2");
        Text(Frame()).ShouldNotContain("displayBandW");
    }

    [Theory]
    [InlineData("""{"type":"launchMissiles","id":1}""")]
    [InlineData("""{"id":1}""")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{"type":"getStatus","id":"seven"}""")]
    public void Anything_else_is_a_protocol_error(string line)
        => Should.Throw<PipeProtocolException>(() => PipeProtocol.Deserialize(Encoding.UTF8.GetBytes(line)));

    [Fact]
    public void A_message_over_64_KB_is_refused_before_it_is_sent()
        => Should.Throw<PipeProtocolException>(() => PipeProtocol.Serialize(new ErrorReply(1, new string('x', PipeProtocol.MaxMessageBytes))));

    [Fact]
    public void Stored_settings_missing_a_newer_value_take_its_default()
    {
        var settings = PipeProtocol.DeserializeSettings("""{"sampleIntervalSeconds":3}""").ShouldNotBeNull();
        settings.SampleIntervalSeconds.ShouldBe(3);
        settings.IdleThresholdSeconds.ShouldBe(300);
        settings.Profile.ShouldBe(MachineProfile.DefaultLaptop);
        PipeProtocol.DeserializeSettings("{ nope").ShouldBeNull();
    }

    private static string Text(PipeMessage message) => Encoding.UTF8.GetString(PipeProtocol.Serialize(message));

    private static byte[] Trim(byte[] line) => line[..^1];
}
```

`tests/PowerLedger.Service.Tests/MessageChannelTests.cs`
```csharp
using System.Text;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class MessageChannelTests
{
    [Fact]
    public async Task Messages_are_read_one_line_at_a_time_however_the_bytes_arrive()
    {
        var bytes = Lines(new GetStatusRequest(1), new OkReply(2), new ErrorReply(null, "x"));
        await using var channel = new MessageChannel(new TrickleStream(bytes, chunk: 3));
        (await channel.ReadAsync()).ShouldBe(new GetStatusRequest(1));
        (await channel.ReadAsync()).ShouldBe(new OkReply(2));
        (await channel.ReadAsync()).ShouldBe(new ErrorReply(null, "x"));
        (await channel.ReadAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task Blank_lines_and_windows_line_endings_are_tolerated()
    {
        var text = "\n\r\n" + """{"type":"getStatus","id":5}""" + "\r\n";
        await using var channel = new MessageChannel(new MemoryStream(Encoding.UTF8.GetBytes(text)));
        (await channel.ReadAsync()).ShouldBe(new GetStatusRequest(5));
        (await channel.ReadAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task A_line_longer_than_64_KB_is_a_protocol_error_without_reading_it_all()
    {
        var stream = new TrickleStream(Encoding.UTF8.GetBytes(new string('x', 200_000)), chunk: 8192);
        await using var channel = new MessageChannel(stream);
        await Should.ThrowAsync<PipeProtocolException>(async () => await channel.ReadAsync());
        stream.Position.ShouldBeLessThan(100_000);
    }

    [Fact]
    public async Task A_connection_that_closes_mid_message_is_a_protocol_error()
    {
        await using var channel = new MessageChannel(new MemoryStream(Encoding.UTF8.GetBytes("""{"type":"getSta""")));
        await Should.ThrowAsync<PipeProtocolException>(async () => await channel.ReadAsync());
    }

    [Fact]
    public async Task Writes_from_many_tasks_never_interleave()
    {
        var sink = new MemoryStream();
        await using (var writer = new MessageChannel(sink))
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(task => Task.Run(async () =>
            {
                for (var i = 0; i < 50; i++) await writer.WriteAsync(new ErrorReply(task * 100 + i, new string('y', 500)));
            })));
        }

        await using var reader = new MessageChannel(new MemoryStream(sink.ToArray()));
        var ids = new List<long?>();
        while (await reader.ReadAsync() is { } message) ids.Add(message.ShouldBeOfType<ErrorReply>().Id);
        ids.Count.ShouldBe(400);
        ids.Distinct().Count().ShouldBe(400);
    }

    private static byte[] Lines(params PipeMessage[] messages) => [.. messages.SelectMany(PipeProtocol.Serialize)];
}

/// <summary>A stream that hands out a few bytes a read, as a busy pipe does.</summary>
internal sealed class TrickleStream(byte[] data, int chunk) : Stream
{
    private int _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var n = Math.Min(Math.Min(chunk, buffer.Length), data.Length - _position);
        data.AsSpan(_position, n).CopyTo(buffer);
        _position += n;
        return n;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Read(buffer.Span));

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter "PipeProtocolTests|MessageChannelTests"`
Expected: build error, `PipeMessage` not found.

- [x] **Step 3: Write the protocol**

`src/PowerLedger.Contracts/PipeMessages.cs`
```csharp
using System.Text.Json.Serialization;

namespace PowerLedger.Contracts;

/// <summary>
/// Everything that travels on the pipe (spec §8): one JSON object per line, its kind in "type". Requests carry an id
/// that their reply repeats; live readings are pushed without one. The type names are the protocol: add kinds, never
/// rename them. A breaking change needs a new pipe name.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SubscribeRequest), "subscribe")]
[JsonDerivedType(typeof(GetStatusRequest), "getStatus")]
[JsonDerivedType(typeof(GetSettingsRequest), "getSettings")]
[JsonDerivedType(typeof(SetSettingsRequest), "setSettings")]
[JsonDerivedType(typeof(SetTariffRequest), "setTariff")]
[JsonDerivedType(typeof(ResetCalibrationRequest), "resetCalibration")]
[JsonDerivedType(typeof(ReportActivityRequest), "reportActivity")]
[JsonDerivedType(typeof(OkReply), "ok")]
[JsonDerivedType(typeof(ErrorReply), "error")]
[JsonDerivedType(typeof(StatusReply), "status")]
[JsonDerivedType(typeof(SettingsReply), "settings")]
[JsonDerivedType(typeof(ReadingFrame), "reading")]
public abstract record PipeMessage;

/// <summary>A message from the App that gets exactly one reply carrying the same id.</summary>
public abstract record PipeRequest(long Id) : PipeMessage;

/// <summary>Start receiving a <see cref="ReadingFrame"/> every tick on this connection.</summary>
public sealed record SubscribeRequest(long Id) : PipeRequest(Id);

public sealed record GetStatusRequest(long Id) : PipeRequest(Id);

public sealed record GetSettingsRequest(long Id) : PipeRequest(Id);

/// <summary>Replace the service settings. Refused whole when any value is out of range.</summary>
public sealed record SetSettingsRequest(long Id, ServiceSettings Settings) : PipeRequest(Id);

/// <summary>Add a tariff. <paramref name="EffectiveFrom"/> defaults to now and may be backdated (spec §8).</summary>
public sealed record SetTariffRequest(long Id, decimal PricePerKwh, string Currency, DateTimeOffset? EffectiveFrom) : PipeRequest(Id);

/// <summary>Forget everything learned for this machine and start calibrating afresh.</summary>
public sealed record ResetCalibrationRequest(long Id) : PipeRequest(Id);

/// <summary>Seconds since the last keyboard or mouse input in the sender's session. The service runs in session 0
/// and cannot see input, so the App reports it every few seconds.</summary>
public sealed record ReportActivityRequest(long Id, double IdleSeconds) : PipeRequest(Id);

/// <summary>The request with this id succeeded.</summary>
public sealed record OkReply(long Id) : PipeMessage;

/// <summary>A request failed, or a line could not be read, in which case <paramref name="Id"/> is null.</summary>
public sealed record ErrorReply(long? Id, string Message) : PipeMessage;

public sealed record StatusReply(long Id, ServiceStatus Status) : PipeMessage;

public sealed record SettingsReply(long Id, ServiceSettings Settings) : PipeMessage;
```

`src/PowerLedger.Contracts/ReadingFrame.cs`
```csharp
using System.Text.Json.Serialization;

namespace PowerLedger.Contracts;

/// <summary>Watts per part for one tick, as the power model split them. Mirrors Core's Components.</summary>
public sealed record ComponentWatts(
    double Cpu, double Gpu, double Display, double Ram, double Storage,
    double Board, double Extras, double Monitors, double PsuLoss, double Unattributed);

/// <summary>One tick as the App sees it, pushed to every subscriber (spec §8).</summary>
/// <param name="Quality">How far to trust the total.</param>
/// <param name="CpuMeasured">The processor's watts came from its energy meter this tick, not the load model.</param>
/// <param name="GpuMeasured">The discrete GPU's watts were reported this tick (0 W for a card Windows has switched off);
/// false means the load model, or no card the service can read.</param>
public sealed record ReadingFrame(
    DateTimeOffset Timestamp, double DeltaSeconds, double TotalW, Quality Quality, ComponentWatts Components,
    bool CpuMeasured, bool GpuMeasured,
    bool OnBattery, bool DisplayOn, bool UserIdle, bool SessionLocked,
    double CpuLoad, double? GpuLoad, double? Brightness, bool Suspect) : PipeMessage
{
    /// <summary>The display band: the internal panel plus opted-in external monitors (spec §6).</summary>
    [JsonIgnore]
    public double DisplayBandW => Components.Display + Components.Monitors;

    /// <summary>The rest band: everything that is not CPU, GPU or display (spec §6).</summary>
    [JsonIgnore]
    public double RestBandW => TotalW - Components.Cpu - Components.Gpu - DisplayBandW;
}
```

`src/PowerLedger.Contracts/ServiceStatus.cs`
```csharp
namespace PowerLedger.Contracts;

/// <summary>What the status screen shows (spec §8).</summary>
/// <param name="Version">The service's version.</param>
/// <param name="StartedAt">When this run of the service began.</param>
/// <param name="Ticks">Readings recorded since then.</param>
/// <param name="Sources">One entry per sensor source.</param>
/// <param name="SuspectTicks">Ticks the validator marked suspect since the sensor set was built.</param>
/// <param name="SensorRestarts">Sensor sets thrown away because a read hung.</param>
/// <param name="Calibration">How far battery calibration has got.</param>
/// <param name="InventoryHash">The hardware identity calibration is keyed to.</param>
/// <param name="DatabaseBytes">The database's size on disk, write-ahead log included.</param>
/// <param name="WriteProblem">Null while writes succeed; otherwise why readings are held in memory.</param>
/// <param name="DatabaseNotice">Null normally; set when a damaged or untrusted database was set aside at start.</param>
/// <param name="Last">The most recent reading, or null before the first.</param>
public sealed record ServiceStatus(
    string Version, DateTimeOffset StartedAt, long Ticks,
    IReadOnlyList<SourceStatus> Sources, int SuspectTicks, int SensorRestarts,
    CalibrationStatus Calibration, string InventoryHash, long DatabaseBytes,
    string? WriteProblem, string? DatabaseNotice, ReadingFrame? Last);

/// <param name="Name">The source's name, e.g. "battery".</param>
/// <param name="Supported">False when this machine cannot answer at all.</param>
/// <param name="Unavailable">Why not, when it cannot.</param>
/// <param name="Failures">Ticks on which the source threw since the sensor set was built.</param>
/// <param name="LastError">The most recent failure, for the status screen.</param>
public sealed record SourceStatus(string Name, bool Supported, string? Unavailable, int Failures, string? LastError);

/// <param name="BatterySamples">Battery ticks learned from so far.</param>
/// <param name="SamplesNeeded">Battery ticks needed before any baseline is trusted.</param>
/// <param name="TrustedBuckets">Brightness buckets with a trusted baseline.</param>
/// <param name="Buckets">All brightness buckets, display-off included.</param>
public sealed record CalibrationStatus(int BatterySamples, int SamplesNeeded, int TrustedBuckets, int Buckets);
```

`src/PowerLedger.Contracts/PipeProtocol.cs`
```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PowerLedger.Contracts;

/// <summary>The wire format the service and the App share (spec §8).</summary>
public static class PipeProtocol
{
    /// <summary>Opened as \\.\pipe\PowerLedger.v1. A breaking protocol change gets a new name.</summary>
    public const string PipeName = "PowerLedger.v1";

    /// <summary>The largest message either side accepts, newline excluded.</summary>
    public const int MaxMessageBytes = 64 * 1024;

    /// <summary>One message as a line of UTF-8 JSON ending in a newline.</summary>
    public static byte[] Serialize(PipeMessage message)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, PipeJson.Default.PipeMessage);
        if (json.Length > MaxMessageBytes)
            throw new PipeProtocolException($"A {message.GetType().Name} came to {json.Length} bytes, over the 64 KB limit.");
        var line = new byte[json.Length + 1];
        json.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        return line;
    }

    /// <summary>One line, newline removed, back into a message. Anything that is not one is a <see cref="PipeProtocolException"/>.</summary>
    public static PipeMessage Deserialize(ReadOnlySpan<byte> line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, PipeJson.Default.PipeMessage)
                ?? throw new PipeProtocolException("The message was empty.");
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            throw new PipeProtocolException("The line was not a PowerLedger message.", error);
        }
    }

    /// <summary>
    /// Stored settings are read and written by reflection, not by the generated code. The generated code assigns every
    /// init-only property, so a setting missing from JSON an older version wrote would come back as zero instead of its
    /// default, and so would a profile field. Reflection builds the object first and sets only what the JSON holds.
    /// </summary>
    private static readonly JsonSerializerOptions StoredSettings = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>Settings as the service stores them.</summary>
    public static string SerializeSettings(ServiceSettings settings) => JsonSerializer.Serialize(settings, StoredSettings);

    /// <summary>Stored settings back, or null when the text is not settings at all.</summary>
    public static ServiceSettings? DeserializeSettings(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ServiceSettings>(json, StoredSettings);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The other end broke the protocol: a message too long, not JSON, or of an unknown kind.</summary>
public sealed class PipeProtocolException(string message, Exception? inner = null) : Exception(message, inner);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PipeMessage))]
internal sealed partial class PipeJson : JsonSerializerContext;
```

`src/PowerLedger.Contracts/MessageChannel.cs`
```csharp
namespace PowerLedger.Contracts;

/// <summary>
/// One end of a PowerLedger pipe: whole messages in and out, one JSON line each, never more than
/// <see cref="PipeProtocol.MaxMessageBytes"/>. A line that grows past the limit is refused before it is read to the
/// end. Reads are for one reader at a time; writes may come from several tasks and are serialised here, so a pushed
/// reading never lands in the middle of a reply.
/// </summary>
public sealed class MessageChannel(Stream stream) : IAsyncDisposable
{
    private readonly byte[] _buffer = new byte[PipeProtocol.MaxMessageBytes + 2];   // room for a full line and its "\r\n"
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _start;
    private int _end;

    /// <summary>The next message, or null when the other end closed the connection between messages.</summary>
    public async ValueTask<PipeMessage?> ReadAsync(CancellationToken cancel = default)
    {
        while (true)
        {
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline >= 0)
            {
                var line = _buffer.AsMemory(_start, newline - _start);
                _start = newline + 1;
                if (line.Length > 0 && line.Span[^1] == (byte)'\r') line = line[..^1];
                if (line.Length == 0) continue;
                return PipeProtocol.Deserialize(line.Span);
            }

            if (_end - _start > PipeProtocol.MaxMessageBytes) throw new PipeProtocolException("A message was longer than 64 KB.");
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            var read = await stream.ReadAsync(_buffer.AsMemory(_end), cancel).ConfigureAwait(false);
            if (read == 0)
            {
                if (_end > _start) throw new PipeProtocolException("The connection closed in the middle of a message.");
                return null;
            }
            _end += read;
        }
    }

    /// <summary>Sends one message. Safe to call from several tasks at once.</summary>
    public async ValueTask WriteAsync(PipeMessage message, CancellationToken cancel = default)
    {
        var line = PipeProtocol.Serialize(message);
        await _writeGate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(line, cancel).ConfigureAwait(false);
            await stream.FlushAsync(cancel).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync().ConfigureAwait(false);
        _writeGate.Dispose();
    }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter "PipeProtocolTests|MessageChannelTests"`
Expected: `Passed! - Failed: 0, Passed: 25`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Contracts tests/PowerLedger.Service.Tests/PipeProtocolTests.cs tests/PowerLedger.Service.Tests/MessageChannelTests.cs
git commit -m "Add the pipe protocol: message kinds, JSON source generation and line framing"
```

---

### Task 4: The newest raw tick

**Files:**
- Modify: `src/PowerLedger.Storage/RawSampleRepository.cs`
- Test: `tests/PowerLedger.Storage.Tests/RawSampleRepositoryTests.cs`

A session left open by a crash is closed at its last recorded tick (Task 14), which needs the newest timestamp in `samples_raw`.

- [x] **Step 1: Write the failing test**

Add to `tests/PowerLedger.Storage.Tests/RawSampleRepositoryTests.cs`, inside the existing class:

```csharp
    [Fact]
    public void Latest_is_the_newest_tick_or_null_on_an_empty_table()
    {
        using var t = new TestDatabase();
        var repo = new RawSampleRepository(t.Db);
        repo.Latest().ShouldBeNull();
        repo.InsertBatch([.. Enumerable.Range(0, 10).Select(s => Fixtures.Reading(s))]);
        repo.Latest().ShouldBe(Fixtures.T0.AddSeconds(9));
    }
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PowerLedger.Storage.Tests --filter RawSampleRepositoryTests`
Expected: build error, `Latest` not found.

- [x] **Step 3: Add the query**

Add to `src/PowerLedger.Storage/RawSampleRepository.cs`, after `Read`:

```csharp
    /// <summary>The newest tick's timestamp, or null when the table is empty.</summary>
    public DateTimeOffset? Latest()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT MAX(ts_ms) FROM samples_raw";
        return cmd.ExecuteScalar() is long ms ? Rows.Time(ms) : null;
    }
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Storage.Tests`
Expected: `Passed! - Failed: 0, Passed: 41`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Storage/RawSampleRepository.cs tests/PowerLedger.Storage.Tests/RawSampleRepositoryTests.cs
git commit -m "Let storage report the newest raw tick"
```

---

### Task 5: Open the database, and set aside a damaged one

**Files:**
- Create: `src/PowerLedger.Service/DatabaseOpener.cs`
- Create: `tests/PowerLedger.Service.Tests/TestDatabase.cs`
- Test: `tests/PowerLedger.Service.Tests/DatabaseOpenerTests.cs`

Spec §10: a corrupt database is renamed `power.corrupt-<date>.db` and a fresh one started, with an alert in the UI. SQLite reports a file that is not a database when it first reads it, and some damage only shows under an integrity check. So the opener runs `PRAGMA quick_check` once at start, which takes about a second on a two-year database. The database and its write-ahead log and shared-memory files move together, so a stale log never meets a fresh database. The file handles must be released before the rename. A failed open can leave a pooled connection holding the file, so every pool is cleared first.

- [x] **Step 1: Write the test helper and the failing tests**

`tests/PowerLedger.Service.Tests/TestDatabase.cs`
```csharp
using Microsoft.Data.Sqlite;
using PowerLedger.Storage;

namespace PowerLedger.Service.Tests;

/// <summary>A fresh migrated database in its own temp folder, deleted on dispose (WAL needs a real file).</summary>
internal sealed class TestDatabase : IDisposable
{
    public TestDatabase()
    {
        Directory.CreateDirectory(Folder);
        Db = SqliteDatabase.OpenAndMigrate(DatabasePath);
    }

    public string Folder { get; } = Path.Combine(Path.GetTempPath(), $"powerledger-service-{Guid.NewGuid():N}");

    public string DatabasePath => Path.Combine(Folder, "power.db");

    public SqliteDatabase Db { get; }

    public void Dispose()
    {
        Db.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
```

`tests/PowerLedger.Service.Tests/DatabaseOpenerTests.cs`
```csharp
using Microsoft.Data.Sqlite;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

public sealed class DatabaseOpenerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 10, 30, 15, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"powerledger-open-{Guid.NewGuid():N}");

    public DatabaseOpenerTests() => Directory.CreateDirectory(_folder);

    private string DbPath => Path.Combine(_folder, "power.db");

    [Fact]
    public void A_healthy_database_opens_with_its_data_and_no_notice()
    {
        using (var first = SqliteDatabase.OpenAndMigrate(DbPath)) new SettingsRepository(first).Set("k", "v");
        var (database, notice) = DatabaseOpener.Open(DbPath, Now);
        using (database)
        {
            notice.ShouldBeNull();
            new SettingsRepository(database).Get("k").ShouldBe("v");
        }
    }

    [Fact]
    public void A_file_that_is_not_a_database_is_set_aside_and_a_fresh_one_started()
    {
        File.WriteAllBytes(DbPath, Enumerable.Repeat((byte)0x5A, 8192).ToArray());
        var (database, notice) = DatabaseOpener.Open(DbPath, Now);
        using (database)
        {
            notice.ShouldNotBeNull().ShouldContain("power.corrupt-20260912-103015.db");
            File.Exists(Path.Combine(_folder, "power.corrupt-20260912-103015.db")).ShouldBeTrue();
            var settings = new SettingsRepository(database);
            settings.Set("fresh", "yes");
            settings.Get("fresh").ShouldBe("yes");
        }
    }

    [Fact]
    public void Setting_aside_moves_the_write_ahead_log_and_shared_memory_with_the_database()
    {
        var suffixes = new[] { "", "-wal", "-shm" };
        foreach (var suffix in suffixes) File.WriteAllText(DbPath + suffix, "content" + suffix);
        var aside = DatabaseOpener.SetAside(DbPath, "corrupt", Now);
        aside.ShouldBe(Path.Combine(_folder, "power.corrupt-20260912-103015.db"));
        foreach (var suffix in suffixes)
        {
            File.Exists(DbPath + suffix).ShouldBeFalse();
            File.ReadAllText(aside + suffix).ShouldBe("content" + suffix);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter DatabaseOpenerTests`
Expected: build error, `DatabaseOpener` not found.

- [x] **Step 3: Write the opener**

`src/PowerLedger.Service/DatabaseOpener.cs`
```csharp
using System.Globalization;
using Microsoft.Data.Sqlite;
using PowerLedger.Storage;

namespace PowerLedger.Service;

/// <summary>Opens the service's one database (spec §7, §10).</summary>
internal static class DatabaseOpener
{
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    /// <summary>
    /// Opens, checks and migrates the database. A file SQLite cannot read as a database is set aside as
    /// power.corrupt-&lt;time&gt;.db and a fresh one started, and the notice says so for the status screen.
    /// </summary>
    public static (SqliteDatabase Database, string? Notice) Open(string path, DateTimeOffset now)
    {
        try
        {
            var database = SqliteDatabase.OpenAndMigrate(path);
            if (QuickCheck(database) is null) return (database, null);
            database.Dispose();
        }
        catch (SqliteException error) when (error.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
        }

        SqliteConnection.ClearAllPools();
        var aside = SetAside(path, "corrupt", now);
        return (SqliteDatabase.OpenAndMigrate(path), $"The database was damaged. It was set aside as {Path.GetFileName(aside)} and a new one started.");
    }

    /// <summary>Null when SQLite's quick integrity check passes; otherwise its first complaint.</summary>
    internal static string? QuickCheck(SqliteDatabase database)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check(1)";
        var result = command.ExecuteScalar() as string;
        return result == "ok" ? null : result ?? "no answer";
    }

    /// <summary>Renames the database and its write-ahead log and shared-memory files out of the way, keeping them together.</summary>
    /// <returns>The database file's new path.</returns>
    internal static string SetAside(string path, string label, DateTimeOffset now)
    {
        var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var aside = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}.{label}-{stamp}.db");
        foreach (var suffix in new[] { "-wal", "-shm", "" })
        {
            if (File.Exists(path + suffix)) File.Move(path + suffix, aside + suffix, overwrite: true);
        }
        return aside;
    }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter DatabaseOpenerTests`
Expected: `Passed! - Failed: 0, Passed: 3`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Service/DatabaseOpener.cs tests/PowerLedger.Service.Tests/TestDatabase.cs tests/PowerLedger.Service.Tests/DatabaseOpenerTests.cs
git commit -m "Open the database and set aside a damaged one"
```

---

### Task 6: The data folder and its security

**Files:**
- Create: `src/PowerLedger.Service/ServicePaths.cs`
- Create: `src/PowerLedger.Service/DataDirectory.cs`
- Test: `tests/PowerLedger.Service.Tests/DataDirectoryTests.cs`

The service runs as LocalSystem and opens the database, so the folder and everything in it must be writable by SYSTEM and administrators only. `C:\ProgramData` lets ordinary users create folders. A user who created `C:\ProgramData\PowerLedger` before the installer did could plant a database or write-ahead log for a SYSTEM process to parse. So the service refuses a folder another account owns, and sets aside database files another account owns, with Task 5's `SetAside`. Enforcement applies only when running as LocalSystem: a console run in development just creates the folders. Setting the ACL cannot be tested unelevated, so the tests check the rules the ACL is built from and that an ordinary user's folder is refused.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.Service.Tests/DataDirectoryTests.cs`
```csharp
using System.Security.AccessControl;
using System.Security.Principal;
using Shouldly;

namespace PowerLedger.Service.Tests;

public sealed class DataDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"powerledger-dir-{Guid.NewGuid():N}");

    [Fact]
    public void A_console_run_just_creates_the_folders()
    {
        var paths = new ServicePaths(_root);
        DataDirectory.Prepare(paths, enforce: false, DateTimeOffset.UtcNow).ShouldBeNull();
        Directory.Exists(paths.DataDirectory).ShouldBeTrue();
        Directory.Exists(paths.Logs).ShouldBeTrue();
        paths.Database.ShouldBe(Path.Combine(_root, "power.db"));
    }

    [Fact]
    public void A_folder_owned_by_an_ordinary_account_is_refused()
    {
        // The test runs as an ordinary user, so the folder it creates belongs to that user: the planted-folder case.
        Directory.CreateDirectory(_root);
        var owner = new DirectoryInfo(_root).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (DataDirectory.IsTrustedOwner(owner)) return;   // an elevated run owns it as Administrators; nothing to prove
        Should.Throw<UntrustedDataDirectoryException>(() => DataDirectory.Prepare(new ServicePaths(_root), enforce: true, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Only_system_and_administrators_are_trusted_owners()
    {
        DataDirectory.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)).ShouldBeTrue();
        DataDirectory.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)).ShouldBeTrue();
        DataDirectory.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)).ShouldBeFalse();
        DataDirectory.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null)).ShouldBeFalse();
        DataDirectory.IsTrustedOwner(null).ShouldBeFalse();
    }

    [Fact]
    public void The_folder_acl_lets_users_read_and_nobody_else_write()
    {
        var security = DataDirectory.Security();
        security.AreAccessRulesProtected.ShouldBeTrue();
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        rules.Count.ShouldBe(3);
        rules.ShouldAllBe(r => r.AccessControlType == AccessControlType.Allow
            && r.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));
        Rights(rules, WellKnownSidType.LocalSystemSid).ShouldBe(FileSystemRights.FullControl);
        Rights(rules, WellKnownSidType.BuiltinAdministratorsSid).ShouldBe(FileSystemRights.FullControl);
        Rights(rules, WellKnownSidType.BuiltinUsersSid).ShouldBe(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static FileSystemRights Rights(List<FileSystemAccessRule> rules, WellKnownSidType who)
        => rules.Single(r => r.IdentityReference == new SecurityIdentifier(who, null)).FileSystemRights;
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter DataDirectoryTests`
Expected: build error, `ServicePaths` not found.

- [x] **Step 3: Write the paths and the folder rules**

`src/PowerLedger.Service/ServicePaths.cs`
```csharp
using Microsoft.Extensions.Configuration;

namespace PowerLedger.Service;

/// <summary>Where the service keeps its files (spec §7): one folder holding the database, its backup and the logs.</summary>
internal sealed record ServicePaths(string DataDirectory)
{
    /// <summary>Configuration key, "--data" on the command line, that moves everything elsewhere for development.</summary>
    public const string DataDirectoryKey = "data";

    public string Database => Path.Combine(DataDirectory, "power.db");

    public string Logs => Path.Combine(DataDirectory, "logs");

    /// <summary>C:\ProgramData\PowerLedger, unless configuration names another folder.</summary>
    public static ServicePaths From(IConfiguration configuration)
    {
        var configured = configuration[DataDirectoryKey];
        return new ServicePaths(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PowerLedger")
            : Path.GetFullPath(configured));
    }
}
```

`src/PowerLedger.Service/DataDirectory.cs`
```csharp
using System.Security.AccessControl;
using System.Security.Principal;

namespace PowerLedger.Service;

/// <summary>
/// The data folder's security (spec §7, §11). The service runs as LocalSystem and opens the database, so the folder
/// and everything in it are writable by SYSTEM and administrators only: a database users could edit would feed crafted
/// input to a SYSTEM process. Users get read access, which is all the App's read-only connection needs while the
/// service holds the write-ahead log open. A folder or database file that another account owns was not made by
/// PowerLedger or its installer, and is not trusted.
/// </summary>
internal static class DataDirectory
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    /// <summary>
    /// Creates the folders. When <paramref name="enforce"/> is set, as it is under the service control manager, refuses a
    /// folder with an untrusted owner, applies the ACL, and sets aside database files with an untrusted owner.
    /// </summary>
    /// <returns>A note for the status screen when database files were set aside; otherwise null.</returns>
    public static string? Prepare(ServicePaths paths, bool enforce, DateTimeOffset now)
    {
        var folder = new DirectoryInfo(paths.DataDirectory);
        folder.Create();
        if (enforce)
        {
            var owner = folder.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (!IsTrustedOwner(owner)) throw new UntrustedDataDirectoryException(paths.DataDirectory, owner);
            folder.SetAccessControl(Security());
        }
        Directory.CreateDirectory(paths.Logs);
        if (!enforce) return null;

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = new FileInfo(paths.Database + suffix);
            if (!file.Exists) continue;
            var fileOwner = file.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (IsTrustedOwner(fileOwner)) continue;
            var aside = DatabaseOpener.SetAside(paths.Database, "untrusted", now);
            return $"A database file owned by {fileOwner?.Value ?? "nobody"} was set aside as {Path.GetFileName(aside)} and a new database started.";
        }
        return null;
    }

    /// <summary>SYSTEM and Administrators are the only owners a PowerLedger folder or database may have.</summary>
    public static bool IsTrustedOwner(SecurityIdentifier? owner) => owner == System || owner == Administrators;

    /// <summary>SYSTEM and Administrators full control, Users read, all inherited by everything inside; nothing from above.</summary>
    public static DirectorySecurity Security()
    {
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
}

/// <summary>The data folder belongs to an account that is neither SYSTEM nor Administrators, so it may have been planted.</summary>
internal sealed class UntrustedDataDirectoryException(string path, SecurityIdentifier? owner)
    : Exception($"{path} is owned by {owner?.Value ?? "nobody"}, not by SYSTEM or Administrators, so PowerLedger will not keep its data there. Delete the folder and start the service again.");
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter DataDirectoryTests`
Expected: `Passed! - Failed: 0, Passed: 4`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Service/ServicePaths.cs src/PowerLedger.Service/DataDirectory.cs tests/PowerLedger.Service.Tests/DataDirectoryTests.cs
git commit -m "Refuse a planted data folder and lock the real one down"
```

---

### Task 7: Settings storage, the profile rule and the model

**Files:**
- Create: `src/PowerLedger.Service/SettingsStore.cs`
- Create: `src/PowerLedger.Service/ModelFactory.cs`
- Create: `tests/PowerLedger.Service.Tests/Samples.cs`
- Create: `tests/PowerLedger.Service.Tests/Facts.cs`
- Test: `tests/PowerLedger.Service.Tests/SettingsTests.cs`

Plan B's handoff: `ToProfile` overwrites every detected field, so detection is folded into the profile only on the first run and when the hardware hash changes, meaning a different machine. Otherwise the stored profile keeps the user's wizard corrections. The hash the profile was detected for is stored beside the settings. The model takes TDPs from the inventory, and uses the chassis defaults when the bundled table does not know the part.

- [x] **Step 1: Write the test helpers and the failing tests**

`tests/PowerLedger.Service.Tests/Samples.cs`
```csharp
using PowerLedger.Core;

namespace PowerLedger.Service.Tests;

internal static class Samples
{
    public static readonly DateTimeOffset T0 = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A plausible tick: the CPU drawing <paramref name="cpuW"/> (null when there is no energy meter), and on
    /// battery when a discharge rate is given.</summary>
    public static Sample At(DateTimeOffset timestamp, double delta = 1, double? cpuW = 8, double cpuLoad = 0.2, double? batteryW = null, double? brightness = 0.5)
        => new(
            Timestamp: timestamp, DeltaSeconds: delta, CpuPackageW: cpuW, IGpuW: null, CpuLoad: cpuLoad,
            DGpuW: null, DGpuLoad: null, DGpuPresent: false, BatteryRateW: batteryW, OnBattery: batteryW is not null,
            Brightness: brightness, DisplayOn: true, MonitorCount: 1, UserIdleSeconds: 0, SessionLocked: false, Suspect: false);
}
```

`tests/PowerLedger.Service.Tests/Facts.cs`
```csharp
using PowerLedger.Contracts;
using PowerLedger.Sensors;

namespace PowerLedger.Service.Tests;

internal static class Facts
{
    public static InventoryFacts Laptop(int ramSticks = 2) => new(
        ChassisKind.Laptop, "11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", "NVIDIA GeForce MX330",
        ramSticks, RamIsDdr5: false, SsdCount: 1, HddCount: 0, DisplayDiagonalInches: 15.3, MonitorCount: 1);

    public static InventoryFacts Desktop() => new(
        ChassisKind.Desktop, "Unknown Chip 9000", null, 4, RamIsDdr5: true, SsdCount: 2, HddCount: 1, DisplayDiagonalInches: 0, MonitorCount: 2);
}
```

`tests/PowerLedger.Service.Tests/SettingsTests.cs`
```csharp
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class SettingsTests
{
    [Fact]
    public void The_first_run_takes_the_profile_from_detection()
    {
        var (settings, changed) = ProfilePolicy.Apply(stored: null, storedHash: null, Facts.Laptop(ramSticks: 2));
        changed.ShouldBeTrue();
        settings.Profile.Chassis.ShouldBe(ChassisKind.Laptop);
        settings.Profile.RamSticks.ShouldBe(2);
        settings.Profile.DisplayDiagonalInches.ShouldBe(15.3);
        settings.SampleIntervalSeconds.ShouldBe(ServiceSettings.Default.SampleIntervalSeconds);
    }

    [Fact]
    public void A_desktop_starts_from_the_desktop_defaults()
    {
        var (settings, _) = ProfilePolicy.Apply(null, null, Facts.Desktop());
        settings.Profile.FanCount.ShouldBe(MachineProfile.DefaultDesktop.FanCount);
        settings.Profile.SsdCount.ShouldBe(2);
        settings.Profile.DisplayDiagonalInches.ShouldBe(0);
    }

    [Fact]
    public void The_same_machine_keeps_the_users_corrections()
    {
        var facts = Facts.Laptop(ramSticks: 2);
        var corrected = ServiceSettings.Default with { Profile = MachineProfile.DefaultLaptop with { RamSticks = 4, ExtrasWatts = 6 } };
        var (settings, changed) = ProfilePolicy.Apply(corrected, facts.Hash, facts);
        changed.ShouldBeFalse();
        settings.ShouldBeSameAs(corrected);
    }

    [Fact]
    public void A_different_machine_takes_detection_but_keeps_what_the_user_chose()
    {
        var stored = ServiceSettings.Default with { SampleIntervalSeconds = 2, Profile = MachineProfile.DefaultLaptop with { RamSticks = 2, ExtrasWatts = 6 } };
        var (settings, changed) = ProfilePolicy.Apply(stored, Facts.Laptop(ramSticks: 2).Hash, Facts.Laptop(ramSticks: 4));
        changed.ShouldBeTrue();
        settings.Profile.RamSticks.ShouldBe(4);
        settings.Profile.ExtrasWatts.ShouldBe(6);
        settings.SampleIntervalSeconds.ShouldBe(2);
    }

    [Fact]
    public void The_store_round_trips_settings_and_the_profile_hash()
    {
        using var t = new TestDatabase();
        var store = new SettingsStore(new SettingsRepository(t.Db));
        store.Load().ShouldBeNull();
        store.ProfileHash().ShouldBeNull();
        var settings = ServiceSettings.Default with { IdleThresholdSeconds = 600 };
        store.Save(settings);
        store.SaveProfileHash("abc");
        store.Load().ShouldBe(settings);
        store.ProfileHash().ShouldBe("abc");
    }

    [Fact]
    public void Unreadable_stored_settings_count_as_none()
    {
        using var t = new TestDatabase();
        new SettingsRepository(t.Db).Set(SettingsStore.SettingsKey, "{ not json");
        new SettingsStore(new SettingsRepository(t.Db)).Load().ShouldBeNull();
    }

    [Fact]
    public void The_model_falls_back_to_the_chassis_default_for_a_processor_the_table_does_not_know()
    {
        var model = ModelFactory.Build(ServiceSettings.Default with { Profile = MachineProfile.DefaultDesktop }, Facts.Desktop(), new CalibrationLearner());
        model.Evaluate(Samples.At(Samples.T0, cpuW: null, cpuLoad: 1)).Components.Cpu.ShouldBe(HardwareFacts.DesktopDefaults.CpuTdpW);
    }

    [Fact]
    public void The_model_uses_the_bundled_tdp_for_a_known_processor()
    {
        var model = ModelFactory.Build(ServiceSettings.Default, Facts.Laptop(), new CalibrationLearner());
        model.Evaluate(Samples.At(Samples.T0, cpuW: null, cpuLoad: 1)).Components.Cpu.ShouldBe(28);
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter "FullyQualifiedName~Tests.SettingsTests"`
Expected: build error, `ProfilePolicy` not found.

- [x] **Step 3: Write the store, the rule and the factory**

`src/PowerLedger.Service/SettingsStore.cs`
```csharp
using PowerLedger.Contracts;
using PowerLedger.Sensors;
using PowerLedger.Storage;

namespace PowerLedger.Service;

/// <summary>The service settings, and the hardware hash the profile was last detected for, kept in the settings table.</summary>
internal sealed class SettingsStore(SettingsRepository settings)
{
    internal const string SettingsKey = "service.settings";
    internal const string ProfileHashKey = "service.profile-hash";

    /// <summary>The stored settings, or null on the first run or when the stored text cannot be read.</summary>
    public ServiceSettings? Load() => settings.Get(SettingsKey) is { } json ? PipeProtocol.DeserializeSettings(json) : null;

    public string? ProfileHash() => settings.Get(ProfileHashKey);

    public void Save(ServiceSettings value) => settings.Set(SettingsKey, PipeProtocol.SerializeSettings(value));

    public void SaveProfileHash(string hash) => settings.Set(ProfileHashKey, hash);
}

/// <summary>
/// When detection may touch the machine profile (Plan B handoff): on the first run, and when the hardware hash
/// changes, which means a different machine. Otherwise the stored profile, with the user's corrections, stands.
/// </summary>
internal static class ProfilePolicy
{
    /// <returns>The settings to use, and whether they differ from what was stored.</returns>
    public static (ServiceSettings Settings, bool Changed) Apply(ServiceSettings? stored, string? storedHash, InventoryFacts facts)
    {
        if (stored is not null && storedHash == facts.Hash) return (stored, false);
        var basis = stored?.Profile ?? (facts.Chassis == ChassisKind.Laptop ? MachineProfile.DefaultLaptop : MachineProfile.DefaultDesktop);
        return ((stored ?? ServiceSettings.Default) with { Profile = facts.ToProfile(basis) }, true);
    }
}
```

`src/PowerLedger.Service/ModelFactory.cs`
```csharp
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Sensors;

namespace PowerLedger.Service;

/// <summary>The power model for the current settings and hardware.</summary>
internal static class ModelFactory
{
    /// <summary>TDPs come from the bundled table through the inventory; a part the table does not know gets the chassis default.</summary>
    public static PowerModel Build(ServiceSettings settings, InventoryFacts facts, IBaselineProvider baselines)
    {
        var defaults = settings.Profile.Chassis == ChassisKind.Laptop ? HardwareFacts.LaptopDefaults : HardwareFacts.DesktopDefaults;
        var hardware = new HardwareFacts(facts.CpuTdpW ?? defaults.CpuTdpW, facts.GpuTdpW ?? defaults.GpuTdpW);
        return new PowerModel(settings.Profile, hardware, new PowerModelOptions(IdleThresholdSeconds: settings.IdleThresholdSeconds), baselines);
    }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter "FullyQualifiedName~Tests.SettingsTests"`
Expected: `Passed! - Failed: 0, Passed: 8`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Service/SettingsStore.cs src/PowerLedger.Service/ModelFactory.cs tests/PowerLedger.Service.Tests/Samples.cs tests/PowerLedger.Service.Tests/Facts.cs tests/PowerLedger.Service.Tests/SettingsTests.cs
git commit -m "Store settings and fold detection into the profile only for a new machine"
```

---

### Task 8: ServiceSignals — what arrives from other threads

**Files:**
- Create: `src/PowerLedger.Service/ServiceSignals.cs`
- Test: `tests/PowerLedger.Service.Tests/ServiceSignalsTests.cs`

The sensor set reads three delegates: whether the display is on, whether the session is locked, and the user's idle seconds (Plan B's `MachineSensors.Create`). Windows notifications set the first two, and App clients report the third over the pipe. A report keeps ageing between messages, because idle time grows while nobody touches the machine. Several sessions may each run the App, so the most recently active user wins, and a report older than 30 s belongs to an App that went quiet. With nothing to go on, idle time is unknown, which the activity source counts as active and so never overstates idle waste.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.Service.Tests/ServiceSignalsTests.cs`
```csharp
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class ServiceSignalsTests
{
    [Fact]
    public void The_display_counts_as_on_and_the_session_as_unlocked_until_told_otherwise()
    {
        var signals = new ServiceSignals(new FakeTimeProvider());
        signals.DisplayOn.ShouldBeTrue();
        signals.SessionLocked.ShouldBeFalse();
        signals.DisplayOn = false;
        signals.SessionLocked = true;
        signals.DisplayOn.ShouldBeFalse();
        signals.SessionLocked.ShouldBeTrue();
    }

    [Fact]
    public void With_no_app_reporting_idle_time_is_unknown()
        => new ServiceSignals(new FakeTimeProvider()).UserIdleSeconds().ShouldBeNull();

    [Fact]
    public void A_report_keeps_ageing_until_the_next_one_arrives()
    {
        var clock = new FakeTimeProvider();
        var signals = new ServiceSignals(clock);
        signals.ReportIdle("a", 10);
        clock.Advance(TimeSpan.FromSeconds(4));
        signals.UserIdleSeconds().ShouldNotBeNull().ShouldBe(14, 1e-9);
        signals.ReportIdle("a", 0);
        signals.UserIdleSeconds().ShouldNotBeNull().ShouldBe(0, 1e-9);
    }

    [Fact]
    public void The_most_recently_active_user_wins()
    {
        var signals = new ServiceSignals(new FakeTimeProvider());
        signals.ReportIdle("a", 600);
        signals.ReportIdle("b", 5);
        signals.UserIdleSeconds().ShouldNotBeNull().ShouldBe(5, 1e-9);
    }

    [Fact]
    public void A_report_from_an_app_that_went_quiet_is_ignored()
    {
        var clock = new FakeTimeProvider();
        var signals = new ServiceSignals(clock);
        signals.ReportIdle("a", 10);
        clock.Advance(ServiceSignals.ReportLifetime + TimeSpan.FromSeconds(1));
        signals.UserIdleSeconds().ShouldBeNull();
    }

    [Fact]
    public void A_client_that_disconnects_is_forgotten_and_nonsense_is_ignored()
    {
        var signals = new ServiceSignals(new FakeTimeProvider());
        signals.ReportIdle("a", 10);
        signals.ForgetClient("a");
        signals.ReportIdle("b", -1);
        signals.ReportIdle("c", double.NaN);
        signals.UserIdleSeconds().ShouldBeNull();
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter ServiceSignalsTests`
Expected: build error, `ServiceSignals` not found.

- [x] **Step 3: Write the signals**

`src/PowerLedger.Service/ServiceSignals.cs`
```csharp
namespace PowerLedger.Service;

/// <summary>
/// Facts that reach the service from outside the sampling loop: whether the console display is lit and the session
/// locked, from Windows notifications, and how long the user has been idle, reported by App clients over the pipe.
/// Written from notification and pipe threads, read from the sensor thread.
/// </summary>
internal sealed class ServiceSignals(TimeProvider clock)
{
    /// <summary>A report older than this comes from an App that has gone quiet, and is ignored.</summary>
    public static readonly TimeSpan ReportLifetime = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (double IdleSeconds, long At)> _reports = new();
    private volatile bool _displayOn = true;
    private volatile bool _sessionLocked;

    public bool DisplayOn
    {
        get => _displayOn;
        set => _displayOn = value;
    }

    public bool SessionLocked
    {
        get => _sessionLocked;
        set => _sessionLocked = value;
    }

    /// <summary>One App client's idle time, keyed by its connection so each session's App keeps its own report.</summary>
    public void ReportIdle(string client, double idleSeconds)
    {
        if (!double.IsFinite(idleSeconds) || idleSeconds < 0) return;
        lock (_gate) _reports[client] = (idleSeconds, clock.GetTimestamp());
    }

    public void ForgetClient(string client)
    {
        lock (_gate) _reports.Remove(client);
    }

    /// <summary>
    /// Seconds since the most recently active user's last input: each fresh report aged by the time since it arrived,
    /// the smallest winning. Null when no App has reported lately.
    /// </summary>
    public double? UserIdleSeconds()
    {
        lock (_gate)
        {
            double? idle = null;
            foreach (var (seconds, at) in _reports.Values)
            {
                var age = clock.GetElapsedTime(at);
                if (age > ReportLifetime) continue;
                var now = seconds + age.TotalSeconds;
                idle = idle is { } best ? Math.Min(best, now) : now;
            }
            return idle;
        }
    }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter ServiceSignalsTests`
Expected: `Passed! - Failed: 0, Passed: 6`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Service/ServiceSignals.cs tests/PowerLedger.Service.Tests/ServiceSignalsTests.cs
git commit -m "Collect display, lock and idle signals from outside the loop"
```

---

### Task 9: TickClock — Δt per tick

**Files:**
- Create: `src/PowerLedger.Service/TickClock.cs`
- Test: `tests/PowerLedger.Service.Tests/TickClockTests.cs`

Spec §6: Δt comes from a monotonic clock, and a Δt beyond the gap threshold counts as asleep. Across a sleep, though, a monotonic counter may not have counted the time asleep. So the first tick after a resume measures from the last tick by the wall clock, and the sleep lands in the database as a gap instead of vanishing. A tick that was never committed, because its sensor read hung, is folded into the next Δt, so a long stall also becomes a gap. The first tick after start counts one interval: the service saw nothing before it, and that time is reported as unmonitored.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.Service.Tests/TickClockTests.cs`
```csharp
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class TickClockTests
{
    private static readonly DateTimeOffset Start = Samples.T0;

    [Fact]
    public void The_first_tick_counts_one_interval_because_the_service_saw_nothing_before_it()
        => new TickClock(new FakeTimeProvider(Start)).Measure(Start, interval: 1).ShouldBe(1);

    [Fact]
    public void Later_ticks_measure_the_monotonic_time_since_the_last_one()
    {
        var clock = new FakeTimeProvider(Start);
        var ticks = new TickClock(clock);
        ticks.Commit(clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMilliseconds(1003));
        ticks.Measure(clock.GetUtcNow(), 1).ShouldBe(1.003, 1e-9);
    }

    [Fact]
    public void A_tick_that_was_never_committed_is_folded_into_the_next()
    {
        var clock = new FakeTimeProvider(Start);
        var ticks = new TickClock(clock);
        ticks.Commit(clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(1));
        ticks.Measure(clock.GetUtcNow(), 1).ShouldBe(1, 1e-9);
        clock.Advance(TimeSpan.FromSeconds(1));
        ticks.Measure(clock.GetUtcNow(), 1).ShouldBe(2, 1e-9);
    }

    [Fact]
    public void After_a_resume_the_wall_clock_measures_the_sleep_so_it_becomes_a_gap()
    {
        var clock = new SleepingClock(Start);
        var ticks = new TickClock(clock);
        ticks.Commit(clock.GetUtcNow());
        clock.Sleep(TimeSpan.FromHours(1));
        ticks.Measure(clock.GetUtcNow(), 1).ShouldBe(0, 1e-9);   // all a monotonic clock that stopped would say
        ticks.MarkResumed();
        ticks.Measure(clock.GetUtcNow(), 1).ShouldBe(3600, 1e-9);
        ticks.Commit(clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(1));
        ticks.Measure(clock.GetUtcNow(), 1).ShouldBe(1, 1e-9);   // back on the monotonic clock
    }

    [Fact]
    public void A_wall_clock_set_back_during_sleep_still_counts_one_interval()
    {
        var clock = new FakeTimeProvider(Start);
        var ticks = new TickClock(clock);
        ticks.Commit(Start);
        ticks.MarkResumed();
        ticks.Measure(Start.AddHours(-1), 1).ShouldBe(1);
    }

    /// <summary>A fake clock whose monotonic timestamp can stand still while the wall clock moves, as across a sleep.</summary>
    private sealed class SleepingClock(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private long _slept;

        public void Sleep(TimeSpan duration)
        {
            _slept += duration.Ticks;
            Advance(duration);
        }

        public override long GetTimestamp() => base.GetTimestamp() - _slept * TimestampFrequency / TimeSpan.TicksPerSecond;
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter TickClockTests`
Expected: build error, `TickClock` not found.

- [x] **Step 3: Write the clock**

`src/PowerLedger.Service/TickClock.cs`
```csharp
namespace PowerLedger.Service;

/// <summary>
/// Δt between ticks (spec §6), from the monotonic clock. The first tick after a resume measures from the last tick by
/// the wall clock instead, because a monotonic counter may not have counted the sleep, and the sleep must reach the
/// database as a gap. A tick that is measured but never committed, because its read hung, is folded into the next.
/// </summary>
internal sealed class TickClock(TimeProvider clock)
{
    private long? _lastStamp;
    private DateTimeOffset _lastWall;
    private bool _resumed;

    /// <summary>
    /// Seconds since the last committed tick. <paramref name="interval"/> before any tick has been committed, and at least
    /// <paramref name="interval"/> after a resume, so a wall clock set back during sleep cannot produce a negative Δt.
    /// </summary>
    public double Measure(DateTimeOffset now, double interval)
    {
        if (_lastStamp is not { } stamp) return interval;
        if (_resumed) return Math.Max((now - _lastWall).TotalSeconds, interval);
        return clock.GetElapsedTime(stamp).TotalSeconds;
    }

    /// <summary>Records that the tick measured at <paramref name="now"/> was taken.</summary>
    public void Commit(DateTimeOffset now)
    {
        _lastStamp = clock.GetTimestamp();
        _lastWall = now;
        _resumed = false;
    }

    /// <summary>The machine woke: the next measurement uses the wall clock.</summary>
    public void MarkResumed() => _resumed = true;
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter TickClockTests`
Expected: `Passed! - Failed: 0, Passed: 5`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Service/TickClock.cs tests/PowerLedger.Service.Tests/TickClockTests.cs
git commit -m "Measure each tick's Δt, and a sleep by the wall clock"
```

---

### Task 10: SensorWorker — the sensor thread and its watchdog

**Files:**
- Create: `src/PowerLedger.Service/ISensorSet.cs`
- Create: `src/PowerLedger.Service/SensorWorker.cs`
- Create: `tests/PowerLedger.Service.Tests/FakeSensorSet.cs`
- Create: `tests/PowerLedger.Service.Tests/WaitFor.cs`
- Test: `tests/PowerLedger.Service.Tests/SensorWorkerTests.cs`

Plan B's handoff: the sensor set is single-threaded, and NVML and the performance-counter API have no timeout. So every call into the set runs on one dedicated thread. A read that overruns its timeout abandons the set. The stuck thread finishes on its own and disposes the set when its call returns; the next read builds a fresh set on a fresh thread. `ISensorSet` is the seam that lets the loop's tests use fake sensors.

- [x] **Step 1: Write the test helpers and the failing tests**

`tests/PowerLedger.Service.Tests/FakeSensorSet.cs`
```csharp
using PowerLedger.Core;
using PowerLedger.Sensors;

namespace PowerLedger.Service.Tests;

/// <summary>A sensor set that answers from a script and records how it was used.</summary>
internal sealed class FakeSensorSet(Func<DateTimeOffset, double, Sample>? script = null) : ISensorSet
{
    private int _reads;
    private volatile bool _disposed;

    public int Reads => Volatile.Read(ref _reads);

    public int ThreadId { get; private set; }

    public bool Disposed => _disposed;

    public Sample Read(DateTimeOffset timestamp, double deltaSeconds)
    {
        Interlocked.Increment(ref _reads);
        ThreadId = Environment.CurrentManagedThreadId;
        return script?.Invoke(timestamp, deltaSeconds) ?? Samples.At(timestamp, deltaSeconds);
    }

    public IReadOnlyList<SourceHealth> Health => [new("fake", true, null, 0, 0, null)];

    public int SuspectCount => 0;

    public void Dispose() => _disposed = true;
}
```

`tests/PowerLedger.Service.Tests/WaitFor.cs`
```csharp
namespace PowerLedger.Service.Tests;

internal static class WaitFor
{
    /// <summary>Polls on real time until the condition holds; fails the test after <paramref name="timeoutMs"/>. Yields for the
    /// first 100 ms rather than sleeping, because a timer sleep lasts a whole 15 ms scheduler tick on Windows.</summary>
    public static async Task True(Func<bool> condition, int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            var elapsed = Environment.TickCount64 - start;
            if (elapsed > timeoutMs) throw new TimeoutException("The condition never became true.");
            if (elapsed < 100) await Task.Yield();
            else await Task.Delay(5);
        }
    }
}
```

`tests/PowerLedger.Service.Tests/SensorWorkerTests.cs`
```csharp
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class SensorWorkerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Every_read_runs_on_one_dedicated_thread_against_one_set()
    {
        var built = new List<FakeSensorSet>();
        using var worker = new SensorWorker(() => Add(built, new FakeSensorSet()), Timeout, new FakeTimeProvider());
        for (var i = 0; i < 3; i++) (await worker.ReadAsync(Samples.T0.AddSeconds(i), 1, CancellationToken.None)).ShouldNotBeNull();
        built.Count.ShouldBe(1);
        built[0].Reads.ShouldBe(3);
        built[0].ThreadId.ShouldNotBe(Environment.CurrentManagedThreadId);
    }

    [Fact]
    public async Task A_hung_read_abandons_the_set_and_the_next_read_starts_a_fresh_one()
    {
        var clock = new FakeTimeProvider();
        using var gate = new ManualResetEventSlim(false);
        var built = new List<FakeSensorSet>();
        using var worker = new SensorWorker(
            () => Add(built, built.Count == 0 ? new FakeSensorSet((t, d) => { gate.Wait(); return Samples.At(t, d); }) : new FakeSensorSet()),
            Timeout, clock);

        var hung = worker.ReadAsync(Samples.T0, 1, CancellationToken.None);
        await WaitFor.True(() => built.Count == 1 && built[0].Reads == 1);
        clock.Advance(Timeout);
        (await hung).ShouldBeNull();
        worker.Abandoned.ShouldBe(1);

        (await worker.ReadAsync(Samples.T0.AddSeconds(1), 1, CancellationToken.None)).ShouldNotBeNull();
        built.Count.ShouldBe(2);

        built[0].Disposed.ShouldBeFalse();   // still stuck in its call
        gate.Set();
        await WaitFor.True(() => built[0].Disposed);
    }

    [Fact]
    public async Task Rebuild_throws_the_set_away_and_disposes_it()
    {
        var built = new List<FakeSensorSet>();
        using var worker = new SensorWorker(() => Add(built, new FakeSensorSet()), Timeout, new FakeTimeProvider());
        await worker.ReadAsync(Samples.T0, 1, CancellationToken.None);
        worker.Rebuild();
        await WaitFor.True(() => built[0].Disposed);
        await worker.ReadAsync(Samples.T0.AddSeconds(1), 1, CancellationToken.None);
        built.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_set_that_cannot_be_built_fails_the_read_and_is_tried_again_next_time()
    {
        var attempts = 0;
        using var worker = new SensorWorker(
            () => ++attempts == 1 ? throw new InvalidOperationException("no sensors") : new FakeSensorSet(),
            Timeout, new FakeTimeProvider());
        await Should.ThrowAsync<InvalidOperationException>(() => worker.ReadAsync(Samples.T0, 1, CancellationToken.None));
        (await worker.ReadAsync(Samples.T0.AddSeconds(1), 1, CancellationToken.None)).ShouldNotBeNull();
        attempts.ShouldBe(2);
    }

    [Fact]
    public async Task Disposing_the_worker_disposes_the_set()
    {
        var set = new FakeSensorSet();
        var worker = new SensorWorker(() => set, Timeout, new FakeTimeProvider());
        await worker.ReadAsync(Samples.T0, 1, CancellationToken.None);
        worker.Dispose();
        await WaitFor.True(() => set.Disposed);
    }

    private static FakeSensorSet Add(List<FakeSensorSet> built, FakeSensorSet set)
    {
        lock (built) built.Add(set);
        return set;
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter SensorWorkerTests`
Expected: build error, `ISensorSet` not found.

- [x] **Step 3: Write the seam and the worker**

`src/PowerLedger.Service/ISensorSet.cs`
```csharp
using PowerLedger.Core;
using PowerLedger.Sensors;

namespace PowerLedger.Service;

/// <summary>The sensor layer as the loop uses it: one validated sample a tick, and what the status screen needs.
/// Single-threaded: <see cref="SensorWorker"/> makes every call from its own thread.</summary>
internal interface ISensorSet : IDisposable
{
    Sample Read(DateTimeOffset timestamp, double deltaSeconds);

    IReadOnlyList<SourceHealth> Health { get; }

    int SuspectCount { get; }
}

/// <summary>The real sensors, assembled by Plan B's factory.</summary>
internal sealed class MachineSensorSet(MachineSensors sensors) : ISensorSet
{
    public Sample Read(DateTimeOffset timestamp, double deltaSeconds) => sensors.Read(timestamp, deltaSeconds);

    public IReadOnlyList<SourceHealth> Health => sensors.Sampler.Health;

    public int SuspectCount => sensors.Validator.SuspectCount;

    public void Dispose() => sensors.Dispose();
}
```

`src/PowerLedger.Service/SensorWorker.cs`
```csharp
using System.Collections.Concurrent;
using PowerLedger.Core;
using PowerLedger.Sensors;

namespace PowerLedger.Service;

/// <param name="Sample">The validated sample.</param>
/// <param name="Health">Each source's health after this tick.</param>
/// <param name="SuspectCount">Suspect ticks since the set was built.</param>
internal sealed record TickResult(Sample Sample, IReadOnlyList<SourceHealth> Health, int SuspectCount);

/// <summary>
/// Runs every call into a sensor set on one dedicated thread, so a native call that hangs cannot stall the loop (Plan
/// B's watchdog rule). A read that overruns its timeout abandons the set: the stuck thread is left to finish on its own
/// and dispose the set when its call returns, and the next read builds a fresh set on a fresh thread. The loop is the
/// only caller.
/// </summary>
internal sealed class SensorWorker(Func<ISensorSet> factory, TimeSpan timeout, TimeProvider clock) : IDisposable
{
    private Lane? _lane;

    /// <summary>Sets abandoned because a read hung.</summary>
    public int Abandoned { get; private set; }

    /// <summary>One tick's sample, or null when the read hung and the set was abandoned. A set is built on first use.</summary>
    public async Task<TickResult?> ReadAsync(DateTimeOffset timestamp, double deltaSeconds, CancellationToken cancel)
    {
        var lane = _lane ??= new Lane(factory);
        try
        {
            return await lane.Run(set => new TickResult(set.Read(timestamp, deltaSeconds), set.Health, set.SuspectCount))
                .WaitAsync(timeout, clock, cancel).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Retire();
            Abandoned++;
            return null;
        }
    }

    /// <summary>Throws the set away, as after a resume; the next read builds a fresh one.</summary>
    public void Rebuild() => Retire();

    public void Dispose() => Retire();

    private void Retire()
    {
        _lane?.Retire();
        _lane = null;
    }

    private sealed class Lane
    {
        private readonly BlockingCollection<Action> _work = new();
        private readonly Func<ISensorSet> _factory;
        private ISensorSet? _set;

        public Lane(Func<ISensorSet> factory)
        {
            _factory = factory;
            new Thread(Drain) { IsBackground = true, Name = "PowerLedger sensors" }.Start();
        }

        public Task<T> Run<T>(Func<ISensorSet, T> call)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _work.Add(() =>
            {
                try
                {
                    done.SetResult(call(_set ??= _factory()));
                }
                catch (Exception error)
                {
                    done.SetException(error);
                }
            });
            return done.Task;
        }

        /// <summary>No more work. The thread disposes the set once the call it is running, if any, returns.</summary>
        public void Retire() => _work.CompleteAdding();

        private void Drain()
        {
            foreach (var work in _work.GetConsumingEnumerable()) work();
            try
            {
                _set?.Dispose();
            }
            catch (Exception)
            {
                // A set that fails to dispose must not take the process down with an unhandled exception on this thread.
            }
        }
    }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter SensorWorkerTests`
Expected: `Passed! - Failed: 0, Passed: 5`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Service/ISensorSet.cs src/PowerLedger.Service/SensorWorker.cs tests/PowerLedger.Service.Tests/FakeSensorSet.cs tests/PowerLedger.Service.Tests/WaitFor.cs tests/PowerLedger.Service.Tests/SensorWorkerTests.cs
git commit -m "Run the sensors on their own thread and replace a set whose read hangs"
```

---

### Task 11: WriteBuffer — batched writes that survive a full disk

**Files:**
- Create: `src/PowerLedger.Service/WriteBuffer.cs`
- Create: `tests/PowerLedger.Service.Tests/Readings.cs`
- Test: `tests/PowerLedger.Service.Tests/WriteBufferTests.cs`

Spec §7 batches 60 samples per transaction, once a minute, and flushes on suspend, shutdown and stop. Spec §10: on a full disk, pause writes, keep an hour in memory and retry every minute. A failed batch is retried whole; `samples_raw` is keyed by timestamp and the insert replaces, so a retry never duplicates.

- [x] **Step 1: Write the test helper and the failing tests**

`tests/PowerLedger.Service.Tests/Readings.cs`
```csharp
using PowerLedger.Contracts;
using PowerLedger.Core;

namespace PowerLedger.Service.Tests;

internal static class Readings
{
    /// <summary>A measured one-second reading at <paramref name="at"/>: half the watts to the CPU, 4 W to the display.</summary>
    public static Reading At(DateTimeOffset at, double watts = 36, double delta = 1) => new(
        at, delta, watts, Quality.Measured,
        new Components(Cpu: watts / 2, Gpu: 0, Display: 4, Ram: 0, Storage: 0, Board: 0, Extras: 0, Monitors: 0, PsuLoss: 0, Unattributed: watts / 2 - 4),
        OnBattery: true, DisplayOn: true, UserIdle: false, SessionLocked: false, CpuLoad: 0.2, GpuLoad: null, Brightness: 0.5, Suspect: false);

    /// <summary>The same, <paramref name="second"/> seconds after <see cref="Samples.T0"/>.</summary>
    public static Reading At(int second, double watts = 36, double delta = 1) => At(Samples.T0.AddSeconds(second), watts, delta);
}
```

`tests/PowerLedger.Service.Tests/WriteBufferTests.cs`
```csharp
using Microsoft.Data.Sqlite;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class WriteBufferTests
{
    [Fact]
    public void A_flush_writes_everything_held_in_one_batch()
    {
        var batches = new List<int>();
        var buffer = new WriteBuffer(batch => batches.Add(batch.Count));
        for (var s = 0; s < 60; s++) buffer.Add(Readings.At(s));
        buffer.Flush().ShouldBeTrue();
        batches.ShouldBe(new[] { 60 });
        buffer.Count.ShouldBe(0);
        buffer.Problem.ShouldBeNull();
    }

    [Fact]
    public void An_empty_flush_writes_nothing()
    {
        var calls = 0;
        new WriteBuffer(_ => calls++).Flush().ShouldBeTrue();
        calls.ShouldBe(0);
    }

    [Fact]
    public void A_failed_write_keeps_the_readings_and_the_next_flush_retries_them()
    {
        var full = true;
        var written = 0;
        var buffer = new WriteBuffer(batch =>
        {
            if (full) throw new SqliteException("SQLite Error 13: 'database or disk is full'.", 13);
            written += batch.Count;
        });
        for (var s = 0; s < 60; s++) buffer.Add(Readings.At(s));
        buffer.Flush().ShouldBeFalse();
        buffer.Count.ShouldBe(60);
        buffer.Problem.ShouldNotBeNull().ShouldContain("disk is full");

        for (var s = 60; s < 120; s++) buffer.Add(Readings.At(s));
        full = false;
        buffer.Flush().ShouldBeTrue();
        written.ShouldBe(120);
        buffer.Problem.ShouldBeNull();
    }

    [Fact]
    public void Past_its_capacity_the_oldest_readings_are_dropped_and_counted()
    {
        var buffer = new WriteBuffer(_ => throw new SqliteException("SQLite Error 10: 'disk I/O error'.", 10), capacity: 100);
        for (var s = 0; s < 150; s++) buffer.Add(Readings.At(s));
        buffer.Flush().ShouldBeFalse();
        buffer.Count.ShouldBe(100);
        buffer.Dropped.ShouldBe(50);
        buffer.Problem.ShouldNotBeNull().ShouldContain("50 older");
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter WriteBufferTests`
Expected: build error, `WriteBuffer` not found.

- [x] **Step 3: Write the buffer**

`src/PowerLedger.Service/WriteBuffer.cs`
```csharp
using Microsoft.Data.Sqlite;
using PowerLedger.Core;

namespace PowerLedger.Service;

/// <summary>
/// Readings waiting to be written as one batch (spec §7): flushed once a minute, on suspend and on stop. When a write
/// fails, as it does on a full disk, the readings stay here and the next flush retries them, up to
/// <paramref name="capacity"/> readings (an hour at one a second); past that the oldest are dropped (spec §10).
/// </summary>
internal sealed class WriteBuffer(Action<IReadOnlyList<Reading>> write, int capacity = 3600)
{
    private readonly List<Reading> _pending = [];
    private SqliteException? _lastError;

    public int Count => _pending.Count;

    /// <summary>Readings dropped because the buffer overflowed while writes were failing.</summary>
    public long Dropped { get; private set; }

    /// <summary>Null while writes succeed; otherwise what is wrong, for the status screen.</summary>
    public string? Problem => _lastError is null
        ? null
        : $"Writes are failing ({_lastError.Message}); {Count} readings are held in memory"
          + (Dropped > 0 ? $" and {Dropped} older ones were dropped." : ".");

    public void Add(Reading reading)
    {
        _pending.Add(reading);
        if (_pending.Count <= capacity) return;
        _pending.RemoveAt(0);
        Dropped++;
    }

    /// <summary>Writes everything held. True when the buffer is empty afterwards.</summary>
    public bool Flush()
    {
        if (_pending.Count == 0) return true;
        try
        {
            write(_pending);
            _pending.Clear();
            _lastError = null;
            return true;
        }
        catch (SqliteException error)
        {
            _lastError = error;
            return false;
        }
    }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter WriteBufferTests`
Expected: `Passed! - Failed: 0, Passed: 4`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Service/WriteBuffer.cs tests/PowerLedger.Service.Tests/Readings.cs tests/PowerLedger.Service.Tests/WriteBufferTests.cs
git commit -m "Batch raw writes and hold them in memory while the disk refuses"
```

---

### Task 12: Rollups — minute and hour rows

**Files:**
- Create: `src/PowerLedger.Service/Rollups.cs`
- Test: `tests/PowerLedger.Service.Tests/RollupsTests.cs`

Two handoff rules meet here. Plan B: build each minute row from the raw rows in storage, never from the loop's own buffer, because `UpsertMinute` replaces the row; and on start, fold every minute since `LastMinuteStart()` that has raw rows. Plan A: pick one way to handle the running hour. The report reads minute rows for any hour without an hour row, so an hour is folded only once it is complete. Folding is idempotent, so the loop can fold a range again after a restart or a failed write. The gap threshold is passed in, as Plan A requires.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.Service.Tests/RollupsTests.cs`
```csharp
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class RollupsTests
{
    private const double Gap = 5;

    [Fact]
    public void Each_minute_with_raw_rows_is_folded_and_empty_minutes_are_skipped()
    {
        using var t = new TestDatabase();
        var (raw, aggregates) = Repositories(t);
        raw.InsertBatch([.. Seconds(0, 60), .. Seconds(120, 60)]);   // minutes 0 and 2; minute 1 is empty
        new Rollups(raw, aggregates).FoldRange(T(0), T(3), Gap);

        var minutes = aggregates.ReadMinutes(T(0), T(3));
        minutes.Select(m => m.Start).ShouldBe(new[] { T(0), T(2) });
        minutes[0].SampleCount.ShouldBe(60);
        minutes[0].EnergyWh.ShouldBe(60 * 36 / 3600.0, 1e-9);
    }

    [Fact]
    public void Ticks_from_a_second_run_inside_a_folded_minute_are_added_not_replaced()
    {
        using var t = new TestDatabase();
        var (raw, aggregates) = Repositories(t);
        var rollups = new Rollups(raw, aggregates);
        raw.InsertBatch([.. Seconds(0, 20)]);
        rollups.FoldRange(T(0), T(1), Gap);
        raw.InsertBatch([.. Seconds(30, 20)]);
        rollups.FoldRange(T(0), T(1), Gap);
        aggregates.ReadMinutes(T(0), T(1)).Single().SampleCount.ShouldBe(40);
    }

    [Fact]
    public void An_hour_is_folded_only_once_it_is_complete()
    {
        using var t = new TestDatabase();
        var (raw, aggregates) = Repositories(t);
        var rollups = new Rollups(raw, aggregates);
        raw.InsertBatch([.. Seconds(0, 1800)]);                       // 10:00 to 10:30
        rollups.FoldRange(T(0), T(30), Gap);
        aggregates.ReadHours(T(0), T(60)).ShouldBeEmpty();
        rollups.FoldRange(T(30), T(60), Gap);
        var hour = aggregates.ReadHours(T(0), T(60)).Single();
        hour.SampleCount.ShouldBe(1800);
        hour.EnergyWh.ShouldBe(1800 * 36 / 3600.0, 1e-9);
    }

    [Fact]
    public void Catch_up_folds_what_a_crashed_run_left_unfolded()
    {
        using var t = new TestDatabase();
        var (raw, aggregates) = Repositories(t);
        var rollups = new Rollups(raw, aggregates);
        raw.InsertBatch([.. Seconds(0, 180)]);                        // 10:00 to 10:03
        rollups.FoldRange(T(0), T(1), Gap);                          // the old run folded 10:00, then died
        rollups.CatchUp(T(5), TimeSpan.FromHours(48), Gap);
        aggregates.ReadMinutes(T(0), T(5)).Select(m => m.SampleCount).ShouldBe(new[] { 60, 60, 60 });
        aggregates.ReadHours(T(0), T(60)).ShouldBeEmpty();           // 10:00 is still running
    }

    [Fact]
    public void Catch_up_folds_a_complete_hour_that_was_never_folded()
    {
        using var t = new TestDatabase();
        var (raw, aggregates) = Repositories(t);
        var rollups = new Rollups(raw, aggregates);
        raw.InsertBatch([.. Seconds(3480, 240)]);                     // 10:58 to 11:02
        rollups.FoldRange(T(58), T(59), Gap);                        // only 10:58 was folded before the crash
        rollups.CatchUp(T(63), TimeSpan.FromHours(48), Gap);
        aggregates.ReadHours(T(0), T(60)).Single().SampleCount.ShouldBe(120);
        aggregates.ReadMinutes(T(60), T(63)).Count.ShouldBe(2);
    }

    [Fact]
    public void Catch_up_on_an_empty_database_does_nothing()
    {
        using var t = new TestDatabase();
        var (raw, aggregates) = Repositories(t);
        new Rollups(raw, aggregates).CatchUp(T(5), TimeSpan.FromHours(48), Gap);
        aggregates.LastMinuteStart().ShouldBeNull();
    }

    private static DateTimeOffset T(int minute) => Samples.T0.AddMinutes(minute);

    private static IEnumerable<PowerLedger.Core.Reading> Seconds(int from, int count)
        => Enumerable.Range(from, count).Select(s => Readings.At(s));

    private static (RawSampleRepository Raw, AggregateRepository Aggregates) Repositories(TestDatabase t)
        => (new RawSampleRepository(t.Db), new AggregateRepository(t.Db));
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter RollupsTests`
Expected: build error, `Rollups` not found.

- [x] **Step 3: Write the rollups**

`src/PowerLedger.Service/Rollups.cs`
```csharp
using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.Service;

/// <summary>
/// Minute and hour rows (spec §7). A minute is folded from the raw rows in storage, never from the loop's own buffer:
/// UpsertMinute replaces the row, so a restart inside a minute must add to what the earlier run wrote (Plan B handoff).
/// An hour is folded only once its last minute has been, because the report reads minute rows for any hour that has no
/// hour row yet (Plan A handoff). Folding is idempotent: folding a range again rewrites the same rows.
/// </summary>
internal sealed class Rollups(RawSampleRepository raw, AggregateRepository aggregates)
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <summary>Folds every minute in [from, to) that has raw rows, then every hour that overlaps the range and ends by <paramref name="to"/>.</summary>
    public void FoldRange(DateTimeOffset from, DateTimeOffset to, double gapThresholdSeconds)
    {
        from = Floor(from, Minute);
        to = Floor(to, Minute);
        for (var chunk = Floor(from, Hour); chunk < to; chunk += Hour)
        {
            var start = chunk < from ? from : chunk;
            var end = chunk + Hour < to ? chunk + Hour : to;
            foreach (var minute in raw.Read(start, end).GroupBy(r => Floor(r.Timestamp, Minute)))
            {
                aggregates.UpsertMinute(Downsampler.ToMinute(minute.Key, [.. minute], gapThresholdSeconds));
            }
        }
        for (var hour = Floor(from, Hour); hour + Hour <= to; hour += Hour) FoldHour(hour);
    }

    /// <summary>At start: whatever a previous run left unfolded, from the newest minute row (or the raw-retention horizon on a
    /// fresh database) up to the current minute, and any complete hour since the newest hour row.</summary>
    public void CatchUp(DateTimeOffset now, TimeSpan horizon, double gapThresholdSeconds)
    {
        var current = Floor(now, Minute);
        var from = aggregates.LastMinuteStart() is { } last && last > current - horizon ? last : current - horizon;
        FoldRange(from, current, gapThresholdSeconds);
        if (aggregates.LastHourStart() is not { } lastHour) return;
        for (var hour = lastHour; hour < Floor(from, Hour); hour += Hour) FoldHour(hour);
    }

    private void FoldHour(DateTimeOffset hourStart)
    {
        var minutes = aggregates.ReadMinutes(hourStart, hourStart + Hour);
        if (minutes.Count > 0) aggregates.UpsertHour(Downsampler.ToHour(hourStart, minutes));
    }

    /// <summary>The start of the minute or hour containing <paramref name="t"/>, in UTC like every stored row.</summary>
    internal static DateTimeOffset Floor(DateTimeOffset t, TimeSpan unit) => new(t.UtcTicks - t.UtcTicks % unit.Ticks, TimeSpan.Zero);
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter RollupsTests`
Expected: `Passed! - Failed: 0, Passed: 6`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Service/Rollups.cs tests/PowerLedger.Service.Tests/RollupsTests.cs
git commit -m "Fold minutes from stored ticks, complete hours from minutes, and catch up at start"
```

---

### Task 13: Retention — the 03:00 purge and the weekly vacuum

**Files:**
- Create: `src/PowerLedger.Service/Retention.cs`
- Test: `tests/PowerLedger.Service.Tests/RetentionTests.cs`

Spec §7: purge daily at 03:00 local and run `PRAGMA incremental_vacuum` weekly. A machine asleep or off at 03:00 purges at its next chance. The last run is kept in the settings table, so a restart does not purge twice. A 03:00 that a clock change skips is taken as the first valid local time after it.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.Service.Tests/RetentionTests.cs`
```csharp
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class RetentionTests
{
    private static readonly TimeZoneInfo Plus2 = TimeZoneInfo.CreateCustomTimeZone("PL+2", TimeSpan.FromHours(2), "PL+2", "PL+2");

    private static DateTimeOffset Local(int day, int hour, int minute = 0) => new(2026, 9, day, hour, minute, 0, TimeSpan.FromHours(2));

    [Fact]
    public void The_latest_purge_time_is_today_at_three_once_it_has_passed_and_yesterday_before()
    {
        RetentionSchedule.LatestPurgeTime(Local(12, 10), Plus2).ShouldBe(Local(12, 3));
        RetentionSchedule.LatestPurgeTime(Local(12, 2, 59), Plus2).ShouldBe(Local(11, 3));
        RetentionSchedule.LatestPurgeTime(Local(12, 3), Plus2).ShouldBe(Local(12, 3));
    }

    [Fact]
    public void A_purge_is_due_when_it_never_ran_or_last_ran_before_the_latest_three_oclock()
    {
        RetentionSchedule.PurgeDue(Local(12, 10), lastPurge: null, Plus2).ShouldBeTrue();
        RetentionSchedule.PurgeDue(Local(12, 10), Local(12, 3, 1), Plus2).ShouldBeFalse();
        RetentionSchedule.PurgeDue(Local(12, 10), Local(11, 23), Plus2).ShouldBeTrue();   // asleep at 03:00: purge on waking
    }

    [Fact]
    public void A_three_oclock_that_the_clock_skips_becomes_the_first_valid_time_after_it()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2000, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 3, 29),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 4, 0, 0), 10, 25));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Skip3", TimeSpan.Zero, "Skip3", "Skip3", "Skip3 summer", [rule]);
        var noon = new DateTimeOffset(2026, 3, 29, 12, 0, 0, TimeSpan.FromHours(1));
        RetentionSchedule.LatestPurgeTime(noon, zone).ShouldBe(new DateTimeOffset(2026, 3, 29, 4, 0, 0, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void The_vacuum_is_due_a_week_after_the_last()
    {
        RetentionSchedule.VacuumDue(Local(12, 3), null).ShouldBeTrue();
        RetentionSchedule.VacuumDue(Local(12, 3), Local(6, 3)).ShouldBeFalse();
        RetentionSchedule.VacuumDue(Local(12, 3), Local(5, 3)).ShouldBeTrue();
    }

    [Fact]
    public void The_runner_purges_once_and_a_restart_does_not_purge_again_the_same_day()
    {
        using var t = new TestDatabase();
        var raw = new RawSampleRepository(t.Db);
        var now = Local(12, 10);
        raw.InsertBatch([Readings.At(now.AddHours(-50)), Readings.At(now.AddHours(-1))]);

        new RetentionRunner(t.Db, Plus2).RunIfDue(now, new RetentionOptions()).ShouldNotBeNull().RawDeleted.ShouldBe(1);
        raw.Count().ShouldBe(1);
        new RetentionRunner(t.Db, Plus2).RunIfDue(now.AddHours(1), new RetentionOptions()).ShouldBeNull();
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter RetentionTests`
Expected: build error, `RetentionSchedule` not found.

- [x] **Step 3: Write the schedule and the runner**

`src/PowerLedger.Service/Retention.cs`
```csharp
using System.Globalization;
using PowerLedger.Storage;

namespace PowerLedger.Service;

/// <summary>When the daily purge and the weekly vacuum are due (spec §7): the purge at 03:00 local, or at the first chance
/// after it when the machine was asleep or off then; the vacuum a week after the last one.</summary>
internal static class RetentionSchedule
{
    public static readonly TimeSpan VacuumEvery = TimeSpan.FromDays(7);
    private static readonly TimeSpan PurgeTimeOfDay = TimeSpan.FromHours(3);

    /// <summary>The latest 03:00 local at or before <paramref name="now"/>.</summary>
    public static DateTimeOffset LatestPurgeTime(DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var candidate = At(today, zone);
        return candidate <= now ? candidate : At(today.AddDays(-1), zone);
    }

    public static bool PurgeDue(DateTimeOffset now, DateTimeOffset? lastPurge, TimeZoneInfo zone)
        => lastPurge is not { } last || last < LatestPurgeTime(now, zone);

    public static bool VacuumDue(DateTimeOffset now, DateTimeOffset? lastVacuum)
        => lastVacuum is not { } last || now - last >= VacuumEvery;

    /// <summary>03:00 local on <paramref name="date"/>, or the first valid local time after it when a clock change skips it.</summary>
    private static DateTimeOffset At(DateTime date, TimeZoneInfo zone)
    {
        var wall = date + PurgeTimeOfDay;
        while (zone.IsInvalidTime(wall)) wall = wall.AddMinutes(30);
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }
}

/// <summary>Runs the purge and the vacuum when due, remembering the last runs in the settings table so a restart does not repeat them.</summary>
internal sealed class RetentionRunner(SqliteDatabase database, TimeZoneInfo zone)
{
    internal const string LastPurgeKey = "retention.last-purge";
    internal const string LastVacuumKey = "retention.last-vacuum";

    private readonly SettingsRepository _settings = new(database);
    private readonly RetentionJob _job = new(database);
    private bool _loaded;
    private DateTimeOffset? _lastPurge;
    private DateTimeOffset? _lastVacuum;

    /// <returns>What the purge removed when it ran; null when nothing was due.</returns>
    public RetentionResult? RunIfDue(DateTimeOffset now, RetentionOptions options)
    {
        if (!_loaded)
        {
            _lastPurge = Read(LastPurgeKey);
            _lastVacuum = Read(LastVacuumKey);
            _loaded = true;
        }
        if (!RetentionSchedule.PurgeDue(now, _lastPurge, zone)) return null;

        var result = _job.Run(now, options);
        _lastPurge = now;
        Write(LastPurgeKey, now);
        if (RetentionSchedule.VacuumDue(now, _lastVacuum))
        {
            _job.IncrementalVacuum();
            _lastVacuum = now;
            Write(LastVacuumKey, now);
        }
        return result;
    }

    private DateTimeOffset? Read(string key)
        => _settings.Get(key) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;

    private void Write(string key, DateTimeOffset at) => _settings.Set(key, at.ToString("O", CultureInfo.InvariantCulture));
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter RetentionTests`
Expected: `Passed! - Failed: 0, Passed: 5`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Service/Retention.cs tests/PowerLedger.Service.Tests/RetentionTests.cs
git commit -m "Purge at 03:00 local, vacuum weekly, and remember both across restarts"
```

---

### Task 14: SessionTracker — the power-state timeline

**Files:**
- Create: `src/PowerLedger.Service/SessionTracker.cs`
- Test: `tests/PowerLedger.Service.Tests/SessionTrackerTests.cs`

Spec §6: each session row says why it started (boot, service start, resume, crash recovered) and why it ended (suspend, shutdown, service stop, crash recovered). A row left open means the service died; it is closed at its last recorded tick, and the new session starts as crash-recovered too. Plan A's rule: close only the id of the live session, so a suspend followed by a shutdown does not look like a lost session. The service counts as starting with Windows when the machine has been up less than five minutes.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.Service.Tests/SessionTrackerTests.cs`
```csharp
using PowerLedger.Contracts;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class SessionTrackerTests
{
    private static readonly DateTimeOffset Now = Samples.T0;

    [Theory]
    [InlineData(2, SessionReason.Boot)]
    [InlineData(600, SessionReason.ServiceStart)]
    public void A_clean_start_is_a_boot_when_windows_has_only_just_started(int uptimeMinutes, SessionReason expected)
    {
        using var t = new TestDatabase();
        var tracker = new SessionTracker(new SessionRepository(t.Db));
        tracker.Start(Now, TimeSpan.FromMinutes(uptimeMinutes), lastTick: null).ShouldBe(expected);
        tracker.Current.ShouldNotBeNull();
    }

    [Fact]
    public void A_session_left_open_is_closed_at_its_last_tick_and_the_new_one_is_crash_recovered()
    {
        using var t = new TestDatabase();
        var sessions = new SessionRepository(t.Db);
        sessions.Open(SessionReason.Boot, Now.AddHours(-2));
        var tracker = new SessionTracker(sessions);

        tracker.Start(Now, TimeSpan.FromHours(3), lastTick: Now.AddMinutes(-7)).ShouldBe(SessionReason.CrashRecovered);

        var all = sessions.List(Now.AddDays(-1), Now.AddDays(1));
        all[0].End.ShouldBe(Now.AddMinutes(-7));
        all[0].EndReason.ShouldBe(SessionReason.CrashRecovered);
        all[1].Reason.ShouldBe(SessionReason.CrashRecovered);
        all[1].End.ShouldBeNull();
    }

    [Fact]
    public void A_crashed_session_with_no_ticks_ends_where_it_began()
    {
        using var t = new TestDatabase();
        var sessions = new SessionRepository(t.Db);
        sessions.Open(SessionReason.Boot, Now.AddHours(-2));
        new SessionTracker(sessions).Start(Now, TimeSpan.FromHours(3), lastTick: Now.AddHours(-5));
        sessions.List(Now.AddDays(-1), Now.AddDays(1))[0].End.ShouldBe(Now.AddHours(-2));
    }

    [Fact]
    public void Suspend_then_resume_then_stop_writes_two_sessions()
    {
        using var t = new TestDatabase();
        var sessions = new SessionRepository(t.Db);
        var tracker = new SessionTracker(sessions);
        tracker.Start(Now, TimeSpan.FromHours(3), null);
        tracker.End(Now.AddMinutes(10), SessionReason.Suspend);
        tracker.End(Now.AddMinutes(11), SessionReason.Shutdown);    // nothing open: a no-op
        tracker.Resume(Now.AddMinutes(30));
        tracker.Resume(Now.AddMinutes(31));                         // already open: a no-op
        tracker.End(Now.AddMinutes(40), SessionReason.ServiceStop);

        sessions.List(Now.AddDays(-1), Now.AddDays(1))
            .Select(s => (s.Reason, s.EndReason, s.Start, s.End))
            .ShouldBe(new (SessionReason, SessionReason?, DateTimeOffset, DateTimeOffset?)[]
            {
                (SessionReason.ServiceStart, SessionReason.Suspend, Now, Now.AddMinutes(10)),
                (SessionReason.Resume, SessionReason.ServiceStop, Now.AddMinutes(30), Now.AddMinutes(40)),
            });
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter SessionTrackerTests`
Expected: build error, `SessionTracker` not found.

- [x] **Step 3: Write the tracker**

`src/PowerLedger.Service/SessionTracker.cs`
```csharp
using PowerLedger.Contracts;
using PowerLedger.Storage;

namespace PowerLedger.Service;

/// <summary>
/// The sessions timeline (spec §6): one open row while the machine is awake and the service runs. A row left open by
/// an earlier run means the service died; it is closed at its last recorded tick as crash-recovered, and the new
/// session starts with the same reason. Only the live session's id is ever closed (Plan A handoff).
/// </summary>
internal sealed class SessionTracker(SessionRepository sessions)
{
    /// <summary>The service counts as starting with Windows when the machine has been up less than this.</summary>
    public static readonly TimeSpan BootWindow = TimeSpan.FromMinutes(5);

    public long? Current { get; private set; }

    public SessionReason Start(DateTimeOffset now, TimeSpan systemUptime, DateTimeOffset? lastTick)
    {
        var reason = systemUptime < BootWindow ? SessionReason.Boot : SessionReason.ServiceStart;
        if (sessions.OpenSession() is { } abandoned)
        {
            sessions.CloseAllOpen(Clamp(lastTick, abandoned.Start, now));
            reason = SessionReason.CrashRecovered;
        }
        Current = sessions.Open(reason, now);
        return reason;
    }

    /// <summary>Closes the live session. A no-op when none is open, as after a suspend.</summary>
    public void End(DateTimeOffset now, SessionReason reason)
    {
        if (Current is not { } id) return;
        sessions.Close(id, now, reason);
        Current = null;
    }

    /// <summary>Opens the session a resume starts. A no-op when one is already open, as when a resume arrives without its suspend.</summary>
    public void Resume(DateTimeOffset now) => Current ??= sessions.Open(SessionReason.Resume, now);

    /// <summary>A crashed session ends at its last tick, which cannot fall before the session began or after now.</summary>
    private static DateTimeOffset Clamp(DateTimeOffset? lastTick, DateTimeOffset start, DateTimeOffset now)
        => lastTick is not { } tick ? start : tick < start ? start : tick > now ? now : tick;
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter SessionTrackerTests`
Expected: `Passed! - Failed: 0, Passed: 5`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Service/SessionTracker.cs tests/PowerLedger.Service.Tests/SessionTrackerTests.cs
git commit -m "Keep the sessions timeline, closing a crashed session at its last tick"
```

---

### Task 15: CalibrationKeeper — the learner for the machine in use

**Files:**
- Create: `src/PowerLedger.Service/CalibrationKeeper.cs`
- Test: `tests/PowerLedger.Service.Tests/CalibrationKeeperTests.cs`

Spec §5 keys calibration by the hardware hash. Plan A's rule: `CalibrationState` holds a list and compares by reference, so save on a timer, never by comparing states. The keeper saves every ten minutes, on suspend and on stop, and before switching to another machine's learner. The status screen shows progress: battery samples against the total needed, and how many brightness buckets are trusted.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.Service.Tests/CalibrationKeeperTests.cs`
```csharp
using PowerLedger.Core;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class CalibrationKeeperTests
{
    private static readonly CalibrationOptions Quick = new(HalfLifeSamples: 10, MinBucketSamples: 1, MinTotalSamples: 2);
    private static readonly DateTimeOffset Now = Samples.T0;

    [Fact]
    public void Using_a_hash_loads_what_was_learned_for_it()
    {
        using var t = new TestDatabase();
        var repository = new CalibrationRepository(t.Db);
        var trained = new CalibrationLearner(Quick);
        Feed(trained, 3);
        repository.Save("abc", trained.Export(), Now);

        var keeper = new CalibrationKeeper(repository, Quick, TimeSpan.FromMinutes(10));
        keeper.Use("abc", Now);
        keeper.Hash.ShouldBe("abc");
        keeper.Learner.TotalSamples.ShouldBe(3);
    }

    [Fact]
    public void Switching_machines_saves_the_old_learner_first()
    {
        using var t = new TestDatabase();
        var repository = new CalibrationRepository(t.Db);
        var keeper = new CalibrationKeeper(repository, Quick, TimeSpan.FromMinutes(10));
        keeper.Use("old", Now);
        Feed(keeper.Learner, 3);
        keeper.Use("new", Now);
        keeper.Learner.TotalSamples.ShouldBe(0);
        Loaded(repository, "old").TotalSamples.ShouldBe(3);
    }

    [Fact]
    public void Saves_happen_on_the_timer_not_on_every_tick()
    {
        using var t = new TestDatabase();
        var repository = new CalibrationRepository(t.Db);
        var keeper = new CalibrationKeeper(repository, Quick, TimeSpan.FromMinutes(10));
        keeper.Use("abc", Now);
        Feed(keeper.Learner, 2);
        keeper.SaveIfDue(Now.AddMinutes(9));
        repository.Load("abc").Buckets.ShouldBeEmpty();
        keeper.SaveIfDue(Now.AddMinutes(10));
        repository.Load("abc").Buckets.ShouldNotBeEmpty();
    }

    [Fact]
    public void Reset_forgets_in_memory_and_in_storage()
    {
        using var t = new TestDatabase();
        var repository = new CalibrationRepository(t.Db);
        var keeper = new CalibrationKeeper(repository, Quick, TimeSpan.FromMinutes(10));
        keeper.Use("abc", Now);
        Feed(keeper.Learner, 3);
        keeper.Save(Now);
        keeper.Reset(Now);
        keeper.Learner.TotalSamples.ShouldBe(0);
        repository.Load("abc").Buckets.ShouldBeEmpty();
    }

    [Fact]
    public void Status_counts_battery_samples_and_trusted_buckets()
    {
        using var t = new TestDatabase();
        var keeper = new CalibrationKeeper(new CalibrationRepository(t.Db), Quick, TimeSpan.FromMinutes(10));
        keeper.Use("abc", Now);
        Feed(keeper.Learner, 3);   // every tick at 50 % brightness: one bucket
        keeper.Status().ShouldBe(new PowerLedger.Contracts.CalibrationStatus(3, 2, 1, CalibrationBuckets.Count));
    }

    private static void Feed(CalibrationLearner learner, int ticks)
    {
        for (var i = 0; i < ticks; i++) learner.Observe(Samples.At(Now.AddSeconds(i), batteryW: 20), cpuW: 8, gpuW: 0, displayW: 3);
    }

    private static CalibrationLearner Loaded(CalibrationRepository repository, string hash)
    {
        var learner = new CalibrationLearner(Quick);
        learner.Import(repository.Load(hash));
        return learner;
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter CalibrationKeeperTests`
Expected: build error, `CalibrationKeeper` not found.

- [x] **Step 3: Write the keeper**

`src/PowerLedger.Service/CalibrationKeeper.cs`
```csharp
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.Service;

/// <summary>
/// The calibration learner for the machine in use, keyed by its hardware hash (spec §5). Saved on a timer, on suspend,
/// on stop and before switching machines; never by comparing states, which compare by reference (Plan A handoff).
/// </summary>
internal sealed class CalibrationKeeper(CalibrationRepository repository, CalibrationOptions options, TimeSpan saveEvery)
{
    private DateTimeOffset _lastSave;

    public CalibrationLearner Learner { get; private set; } = new(options);

    public string? Hash { get; private set; }

    /// <summary>Switches to the learner for <paramref name="hash"/>, saving the current one first. A no-op for the same hash.</summary>
    public void Use(string hash, DateTimeOffset now)
    {
        if (hash == Hash) return;
        if (Hash is not null) Save(now);
        var learner = new CalibrationLearner(options);
        learner.Import(repository.Load(hash));
        Learner = learner;
        Hash = hash;
        _lastSave = now;
    }

    public void SaveIfDue(DateTimeOffset now)
    {
        if (now - _lastSave >= saveEvery) Save(now);
    }

    public void Save(DateTimeOffset now)
    {
        if (Hash is null) return;
        repository.Save(Hash, Learner.Export(), now);
        _lastSave = now;
    }

    /// <summary>Forgets what was learned for this machine, in memory and in storage.</summary>
    public void Reset(DateTimeOffset now)
    {
        Learner.Reset();
        if (Hash is not null) repository.Clear(Hash);
        _lastSave = now;
    }

    public CalibrationStatus Status()
    {
        var trusted = 0;
        for (var bucket = CalibrationBuckets.DisplayOff; bucket <= CalibrationBuckets.MaxBucket; bucket++)
        {
            if (Learner.GetBaseline(bucket) is not null) trusted++;
        }
        return new CalibrationStatus(Learner.TotalSamples, options.MinTotalSamples, trusted, CalibrationBuckets.Count);
    }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter CalibrationKeeperTests`
Expected: `Passed! - Failed: 0, Passed: 5`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Service/CalibrationKeeper.cs tests/PowerLedger.Service.Tests/CalibrationKeeperTests.cs
git commit -m "Keep one calibration learner per machine, saved on a timer"
```

---

### Task 16: What the loop publishes — frames, the live feed and the status board

**Files:**
- Create: `src/PowerLedger.Service/Frames.cs`
- Create: `src/PowerLedger.Service/LiveFeed.cs`
- Create: `src/PowerLedger.Service/StatusBoard.cs`
- Test: `tests/PowerLedger.Service.Tests/PublishingTests.cs`

Plan B's known gap is closed here for the live view: each frame says whether the CPU and GPU watts were measured this tick or modelled, so the Now screen can mark the modelled ones. Plan B's rule: the pipe thread reads a status snapshot that the loop publishes each tick with a single reference swap. It never touches the sampler or the validator. Live readings fan out through one small queue per subscriber. A queue that fills drops its oldest frames, so a stalled App can never hold up the loop or another client.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.Service.Tests/PublishingTests.cs`
```csharp
using PowerLedger.Contracts;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class PublishingTests
{
    [Fact]
    public void A_reading_becomes_a_frame_field_for_field_with_the_reports_bands()
    {
        var reading = Readings.At(5, watts: 36);
        var frame = Frames.From(reading, Samples.At(reading.Timestamp));   // energy meter present, no discrete GPU
        frame.CpuMeasured.ShouldBeTrue();
        frame.GpuMeasured.ShouldBeFalse();
        frame.Timestamp.ShouldBe(reading.Timestamp);
        frame.TotalW.ShouldBe(36);
        frame.Quality.ShouldBe(Quality.Measured);
        frame.Components.Cpu.ShouldBe(18);
        frame.Components.Unattributed.ShouldBe(14);
        frame.DisplayBandW.ShouldBe(4);
        frame.RestBandW.ShouldBe(14);
        frame.OnBattery.ShouldBeTrue();
        frame.Brightness.ShouldBe(0.5);
    }

    [Fact]
    public void Source_health_becomes_source_status()
        => Frames.From(new SourceHealth("nvidia-gpu", true, null, 3, 12, "NVML error 15"))
            .ShouldBe(new SourceStatus("nvidia-gpu", true, null, 3, "NVML error 15"));

    [Fact]
    public async Task Every_subscriber_gets_every_frame()
    {
        var feed = new LiveFeed();
        var a = feed.Subscribe();
        var b = feed.Subscribe();
        feed.Publish(PipeProtocolTests.Frame(10));
        (await a.ReadAsync()).TotalW.ShouldBe(10);
        (await b.ReadAsync()).TotalW.ShouldBe(10);
    }

    [Fact]
    public void A_subscriber_that_falls_behind_loses_its_oldest_frames_and_holds_nobody_up()
    {
        var feed = new LiveFeed();
        var slow = feed.Subscribe();
        for (var i = 0; i < 20; i++) feed.Publish(PipeProtocolTests.Frame(i));
        var kept = new List<double>();
        while (slow.TryRead(out var frame)) kept.Add(frame.TotalW);
        kept.ShouldBe(Enumerable.Range(20 - LiveFeed.Backlog, LiveFeed.Backlog).Select(i => (double)i));
    }

    [Fact]
    public async Task Unsubscribing_ends_the_stream()
    {
        var feed = new LiveFeed();
        var reader = feed.Subscribe();
        feed.Unsubscribe(reader);
        feed.Subscribers.ShouldBe(0);
        (await reader.WaitToReadAsync()).ShouldBeFalse();
        feed.Publish(PipeProtocolTests.Frame());   // nobody listening is fine
    }

    [Fact]
    public void The_board_holds_the_latest_of_each()
    {
        var board = new StatusBoard();
        board.Status.ShouldBeNull();
        board.Settings.ShouldBeNull();
        board.Publish(ServiceSettings.Default);
        board.Settings.ShouldBeSameAs(ServiceSettings.Default);
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter PublishingTests`
Expected: build error, `Frames` not found.

- [x] **Step 3: Write the three pieces**

`src/PowerLedger.Service/Frames.cs`
```csharp
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Sensors;

namespace PowerLedger.Service;

/// <summary>Core and Sensors types turned into the Contracts shapes that travel on the pipe.</summary>
internal static class Frames
{
    /// <summary>The reading, plus which parts the tick's sample measured rather than modelled (Plan B's provenance gap).</summary>
    public static ReadingFrame From(Reading r, Sample s) => new(
        r.Timestamp, r.DeltaSeconds, r.TotalW, r.Quality,
        new ComponentWatts(
            r.Components.Cpu, r.Components.Gpu, r.Components.Display, r.Components.Ram, r.Components.Storage,
            r.Components.Board, r.Components.Extras, r.Components.Monitors, r.Components.PsuLoss, r.Components.Unattributed),
        CpuMeasured: s.CpuPackageW is { } cpu && double.IsFinite(cpu),
        GpuMeasured: s.DGpuW is { } gpu && double.IsFinite(gpu),
        r.OnBattery, r.DisplayOn, r.UserIdle, r.SessionLocked, r.CpuLoad, r.GpuLoad, r.Brightness, r.Suspect);

    public static SourceStatus From(SourceHealth health)
        => new(health.Name, health.Supported, health.Unavailable, health.Failures, health.LastError);
}
```

`src/PowerLedger.Service/LiveFeed.cs`
```csharp
using System.Threading.Channels;
using PowerLedger.Contracts;

namespace PowerLedger.Service;

/// <summary>
/// Hands each tick's reading to every subscribed pipe client (spec §8). Each subscriber has a small queue that drops its
/// oldest frames when the client falls behind, so a stalled App never holds up the loop or another client.
/// </summary>
internal sealed class LiveFeed
{
    /// <summary>Frames a subscriber may fall behind by before the oldest are dropped.</summary>
    public const int Backlog = 8;

    private readonly Lock _gate = new();
    private Channel<ReadingFrame>[] _subscribers = [];

    public int Subscribers => Volatile.Read(ref _subscribers).Length;

    public ChannelReader<ReadingFrame> Subscribe()
    {
        var channel = Channel.CreateBounded<ReadingFrame>(new BoundedChannelOptions(Backlog)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        lock (_gate) _subscribers = [.. _subscribers, channel];
        return channel.Reader;
    }

    public void Unsubscribe(ChannelReader<ReadingFrame> reader)
    {
        lock (_gate)
        {
            var channel = _subscribers.FirstOrDefault(c => c.Reader == reader);
            if (channel is null) return;
            channel.Writer.TryComplete();
            _subscribers = [.. _subscribers.Where(c => c != channel)];
        }
    }

    /// <summary>Never blocks: a full queue drops its oldest frame.</summary>
    public void Publish(ReadingFrame frame)
    {
        foreach (var channel in Volatile.Read(ref _subscribers)) channel.Writer.TryWrite(frame);
    }
}
```

`src/PowerLedger.Service/StatusBoard.cs`
```csharp
using PowerLedger.Contracts;

namespace PowerLedger.Service;

/// <summary>The latest status and settings the loop has published, for the pipe to read from any thread.</summary>
internal sealed class StatusBoard
{
    private ServiceStatus? _status;
    private ServiceSettings? _settings;

    public ServiceStatus? Status => Volatile.Read(ref _status);

    public ServiceSettings? Settings => Volatile.Read(ref _settings);

    public void Publish(ServiceStatus status) => Volatile.Write(ref _status, status);

    public void Publish(ServiceSettings settings) => Volatile.Write(ref _settings, settings);
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter PublishingTests`
Expected: `Passed! - Failed: 0, Passed: 6`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Service/Frames.cs src/PowerLedger.Service/LiveFeed.cs src/PowerLedger.Service/StatusBoard.cs tests/PowerLedger.Service.Tests/PublishingTests.cs
git commit -m "Publish readings to subscribers and a status snapshot for the pipe"
```

---

### Task 17: SamplingLoop — the service's heart

**Files:**
- Create: `src/PowerLedger.Service/LoopCommands.cs`
- Create: `src/PowerLedger.Service/SamplingLoop.cs`
- Test: `tests/PowerLedger.Service.Tests/SamplingLoopTests.cs`

The loop brings Tasks 5–16 together. Each tick: read the sensors through the worker, run the model, feed the learner the same tick's parts (Plan A's rule), buffer the reading, publish it. At each minute boundary: write the batch, fold the finished minute (and hour), save calibration when due, run retention when due. Between ticks: suspend, resume, settings and calibration-reset commands. On resume the sensor set is rebuilt and the hardware re-detected (Plan B's rules), and the tick timer starts over, so a tick queued while the machine slept does not fire before the new sensors are ready. A failed write holds back folding, so no minute is folded from half its rows.

The test is spec §12's service test: host the loop with fake sensors and a temp database, drive a fake clock through ten simulated minutes including a suspend and resume, and check rows, aggregates and sessions. A second test starts over a crashed run, a third changes settings mid-run, and a fourth hangs a sensor read.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.Service.Tests/SamplingLoopTests.cs`
```csharp
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class SamplingLoopTests
{
    // 00:55 UTC is 02:55 in a UTC+2 zone: five minutes before both the 03:00 purge and the top of a UTC hour.
    private static readonly DateTimeOffset Start = new(2026, 9, 12, 0, 55, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Plus2 = TimeZoneInfo.CreateCustomTimeZone("PL+2", TimeSpan.FromHours(2), "PL+2", "PL+2");

    [Fact]
    public async Task Ten_minutes_with_a_sleep_in_the_middle_leave_the_right_rows_behind()
    {
        using var t = new TestDatabase();
        await using var loop = new Harness(t);
        var frames = loop.Feed.Subscribe();
        await loop.StartAsync();
        await loop.Ticks(300);                                      // 00:55:01 to 01:00:00
        await loop.Send(new SuspendCommand());
        loop.Clock.Advance(TimeSpan.FromMinutes(2));                // asleep until 01:02:00; the timer's tick is ignored
        await loop.Send(new ResumeCommand());
        await loop.Ticks(180);                                      // 01:02:01 to 01:05:00
        await loop.StopAsync();

        var raw = new RawSampleRepository(t.Db);
        raw.Count().ShouldBe(480);

        var aggregates = new AggregateRepository(t.Db);
        var minutes = aggregates.ReadMinutes(Start, Start.AddMinutes(11));
        minutes.Select(m => m.Start.Minute).ShouldBe(new[] { 55, 56, 57, 58, 59, 0, 2, 3, 4, 5 });
        minutes.Sum(m => m.SampleCount).ShouldBe(480);
        minutes.Single(m => m.Start == Start.AddMinutes(7)).GapSeconds.ShouldBe(121, 1e-6);   // 01:00:00 to 01:02:01
        minutes.Sum(m => m.EnergyWh).ShouldBe(raw.Read(Start, Start.AddMinutes(11)).Sum(r => EnergyIntegrator.Integrate(r).Wh), 1e-9);

        var hour = aggregates.ReadHours(Start.AddHours(-1), Start.AddHours(2)).Single();       // 01:00 is not over yet
        hour.Start.ShouldBe(Start.AddMinutes(-55));
        hour.SampleCount.ShouldBe(299);

        var sessions = new SessionRepository(t.Db).List(Start.AddHours(-1), Start.AddHours(1));
        sessions.Select(s => (s.Reason, s.EndReason, s.Start, s.End)).ShouldBe(new (SessionReason, SessionReason?, DateTimeOffset, DateTimeOffset?)[]
        {
            (SessionReason.ServiceStart, SessionReason.Suspend, Start, Start.AddMinutes(5)),
            (SessionReason.Resume, SessionReason.ServiceStop, Start.AddMinutes(7), Start.AddMinutes(10)),
        });

        loop.Sets.Count.ShouldBe(2);                                // a fresh sensor set after the resume
        await WaitFor.True(() => loop.Sets.All(s => s.Disposed));

        new SettingsRepository(t.Db).Get(RetentionRunner.LastPurgeKey)
            .ShouldBe(Start.AddMinutes(5).ToString("O", CultureInfo.InvariantCulture));   // the 03:00 local purge
        var learned = new CalibrationLearner();
        learned.Import(new CalibrationRepository(t.Db).Load(Facts.Laptop().Hash));
        learned.TotalSamples.ShouldBe(480);

        var status = loop.Board.Status.ShouldNotBeNull();
        status.Ticks.ShouldBe(480);
        status.InventoryHash.ShouldBe(Facts.Laptop().Hash);
        status.Sources.Single().Name.ShouldBe("fake");
        ReadingFrame? last = null;
        while (frames.TryRead(out var frame)) last = frame;
        last.ShouldNotBeNull().Timestamp.ShouldBe(Start.AddMinutes(10));
    }

    [Fact]
    public async Task After_a_crash_the_open_session_closes_at_its_last_tick_and_the_unfolded_minutes_are_caught_up()
    {
        using var t = new TestDatabase();
        var sessions = new SessionRepository(t.Db);
        sessions.Open(SessionReason.Boot, Start.AddMinutes(-10));
        new RawSampleRepository(t.Db).InsertBatch([.. Enumerable.Range(0, 150).Select(s => Readings.At(Start.AddMinutes(-5).AddSeconds(s)))]);

        await using var loop = new Harness(t);
        await loop.StartAsync();
        await loop.StopAsync();

        var list = sessions.List(Start.AddHours(-1), Start.AddHours(1));
        list[0].EndReason.ShouldBe(SessionReason.CrashRecovered);
        list[0].End.ShouldBe(Start.AddMinutes(-5).AddSeconds(149));
        list[1].Reason.ShouldBe(SessionReason.CrashRecovered);
        new AggregateRepository(t.Db).ReadMinutes(Start.AddMinutes(-5), Start).Select(m => m.SampleCount).ShouldBe(new[] { 60, 60, 30 });
    }

    [Fact]
    public async Task New_settings_take_effect_between_ticks_and_bad_ones_are_refused()
    {
        using var t = new TestDatabase();
        await using var loop = new Harness(t);
        await loop.StartAsync();
        await loop.Ticks(3);

        var slower = loop.Board.Settings.ShouldNotBeNull() with { SampleIntervalSeconds = 2 };
        await loop.Send(new ApplySettingsCommand(slower));
        loop.Board.Settings.ShouldBe(slower);
        new SettingsStore(new SettingsRepository(t.Db)).Load().ShouldBe(slower);

        var before = loop.TickCount;
        loop.Clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(100);
        loop.TickCount.ShouldBe(before);                            // one second is no longer a tick
        loop.Clock.Advance(TimeSpan.FromSeconds(1));
        await WaitFor.True(() => loop.TickCount == before + 1);

        await Should.ThrowAsync<ArgumentException>(() => loop.Send(new ApplySettingsCommand(slower with { SampleIntervalSeconds = 9 })));
        loop.Board.Settings.ShouldBe(slower);
    }

    [Fact]
    public async Task A_hung_sensor_read_costs_one_tick_and_the_stall_becomes_a_gap()
    {
        using var t = new TestDatabase();
        using var gate = new ManualResetEventSlim(false);
        var reads = 0;
        // 10.5 s keeps the watchdog's deadline off the one-second tick grid, so the fake clock fires them in a fixed order.
        var options = LoopOptions.Default with { SensorTimeout = TimeSpan.FromSeconds(10.5) };
        await using var loop = new Harness(t, options, index => index == 0
            ? new FakeSensorSet((ts, delta) =>
            {
                if (Interlocked.Increment(ref reads) == 3) gate.Wait();
                return Samples.At(ts, delta, batteryW: 20);
            })
            : new FakeSensorSet((ts, delta) => Samples.At(ts, delta, batteryW: 20)));
        try
        {
            await loop.StartAsync();
            await loop.Ticks(2);                                    // 00:55:01 and 00:55:02
            loop.Clock.Advance(TimeSpan.FromSeconds(1));            // the third read hangs
            await WaitFor.True(() => Volatile.Read(ref reads) == 3);
            loop.Clock.Advance(options.SensorTimeout);              // the watchdog gives up at 00:55:13.5 and a fresh set reads at once
            await WaitFor.True(() => loop.TickCount == 3);
            loop.Board.Status.ShouldNotBeNull().SensorRestarts.ShouldBe(1);
            loop.Sets.Count.ShouldBe(2);
        }
        finally
        {
            gate.Set();
        }
        await loop.StopAsync();

        var last = new RawSampleRepository(t.Db).Read(Start, Start.AddMinutes(1))[^1];
        last.Timestamp.ShouldBe(Start.AddSeconds(13.5));
        last.DeltaSeconds.ShouldBe(11.5, 1e-6);                      // from the last good tick, so it counts as a gap
    }

    /// <summary>The loop wired to a fake clock, fake sensors that report 20 W from the battery, and a temp database.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly SamplingLoop _loop;
        private readonly List<FakeSensorSet> _sets = [];

        public Harness(TestDatabase database, LoopOptions? options = null, Func<int, FakeSensorSet>? makeSet = null)
        {
            Clock.SetLocalTimeZone(Plus2);
            makeSet ??= _ => new FakeSensorSet((ts, delta) => Samples.At(ts, delta, batteryW: 20));
            var environment = new LoopEnvironment(
                Sensors: () =>
                {
                    lock (_sets)
                    {
                        var set = makeSet(_sets.Count);
                        _sets.Add(set);
                        return set;
                    }
                },
                Inventory: () => Facts.Laptop(),
                SystemUptime: () => TimeSpan.FromHours(3),
                SystemShuttingDown: () => false,
                DatabaseNotice: null);
            _loop = new SamplingLoop(database.Db, environment, Commands, Feed, Board, Clock, NullLogger<SamplingLoop>.Instance, options);
        }

        public FakeTimeProvider Clock { get; } = new(Start);

        public LoopCommands Commands { get; } = new();

        public LiveFeed Feed { get; } = new();

        public StatusBoard Board { get; } = new();

        public long TickCount => _loop.Ticks;

        public List<FakeSensorSet> Sets
        {
            get
            {
                lock (_sets) return [.. _sets];
            }
        }

        /// <summary>Starts the loop and waits until its timer runs, so the first advance of the clock is a tick.</summary>
        public async Task StartAsync()
        {
            await _loop.StartAsync(CancellationToken.None);
            await _loop.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public Task StopAsync() => _loop.StopAsync(CancellationToken.None);

        /// <summary>Advances the clock a second at a time, waiting for the loop to finish each tick before the next.</summary>
        public async Task Ticks(int count)
        {
            for (var i = 0; i < count; i++)
            {
                var expected = _loop.Ticks + 1;
                Clock.Advance(TimeSpan.FromSeconds(1));
                await WaitFor.True(() => _loop.Ticks >= expected);
            }
        }

        public Task Send(LoopCommand command) => Commands.SendAsync(command).WaitAsync(TimeSpan.FromSeconds(5));

        public async ValueTask DisposeAsync()
        {
            await _loop.StopAsync(CancellationToken.None);
            _loop.Dispose();
        }
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter SamplingLoopTests`
Expected: build error, `SamplingLoop` not found.

- [x] **Step 3: Write the commands and the loop**

`src/PowerLedger.Service/LoopCommands.cs`
```csharp
using System.Threading.Channels;
using PowerLedger.Contracts;

namespace PowerLedger.Service;

/// <summary>Something only the sampling loop may do, sent from another thread. <see cref="Done"/> completes once the loop has done it.</summary>
internal abstract class LoopCommand
{
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Done => _done.Task;

    internal void Complete() => _done.TrySetResult();

    internal void Fail(Exception error) => _done.TrySetException(error);
}

/// <summary>The machine is about to sleep: write everything and close the session.</summary>
internal sealed class SuspendCommand : LoopCommand
{
}

/// <summary>The machine woke: rebuild the sensors, detect the hardware, open a session.</summary>
internal sealed class ResumeCommand : LoopCommand
{
}

/// <summary>Replace the settings; refused whole when any value is out of range.</summary>
internal sealed class ApplySettingsCommand(ServiceSettings settings) : LoopCommand
{
    public ServiceSettings Settings { get; } = settings;
}

/// <summary>Forget what calibration has learned for this machine.</summary>
internal sealed class ResetCalibrationCommand : LoopCommand
{
}

/// <summary>The loop's inbox.</summary>
internal sealed class LoopCommands
{
    private readonly Channel<LoopCommand> _channel = Channel.CreateUnbounded<LoopCommand>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<LoopCommand> Reader => _channel.Reader;

    /// <summary>Queues the command. The task completes once the loop has carried it out, and fails if the loop refused it.</summary>
    public Task SendAsync(LoopCommand command)
    {
        if (!_channel.Writer.TryWrite(command)) command.Fail(new InvalidOperationException("The service is stopping."));
        return command.Done;
    }

    /// <summary>Closes the inbox and fails whatever is still queued, so no sender waits for ever.</summary>
    internal void Close()
    {
        _channel.Writer.TryComplete();
        while (_channel.Reader.TryRead(out var command)) command.Fail(new InvalidOperationException("The service is stopping."));
    }
}
```

`src/PowerLedger.Service/SamplingLoop.cs`
```csharp
using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Sensors;
using PowerLedger.Storage;

namespace PowerLedger.Service;

/// <param name="SensorTimeout">A sensor read that takes longer abandons the set (Plan B's watchdog rule).</param>
/// <param name="CalibrationSaveEvery">How often the learner is saved while the service runs.</param>
/// <param name="Calibration">Learner thresholds; the defaults are spec §5's.</param>
internal sealed record LoopOptions(TimeSpan SensorTimeout, TimeSpan CalibrationSaveEvery, CalibrationOptions Calibration)
{
    public static LoopOptions Default { get; } = new(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(10), new CalibrationOptions());
}

/// <summary>What the loop needs from the machine and the host, so tests can supply fakes.</summary>
/// <param name="Sensors">Builds a sensor set, on the sensor thread; again after every resume or abandoned set.</param>
/// <param name="Inventory">Detects the hardware, at start and on every resume.</param>
/// <param name="SystemUptime">How long Windows has been running, to tell a boot from a service restart.</param>
/// <param name="SystemShuttingDown">True once the service control manager has announced a shutdown.</param>
/// <param name="DatabaseNotice">What start-up had to do to the database, for the status screen.</param>
internal sealed record LoopEnvironment(
    Func<ISensorSet> Sensors, Func<InventoryFacts> Inventory, Func<TimeSpan> SystemUptime, Func<bool> SystemShuttingDown, string? DatabaseNotice);

/// <summary>
/// The service's heart (spec §3, §6, §7). Once a sample interval it reads the sensors, runs the model, feeds the
/// learner, buffers the reading and pushes it to subscribers. At each minute boundary it writes the batch, folds the
/// finished minute and hour, and runs whatever housekeeping is due. Between ticks it carries out suspend, resume,
/// settings and calibration commands. Nothing else touches the sensor set, the model, the learner, the buffer, the
/// sessions or the settings, so none of them needs a lock.
/// </summary>
internal sealed class SamplingLoop : BackgroundService
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly string Version =
        typeof(SamplingLoop).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    private readonly SqliteDatabase _database;
    private readonly LoopEnvironment _environment;
    private readonly LoopCommands _commands;
    private readonly LiveFeed _feed;
    private readonly StatusBoard _board;
    private readonly TimeProvider _clock;
    private readonly ILogger<SamplingLoop> _log;
    private readonly LoopOptions _options;
    private readonly RawSampleRepository _raw;
    private readonly InventoryRepository _inventory;
    private readonly SettingsStore _settingsStore;
    private readonly SessionTracker _sessions;
    private readonly CalibrationKeeper _calibration;
    private readonly Rollups _rollups;
    private readonly RetentionRunner _retention;
    private readonly WriteBuffer _buffer;
    private readonly SensorWorker _worker;
    private readonly TickClock _tickClock;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ServiceSettings _settings = ServiceSettings.Default;
    private InventoryFacts? _facts;
    private PowerModel? _model;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _minute;
    private DateTimeOffset? _foldFrom;
    private bool _suspended;
    private long _ticks;
    private long _databaseBytes;
    private int _failures;

    public SamplingLoop(
        SqliteDatabase database, LoopEnvironment environment, LoopCommands commands, LiveFeed feed, StatusBoard board,
        TimeProvider clock, ILogger<SamplingLoop> log, LoopOptions? options = null)
    {
        _database = database;
        _environment = environment;
        _commands = commands;
        _feed = feed;
        _board = board;
        _clock = clock;
        _log = log;
        _options = options ?? LoopOptions.Default;
        _raw = new RawSampleRepository(database);
        _inventory = new InventoryRepository(database);
        _settingsStore = new SettingsStore(new SettingsRepository(database));
        _sessions = new SessionTracker(new SessionRepository(database));
        _calibration = new CalibrationKeeper(new CalibrationRepository(database), _options.Calibration, _options.CalibrationSaveEvery);
        _rollups = new Rollups(_raw, new AggregateRepository(database));
        _retention = new RetentionRunner(database, clock.LocalTimeZone);
        _buffer = new WriteBuffer(_raw.InsertBatch);
        _worker = new SensorWorker(environment.Sensors, _options.SensorTimeout, clock);
        _tickClock = new TickClock(clock);
    }

    /// <summary>Readings recorded since start. Rises only once a tick is completely done.</summary>
    public long Ticks => Interlocked.Read(ref _ticks);

    private TimeSpan Interval => TimeSpan.FromSeconds(_settings.SampleIntervalSeconds);

    private double GapThreshold => EnergyIntegrator.GapThresholdFor(_settings.SampleIntervalSeconds);

    /// <summary>
    /// Completes once the loop has started and its timer runs, so every tick the clock gives from then on is seen; fails
    /// if start-up failed. .NET runs ExecuteAsync on the thread pool, so StartAsync returns before this.
    /// </summary>
    internal Task Ready => _ready.Task;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try
        {
            Start();
        }
        catch (Exception error)
        {
            _log.LogCritical(error, "PowerLedger could not start");
            _ready.TrySetException(error);
            throw;
        }
        var timer = new PeriodicTimer(Interval, _clock);
        _ready.TrySetResult();
        Task<bool>? tick = null;
        Task<bool>? inbox = null;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                tick ??= timer.WaitForNextTickAsync(stop).AsTask();
                inbox ??= _commands.Reader.WaitToReadAsync(stop).AsTask();
                await Task.WhenAny(tick, inbox).ConfigureAwait(false);
                if (stop.IsCancellationRequested) break;

                if (inbox.IsCompleted)
                {
                    var open = await inbox.ConfigureAwait(false);
                    inbox = null;
                    if (!open) break;
                    while (_commands.Reader.TryRead(out var command))
                    {
                        if (!Handle(command)) continue;
                        timer.Dispose();                           // a new interval, or a machine that just woke
                        timer = new PeriodicTimer(Interval, _clock);
                        tick = null;
                    }
                }

                if (tick is { IsCompleted: true })
                {
                    var running = await tick.ConfigureAwait(false);
                    tick = null;
                    if (!running) break;
                    if (!_suspended) await TickAsync(stop).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        finally
        {
            timer.Dispose();
            Stop();
        }
    }

    private void Start()
    {
        var now = _clock.GetUtcNow();
        _startedAt = now;
        _minute = Rollups.Floor(now, Minute);
        var stored = _settingsStore.Load();
        if (stored?.Validate() is { } problem)
        {
            _log.LogWarning("Stored settings were refused ({Problem}); starting from the defaults", problem);
            stored = null;
        }
        _settings = stored ?? ServiceSettings.Default;
        _rollups.CatchUp(now, TimeSpan.FromHours(_settings.RawRetentionHours), GapThreshold);
        var reason = _sessions.Start(now, _environment.SystemUptime(), _raw.Latest());
        _log.LogInformation("PowerLedger {Version} started ({Reason})", Version, reason);
        Detect(now, stored);
        RefreshDatabaseSize();
    }

    /// <summary>Detects the hardware, records it, and folds it into the profile when this is a different machine.</summary>
    private void Detect(DateTimeOffset now, ServiceSettings? stored)
    {
        var facts = _environment.Inventory();
        _inventory.Upsert(new InventoryRecord(facts.Hash, now, facts.ToJson()));
        var (settings, changed) = ProfilePolicy.Apply(stored, _settingsStore.ProfileHash(), facts);
        if (changed)
        {
            _settingsStore.Save(settings);
            _settingsStore.SaveProfileHash(facts.Hash);
            _log.LogInformation("Machine profile taken from detection for hardware {Hash}", facts.Hash);
        }
        _settings = settings;
        _facts = facts;
        _calibration.Use(facts.Hash, now);
        _model = ModelFactory.Build(settings, facts, _calibration.Learner);
        _board.Publish(settings);
    }

    private async Task TickAsync(CancellationToken stop)
    {
        var now = _clock.GetUtcNow();
        try
        {
            var result = await _worker.ReadAsync(now, _tickClock.Measure(now, _settings.SampleIntervalSeconds), stop).ConfigureAwait(false);
            if (result is null)
            {
                _log.LogWarning("A sensor read took longer than {Timeout}; the sensor set was replaced", _options.SensorTimeout);
                return;
            }
            _tickClock.Commit(now);
            var reading = _model!.Evaluate(result.Sample);
            _calibration.Learner.Observe(result.Sample, reading.Components.Cpu, reading.Components.Gpu, reading.Components.Display);
            _buffer.Add(reading);
            var frame = Frames.From(reading, result.Sample);
            _feed.Publish(frame);
            OnMinute(now);
            var ticks = Ticks + 1;
            Publish(result, frame, ticks);
            Interlocked.Exchange(ref _ticks, ticks);
            _failures = 0;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _failures++;
            if ((_failures & (_failures - 1)) == 0) _log.LogError(error, "A tick failed ({Failures} in a row)", _failures);
        }
    }

    private void OnMinute(DateTimeOffset now)
    {
        var minute = Rollups.Floor(now, Minute);
        if (minute == _minute) return;
        WriteAndFold(minute > _minute ? minute : _minute + Minute);   // a clock set back still folds the minute it left
        _minute = minute;
        _calibration.SaveIfDue(now);
        var purged = _retention.RunIfDue(now, RetentionOptions.Clamped(_settings.RawRetentionHours, _settings.HistoryRetentionYears));
        if (purged is not null) _log.LogInformation("Retention removed {Raw} raw rows and {Minutes} minute rows", purged.RawDeleted, purged.MinutesDeleted);
        RefreshDatabaseSize();
    }

    /// <summary>
    /// Writes the buffer, then folds every unfolded minute before <paramref name="upTo"/>. When the write fails nothing is
    /// folded, because a minute folded from half its rows would stay wrong; the next write that succeeds folds the backlog.
    /// </summary>
    private void WriteAndFold(DateTimeOffset upTo)
    {
        var from = _foldFrom ?? _minute;
        if (!_buffer.Flush())
        {
            _foldFrom = from;
            _log.LogWarning("Writing readings failed: {Problem}", _buffer.Problem);
            return;
        }
        _rollups.FoldRange(from, upTo, GapThreshold);
        _foldFrom = null;
    }

    private void Publish(TickResult result, ReadingFrame frame, long ticks) => _board.Publish(new ServiceStatus(
        Version, _startedAt, ticks, [.. result.Health.Select(Frames.From)], result.SuspectCount, _worker.Abandoned,
        _calibration.Status(), _facts?.Hash ?? "", _databaseBytes, _buffer.Problem, _environment.DatabaseNotice, frame));

    /// <returns>True when the tick timer must start over: the sample interval changed, or the machine woke, and a tick
    /// the timer queued while it slept must not fire before the rebuilt sensors have had a full interval.</returns>
    private bool Handle(LoopCommand command)
    {
        var now = _clock.GetUtcNow();
        try
        {
            var restartTimer = false;
            switch (command)
            {
                case SuspendCommand:
                    Suspend(now);
                    break;
                case ResumeCommand:
                    Resume(now);
                    restartTimer = true;
                    break;
                case ApplySettingsCommand apply:
                    restartTimer = Apply(apply.Settings);
                    break;
                case ResetCalibrationCommand:
                    _calibration.Reset(now);
                    _log.LogInformation("Calibration reset for hardware {Hash}", _calibration.Hash);
                    break;
            }
            command.Complete();
            return restartTimer;
        }
        catch (Exception error)
        {
            _log.LogError(error, "{Command} failed", command.GetType().Name);
            command.Fail(error);
            return false;
        }
    }

    private void Suspend(DateTimeOffset now)
    {
        if (_suspended) return;
        WriteAndFold(Rollups.Floor(now, Minute) + Minute);
        _calibration.Save(now);
        _sessions.End(now, SessionReason.Suspend);
        _suspended = true;
        _log.LogInformation("Suspended");
    }

    private void Resume(DateTimeOffset now)
    {
        _worker.Rebuild();
        Detect(now, _settings);
        _sessions.Resume(now);
        _tickClock.MarkResumed();
        _suspended = false;
        _log.LogInformation("Resumed");
    }

    private bool Apply(ServiceSettings settings)
    {
        if (settings.Validate() is { } problem) throw new ArgumentException(problem, nameof(settings));
        var intervalChanged = settings.SampleIntervalSeconds != _settings.SampleIntervalSeconds;
        _settingsStore.Save(settings);
        _settings = settings;
        _model = ModelFactory.Build(settings, _facts!, _calibration.Learner);
        _board.Publish(settings);
        return intervalChanged;
    }

    private void Stop()
    {
        var now = _clock.GetUtcNow();
        try
        {
            if (!_suspended) WriteAndFold(Rollups.Floor(now, Minute) + Minute);
            _calibration.Save(now);
            _sessions.End(now, _environment.SystemShuttingDown() ? SessionReason.Shutdown : SessionReason.ServiceStop);
            _log.LogInformation("PowerLedger stopped after {Ticks} readings", Ticks);
        }
        catch (Exception error)
        {
            _log.LogError(error, "Could not finish writing while stopping");
        }
        finally
        {
            _worker.Dispose();
            _commands.Close();
        }
    }

    private void RefreshDatabaseSize()
    {
        static long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
        _databaseBytes = Size(_database.Path) + Size(_database.Path + "-wal");
    }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter SamplingLoopTests`
Expected: `Passed! - Failed: 0, Passed: 4`. The ten-minute run takes a few seconds of real time.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Service/LoopCommands.cs src/PowerLedger.Service/SamplingLoop.cs tests/PowerLedger.Service.Tests/SamplingLoopTests.cs
git commit -m "Add the sampling loop: ticks, minute boundaries, sleep, settings and a watchdog"
```

---

### Task 18: PipeHandler — request in, reply out

**Files:**
- Create: `src/PowerLedger.Service/PipeHandler.cs`
- Test: `tests/PowerLedger.Service.Tests/PipeHandlerTests.cs`

The handler answers each request without knowing about pipes, so it can be tested alone. Spec §8 and §11: everything a client sends is range-checked first, and nothing it can say names a file or runs a command. Anything that changes what the loop is doing goes to the loop as a command, and the reply waits until the loop has done it, or says the loop did not answer in time. A new tariff is written straight to its table: the loop never touches tariffs, because cost is computed at query time. Idle reports go straight to the signals.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.Service.Tests/PipeHandlerTests.cs`
```csharp
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

public sealed class PipeHandlerTests : IDisposable
{
    private static readonly DateTimeOffset Now = Samples.T0;
    private readonly TestDatabase _database = new();
    private readonly LoopCommands _commands = new();
    private readonly StatusBoard _board = new();
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly ServiceSignals _signals;
    private readonly PipeHandler _handler;

    public PipeHandlerTests()
    {
        _signals = new ServiceSignals(_clock);
        _handler = new PipeHandler(_commands, _board, _signals, new TariffRepository(_database.Db), _clock);
    }

    [Fact]
    public async Task Status_and_settings_are_refused_until_the_loop_has_published_them()
    {
        (await Send(new GetStatusRequest(1))).ShouldBe(new ErrorReply(1, PipeHandler.Starting));
        (await Send(new GetSettingsRequest(2))).ShouldBe(new ErrorReply(2, PipeHandler.Starting));

        _board.Publish(ServiceSettings.Default);
        (await Send(new GetSettingsRequest(3))).ShouldBe(new SettingsReply(3, ServiceSettings.Default));
    }

    [Fact]
    public async Task Good_settings_go_to_the_loop_and_are_answered_once_it_has_applied_them()
    {
        var settings = ServiceSettings.Default with { IdleThresholdSeconds = 900 };
        var reply = Send(new SetSettingsRequest(4, settings));
        var command = (await _commands.Reader.ReadAsync()).ShouldBeOfType<ApplySettingsCommand>();
        command.Settings.ShouldBe(settings);
        reply.IsCompleted.ShouldBeFalse();
        command.Complete();
        (await reply).ShouldBe(new OkReply(4));
    }

    [Fact]
    public async Task Bad_settings_are_refused_without_bothering_the_loop()
    {
        var reply = await Send(new SetSettingsRequest(5, ServiceSettings.Default with { SampleIntervalSeconds = 60 }));
        reply.ShouldBeOfType<ErrorReply>().Message.ShouldContain("sample interval");
        _commands.Reader.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_loop_that_never_answers_gets_the_client_an_error_not_a_hang()
    {
        var reply = Send(new ResetCalibrationRequest(6));
        (await _commands.Reader.ReadAsync()).ShouldBeOfType<ResetCalibrationCommand>();
        _clock.Advance(PipeHandler.LoopTimeout);
        (await reply).ShouldBe(new ErrorReply(6, PipeHandler.NoAnswer));
    }

    [Fact]
    public async Task A_tariff_is_stored_now_or_backdated_and_nonsense_is_refused()
    {
        (await Send(new SetTariffRequest(7, 0.31m, "EUR", null))).ShouldBe(new OkReply(7));
        (await Send(new SetTariffRequest(8, 0.25m, "EUR", Now.AddDays(-30)))).ShouldBe(new OkReply(8));
        var tariffs = new TariffRepository(_database.Db).All();
        tariffs.Select(t => (t.EffectiveFrom, t.PricePerKwh)).ShouldBe(new[] { (Now.AddDays(-30), 0.25m), (Now, 0.31m) });

        (await Send(new SetTariffRequest(9, -1m, "EUR", null))).ShouldBeOfType<ErrorReply>();
        (await Send(new SetTariffRequest(10, 0.3m, "eur", null))).ShouldBeOfType<ErrorReply>();
        (await Send(new SetTariffRequest(11, 0.3m, "EURO", null))).ShouldBeOfType<ErrorReply>();
        (await Send(new SetTariffRequest(12, 0.3m, "EUR", Now.AddDays(3)))).ShouldBeOfType<ErrorReply>();
        (await Send(new SetTariffRequest(13, 0.3m, "EUR", new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero)))).ShouldBeOfType<ErrorReply>();
        new TariffRepository(_database.Db).All().Count.ShouldBe(2);
    }

    [Fact]
    public async Task Idle_reports_reach_the_signals_and_nonsense_is_refused()
    {
        (await Send(new ReportActivityRequest(14, 42))).ShouldBe(new OkReply(14));
        _signals.UserIdleSeconds().ShouldBe(42);
        (await Send(new ReportActivityRequest(15, double.NaN))).ShouldBeOfType<ErrorReply>();
    }

    [Fact]
    public async Task Subscribing_is_acknowledged_and_a_client_sending_replies_is_told_off()
    {
        (await Send(new SubscribeRequest(16))).ShouldBe(new OkReply(16));
        (await Send(new OkReply(17))).ShouldBeOfType<ErrorReply>().Id.ShouldBeNull();
    }

    public void Dispose() => _database.Dispose();

    private Task<PipeMessage> Send(PipeMessage message) => _handler.HandleAsync(message, "client-1", CancellationToken.None);
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter PipeHandlerTests`
Expected: build error, `PipeHandler` not found.

- [x] **Step 3: Write the handler**

`src/PowerLedger.Service/PipeHandler.cs`
```csharp
using System.Text.RegularExpressions;
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.Service;

/// <summary>
/// Answers pipe requests (spec §8). Everything a client sends is range-checked here, and nothing it can say names a
/// file or runs a command (spec §11). Anything that changes what the loop is doing goes to the loop as a command, and
/// the reply waits until the loop has done it.
/// </summary>
internal sealed partial class PipeHandler(LoopCommands commands, StatusBoard board, ServiceSignals signals, TariffRepository tariffs, TimeProvider clock)
{
    /// <summary>How long a request waits for the loop before the client is told it did not answer.</summary>
    public static readonly TimeSpan LoopTimeout = TimeSpan.FromSeconds(10);

    internal const string Starting = "The service is still starting.";
    internal const string NoAnswer = "The service did not answer in time.";

    private const decimal MaxPricePerKwh = 1_000_000m;   // room for currencies with small units
    private static readonly DateTimeOffset EarliestTariff = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <param name="client">The connection the request came on, so idle reports stay per client.</param>
    public async Task<PipeMessage> HandleAsync(PipeMessage message, string client, CancellationToken cancel)
    {
        switch (message)
        {
            case SubscribeRequest request:
                return new OkReply(request.Id);
            case GetStatusRequest request:
                return board.Status is { } status ? new StatusReply(request.Id, status) : new ErrorReply(request.Id, Starting);
            case GetSettingsRequest request:
                return board.Settings is { } settings ? new SettingsReply(request.Id, settings) : new ErrorReply(request.Id, Starting);
            case SetSettingsRequest request:
                if (request.Settings is null) return new ErrorReply(request.Id, "The settings are missing.");
                if (request.Settings.Validate() is { } problem) return new ErrorReply(request.Id, problem);
                return await RunAsync(request.Id, new ApplySettingsCommand(request.Settings), cancel).ConfigureAwait(false);
            case SetTariffRequest request:
                return SetTariff(request);
            case ResetCalibrationRequest request:
                return await RunAsync(request.Id, new ResetCalibrationCommand(), cancel).ConfigureAwait(false);
            case ReportActivityRequest request:
                if (!double.IsFinite(request.IdleSeconds) || request.IdleSeconds < 0)
                    return new ErrorReply(request.Id, "Idle time must be a number of seconds, zero or more.");
                signals.ReportIdle(client, request.IdleSeconds);
                return new OkReply(request.Id);
            case PipeRequest request:
                return new ErrorReply(request.Id, "The service does not handle that request.");
            default:
                return new ErrorReply(null, "Only requests may be sent to the service.");
        }
    }

    private async Task<PipeMessage> RunAsync(long id, LoopCommand command, CancellationToken cancel)
    {
        try
        {
            await commands.SendAsync(command).WaitAsync(LoopTimeout, clock, cancel).ConfigureAwait(false);
            return new OkReply(id);
        }
        catch (TimeoutException)
        {
            return new ErrorReply(id, NoAnswer);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new ErrorReply(id, error.Message);
        }
    }

    private PipeMessage SetTariff(SetTariffRequest request)
    {
        if (request.PricePerKwh is < 0 or > MaxPricePerKwh) return new ErrorReply(request.Id, "The price per kWh must be zero or more.");
        if (request.Currency is null || !CurrencyCode().IsMatch(request.Currency))
            return new ErrorReply(request.Id, "The currency must be a three-letter ISO 4217 code such as USD.");
        var now = clock.GetUtcNow();
        var from = request.EffectiveFrom ?? now;
        if (from < EarliestTariff || from > now.AddDays(1)) return new ErrorReply(request.Id, "A tariff can start at any time from 2000 until tomorrow.");
        try
        {
            tariffs.Add(new Tariff(from, request.PricePerKwh, request.Currency));
            return new OkReply(request.Id);
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            return new ErrorReply(request.Id, $"The tariff could not be saved: {error.Message}");
        }
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyCode();
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter PipeHandlerTests`
Expected: `Passed! - Failed: 0, Passed: 7`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Service/PipeHandler.cs tests/PowerLedger.Service.Tests/PipeHandlerTests.cs
git commit -m "Answer pipe requests, range-checked, with loop work routed through its inbox"
```

---

### Task 19: PipeServer — the named pipe

**Files:**
- Create: `src/PowerLedger.Service/PipeServer.cs`
- Test: `tests/PowerLedger.Service.Tests/PipeServerTests.cs`

Spec §8 and §11: `\\.\pipe\PowerLedger.v1`, local authenticated users only, remote access denied, several clients at once. The first instance is created with `FirstPipeInstance`, so the service fails loudly if another process already serves the name. Ordinary users get read and write but not `CreateNewInstance`, so nobody else can add instances while the service runs. One listening instance always waits for the next client. Each client has its own task. A client that breaks the protocol is told why and disconnected. A subscribed client's frames and its replies share one writer, so they never interleave.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.Service.Tests/PipeServerTests.cs`
```csharp
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using PowerLedger.Contracts;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

public sealed class PipeServerTests : IAsyncLifetime
{
    private readonly string _name = $"PowerLedger.test.{Guid.NewGuid():N}";
    private readonly TestDatabase _database = new();
    private readonly StatusBoard _board = new();
    private readonly LiveFeed _feed = new();
    private readonly ServiceSignals _signals = new(TimeProvider.System);
    private PipeServer _server = null!;

    public async Task InitializeAsync()
    {
        var handler = new PipeHandler(new LoopCommands(), _board, _signals, new TariffRepository(_database.Db), TimeProvider.System);
        _server = new PipeServer(handler, _feed, _signals, NullLogger<PipeServer>.Instance, _name);
        await _server.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync(CancellationToken.None);
        _server.Dispose();
        _database.Dispose();
    }

    [Fact]
    public async Task A_request_gets_its_reply_on_the_same_connection()
    {
        await using var client = await ConnectAsync();
        await client.WriteAsync(new GetSettingsRequest(1));
        (await client.ReadAsync()).ShouldBe(new ErrorReply(1, PipeHandler.Starting));
        _board.Publish(ServiceSettings.Default);
        await client.WriteAsync(new GetSettingsRequest(2));
        (await client.ReadAsync()).ShouldBe(new SettingsReply(2, ServiceSettings.Default));
    }

    [Fact]
    public async Task A_subscriber_receives_each_published_frame()
    {
        await using var client = await ConnectAsync();
        await client.WriteAsync(new SubscribeRequest(1));
        (await client.ReadAsync()).ShouldBe(new OkReply(1));
        await WaitFor.True(() => _feed.Subscribers == 1);
        _feed.Publish(PipeProtocolTests.Frame(12));
        (await client.ReadAsync()).ShouldBeOfType<ReadingFrame>().TotalW.ShouldBe(12);
    }

    [Fact]
    public async Task Several_clients_are_served_at_once_and_a_client_that_leaves_is_forgotten()
    {
        await using var a = await ConnectAsync();
        var b = await ConnectAsync();
        await a.WriteAsync(new ReportActivityRequest(1, 30));
        await b.WriteAsync(new ReportActivityRequest(1, 10));
        (await a.ReadAsync()).ShouldBe(new OkReply(1));
        (await b.ReadAsync()).ShouldBe(new OkReply(1));
        _signals.UserIdleSeconds().ShouldNotBeNull().ShouldBeLessThan(11);

        await b.DisposeAsync();
        await WaitFor.True(() => _signals.UserIdleSeconds() is > 29);
    }

    [Fact]
    public async Task A_client_that_sends_an_oversized_line_is_told_why_and_disconnected()
    {
        var pipe = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        await using var client = new MessageChannel(pipe);
        var junk = new byte[PipeProtocol.MaxMessageBytes + 100];
        Array.Fill(junk, (byte)'x');
        var writing = Task.Run(async () =>
        {
            try
            {
                await pipe.WriteAsync(junk);
            }
            catch (IOException)
            {
                // the server hangs up before reading it all
            }
        });

        (await client.ReadAsync()).ShouldBeOfType<ErrorReply>().Id.ShouldBeNull();
        (await client.ReadAsync()).ShouldBeNull();
        await writing;
    }

    [Fact]
    public void No_other_process_can_serve_the_name_while_the_service_does()
        => Should.Throw<UnauthorizedAccessException>(() => PipeServer.Create(_name, first: true).Dispose());

    [Fact]
    public void The_acl_denies_the_network_and_lets_users_read_and_write_but_not_serve()
    {
        var rules = PipeServer.Security().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        rules.ShouldContain(r => r.IdentityReference == new SecurityIdentifier(WellKnownSidType.NetworkSid, null) && r.AccessControlType == AccessControlType.Deny);
        var users = rules.Single(r => r.IdentityReference == new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null));
        users.PipeAccessRights.HasFlag(PipeAccessRights.ReadData | PipeAccessRights.WriteData).ShouldBeTrue();
        users.PipeAccessRights.HasFlag(PipeAccessRights.CreateNewInstance).ShouldBeFalse();
    }

    private async Task<MessageChannel> ConnectAsync()
    {
        var pipe = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        return new MessageChannel(pipe);
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter PipeServerTests`
Expected: build error, `PipeServer` not found.

- [x] **Step 3: Write the server**

`src/PowerLedger.Service/PipeServer.cs`
```csharp
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PowerLedger.Contracts;

namespace PowerLedger.Service;

/// <summary>
/// The service's end of \\.\pipe\PowerLedger.v1 (spec §8, §11). Network logons are denied; local signed-in users may
/// read and write but not create instances, so no other process can serve the name while the service runs. One
/// listening instance always waits for the next client, and each client is served on its own task, so a slow or
/// broken client holds up nobody else.
/// </summary>
internal sealed class PipeServer(PipeHandler handler, LiveFeed feed, ServiceSignals signals, ILogger<PipeServer> log, string pipeName) : BackgroundService
{
    /// <summary>More clients than this wait for a free instance.</summary>
    public const int MaxClients = 16;

    private readonly List<Task> _clients = [];
    private readonly Lock _gate = new();

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var first = true;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                NamedPipeServerStream server;
                try
                {
                    server = Create(pipeName, first);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    if (first)
                    {
                        log.LogCritical(error, "Another process already serves the pipe {Pipe}, so the App cannot reach this service", pipeName);
                        throw;
                    }
                    log.LogWarning(error, "No free pipe instance; trying again in a second");
                    await Task.Delay(TimeSpan.FromSeconds(1), stop).ConfigureAwait(false);
                    continue;
                }

                first = false;
                try
                {
                    await server.WaitForConnectionAsync(stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                    break;
                }
                catch (IOException error)
                {
                    log.LogDebug(error, "A client left before it was served");
                    await server.DisposeAsync().ConfigureAwait(false);
                    continue;
                }
                Track(ServeAsync(server, stop));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        finally
        {
            Task[] running;
            lock (_gate) running = [.. _clients];
            await Task.WhenAll(running).ConfigureAwait(false);
        }
    }

    /// <summary>One instance of the pipe. The first must be the first anywhere, which fails if another process already serves the name.</summary>
    internal static NamedPipeServerStream Create(string name, bool first) => NamedPipeServerStreamAcl.Create(
        name, PipeDirection.InOut, MaxClients, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
        0, 0, Security(), HandleInheritability.None, 0);

    /// <summary>
    /// Network logons denied; SYSTEM, administrators and the account running the server in full; every other local
    /// signed-in user may read and write. The running account's own entry lets a console run add instances; under the
    /// service control manager it is SYSTEM again.
    /// </summary>
    internal static PipeSecurity Security()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (identity.User is { } self) security.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    private void Track(Task client)
    {
        lock (_gate) _clients.Add(client);
        _ = client.ContinueWith(
            done =>
            {
                lock (_gate) _clients.Remove(done);
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task ServeAsync(NamedPipeServerStream stream, CancellationToken stop)
    {
        var client = Guid.NewGuid().ToString("N");
        using var done = CancellationTokenSource.CreateLinkedTokenSource(stop);
        await using var channel = new MessageChannel(stream);
        ChannelReader<ReadingFrame>? frames = null;
        Task? pump = null;
        try
        {
            while (await channel.ReadAsync(done.Token).ConfigureAwait(false) is { } message)
            {
                var reply = await handler.HandleAsync(message, client, done.Token).ConfigureAwait(false);
                await channel.WriteAsync(reply, done.Token).ConfigureAwait(false);
                if (message is SubscribeRequest && frames is null)
                {
                    frames = feed.Subscribe();
                    pump = PumpAsync(channel, frames, done.Token);
                }
            }
        }
        catch (PipeProtocolException error)
        {
            log.LogWarning("A client broke the protocol and was disconnected: {Reason}", error.Message);
            await TryWriteAsync(channel, new ErrorReply(null, error.Message), done.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The client went away, or the service is stopping.
        }
        catch (Exception error)
        {
            log.LogError(error, "Serving a pipe client failed");
        }
        finally
        {
            await done.CancelAsync().ConfigureAwait(false);
            if (frames is not null) feed.Unsubscribe(frames);
            signals.ForgetClient(client);
            if (pump is not null) await pump.ConfigureAwait(false);
        }
    }

    private static async Task PumpAsync(MessageChannel channel, ChannelReader<ReadingFrame> frames, CancellationToken cancel)
    {
        try
        {
            await foreach (var frame in frames.ReadAllAsync(cancel).ConfigureAwait(false))
            {
                await channel.WriteAsync(frame, cancel).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The client went away, or the connection is closing.
        }
    }

    private static async Task TryWriteAsync(MessageChannel channel, PipeMessage message, CancellationToken cancel)
    {
        try
        {
            await channel.WriteAsync(message, cancel).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Nobody left to tell.
        }
    }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter PipeServerTests`
Expected: `Passed! - Failed: 0, Passed: 6`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Service/PipeServer.cs tests/PowerLedger.Service.Tests/PipeServerTests.cs
git commit -m "Serve the named pipe to local users, one task per client"
```

---

### Task 20: Windows notifications — sleep, wake, display and lock

**Files:**
- Create: `src/PowerLedger.Service/PowerNotifications.cs`
- Create: `src/PowerLedger.Service/PowerLedgerServiceLifetime.cs`
- Test: `tests/PowerLedger.Service.Tests/NotificationsTests.cs`

Suspend, resume and the console display's state come through the power manager's callback registrations (see "Decisions made while planning"). Windows allows about two seconds to act on a suspend, so the callback holds the machine up to 1.5 s while the loop writes and closes the session. Only the automatic resume is used, because it always arrives, while the user-present resume follows only when someone touches the machine. The service lifetime adds what the default one drops: session lock changes; telling a system shutdown from a plain service stop; and a failure exit code when the sampling loop died, because a service that stops itself cleanly never triggers the recovery actions of spec §10. The callbacks' logic is tested directly; the registration itself is a hardware test.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.Service.Tests/NotificationsTests.cs`
```csharp
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class NotificationsTests
{
    private const uint PbtApmSuspend = 0x4;
    private const uint PbtApmResumeSuspend = 0x7;
    private const uint PbtApmResumeAutomatic = 0x12;
    private const uint PbtPowerSettingChange = 0x8013;

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]   // dimmed is still lit
    public void The_console_display_state_reaches_the_signals(int state, bool expected)
    {
        var signals = new ServiceSignals(TimeProvider.System) { DisplayOn = !expected };
        var notifications = new PowerNotifications(new LoopCommands(), signals, NullLogger<PowerNotifications>.Instance);
        var setting = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.Copy(Guid.Parse("6FE69556-704A-47A0-8F24-C28D936FDA47").ToByteArray(), 0, setting, 16);
            Marshal.WriteInt32(setting, 16, 4);
            Marshal.WriteInt32(setting, 20, state);
            notifications.OnDisplay(IntPtr.Zero, PbtPowerSettingChange, setting).ShouldBe(0u);
        }
        finally
        {
            Marshal.FreeHGlobal(setting);
        }
        signals.DisplayOn.ShouldBe(expected);
    }

    [Fact]
    public async Task A_suspend_holds_the_machine_until_the_loop_has_written()
    {
        var commands = new LoopCommands();
        var notifications = new PowerNotifications(commands, new ServiceSignals(TimeProvider.System), NullLogger<PowerNotifications>.Instance);
        var callback = Task.Run(() => notifications.OnSuspendResume(IntPtr.Zero, PbtApmSuspend, IntPtr.Zero));
        var command = await commands.Reader.ReadAsync();
        command.ShouldBeOfType<SuspendCommand>();
        callback.IsCompleted.ShouldBeFalse();
        command.Complete();
        await callback.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Only_the_automatic_resume_wakes_the_loop()
    {
        var commands = new LoopCommands();
        var notifications = new PowerNotifications(commands, new ServiceSignals(TimeProvider.System), NullLogger<PowerNotifications>.Instance);
        notifications.OnSuspendResume(IntPtr.Zero, PbtApmResumeAutomatic, IntPtr.Zero);
        notifications.OnSuspendResume(IntPtr.Zero, PbtApmResumeSuspend, IntPtr.Zero);
        commands.Reader.TryRead(out var first).ShouldBeTrue();
        first.ShouldBeOfType<ResumeCommand>();
        commands.Reader.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public void Session_lock_and_unlock_reach_the_signals()
    {
        var signals = new ServiceSignals(TimeProvider.System);
        PowerLedgerServiceLifetime.Apply(SessionChangeReason.SessionLock, signals);
        signals.SessionLocked.ShouldBeTrue();
        PowerLedgerServiceLifetime.Apply(SessionChangeReason.RemoteConnect, signals);
        signals.SessionLocked.ShouldBeTrue();
        PowerLedgerServiceLifetime.Apply(SessionChangeReason.SessionUnlock, signals);
        signals.SessionLocked.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Hardware")]
    public async Task Windows_reports_the_display_state_as_soon_as_the_service_registers()
    {
        var signals = new ServiceSignals(TimeProvider.System) { DisplayOn = false };
        var notifications = new PowerNotifications(new LoopCommands(), signals, NullLogger<PowerNotifications>.Instance);
        await notifications.StartAsync(CancellationToken.None);
        try
        {
            await WaitFor.True(() => signals.DisplayOn);
        }
        finally
        {
            await notifications.StopAsync(CancellationToken.None);
        }
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter NotificationsTests`
Expected: build error, `PowerNotifications` not found.

- [x] **Step 3: Write the notifications and the lifetime**

`src/PowerLedger.Service/PowerNotifications.cs`
```csharp
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PowerLedger.Service;

/// <summary>
/// Suspend, resume and the console display's state (spec §6), through the power manager's callback registrations.
/// They need neither a window nor the service control handler, so a console run gets them too. A suspend holds the
/// machine for up to <see cref="SuspendGrace"/> while the loop writes its buffer and closes the session.
/// </summary>
internal sealed class PowerNotifications : IHostedService
{
    /// <summary>How long a suspend waits for the loop; Windows allows about two seconds.</summary>
    public static readonly TimeSpan SuspendGrace = TimeSpan.FromMilliseconds(1500);

    private const uint DeviceNotifyCallback = 2;
    private const uint PbtApmSuspend = 0x4;
    private const uint PbtApmResumeAutomatic = 0x12;
    private const uint PbtPowerSettingChange = 0x8013;
    private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    private readonly LoopCommands _commands;
    private readonly ServiceSignals _signals;
    private readonly ILogger<PowerNotifications> _log;
    private readonly DeviceNotifyCallbackRoutine _onSuspendResume;   // held for as long as Windows may call it
    private readonly DeviceNotifyCallbackRoutine _onDisplay;
    private IntPtr _suspendRegistration;
    private IntPtr _displayRegistration;

    public PowerNotifications(LoopCommands commands, ServiceSignals signals, ILogger<PowerNotifications> log)
    {
        _commands = commands;
        _signals = signals;
        _log = log;
        _onSuspendResume = OnSuspendResume;
        _onDisplay = OnDisplay;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint DeviceNotifyCallbackRoutine(IntPtr context, uint type, IntPtr setting);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var suspend = new DeviceNotifySubscribeParameters { Callback = _onSuspendResume };
        var status = PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, ref suspend, out _suspendRegistration);
        if (status != 0) _log.LogWarning("Suspend and resume notifications are unavailable (error {Status}); sleep will show only as gaps", status);

        var display = new DeviceNotifySubscribeParameters { Callback = _onDisplay };
        var setting = ConsoleDisplayState;
        status = PowerSettingRegisterNotification(ref setting, DeviceNotifyCallback, ref display, out _displayRegistration);
        if (status != 0) _log.LogWarning("Display state notifications are unavailable (error {Status}); the display is taken as on", status);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_suspendRegistration != IntPtr.Zero) PowerUnregisterSuspendResumeNotification(_suspendRegistration);
        if (_displayRegistration != IntPtr.Zero) PowerSettingUnregisterNotification(_displayRegistration);
        _suspendRegistration = _displayRegistration = IntPtr.Zero;
        return Task.CompletedTask;
    }

    /// <summary>Called by Windows on a system thread when the machine is about to sleep or has woken.</summary>
    internal uint OnSuspendResume(IntPtr context, uint type, IntPtr setting)
    {
        switch (type)
        {
            case PbtApmSuspend:
                try
                {
                    _commands.SendAsync(new SuspendCommand()).Wait(SuspendGrace);
                }
                catch (AggregateException error)
                {
                    _log.LogWarning(error.InnerException, "Preparing for sleep failed");
                }
                break;
            case PbtApmResumeAutomatic:
                _ = _commands.SendAsync(new ResumeCommand());
                break;
        }
        return 0;
    }

    /// <summary>Called by Windows when the console display turns on, off or dims. POWERBROADCAST_SETTING is a GUID, a
    /// DWORD length, then the data: 0 off, 1 on, 2 dimmed.</summary>
    internal uint OnDisplay(IntPtr context, uint type, IntPtr setting)
    {
        if (type == PbtPowerSettingChange && setting != IntPtr.Zero && Marshal.ReadInt32(setting, 16) >= 4)
        {
            _signals.DisplayOn = Marshal.ReadInt32(setting, 20) != 0;
        }
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceNotifySubscribeParameters
    {
        public DeviceNotifyCallbackRoutine Callback;
        public IntPtr Context;
    }

    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref DeviceNotifySubscribeParameters recipient, out IntPtr registration);

    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registration);

    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerSettingRegisterNotification(ref Guid setting, uint flags, ref DeviceNotifySubscribeParameters recipient, out IntPtr registration);

    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerSettingUnregisterNotification(IntPtr registration);
}
```

`src/PowerLedger.Service/PowerLedgerServiceLifetime.cs`
```csharp
using System.ServiceProcess;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PowerLedger.Service;

/// <summary>Set when the service control manager announces a system shutdown, so the session ends as a shutdown, not a stop.</summary>
internal sealed class ShutdownSignal
{
    private volatile bool _shuttingDown;

    public bool SystemShuttingDown
    {
        get => _shuttingDown;
        set => _shuttingDown = value;
    }
}

/// <summary>
/// The Windows service lifetime plus three things the default one drops: session lock changes, which the activity
/// source reports; the difference between a system shutdown and a plain service stop; and a failure exit code when
/// the sampling loop died, so the service control manager's recovery actions restart the service (spec §10).
/// </summary>
internal sealed class PowerLedgerServiceLifetime : WindowsServiceLifetime
{
    /// <summary>ERROR_EXCEPTION_IN_SERVICE.</summary>
    private const int ExceptionInService = 1064;

    private readonly ServiceSignals _signals;
    private readonly ShutdownSignal _shutdown;
    private readonly SamplingLoop _loop;

    public PowerLedgerServiceLifetime(
        IHostEnvironment environment, IHostApplicationLifetime applicationLifetime, ILoggerFactory loggerFactory,
        IOptions<HostOptions> hostOptions, IOptions<WindowsServiceLifetimeOptions> serviceOptions,
        ServiceSignals signals, ShutdownSignal shutdown, SamplingLoop loop)
        : base(environment, applicationLifetime, loggerFactory, hostOptions, serviceOptions)
    {
        _signals = signals;
        _shutdown = shutdown;
        _loop = loop;
        CanHandleSessionChangeEvent = true;
        CanShutdown = true;
    }

    /// <summary>Lock and unlock set the session's lock state; every other session change leaves it alone.</summary>
    internal static void Apply(SessionChangeReason reason, ServiceSignals signals)
    {
        if (reason == SessionChangeReason.SessionLock) signals.SessionLocked = true;
        else if (reason == SessionChangeReason.SessionUnlock) signals.SessionLocked = false;
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        Apply(changeDescription.Reason, _signals);
        base.OnSessionChange(changeDescription);
    }

    protected override void OnShutdown()
    {
        _shutdown.SystemShuttingDown = true;
        base.OnShutdown();
    }

    /// <summary>A stop the loop caused by failing is reported as a failure; the recovery actions act only on those.</summary>
    protected override void OnStop()
    {
        if (_loop.ExecuteTask is { IsFaulted: true }) ExitCode = ExceptionInService;
        base.OnStop();
    }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter NotificationsTests`
Expected: `Passed! - Failed: 0, Passed: 7`, the hardware test included.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Service/PowerNotifications.cs src/PowerLedger.Service/PowerLedgerServiceLifetime.cs tests/PowerLedger.Service.Tests/NotificationsTests.cs
git commit -m "Hear sleep, wake, display and lock changes from Windows"
```

---

### Task 21: Compose the host

**Files:**
- Create: `src/PowerLedger.Service/ServiceHost.cs`
- Modify: `src/PowerLedger.Service/Program.cs`
- Test: `tests/PowerLedger.Service.Tests/ServiceHostTests.cs`

Plan A's rule: one `SqliteDatabase` for the service's lifetime. Spec §10: Serilog rolling files in the data folder's `logs`, kept seven days, 5 MB a file, hardware names only. Under the service control manager, warnings and errors also go to the Application event log, where an administrator looks when a service will not start; a console run logs to the console instead. The loop is registered once and resolved by both the host and `Program`, so a loop that died makes the process exit with 1. `--data` moves the files and `--pipe` renames the pipe, so a console run in development cannot collide with an installed service. Under the service control manager, idle time comes from the App. In a console run the process is in the user's own session, so the activity source reads it directly. The host test runs the real sensors from the console, so it is tagged Hardware.

- [x] **Step 1: Write the failing test**

`tests/PowerLedger.Service.Tests/ServiceHostTests.cs`
```csharp
using System.IO.Pipes;
using Microsoft.Data.Sqlite;
using PowerLedger.Contracts;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

[Trait("Category", "Hardware")]
public class ServiceHostTests
{
    [Fact]
    public async Task From_the_console_the_service_records_readings_and_answers_on_its_pipe()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"powerledger-host-{Guid.NewGuid():N}");
        var pipeName = $"PowerLedger.test.{Guid.NewGuid():N}";
        var host = ServiceHost.Build(["--data", folder, "--pipe", pipeName]);
        await host.StartAsync();
        try
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            await using var client = new MessageChannel(pipe);

            ServiceStatus? status = null;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            for (var id = 1; status is null; id++)
            {
                await client.WriteAsync(new GetStatusRequest(id));
                if (await client.ReadAsync() is StatusReply { Status.Ticks: >= 3 } reply) status = reply.Status;
                else if (DateTime.UtcNow > deadline) throw new TimeoutException("The service never reported three readings.");
                else await Task.Delay(500);
            }

            status.Sources.ShouldContain(s => s.Name == "battery");
            status.Last.ShouldNotBeNull().TotalW.ShouldBeGreaterThan(0);
            status.InventoryHash.Length.ShouldBe(16);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
            Serilog.Log.CloseAndFlush();
        }

        using (var database = new SqliteDatabase(Path.Combine(folder, "power.db"), readOnly: true))
        {
            new RawSampleRepository(database).Count().ShouldBeGreaterThanOrEqualTo(3);
            new SessionRepository(database).List(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1))
                .Single().EndReason.ShouldBe(SessionReason.ServiceStop);
        }
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PowerLedger.Service.Tests --filter ServiceHostTests`
Expected: build error, `ServiceHost` not found.

- [x] **Step 3: Write the host and the entry point**

`src/PowerLedger.Service/ServiceHost.cs`
```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using PowerLedger.Contracts;
using PowerLedger.Sensors;
using PowerLedger.Storage;
using Serilog;

namespace PowerLedger.Service;

/// <summary>The database the service owns for its lifetime (Plan A: one per process), and what start-up had to do to get it.</summary>
internal sealed record OpenedDatabase(SqliteDatabase Database, string? Notice)
{
    public static OpenedDatabase Open(string path, DateTimeOffset now, string? folderNotice)
    {
        var (database, notice) = DatabaseOpener.Open(path, now);
        var notes = string.Join(" ", new[] { folderNotice, notice }.Where(n => n is not null));
        return new OpenedDatabase(database, notes.Length > 0 ? notes : null);
    }
}

/// <summary>Builds the service's host: configuration, logging, the database and every component, wired once.</summary>
internal static class ServiceHost
{
    public const string ServiceName = "PowerLedger";

    /// <summary>Configuration key, "--pipe" on the command line, that renames the pipe for a development run.</summary>
    public const string PipeKey = "pipe";

    /// <exception cref="UntrustedDataDirectoryException">The data folder belongs to another account.</exception>
    public static IHost Build(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        var asService = WindowsServiceHelpers.IsWindowsService();
        var paths = ServicePaths.From(builder.Configuration);
        var folderNotice = DataDirectory.Prepare(paths, enforce: asService, DateTimeOffset.UtcNow);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(paths.Logs, "service-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7,
                fileSizeLimitBytes: 5 * 1024 * 1024, rollOnFileSizeLimit: true)
            .CreateLogger();
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger, dispose: false);
        if (asService)
        {
            builder.Logging.AddEventLog(settings => settings.SourceName = ServiceName);
            builder.Logging.AddFilter<EventLogLoggerProvider>(level => level >= LogLevel.Warning);
        }
        else
        {
            builder.Logging.AddConsole();
        }

        builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);
        if (asService) builder.Services.AddSingleton<IHostLifetime, PowerLedgerServiceLifetime>();

        var pipeName = builder.Configuration[PipeKey] is { Length: > 0 } name ? name : PipeProtocol.PipeName;
        var services = builder.Services;
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ServiceSignals>();
        services.AddSingleton<ShutdownSignal>();
        services.AddSingleton<LoopCommands>();
        services.AddSingleton<LiveFeed>();
        services.AddSingleton<StatusBoard>();
        services.AddSingleton(provider => OpenedDatabase.Open(paths.Database, provider.GetRequiredService<TimeProvider>().GetUtcNow(), folderNotice));
        services.AddSingleton(provider => provider.GetRequiredService<OpenedDatabase>().Database);
        services.AddSingleton(provider => new TariffRepository(provider.GetRequiredService<SqliteDatabase>()));
        services.AddSingleton(provider => LoopEnvironmentFor(provider, asService));
        services.AddSingleton<PipeHandler>();
        services.AddHostedService<PowerNotifications>();
        services.AddSingleton<SamplingLoop>();                  // also resolved by Program and the lifetime, to see whether it failed
        services.AddHostedService(provider => provider.GetRequiredService<SamplingLoop>());
        services.AddHostedService(provider => new PipeServer(
            provider.GetRequiredService<PipeHandler>(), provider.GetRequiredService<LiveFeed>(),
            provider.GetRequiredService<ServiceSignals>(), provider.GetRequiredService<ILogger<PipeServer>>(), pipeName));
        return builder.Build();
    }

    private static LoopEnvironment LoopEnvironmentFor(IServiceProvider provider, bool asService)
    {
        var signals = provider.GetRequiredService<ServiceSignals>();
        var shutdown = provider.GetRequiredService<ShutdownSignal>();
        // In session 0 input is invisible, so idle time comes from the App; a console run reads its own session.
        Func<double?>? idle = asService ? signals.UserIdleSeconds : null;
        return new LoopEnvironment(
            Sensors: () => new MachineSensorSet(MachineSensors.Create(() => signals.DisplayOn, () => signals.SessionLocked, idle)),
            Inventory: HardwareInventory.Detect,
            SystemUptime: () => TimeSpan.FromMilliseconds(Environment.TickCount64),
            SystemShuttingDown: () => shutdown.SystemShuttingDown,
            DatabaseNotice: provider.GetRequiredService<OpenedDatabase>().Notice);
    }
}
```

`src/PowerLedger.Service/Program.cs`
```csharp
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace PowerLedger.Service;

internal static class Program
{
    private static int Main(string[] args)
    {
        IHost host;
        try
        {
            host = ServiceHost.Build(args);
        }
        catch (Exception error) when (error is UntrustedDataDirectoryException or IOException or UnauthorizedAccessException)
        {
            Fatal($"PowerLedger cannot use its data folder: {error.Message}");
            return 1;
        }

        try
        {
            var loop = host.Services.GetRequiredService<SamplingLoop>();
            host.Run();
            return loop.ExecuteTask is { IsFaulted: true } ? 1 : 0;
        }
        catch (Exception error)
        {
            Log.Fatal(error, "PowerLedger stopped on an error");
            Fatal($"PowerLedger stopped on an error: {error.Message}");
            return 1;
        }
        finally
        {
            host.Dispose();
            Log.CloseAndFlush();
        }
    }

    /// <summary>The Application event log is where an administrator looks when a service will not start; the log file may not exist yet.</summary>
    private static void Fatal(string message)
    {
        Console.Error.WriteLine(message);
        try
        {
            EventLog.WriteEntry(ServiceHost.ServiceName, message, EventLogEntryType.Error);
        }
        catch (Exception)
        {
            // No event source and no right to create one: standard error is all there is.
        }
    }
}
```

- [x] **Step 4: Run the tests to verify they pass**

Run: `dotnet build -c Release`
Expected: `Build succeeded.` with 0 warnings.

Run: `dotnet test tests/PowerLedger.Service.Tests --filter ServiceHostTests`
Expected: `Passed! - Failed: 0, Passed: 1`. The run takes about five seconds, most of it the real sensors starting.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Service/ServiceHost.cs src/PowerLedger.Service/Program.cs tests/PowerLedger.Service.Tests/ServiceHostTests.cs
git commit -m "Compose the service host: logging, database, loop, notifications and pipe"
```

---

### Task 22: Run it for real, and the development scripts

**Files:**
- Create: `scripts/dev-service.ps1`
- Create: `scripts/pipe-status.ps1`
- Create: `src/PowerLedger.Service/README.md`

The tests prove each part and the whole loop on a fake clock. This task runs the real service from the console on the development laptop, kills it to prove crash recovery, and measures it against the spec §12 performance gate. The two scripts are for the owner: one installs the service for development from an elevated PowerShell (the installer in Plan E replaces it), and one asks a running service for its status.

- [x] **Step 1: Write the scripts and the README**

`scripts/dev-service.ps1`
```powershell
#Requires -RunAsAdministrator
<#
.SYNOPSIS
Installs, removes, starts or stops the PowerLedger service on this machine for development.

.DESCRIPTION
Run from an elevated PowerShell. "install" publishes the service to Program Files, registers it to start with
Windows as LocalSystem, and sets the recovery actions from spec §10: restart after 5 s, up to three times a day,
including after a failure exit. The installer (Plan E) replaces this script for real installs.
#>
param(
    [Parameter(Mandatory)]
    [ValidateSet('install', 'uninstall', 'start', 'stop', 'status')]
    [string]$Action,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$name = 'PowerLedger'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $env:ProgramFiles 'PowerLedger\Service'

switch ($Action) {
    'install' {
        dotnet publish (Join-Path $root 'src\PowerLedger.Service') -c $Configuration -o $target --nologo
        if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
        $exe = Join-Path $target 'PowerLedger.Service.exe'
        sc.exe create $name binPath= "`"$exe`"" start= auto obj= LocalSystem DisplayName= 'PowerLedger'
        sc.exe description $name 'Records how much power this PC uses.'
        sc.exe failure $name reset= 86400 actions= restart/5000/restart/5000/restart/5000
        sc.exe failureflag $name 1
        sc.exe start $name
    }
    'uninstall' {
        sc.exe stop $name
        Start-Sleep -Seconds 3
        sc.exe delete $name
    }
    'start' { sc.exe start $name }
    'stop' { sc.exe stop $name }
    'status' { sc.exe query $name }
}
```

`scripts/pipe-status.ps1`
```powershell
<#
.SYNOPSIS
Asks a running PowerLedger service for its status and prints the reply.

.EXAMPLE
./scripts/pipe-status.ps1
./scripts/pipe-status.ps1 -Pipe PowerLedger.dev
#>
param(
    [string]$Pipe = 'PowerLedger.v1',
    [string]$Request = '{"type":"getStatus","id":1}'
)

$ErrorActionPreference = 'Stop'
$client = [System.IO.Pipes.NamedPipeClientStream]::new('.', $Pipe, [System.IO.Pipes.PipeDirection]::InOut)
try {
    $client.Connect(5000)
    $writer = [System.IO.StreamWriter]::new($client, [System.Text.UTF8Encoding]::new($false))
    $writer.AutoFlush = $true
    $reader = [System.IO.StreamReader]::new($client)
    $writer.Write($Request + "`n")
    $reader.ReadLine()
}
finally {
    $client.Dispose()
}
```

`src/PowerLedger.Service/README.md`
```markdown
# PowerLedger.Service

The Windows service that records how much power the PC uses. It samples once a second, stores readings in
`C:\ProgramData\PowerLedger\power.db`, and serves live readings, status and settings on `\\.\pipe\PowerLedger.v1`.

## Running it

- **As a service:** `scripts/dev-service.ps1 install` from an elevated PowerShell. It starts with Windows as
  LocalSystem and restarts itself after a failure.
- **From the console, for development:** `dotnet run --project src/PowerLedger.Service -c Release -- --data <folder> --pipe PowerLedger.dev`.
  `--data` keeps the files out of ProgramData and `--pipe` keeps clear of an installed service. Ctrl+C stops it
  cleanly. The console run reads idle time from its own session and assumes the session is unlocked.

`scripts/pipe-status.ps1 [-Pipe PowerLedger.dev]` prints the service's status.

## Files

| Path | What |
|---|---|
| `power.db` | readings, rollups, sessions, tariffs, calibration, settings |
| `power.db.bak` | a copy taken before each schema upgrade |
| `power.corrupt-*.db`, `power.untrusted-*.db` | a damaged database, or one another account owned, set aside at start |
| `logs\service-*.log` | seven days of logs, 5 MB a file; warnings and errors also go to the Application event log |

Only SYSTEM and administrators can write the folder; users can read it. The service refuses a folder another
account owns.
```

- [x] **Step 2: Run the service from the console**

Build, then start the service in the background with a scratch folder and a development pipe name:

```bash
dotnet build src/PowerLedger.Service -c Release
src/PowerLedger.Service/bin/Release/net10.0-windows/PowerLedger.Service.exe --data "$TEMP/pl-run" --pipe PowerLedger.dev
```

After 20 s, from PowerShell:

```powershell
./scripts/pipe-status.ps1 -Pipe PowerLedger.dev
```

Expected: one line of JSON of type `status`, with `ticks` near 20, a `battery` source, an `inventoryHash`, and a `last` reading whose `cpuMeasured` is true on a Windows 11 machine with energy-meter rails.

- [x] **Step 3: Kill it and start it again**

End the process without letting it stop (Task Manager's "End task", or `taskkill /F /IM PowerLedger.Service.exe`), start it again with the same arguments for 15 s, then end it the same way. Read the database:

```bash
python -c "import sqlite3,os; c=sqlite3.connect(os.path.expandvars(r'%TEMP%\pl-run\power.db')); print(c.execute('select reason,end_reason from sessions').fetchall()); print(c.execute('select count(*) from samples_1m').fetchone())"
```

Expected: the first session ended `CrashRecovered` and the second started `CrashRecovered`. The minute table has a row for every minute that has raw rows once the next run has started: a minute whose run dies before the minute ends is folded by the next start's catch-up. On the development laptop, 2026-09-15, the second run died inside the minute the first had died in; the third start folded that minute and closed the second session at its start, since it had stored no tick.

- [x] **Step 4: Measure the performance gate**

Start the service again and let it run for five minutes, then sample it from PowerShell:

```powershell
$p = Get-Process PowerLedger.Service
$cpu0 = $p.TotalProcessorTime; Start-Sleep 60; $p.Refresh()
$private = (Get-Counter "\Process(PowerLedger.Service)\Working Set - Private").CounterSamples[0].CookedValue
"{0:N2} % CPU, {1:N1} MB private working set, {2:N1} MB working set" -f (($p.TotalProcessorTime - $cpu0).TotalSeconds / 60 / [Environment]::ProcessorCount * 100), ($private / 1MB), ($p.WorkingSet64 / 1MB)
```

Expected: under 0.5 % CPU and under 50 MB private working set, the figure Task Manager shows (spec §12). The full working set also counts shared system and driver DLL images. Measured on the development laptop, 2026-09-15, after five minutes: 0.04 % CPU, 36.8 MB private working set, 88 MB working set, of which `nvml.dll` alone is 16.5 MB and `System.Private.CoreLib` 15 MB. Stop the service with Ctrl+C in its console. A console process started without a window refuses a plain `taskkill`; `taskkill /F` ends it, and the next start records that as a crash.

- [x] **Step 5: Commit**

```bash
git add scripts/dev-service.ps1 scripts/pipe-status.ps1 src/PowerLedger.Service/README.md
git commit -m "Add development scripts to install and query the service"
```

---

### Task 23: Final verification, the spec, and the handoff to Plan D

- [ ] **Step 1: Clean build with warnings as errors**

Run: `dotnet build -c Release`
Expected: `Build succeeded.` and `0 Warning(s)`.

- [ ] **Step 2: Full test run**

Run: `dotnet test -c Release`
Expected: Core `Passed: 110`, Storage `Passed: 41`, Sensors `Passed: 99`, Service `Passed: 126`, no failures and no skips.

- [ ] **Step 3: Confirm CI can skip the hardware tests**

Run: `dotnet test -c Release --filter "Category!=Hardware"`
Expected: Service `Passed: 124` (the display registration and the console host excluded), Sensors `Passed: 94`, Core and Storage unchanged, no failures.

- [ ] **Step 4: Bring the spec in line**

In `docs/superpowers/specs/2026-09-08-powerledger-design.md`:

§4, in the Display row, replace ``RegisterPowerSettingNotification(GUID_CONSOLE_DISPLAY_STATE)` with service handle`` with ``PowerSettingRegisterNotification(GUID_CONSOLE_DISPLAY_STATE)` with a callback``.

§4, in the Activity row, replace ``WTSRegisterSessionNotification` for the lock state`` with `session-change events from the service control manager for the lock state`.

§6, replace the line that begins `- Suspend and resume are handled through` with:

```markdown
- Suspend and resume arrive through the power manager's callback registration (`PowerRegisterSuspendResumeNotification`), which works the same in a console run: on suspend the write buffer is flushed, the current minute folded and the session row closed, with the machine held up to 1.5 s; on resume a new session row opens, the sensor set is rebuilt, the hardware inventory re-runs, and the first Δt is measured by the wall clock so the sleep is recorded as a gap.
```

§7, replace the line that begins `- File:` with:

```markdown
- File: `C:\ProgramData\PowerLedger\power.db`. ACL: SYSTEM and Administrators full control, Users read and execute, inherited by everything in the folder and nothing inherited from above. A database users could edit would feed crafted input to a SYSTEM process. A read-only SQLite connection reads a WAL database whose `-wal` and `-shm` it cannot write (verified with SQLite 3.51), so the App can read history while the service runs; with the service stopped the App shows it as not running. The service refuses a data folder another account owns, because anyone may create folders in ProgramData, and sets aside database files another account owns.
```

§7, replace the line that begins `- Writes are batched:` with:

```markdown
- Writes are batched: 60 samples per transaction (one per minute). The buffer flushes on suspend, shutdown and service stop. `ServiceBase` offers no preshutdown hook; a minute of readings writes in milliseconds, well inside the shutdown allowance.
```

§8, replace the line that begins `- Messages:` with:

```markdown
- Messages: `subscribe` (the server pushes a `reading` frame each tick, saying which parts were measured), `getStatus` (service version, per-source health and suspect counts, sensor restarts, calibration progress, DB size, write problems), `getSettings`, `setSettings`, `setTariff` (inserts a `tariffs` row; `effectiveFrom` defaults to now and may be backdated by the user), `resetCalibration`, and `reportActivity` (the App's idle seconds every few seconds, because the service in session 0 cannot see input).
```

- [ ] **Step 5: Record the public surface Plan D will build on**

Plan D (the App) consumes exactly these:

| Type | Used by Plan D for |
|---|---|
| `PipeProtocol.PipeName`, `MessageChannel` | connect with `new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous)` and wrap it |
| `SubscribeRequest` → `ReadingFrame` pushes | the Now screen; `DisplayBandW` and `RestBandW` are the report's bands, `CpuMeasured` and `GpuMeasured` mark modelled parts |
| `GetStatusRequest` → `ServiceStatus` | the status screen, calibration progress, write problems, the database notice |
| `GetSettingsRequest`, `SetSettingsRequest`, `ServiceSettings` | the settings screen and the wizard; show an `ErrorReply`'s message as it is |
| `SetTariffRequest` | the tariff dialog |
| `ResetCalibrationRequest` | the settings screen |
| `ReportActivityRequest` | every 5 s from the tray App: seconds since `GetLastInputInfo` |
| `SqliteDatabase(path, readOnly: true)`, `ReportQueries`, `AggregateRepository`, `SessionRepository`, `TariffRepository` | history, reports and sessions, read-only |

**Rules Plan D must follow.** These are contracts the types cannot enforce:

- Send `reportActivity` every 5 s from every running App. The service ignores a report older than 30 s and takes the most recently active user, so without the App idle waste reads as zero.
- Correlate replies by id. Once subscribed, frames can arrive between a request and its reply.
- Reconnect with backoff from 1 s to 30 s (spec §8), and subscribe again after reconnecting.
- Check the server before sending settings or tariffs. While the service runs no other process can serve the name, but while it is stopped anyone can. `GetNamedPipeServerProcessId` gives the process, and its image must be the installed service (Plan E fixes the path).
- The App never writes the database (spec §3). It opens `C:\ProgramData\PowerLedger\power.db` read-only, which works while the service runs; when opening fails, show the service as not running.
- Move Plan A's report arithmetic (`Summarise`, `Days`) into Core before computing live totals in memory, rather than writing it twice.

**Known gaps, deliberately left to later plans:**

- Plan B's other gaps stand: non-NVIDIA discrete GPUs are charged nothing, an all-in-one's panel is not charged, and a laptop shut on a dock still charges its panel.
- A wall-clock change is not noted in the sessions table, though it never produces negative energy (spec §6). The table has no column for it.
- The service installs no event source of its own. The first warning written under the service control manager creates one, which LocalSystem may do.
- Modern Standby may deliver no suspend at all. The loop then sees a long Δt, which the integrator records as a gap, so energy stays right but the sessions table shows no suspend.

- [ ] **Step 6: Commit**

```bash
git add docs/superpowers/specs/2026-09-08-powerledger-design.md docs/superpowers/plans
git commit -m "Complete Plan C: the service runs, records and serves"
```

---

## Self-review against the spec

| Spec section | Covered by |
|---|---|
| §3 Service project, rules: App never writes, one job per file | Tasks 1, 17–21; the loop is the only writer |
| §4 sampling at the configured interval, per-source isolation, a failing source never stops the loop | Plan B's sampler inside Task 10's watchdog thread; Task 17 |
| §6 Δt from a monotonic clock, gaps, suspend and resume, sessions and their reasons | Tasks 9, 14, 17, 20 |
| §7 batched writes and flushes, minute and hour jobs, 03:00 purge, weekly vacuum, migrations with backup | Tasks 11–13, 17; migrations and backup are Plan A's, run by Task 5's opener |
| §7 data folder and ACL | Task 6 (revised: Users read, not modify) |
| §8 pipe name, ACL, NDJSON with source generation, 64 KB limit, message kinds, several clients, range-checked settings | Tasks 2, 3, 18, 19 |
| §10 corrupt database, disk full, service crash and recovery, schema migration | Tasks 5, 11, 14, 20, 22 |
| §10 logging to rolling files | Task 21 |
| §11 no remote pipe access, closed settings set, nothing path-like over the pipe | Tasks 2, 18, 19 |
| §12 service test: fake sources, temp database, ten simulated minutes with suspend and resume; pipe round-trip and reconnect | Task 17; Task 19 |
| §12 performance gate | Task 22 |
| §9 UI, §13 installer | **Plans D and E**, out of scope by design |

Plan A's handoff rules: the explicit gap threshold (Tasks 12, 17), one `SqliteDatabase` (Task 21), the learner fed the same tick (Task 17), saving calibration on a timer (Task 15), hours folded only when complete (Task 12), and closing only the live session (Task 14). Plan B's: rebuild on resume (Task 17), idle time from the user's session (Tasks 8, 18), a status snapshot (Task 16), the watchdog (Task 10), minute rows from storage with catch-up (Task 12), detection folded in only for a new machine (Task 7), and per-component provenance for the live view (Task 16).

Names were checked across tasks. `ServiceSettings`, `PipeMessage` and its kinds, `ReadingFrame`, `ServiceStatus` and `MessageChannel` come from Tasks 2 and 3. `LoopCommand` and its kinds come from Task 17 and are sent by Tasks 18 and 20. `TickResult` comes from Task 10 and is used by Task 17. `Rollups.Floor` comes from Task 12 and is used by Task 17. `RetentionRunner.LastPurgeKey` comes from Task 13 and is read by Task 17's test. `PipeHandler.Starting` and `NoAnswer` come from Task 18 and are used by Task 19's test.
