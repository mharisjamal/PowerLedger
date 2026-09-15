# PowerLedger Plan D3 — Settings, the first-run wizard, and changes over the pipe — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user change what PowerLedger knows: the tariff, the machine profile, the idle threshold, the sample interval and retention through the service, and the CO₂ factor, the theme and starting with Windows in the App. Add the first-run wizard that asks for the tariff, confirms the detected hardware and explains measured against estimated readings.

**Architecture:** The App's pipe link gains the three writes the service already answers (Plan C): settings, a tariff, and a calibration reset. Before sending any of them it checks that the process serving the pipe is the installed service. The App's own preferences become live: a change applies at once, reaches the screens that show CO₂, and is saved to `ui.json`. Two form view models, `TariffForm` and `ServiceForm`, hold the typing and the validation, and both the Settings screen and the wizard use them. The wizard is a page of the main window, shown until it is finished once.

**Tech Stack:** as Plan D2. The server check uses `GetNamedPipeServerProcessId`, `OpenProcess` and `QueryFullProcessImageName`, and reads the service's registered command line from `HKLM\SYSTEM\CurrentControlSet\Services\PowerLedger`. Spec: `docs/superpowers/specs/2026-09-08-powerledger-design.md` §8, §9, §11.

**Git rule (from the owner):** commit locally after every task. Never add a remote, push, or create a GitHub repo.

**Scope note:** Plan E is the installer. It registers the service, adds the Run entry and starts the App, which then shows the wizard.

---

## What already exists

`main` holds Plans A to D2. The service answers `setSettings`, `setTariff` and `resetCalibration` (Plan C's `PipeHandler`). It range-checks everything and seeds the machine profile from detection on its first run (`ProfilePolicy`). The App reads history read-only, keeps its preferences in `ui.json` through `UiPreferencesStore`, switches palettes through `ThemeManager`, and sets the Run entry through `StartWithWindows`.

D2 left rules for D3, and this plan honours them:
- The server check before any change.
- The CO₂ factor and the theme at run time.
- A new tariff showing on the next refresh.
- Settings as a page that reads while it shows.
- The existing patterns: WPF-free view models, `UiThreads`, `DynamicResource`, the `Segment` style, and `Describe()` on drawn controls.

## Decisions made while planning

- **The server check compares executables, not names.** Anyone may create a pipe with PowerLedger's name before the service does. So before sending a change, the App asks Windows which process serves the pipe, reads that process's executable, and compares it with the command line Windows has registered for the PowerLedger service. A development run started with `--pipe` names its own pipe and skips the check. When the service is not installed, changes are refused with a sentence that says so.
- **Choices are segmented buttons; numbers are text boxes.** Chassis, power supply tier, sample interval and theme have few values, so they use D2's `Segment` style. A `ComboBox` would need a large template to fit the palette. Numbers are typed, and the form says which one it could not read.
- **One save per kind of change.** The service takes its settings whole, so the machine profile, the idle threshold, the sample interval and retention share one Save. A tariff is its own request and gets its own Save. A calibration reset asks for confirmation inline before it is sent. The App's own preferences apply when chosen; the CO₂ factor, being typed, applies on Save.
- **No CO₂ country table and no suggested tariffs.** Spec §16 leaves both sources to the owner, so D3 invents neither. The CO₂ factor is typed, with the world average as the default. The wizard's tariff step starts in the region's currency but suggests no price. These are the two gaps this plan leaves for the owner.
- **The wizard is a page of the main window,** shown in place of the rail until it is finished once. `ui.json` records that, and Settings can run it again. The installer (Plan E) opens the App, so the first window a new user sees is the wizard.

## File structure

```
src/PowerLedger.App/
  Service/ServerCheck.cs        IServerCheck, InstalledServiceCheck, TrustAnyServer
  Service/ServiceLink.cs        Modify: WriteResult; SetSettingsAsync, SetTariffAsync, ResetCalibrationAsync behind the check
  History/MachineHistory.cs     IMachineHistory, DetectedHardware
  History/HistoryReader.cs      Modify: implements IMachineHistory
  Preferences/UiPreferences.cs  Modify: FirstRunDone
  Preferences/AppPreferences.cs IUiSettings and the App's live preferences
  Now/NowViewModel.cs, Report/ReportViewModel.cs, Report/MonthlyReports.cs   Modify: the CO₂ factor can change
  Settings/TariffForm.cs        a tariff: price, currency, first day
  Settings/ServiceForm.cs       the machine profile, idle threshold, sample interval and retention
  Settings/SettingsViewModel.cs, Settings/SettingsView.xaml(.cs)
  Setup/WizardViewModel.cs, Setup/WizardView.xaml(.cs)
  Shell/ShellViewModel.cs, Shell/MainWindow.xaml, Theme/Styles.xaml, App.xaml.cs   Modify
tests/PowerLedger.App.Tests/
  ServerCheckTests.cs, MachineHistoryTests.cs, AppPreferencesTests.cs, TariffFormTests.cs, ServiceFormTests.cs,
  SettingsViewModelTests.cs, WizardViewModelTests.cs, FakeMachineHistory.cs, FakeUiSettings.cs
  ServiceLinkTests.cs, FakeService.cs, FakeLink.cs, UiPreferencesTests.cs, NowViewModelTests.cs, ReportViewModelTests.cs,
  MonthlyReportsTests.cs, ShellViewModelTests.cs, BreakdownViewModelTests.cs, RenderingTests.cs   Modify
```

---

### Task 1: Check the server before sending changes

**Files:**
- Create: `src/PowerLedger.App/Service/ServerCheck.cs`
- Test: `tests/PowerLedger.App.Tests/ServerCheckTests.cs`

`InstalledServiceCheck.Refusal` returns null when the process serving a pipe may be sent changes, and otherwise says why not. It reads the server's process ID from the pipe handle, then the process's executable, which needs only the limited query right a normal user has on a service's process. It compares that with the executable in the command line Windows has registered for the PowerLedger service.

The registry read is passed in, so a test compares against its own process: in the test, both ends of the pipe live in the test host. `ExecutableOf` pulls the executable out of a registered command line, quoted or not.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.App.Tests/ServerCheckTests.cs`
```csharp
using System.IO.Pipes;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class ServerCheckTests : IDisposable
{
    private readonly NamedPipeServerStream _server;
    private readonly NamedPipeClientStream _client;

    public ServerCheckTests()
    {
        var name = $"PowerLedger.check-test.{Guid.NewGuid():N}";
        _server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var waiting = _server.WaitForConnectionAsync();
        _client = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        _client.Connect(2000);
        waiting.Wait(2000);
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }

    [Fact]
    public void The_server_behind_a_pipe_is_known_by_its_executable()
        => InstalledServiceCheck.ServerImage(_client.SafePipeHandle).ShouldBe(Environment.ProcessPath, StringCompareShould.IgnoreCase);

    [Fact]
    public void The_installed_service_may_be_sent_changes()
        => new InstalledServiceCheck(() => Environment.ProcessPath).Refusal(_client.SafePipeHandle).ShouldBeNull();

    [Fact]
    public void Another_program_serving_the_pipe_is_refused()
        => new InstalledServiceCheck(() => @"C:\Program Files\PowerLedger\PowerLedger.Service.exe").Refusal(_client.SafePipeHandle)
            .ShouldBe("The program serving PowerLedger's pipe isn't the installed service, so nothing was sent.");

    [Fact]
    public void Without_an_installed_service_changes_are_refused()
        => new InstalledServiceCheck(() => null).Refusal(_client.SafePipeHandle)
            .ShouldBe("The PowerLedger service isn't installed, so nothing can be changed.");

    [Fact]
    public void A_development_run_trusts_its_own_pipe() => new TrustAnyServer().Refusal(_client.SafePipeHandle).ShouldBeNull();

    [Theory]
    [InlineData("\"C:\\Program Files\\PowerLedger\\PowerLedger.Service.exe\" --data x", @"C:\Program Files\PowerLedger\PowerLedger.Service.exe")]
    [InlineData(@"C:\PowerLedger\PowerLedger.Service.exe --pipe dev", @"C:\PowerLedger\PowerLedger.Service.exe")]
    [InlineData(@"C:\PowerLedger\PowerLedger.Service.exe", @"C:\PowerLedger\PowerLedger.Service.exe")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_executable_comes_out_of_the_registered_command_line(string? commandLine, string? executable)
        => InstalledServiceCheck.ExecutableOf(commandLine).ShouldBe(executable);
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.App.Tests --filter ServerCheckTests`
Expected: build error, `InstalledServiceCheck` not found.

- [x] **Step 3: Write the check**

`src/PowerLedger.App/Service/ServerCheck.cs`
```csharp
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PowerLedger.App;

/// <summary>Whether the process at the other end of the pipe may be sent changes (Plan C's rule, spec §11).</summary>
internal interface IServerCheck
{
    /// <summary>Null when the server may be sent changes; otherwise why not, in words the App can show.</summary>
    string? Refusal(SafePipeHandle pipe);
}

/// <summary>A development run, started with --pipe, names its own pipe and trusts whatever serves it.</summary>
internal sealed class TrustAnyServer : IServerCheck
{
    public string? Refusal(SafePipeHandle pipe) => null;
}

/// <summary>
/// The process serving the pipe must be the installed service: the executable Windows starts for the PowerLedger
/// service. Anyone may create a pipe with PowerLedger's name before the service does, so the name proves nothing.
/// </summary>
/// <param name="installedImage">The installed service's executable, or null when it is not installed.</param>
internal sealed class InstalledServiceCheck(Func<string?> installedImage) : IServerCheck
{
    public const string ServiceName = "PowerLedger";
    private const uint QueryLimitedInformation = 0x1000;

    public static InstalledServiceCheck FromRegistry() => new(RegisteredImage);

    public string? Refusal(SafePipeHandle pipe)
    {
        if (installedImage() is not { } expected) return "The PowerLedger service isn't installed, so nothing can be changed.";
        if (ServerImage(pipe) is not { } actual) return "The program serving PowerLedger's pipe couldn't be identified, so nothing was sent.";
        return string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase)
            ? null
            : "The program serving PowerLedger's pipe isn't the installed service, so nothing was sent.";
    }

    /// <summary>The executable of the process serving a pipe, or null when Windows won't say.</summary>
    internal static string? ServerImage(SafePipeHandle pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe, out var id)) return null;
        var process = OpenProcess(QueryLimitedInformation, false, id);
        if (process == IntPtr.Zero) return null;
        try
        {
            var name = new StringBuilder(1024);
            var length = name.Capacity;
            return QueryFullProcessImageName(process, 0, name, ref length) ? name.ToString(0, length) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>The executable a registered command line starts: the quoted part, or everything up to ".exe".</summary>
    internal static string? ExecutableOf(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var line = commandLine.Trim();
        if (line.StartsWith('"'))
        {
            var close = line.IndexOf('"', 1);
            return close > 1 ? line[1..close] : null;
        }
        var exe = line.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? line[..(exe + 4)] : line;
    }

    private static string? RegisteredImage()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
        return ExecutableOf(key?.GetValue("ImagePath") as string);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter ServerCheckTests`
Expected: `Passed! - Failed: 0, Passed: 10`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.App/Service/ServerCheck.cs tests/PowerLedger.App.Tests/ServerCheckTests.cs
git commit -m "Check that the installed service serves the pipe before sending it changes"
```

---

### Task 2: Send settings, a tariff and a calibration reset

**Files:**
- Modify: `src/PowerLedger.App/Service/ServiceLink.cs`
- Modify: `src/PowerLedger.App/App.xaml.cs`
- Test: `tests/PowerLedger.App.Tests/ServiceLinkTests.cs`
- Modify: `tests/PowerLedger.App.Tests/FakeService.cs`, `tests/PowerLedger.App.Tests/FakeLink.cs`

The link checks the server once per connection, when it connects, and keeps the answer with the connection. Each write comes back as a `WriteResult`, whose `Problem` is null when the service applied the change. Otherwise the problem reads as a sentence: the service's own refusal (it range-checks everything), the check's refusal, "isn't running", or "didn't answer". The App passes `InstalledServiceCheck.FromRegistry()`, or `TrustAnyServer` for a run started with `--pipe`.

- [x] **Step 1: Write the failing tests**

In `tests/PowerLedger.App.Tests/FakeService.cs`, add after `Status`:

```csharp
    /// <summary>When set, the service refuses every change with this message, as it does a value out of range.</summary>
    public string? Refuse { get; set; }
```

and replace `Reply` with:

```csharp
    private PipeMessage Reply(PipeRequest request) => request switch
    {
        GetStatusRequest r => new StatusReply(r.Id, Status),
        GetSettingsRequest r => new SettingsReply(r.Id, ServiceSettings.Default),
        SetSettingsRequest or SetTariffRequest or ResetCalibrationRequest when Refuse is { } refusal => new ErrorReply(request.Id, refusal),
        _ => new OkReply(request.Id),
    };
```

In `tests/PowerLedger.App.Tests/ServiceLinkTests.cs`, change the link's construction to `new PipeServiceLink(_name, new FixedIdle(42), _clock, new TrustAnyServer())`, and append inside the class:

```csharp
    [Fact]
    public async Task Settings_a_tariff_and_a_reset_reach_the_service()
    {
        var settings = ServiceSettings.Default with { IdleThresholdSeconds = 600 };
        (await _link.SetSettingsAsync(settings)).ShouldBe(WriteResult.Done);
        (await _link.SetTariffAsync(0.17m, "USD", null)).ShouldBe(WriteResult.Done);
        (await _link.ResetCalibrationAsync()).ShouldBe(WriteResult.Done);

        _service.Requests.OfType<SetSettingsRequest>().Single().Settings.ShouldBe(settings);
        var tariff = _service.Requests.OfType<SetTariffRequest>().Single();
        (tariff.PricePerKwh, tariff.Currency, tariff.EffectiveFrom).ShouldBe((0.17m, "USD", (DateTimeOffset?)null));
        _service.Requests.OfType<ResetCalibrationRequest>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task A_refusal_comes_back_in_the_services_words()
    {
        _service.Refuse = "The idle threshold must be between 60 and 1800 seconds.";
        var result = await _link.SetSettingsAsync(ServiceSettings.Default);
        result.Succeeded.ShouldBeFalse();
        result.Problem.ShouldBe("The idle threshold must be between 60 and 1800 seconds.");
    }

    [Fact]
    public async Task A_server_that_fails_the_check_is_sent_nothing()
    {
        var name = $"PowerLedger.app-test.{Guid.NewGuid():N}";
        await using var service = new FakeService(name);
        await using var checkedLink = new PipeServiceLink(name, new FixedIdle(0), _clock, new RefuseAll());
        service.Start();
        checkedLink.Start();
        await WaitFor.True(() => checkedLink.IsConnected);

        (await checkedLink.SetTariffAsync(0.2m, "EUR", null)).Problem.ShouldBe(RefuseAll.Reason);
        service.Requests.OfType<SetTariffRequest>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Nothing_is_changed_while_the_service_is_away()
    {
        await using var alone = new PipeServiceLink($"PowerLedger.nobody.{Guid.NewGuid():N}", new FixedIdle(0), _clock, new TrustAnyServer());
        (await alone.ResetCalibrationAsync()).ShouldBe(WriteResult.NotConnected);
    }

    private sealed class RefuseAll : IServerCheck
    {
        public const string Reason = "Not the installed service.";

        public string? Refusal(Microsoft.Win32.SafeHandles.SafePipeHandle pipe) => Reason;
    }
```

In `tests/PowerLedger.App.Tests/FakeLink.cs`, add:

```csharp
    /// <summary>Every change the App asked for, in order.</summary>
    public List<object> Writes { get; } = [];

    /// <summary>What every change comes back as.</summary>
    public WriteResult Answer { get; set; } = WriteResult.Done;

    public Task<WriteResult> SetSettingsAsync(ServiceSettings settings, CancellationToken cancel = default) => Write(settings);

    public Task<WriteResult> SetTariffAsync(decimal pricePerKwh, string currency, DateTimeOffset? effectiveFrom, CancellationToken cancel = default)
        => Write((pricePerKwh, currency, effectiveFrom));

    public Task<WriteResult> ResetCalibrationAsync(CancellationToken cancel = default) => Write("reset");

    private Task<WriteResult> Write(object change)
    {
        if (!IsConnected) return Task.FromResult(WriteResult.NotConnected);
        Writes.Add(change);
        return Task.FromResult(Answer);
    }
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.App.Tests --filter ServiceLinkTests`
Expected: build error, `WriteResult` not found and `PipeServiceLink` takes three arguments.

- [x] **Step 3: Add the writes**

In `src/PowerLedger.App/Service/ServiceLink.cs`, add before `IServiceLink`:

```csharp
/// <summary>What became of a change sent to the service: <see cref="Problem"/> is null when it was applied, and otherwise
/// says why not in words the App can show.</summary>
internal sealed record WriteResult(string? Problem)
{
    public static WriteResult Done { get; } = new((string?)null);

    public static WriteResult NotConnected { get; } = new("The service isn't running, so nothing was changed.");

    public static WriteResult NoAnswer { get; } = new("The service didn't answer, so the change may not have been made.");

    public bool Succeeded => Problem is null;
}
```

add to `IServiceLink`, after `GetSettingsAsync`:

```csharp
    /// <summary>Asks the service to use these settings (spec §8), once the server has passed the check.</summary>
    Task<WriteResult> SetSettingsAsync(ServiceSettings settings, CancellationToken cancel = default);

    /// <summary>Adds a tariff; <paramref name="effectiveFrom"/> null means from now (spec §8).</summary>
    Task<WriteResult> SetTariffAsync(decimal pricePerKwh, string currency, DateTimeOffset? effectiveFrom, CancellationToken cancel = default);

    /// <summary>Forgets the learned baseline (spec §5).</summary>
    Task<WriteResult> ResetCalibrationAsync(CancellationToken cancel = default);
```

change the class to take the check and describe it:

```csharp
/// <summary>
/// The App's end of \\.\pipe\PowerLedger.v1. It connects, subscribes, passes frames on, answers requests by id and
/// reports idle time every five seconds. When the service goes away it tries again after 1, 2, 4, 8, 16 and then every
/// 30 seconds (spec §8), and subscribes again once back. Each connection's server is checked when it connects, and a
/// change is sent only to one that passed.
/// </summary>
internal sealed class PipeServiceLink(string pipeName, IIdleSource idle, TimeProvider clock, IServerCheck check) : IServiceLink
```

add the field `private volatile string? _refusal;` after `_channel`, and add after `GetSettingsAsync`:

```csharp
    public Task<WriteResult> SetSettingsAsync(ServiceSettings settings, CancellationToken cancel = default)
        => WriteAsync(new SetSettingsRequest(NextId(), settings), cancel);

    public Task<WriteResult> SetTariffAsync(decimal pricePerKwh, string currency, DateTimeOffset? effectiveFrom, CancellationToken cancel = default)
        => WriteAsync(new SetTariffRequest(NextId(), pricePerKwh, currency, effectiveFrom), cancel);

    public Task<WriteResult> ResetCalibrationAsync(CancellationToken cancel = default) => WriteAsync(new ResetCalibrationRequest(NextId()), cancel);
```

In `RunAsync`, pass the check's answer to `ServeAsync`:

```csharp
                await stream.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, stop).ConfigureAwait(false);
                failures = 0;
                await ServeAsync(stream, check.Refusal(stream.SafePipeHandle), stop).ConfigureAwait(false);
```

change `ServeAsync` to keep it with the connection:

```csharp
    /// <summary>One connection, from subscribing to its end. <paramref name="refusal"/> is the server check's answer.</summary>
    private async Task ServeAsync(Stream stream, string? refusal, CancellationToken stop)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(stop);
        await using var channel = new MessageChannel(stream);
        var reading = ReadAsync(channel, connection.Token);
        Task? reporting = null;
        try
        {
            if (await RequestAsync(channel, new SubscribeRequest(NextId()), connection.Token).ConfigureAwait(false) is not OkReply) return;
            _refusal = refusal;
            _channel = channel;
```

and add before `RequestAsync`:

```csharp
    /// <summary>A change: sent only while connected to a server that passed the check, and answered in words.</summary>
    private async Task<WriteResult> WriteAsync(PipeRequest request, CancellationToken cancel)
    {
        if (_channel is null) return WriteResult.NotConnected;
        if (_refusal is { } refusal) return new WriteResult(refusal);
        return await SendAsync(request, cancel).ConfigureAwait(false) switch
        {
            OkReply => WriteResult.Done,
            ErrorReply error => new WriteResult(error.Message),
            _ => WriteResult.NoAnswer,
        };
    }
```

In `src/PowerLedger.App/App.xaml.cs`, replace the link's construction with:

```csharp
        IServerCheck check = options.PipeName == PipeProtocol.PipeName ? InstalledServiceCheck.FromRegistry() : new TrustAnyServer();
        _link = new PipeServiceLink(options.PipeName, new LastInputIdleSource(), TimeProvider.System, check);
```

and add `using PowerLedger.Contracts;`.

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter ServiceLinkTests`
Expected: all pass, 4 of them new.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.App tests/PowerLedger.App.Tests
git commit -m "Send settings, tariffs and calibration resets to a service that passed the check"
```

---

### Task 3: Tariffs and the detected hardware from history

**Files:**
- Create: `src/PowerLedger.App/History/MachineHistory.cs`
- Modify: `src/PowerLedger.App/History/HistoryReader.cs`
- Test: `tests/PowerLedger.App.Tests/MachineHistoryTests.cs`
- Create: `tests/PowerLedger.App.Tests/FakeMachineHistory.cs`

Settings lists every tariff, oldest first. The wizard shows what the service detected (spec §5): the chassis, processor and graphics names, memory, drives, panel size and the displays attached. Both come from the database the App already reads. A detection whose JSON cannot be read is treated as none: the wizard then asks without showing what was found.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.App.Tests/FakeMachineHistory.cs`
```csharp
using PowerLedger.Contracts;
using PowerLedger.Core;

namespace PowerLedger.App.Tests;

/// <summary>Tariffs and a detection as the test sets them.</summary>
internal sealed class FakeMachineHistory : IMachineHistory
{
    public List<Tariff>? TariffList { get; set; } = [new Tariff(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), 0.17m, "USD")];

    public DetectedHardware? Hardware { get; set; } = new(
        ChassisKind.Laptop, "11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", "NVIDIA GeForce MX330", 2, false, 1, 0, 15.3, 1);

    public IReadOnlyList<Tariff>? Tariffs() => TariffList;

    public DetectedHardware? Detected() => Hardware;
}
```

`tests/PowerLedger.App.Tests/MachineHistoryTests.cs`
```csharp
using System.IO;
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class MachineHistoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 14, 30, 0, TimeSpan.Zero);
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"powerledger-machine-{Guid.NewGuid():N}.db");
    private readonly SqliteDatabase _writer;

    public MachineHistoryTests() => _writer = SqliteDatabase.OpenAndMigrate(_path);

    public void Dispose()
    {
        _writer.Dispose();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Tariffs_come_back_oldest_first()
    {
        var tariffs = new TariffRepository(_writer);
        tariffs.Add(new Tariff(Now.AddDays(-10), 0.20m, "EUR"));
        tariffs.Add(new Tariff(Now.AddDays(-40), 0.17m, "EUR"));

        using var readOnly = new SqliteDatabase(_path, readOnly: true);
        new HistoryReader(readOnly).Tariffs().ShouldNotBeNull().Select(t => t.PricePerKwh).ShouldBe(new[] { 0.17m, 0.20m });
    }

    [Fact]
    public void The_newest_detection_is_read_with_every_field()
    {
        new InventoryRepository(_writer).Upsert(new InventoryRecord("hash", Now,
            """{"Chassis":1,"CpuName":"Intel Core i7-1165G7","GpuName":"NVIDIA GeForce MX330","RamSticks":2,"RamIsDdr5":false,"SsdCount":1,"HddCount":0,"DisplayDiagonalInches":15.3,"MonitorCount":1,"Hash":"x"}"""));

        using var readOnly = new SqliteDatabase(_path, readOnly: true);
        new HistoryReader(readOnly).Detected().ShouldBe(new DetectedHardware(ChassisKind.Laptop, "Intel Core i7-1165G7", "NVIDIA GeForce MX330", 2, false, 1, 0, 15.3, 1));
    }

    [Fact]
    public void A_damaged_detection_or_none_reads_as_nothing()
    {
        using var readOnly = new SqliteDatabase(_path, readOnly: true);
        var reader = new HistoryReader(readOnly);
        reader.Detected().ShouldBeNull();
        new InventoryRepository(_writer).Upsert(new InventoryRecord("hash", Now, "{ not json"));
        reader.Detected().ShouldBeNull();
    }

    [Fact]
    public void The_detection_reads_as_one_line()
        => new DetectedHardware(ChassisKind.Laptop, "11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", "NVIDIA GeForce MX330", 2, false, 1, 0, 15.3, 1)
            .Summary(System.Globalization.CultureInfo.GetCultureInfo("en-US"))
            .ShouldBe("Laptop · Core i7-1165G7 · GeForce MX330 · 2 × DDR4 · 1 SSD · 15.3 in panel · 1 display");
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.App.Tests --filter MachineHistoryTests`
Expected: build error, `IMachineHistory` and `DetectedHardware` not found.

- [x] **Step 3: Read them**

`src/PowerLedger.App/History/MachineHistory.cs`
```csharp
using System.Globalization;
using System.Text.Json;
using PowerLedger.Contracts;
using PowerLedger.Core;

namespace PowerLedger.App;

/// <summary>What the service detected about this machine (spec §5), as the wizard and Settings show it.</summary>
internal sealed record DetectedHardware(
    ChassisKind Chassis, string? CpuName, string? GpuName, int RamSticks, bool RamIsDdr5, int SsdCount, int HddCount,
    double DisplayDiagonalInches, int MonitorCount)
{
    /// <summary>"Laptop · Core i7-1165G7 · GeForce MX330 · 2 × DDR4 · 1 SSD · 15.3 in panel · 1 display".</summary>
    public string Summary(CultureInfo culture)
    {
        var parts = new List<string?>
        {
            Chassis == ChassisKind.Laptop ? "Laptop" : "Desktop",
            NowViewModel.ShortName(CpuName),
            NowViewModel.ShortName(GpuName),
            $"{RamSticks.ToString(culture)} × {(RamIsDdr5 ? "DDR5" : "DDR4")}",
            SsdCount > 0 ? $"{SsdCount.ToString(culture)} SSD" : null,
            HddCount > 0 ? $"{HddCount.ToString(culture)} HDD" : null,
            DisplayDiagonalInches > 0 ? $"{DisplayDiagonalInches.ToString("0.#", culture)} in panel" : null,
            $"{MonitorCount.ToString(culture)} {(MonitorCount == 1 ? "display" : "displays")}",
        };
        return string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
    }

    /// <summary>The service's detection as stored, or null when it cannot be read.</summary>
    internal static DetectedHardware? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            int Int(string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
            return new DetectedHardware(
                Int("Chassis") == (int)ChassisKind.Desktop ? ChassisKind.Desktop : ChassisKind.Laptop,
                root.TryGetProperty("CpuName", out var cpu) && cpu.ValueKind == JsonValueKind.String ? cpu.GetString() : null,
                root.TryGetProperty("GpuName", out var gpu) && gpu.ValueKind == JsonValueKind.String ? gpu.GetString() : null,
                Int("RamSticks"),
                root.TryGetProperty("RamIsDdr5", out var ddr5) && ddr5.ValueKind == JsonValueKind.True,
                Int("SsdCount"),
                Int("HddCount"),
                root.TryGetProperty("DisplayDiagonalInches", out var inches) && inches.TryGetDouble(out var size) ? size : 0,
                Int("MonitorCount"));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The machine's tariffs and detection, read-only from the service's database.</summary>
internal interface IMachineHistory
{
    /// <summary>Every tariff, oldest first; null when the database cannot be read.</summary>
    IReadOnlyList<Tariff>? Tariffs();

    /// <summary>The newest detection; null when there is none yet, or it cannot be read.</summary>
    DetectedHardware? Detected();
}
```

In `src/PowerLedger.App/History/HistoryReader.cs`, change the declaration to:

```csharp
internal sealed class HistoryReader(SqliteDatabase database) : IHistory, IRangeHistory, IMachineHistory
```

and add before `LocalMidnight`:

```csharp
    public IReadOnlyList<Tariff>? Tariffs()
    {
        try
        {
            return new TariffRepository(database).All();
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public DetectedHardware? Detected()
    {
        try
        {
            return new InventoryRepository(database).Latest() is { } record ? DetectedHardware.Parse(record.Json) : null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter MachineHistoryTests`
Expected: `Passed! - Failed: 0, Passed: 4`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.App/History tests/PowerLedger.App.Tests/MachineHistoryTests.cs tests/PowerLedger.App.Tests/FakeMachineHistory.cs
git commit -m "Read the tariffs and the detected hardware for Settings and the wizard"
```

---

### Task 4: Preferences that apply at once

**Files:**
- Modify: `src/PowerLedger.App/Preferences/UiPreferences.cs`
- Create: `src/PowerLedger.App/Preferences/AppPreferences.cs`
- Modify: `src/PowerLedger.App/Now/NowViewModel.cs`, `src/PowerLedger.App/Report/ReportViewModel.cs`, `src/PowerLedger.App/Report/MonthlyReports.cs`
- Test: `tests/PowerLedger.App.Tests/AppPreferencesTests.cs`
- Create: `tests/PowerLedger.App.Tests/FakeUiSettings.cs`
- Modify: `tests/PowerLedger.App.Tests/UiPreferencesTests.cs`, `NowViewModelTests.cs`, `ReportViewModelTests.cs`

`IUiSettings` is how Settings and the wizard change what only the App cares about: the theme, the CO₂ factor, starting with Windows, and whether the first run is done. `AppPreferences` applies each change at once and saves `ui.json`. It is handed the theme and CO₂ actions, so it needs no WPF and a test can watch them. A factor outside what a real grid has is refused with a sentence and not applied.

The screens that show CO₂ take the factor at construction, so each gets a setter. The Now screen redraws today's ledger, the Report screen reads again, and the monthly job reads the factor at each check.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.App.Tests/FakeUiSettings.cs`
```csharp
namespace PowerLedger.App.Tests;

/// <summary>The App's preferences held in memory, recording each change.</summary>
internal sealed class FakeUiSettings : IUiSettings
{
    public UiPreferences Current { get; private set; } = UiPreferences.Default;

    public bool StartsWithWindows { get; private set; } = true;

    public List<string> Changes { get; } = [];

    public string? Choose(ThemeChoice theme)
    {
        Current = Current with { Theme = theme };
        Changes.Add($"theme {theme}");
        return null;
    }

    public string? UseCo2(double kgPerKwh)
    {
        if (!(kgPerKwh >= 0 && kgPerKwh < UiPreferences.MaxCo2KgPerKwh)) return "refused";
        Current = Current with { Co2KgPerKwh = kgPerKwh };
        Changes.Add($"co2 {kgPerKwh}");
        return null;
    }

    public string? StartWithWindows(bool enabled)
    {
        StartsWithWindows = enabled;
        Changes.Add($"autostart {enabled}");
        return null;
    }

    public string? FinishFirstRun()
    {
        Current = Current with { FirstRunDone = true };
        Changes.Add("first run done");
        return null;
    }
}
```

`tests/PowerLedger.App.Tests/AppPreferencesTests.cs`
```csharp
using System.IO;
using Microsoft.Win32;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class AppPreferencesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"powerledger-prefs-{Guid.NewGuid():N}");
    private readonly string _runKey = $@"Software\PowerLedgerTests\{Guid.NewGuid():N}";
    private readonly List<ThemeChoice> _themes = [];
    private readonly List<double> _factors = [];

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        Registry.CurrentUser.DeleteSubKeyTree(_runKey, throwOnMissingSubKey: false);
    }

    private UiPreferencesStore Store => new(Path.Combine(_folder, "ui.json"));

    private AppPreferences Preferences() => new(
        Store, UiPreferences.Default, _themes.Add, _factors.Add, new StartWithWindows(@"C:\Program Files\PowerLedger\PowerLedger.exe", _runKey));

    [Fact]
    public void A_theme_applies_at_once_and_is_saved()
    {
        var preferences = Preferences();
        preferences.Choose(ThemeChoice.Light).ShouldBeNull();

        _themes.ShouldBe(new[] { ThemeChoice.Light });
        preferences.Current.Theme.ShouldBe(ThemeChoice.Light);
        Store.Load().Theme.ShouldBe(ThemeChoice.Light);
    }

    [Fact]
    public void A_co2_factor_reaches_the_screens_and_one_out_of_range_is_refused()
    {
        var preferences = Preferences();
        preferences.UseCo2(0.23).ShouldBeNull();
        _factors.ShouldBe(new[] { 0.23 });
        Store.Load().Co2KgPerKwh.ShouldBe(0.23);

        preferences.UseCo2(5).ShouldBe("A grid's intensity is between 0 and 2 kg of CO₂ per kWh.");
        preferences.UseCo2(double.NaN).ShouldNotBeNull();
        _factors.Count.ShouldBe(1);
        preferences.Current.Co2KgPerKwh.ShouldBe(0.23);
    }

    [Fact]
    public void Starting_with_windows_and_the_first_run_are_remembered()
    {
        var preferences = Preferences();
        preferences.StartWithWindows(true).ShouldBeNull();
        preferences.StartsWithWindows.ShouldBeTrue();
        preferences.StartWithWindows(false).ShouldBeNull();
        preferences.StartsWithWindows.ShouldBeFalse();

        preferences.FinishFirstRun().ShouldBeNull();
        Store.Load().FirstRunDone.ShouldBeTrue();
    }

    [Fact]
    public void A_preference_that_cannot_be_saved_says_so_and_still_applies()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "ui.json.tmp"), "");
        using var blocker = File.Open(Path.Combine(_folder, "ui.json.tmp"), FileMode.Open, FileAccess.Read, FileShare.None);

        var preferences = Preferences();
        preferences.Choose(ThemeChoice.Dark).ShouldStartWith("Couldn't save your preferences:");
        _themes.ShouldBe(new[] { ThemeChoice.Dark });
    }
}
```

Append to `tests/PowerLedger.App.Tests/UiPreferencesTests.cs`, inside the class:

```csharp
    [Fact]
    public void The_first_run_is_remembered()
    {
        var store = new UiPreferencesStore(File);
        store.Load().FirstRunDone.ShouldBeFalse();
        store.Save(UiPreferences.Default with { FirstRunDone = true });
        store.Load().FirstRunDone.ShouldBeTrue();
    }
```

Append to `tests/PowerLedger.App.Tests/NowViewModelTests.cs`, inside the class:

```csharp
    [Fact]
    public void A_new_co2_factor_redraws_todays_ledger()
    {
        var model = Model();
        _history.Snapshot = Snapshots.Typical(Now);
        model.RefreshHistory();
        model.Today.Co2.ShouldBe("0.11");                                     // 0.284 kWh at 0.38

        model.Co2KgPerKwh = 0.8;
        model.Today.Co2.ShouldBe("0.23");
        model.Today.Co2Factor.ShouldBe("0.80 kg / kWh grid");
    }
```

Append to `tests/PowerLedger.App.Tests/ReportViewModelTests.cs`, inside the class:

```csharp
    [Fact]
    public void A_new_co2_factor_reads_the_report_again()
    {
        var model = Model();
        model.Show();
        model.Data.Co2.ShouldBe("1.04 kg");

        model.Co2KgPerKwh = 0.2;
        model.Data.Co2.ShouldBe("0.55 kg");
    }
```

Append to `tests/PowerLedger.App.Tests/MonthlyReportsTests.cs`, inside the class:

```csharp
    [Fact]
    public void The_co2_factor_can_change_between_checks()
    {
        using var job = Job();
        job.Co2KgPerKwh = 0.9;
        job.Check().First().Data.Co2Note.ShouldBe("at 0.90 kg / kWh");
    }
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet build tests/PowerLedger.App.Tests`
Expected: build errors, `IUiSettings`, `AppPreferences`, `FirstRunDone` and the `Co2KgPerKwh` setters not found.

- [x] **Step 3: Make the preferences live**

In `src/PowerLedger.App/Preferences/UiPreferences.cs`, add after `Co2KgPerKwh`:

```csharp
    /// <summary>The first-run wizard was finished once; it shows until then (spec §9).</summary>
    public bool FirstRunDone { get; init; }
```

`src/PowerLedger.App/Preferences/AppPreferences.cs`
```csharp
using System.IO;

namespace PowerLedger.App;

/// <summary>What only the App cares about, as Settings and the wizard change it. Each change applies at once and is saved
/// to ui.json; a method returns null when all went well, and otherwise says what went wrong.</summary>
internal interface IUiSettings
{
    UiPreferences Current { get; }

    bool StartsWithWindows { get; }

    string? Choose(ThemeChoice theme);

    string? UseCo2(double kgPerKwh);

    string? StartWithWindows(bool enabled);

    string? FinishFirstRun();
}

/// <summary>
/// The App's live preferences. A theme goes to <paramref name="applyTheme"/>, a CO₂ factor to <paramref name="applyCo2"/>
/// (the screens that show CO₂), and starting with Windows to the Run entry; each is then saved. A change applies even
/// when ui.json cannot be written, and the answer says so.
/// </summary>
internal sealed class AppPreferences(
    UiPreferencesStore store, UiPreferences initial, Action<ThemeChoice> applyTheme, Action<double> applyCo2, StartWithWindows autostart) : IUiSettings
{
    public UiPreferences Current { get; private set; } = initial;

    public bool StartsWithWindows => autostart.IsEnabled;

    public string? Choose(ThemeChoice theme)
    {
        applyTheme(theme);
        return Save(Current with { Theme = theme });
    }

    public string? UseCo2(double kgPerKwh)
    {
        if (!(double.IsFinite(kgPerKwh) && kgPerKwh >= 0 && kgPerKwh < UiPreferences.MaxCo2KgPerKwh))
            return $"A grid's intensity is between 0 and {UiPreferences.MaxCo2KgPerKwh:0} kg of CO₂ per kWh.";
        applyCo2(kgPerKwh);
        return Save(Current with { Co2KgPerKwh = kgPerKwh });
    }

    public string? StartWithWindows(bool enabled)
    {
        try
        {
            autostart.Set(enabled);
            return null;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return "Couldn't change starting with Windows: " + error.Message;
        }
    }

    public string? FinishFirstRun() => Save(Current with { FirstRunDone = true });

    private string? Save(UiPreferences next)
    {
        Current = next;
        try
        {
            store.Save(next);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "Couldn't save your preferences: " + error.Message;
        }
    }
}
```

In `src/PowerLedger.App/Now/NowViewModel.cs`, change `private readonly double _co2KgPerKwh;` to `private double _co2KgPerKwh;`, and add after `Legend`:

```csharp
    /// <summary>Kilograms of CO₂ per kWh for today's ledger; a new factor redraws it (Settings, spec §9).</summary>
    public double Co2KgPerKwh
    {
        get => _co2KgPerKwh;
        set
        {
            _co2KgPerKwh = value;
            if (_snapshot is not null) Today = TodayOf(_snapshot, TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _zone));
        }
    }
```

In `src/PowerLedger.App/Report/ReportViewModel.cs`, change `private readonly double _co2KgPerKwh;` to `private double _co2KgPerKwh;`, and add after `ExportCsv`:

```csharp
    /// <summary>Kilograms of CO₂ per kWh; a new factor reads the report again.</summary>
    public double Co2KgPerKwh
    {
        get => _co2KgPerKwh;
        set
        {
            _co2KgPerKwh = value;
            if (_range is not null) Refresh();
        }
    }
```

In `src/PowerLedger.App/Report/MonthlyReports.cs`, remove the field `_co2KgPerKwh`, set `Co2KgPerKwh = co2KgPerKwh;` in the constructor instead of assigning the field, use `Co2KgPerKwh` where `ReportData.From` is called, and add after `FileName`:

```csharp
    /// <summary>Kilograms of CO₂ per kWh, read at each check, so a change in Settings reaches the next report.</summary>
    public double Co2KgPerKwh { get; set; }
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter "AppPreferencesTests|UiPreferencesTests|NowViewModelTests|ReportViewModelTests|MonthlyReportsTests"`
Expected: all pass.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.App tests/PowerLedger.App.Tests
git commit -m "Apply the theme, the CO2 factor and starting with Windows at once, and remember the first run"
```

---

### Task 5: The tariff form and the service form

**Files:**
- Create: `src/PowerLedger.App/Settings/TariffForm.cs`
- Create: `src/PowerLedger.App/Settings/ServiceForm.cs`
- Test: `tests/PowerLedger.App.Tests/TariffFormTests.cs`
- Test: `tests/PowerLedger.App.Tests/ServiceFormTests.cs`

Both Settings and the wizard need the same two forms, so they are their own view models.

`TariffForm` takes a price per kWh, a currency and the first day it applies. The price is read in the user's culture, or with a point, but never with thousands separators, so "0.17" cannot become 17 in a culture that uses the point to group thousands. The currency starts as the region's. The tariff applies from that day's local midnight, and energy already recorded is repriced from then (spec §7).

`ServiceForm` holds everything the service's settings carry. It fills from what the service sends, shows the idle threshold in minutes as spec §6 does, and sends the settings whole. Each number it cannot read is named, and then the service's own checks (`ServiceSettings.Validate`) run before anything is sent. A form the service never filled cannot be saved, so a stopped service is never sent defaults.

Both forms return whether the save worked, so the wizard can wait for it. Messages reach the UI thread through `UiThreads`.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.App.Tests/TariffFormTests.cs`
```csharp
using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.App.Tests;

public class TariffFormTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 14, 30, 0, TimeSpan.Zero);
    private readonly FakeLink _link = new();
    private readonly FakeTimeProvider _clock = new(Now);

    public TariffFormTests() => _link.Connect(true);

    private TariffForm Form(string culture = "en-US") => new(_link, UiThreads.Inline, _clock, TimeZoneInfo.Utc, CultureInfo.GetCultureInfo(culture), "USD");

    private (decimal Price, string Currency, DateTimeOffset? From) Sent => ((decimal, string, DateTimeOffset?))_link.Writes.Single();

    [Fact]
    public async Task A_price_goes_to_the_service_from_its_days_midnight()
    {
        var form = Form();
        form.Currency.ShouldBe("USD");
        form.From.ShouldBe(new DateTime(2026, 9, 15));
        form.Price = "0.17";
        form.From = new DateTime(2026, 9, 1);

        (await form.SaveAsync()).ShouldBeTrue();

        Sent.ShouldBe((0.17m, "USD", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)));
        form.Message.ShouldBe("Saved: $0.17 / kWh from 1 Sep 2026.");
    }

    [Theory]
    [InlineData("de-DE", "0,17")]
    [InlineData("de-DE", "0.17")]
    [InlineData("en-US", " 0.17 ")]
    public async Task A_price_reads_in_the_users_culture_or_with_a_point(string culture, string typed)
    {
        var form = Form(culture);
        form.Price = typed;
        (await form.SaveAsync()).ShouldBeTrue();
        Sent.Price.ShouldBe(0.17m);
    }

    [Theory]
    [InlineData("abc", "EUR", "Type the price per kWh as a number, like 0.17.")]
    [InlineData("-1", "EUR", "Type the price per kWh as a number, like 0.17.")]
    [InlineData("2000000", "EUR", "The price per kWh must be between 0 and 1,000,000.")]
    [InlineData("0.17", "EU", "The currency is a three-letter code, like USD or EUR.")]
    [InlineData("0.17", "E1R", "The currency is a three-letter code, like USD or EUR.")]
    public async Task What_cannot_be_sent_is_said_and_nothing_is_sent(string price, string currency, string message)
    {
        var form = Form();
        form.Price = price;
        form.Currency = currency;
        (await form.SaveAsync()).ShouldBeFalse();
        form.Message.ShouldBe(message);
        _link.Writes.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_currency_is_sent_in_capitals()
    {
        var form = Form();
        form.Price = "0.25";
        form.Currency = " eur ";
        await form.SaveAsync();
        Sent.Currency.ShouldBe("EUR");
    }

    [Fact]
    public async Task The_services_refusal_is_shown()
    {
        _link.Answer = new WriteResult("A tariff can start at any time from 2000 until tomorrow.");
        var form = Form();
        form.Price = "0.17";
        (await form.SaveAsync()).ShouldBeFalse();
        form.Message.ShouldBe("A tariff can start at any time from 2000 until tomorrow.");
    }

    [Fact]
    public async Task A_saved_tariff_is_announced()
    {
        var form = Form();
        var saved = 0;
        form.Saved += () => saved++;
        form.Price = "0.17";
        await form.SaveAsync();
        saved.ShouldBe(1);
        form.IsEmpty.ShouldBeFalse();
    }
}
```

`tests/PowerLedger.App.Tests/ServiceFormTests.cs`
```csharp
using System.Globalization;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

public class ServiceFormTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeLink _link = new();

    public ServiceFormTests() => _link.Connect(true);

    private ServiceForm Form()
    {
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default);
        return form;
    }

    [Fact]
    public void Loading_fills_every_field()
    {
        var form = Form();
        form.IsLoaded.ShouldBeTrue();
        form.Chassis.ShouldBe(ChassisKind.Laptop);
        form.RamSticks.ShouldBe("1");
        form.PanelInches.ShouldBe("15.6");
        form.MonitorWatts.ShouldBe("25");
        form.CpuTdp.ShouldBe("");
        form.IdleMinutes.ShouldBe("5");
        form.SampleInterval.ShouldBe("1");
        form.RawHours.ShouldBe("48");
        form.HistoryYears.ShouldBe("2");
    }

    [Fact]
    public async Task What_is_typed_goes_to_the_service_whole()
    {
        var form = Form();
        form.Chassis = ChassisKind.Desktop;
        form.PsuTier = PsuTier.Gold;
        form.RamSticks = "4";
        form.RamIsDdr5 = true;
        form.SsdCount = "2";
        form.HddCount = "1";
        form.FanCount = "5";
        form.PanelInches = "0";
        form.ExternalMonitors = "2";
        form.IncludeMonitors = true;
        form.MonitorWatts = "30";
        form.ExtrasWatts = "12.5";
        form.CpuTdp = "125";
        form.GpuTdp = " ";
        form.IdleMinutes = "10";
        form.SampleInterval = "2";
        form.RawHours = "72";
        form.HistoryYears = "5";

        (await form.SaveAsync()).ShouldBeTrue();

        var sent = (ServiceSettings)_link.Writes.Single();
        sent.Profile.ShouldBe(new MachineProfile
        {
            Chassis = ChassisKind.Desktop, PsuTier = PsuTier.Gold, RamSticks = 4, RamIsDdr5 = true, SsdCount = 2, HddCount = 1,
            FanCount = 5, DisplayDiagonalInches = 0, ExternalMonitors = 2, IncludeMonitors = true, MonitorWatts = 30, ExtrasWatts = 12.5,
            CpuTdpOverrideW = 125, GpuTdpOverrideW = null,
        });
        (sent.IdleThresholdSeconds, sent.SampleIntervalSeconds, sent.RawRetentionHours, sent.HistoryRetentionYears).ShouldBe((600, 2, 72, 5));
        form.Message.ShouldBe("Saved.");
    }

    [Theory]
    [InlineData("RamSticks", "two", "Type the memory sticks as a whole number.")]
    [InlineData("MonitorWatts", "lots", "Type a monitor's watts as a number.")]
    [InlineData("IdleMinutes", "45", "The idle threshold is between 1 and 30 minutes.")]
    [InlineData("RawHours", "12", "Second-by-second history must be kept between 24 and 168 hours.")]
    [InlineData("PanelInches", "80", "The panel size must be 0 for none, or between 7 and 50 inches.")]
    public async Task What_cannot_be_sent_is_said(string field, string typed, string message)
    {
        var form = Form();
        typeof(ServiceForm).GetProperty(field)!.SetValue(form, typed);
        (await form.SaveAsync()).ShouldBeFalse();
        form.Message.ShouldBe(message);
        _link.Writes.ShouldBeEmpty();
    }

    [Fact]
    public void A_form_the_service_never_filled_cannot_be_saved()
    {
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.IsLoaded.ShouldBeFalse();
        form.Save.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void Choosing_desktop_shows_the_power_supply()
    {
        var form = Form();
        form.IsDesktop.ShouldBeFalse();
        form.Chassis = ChassisKind.Desktop;
        form.IsDesktop.ShouldBeTrue();
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.App.Tests --filter "TariffFormTests|ServiceFormTests"`
Expected: build error, `TariffForm` and `ServiceForm` not found.

- [x] **Step 3: Write the forms**

`src/PowerLedger.App/Settings/TariffForm.cs`
```csharp
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PowerLedger.App;

/// <summary>
/// A new tariff (spec §8, §9): the price per kWh, its ISO 4217 currency, and the first day it applies, from that day's
/// local midnight. The day may be in the past; energy already recorded is repriced from then (spec §7). What was typed is
/// checked before anything is sent, and the form says what came of it.
/// </summary>
internal sealed class TariffForm : ObservableObject
{
    private const decimal MaxPrice = 1_000_000m;

    private readonly IServiceLink _link;
    private readonly UiThreads _threads;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly CultureInfo _culture;
    private string _price = "";
    private string _currency;
    private DateTime? _from;
    private string? _message;

    public TariffForm(IServiceLink link, UiThreads threads, TimeProvider clock, TimeZoneInfo zone, CultureInfo culture, string regionCurrency)
    {
        _link = link;
        _threads = threads;
        _clock = clock;
        _zone = zone;
        _culture = culture;
        _currency = regionCurrency;
        _from = Ranges.LocalDay(clock.GetUtcNow(), zone).ToDateTime(TimeOnly.MinValue);
        Save = new RelayCommand(() => _ = SaveAsync());
    }

    /// <summary>Raised on the UI thread once the service has taken a tariff.</summary>
    public event Action? Saved;

    public string Price
    {
        get => _price;
        set
        {
            if (SetProperty(ref _price, value)) OnPropertyChanged(nameof(IsEmpty));
        }
    }

    public string Currency { get => _currency; set => SetProperty(ref _currency, value); }

    /// <summary>The first day the price applies.</summary>
    public DateTime? From { get => _from; set => SetProperty(ref _from, value); }

    /// <summary>What came of the last save, or why it could not be sent.</summary>
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }

    /// <summary>Nothing typed: the wizard takes that as "later".</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Price);

    public ICommand Save { get; }

    /// <summary>Sends the tariff typed, and says whether the service took it.</summary>
    public async Task<bool> SaveAsync()
    {
        if (Read(out var problem) is not { } tariff)
        {
            Message = problem;
            return false;
        }
        Message = "Saving…";
        var result = await _link.SetTariffAsync(tariff.Price, tariff.Currency, tariff.From).ConfigureAwait(false);
        _threads.Post(() =>
        {
            Message = result.Succeeded
                ? $"Saved: {Money.Rate(tariff.Price, tariff.Currency, _culture)} / kWh from {tariff.From.ToString("d MMM yyyy", _culture)}."
                : result.Problem;
            if (result.Succeeded) Saved?.Invoke();
        });
        return result.Succeeded;
    }

    /// <summary>The tariff typed, or null and why it can't be sent.</summary>
    internal (decimal Price, string Currency, DateTimeOffset From)? Read(out string? problem)
    {
        const NumberStyles plain = NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowDecimalPoint;
        if (!decimal.TryParse(Price, plain, _culture, out var price) && !decimal.TryParse(Price, plain, CultureInfo.InvariantCulture, out price))
        {
            problem = "Type the price per kWh as a number, like 0.17.";
            return null;
        }
        if (price > MaxPrice)
        {
            problem = "The price per kWh must be between 0 and 1,000,000.";
            return null;
        }
        var code = Currency.Trim().ToUpperInvariant();
        if (code.Length != 3 || !code.All(c => c is >= 'A' and <= 'Z'))
        {
            problem = "The currency is a three-letter code, like USD or EUR.";
            return null;
        }
        var day = From is { } chosen ? DateOnly.FromDateTime(chosen) : Ranges.LocalDay(_clock.GetUtcNow(), _zone);
        problem = null;
        return (price, code, Ranges.Midnight(day, _zone));
    }
}
```

`src/PowerLedger.App/Settings/ServiceForm.cs`
```csharp
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>
/// What the service is told (spec §8): the machine profile as detected and corrected, the idle threshold in minutes, the
/// sample interval, and how long history is kept. Numbers are typed in the user's culture. Each one that can't be read is
/// named; then the service's own checks run before the settings are sent, whole. A form the service never filled can't
/// be saved, so a stopped service is never sent defaults.
/// </summary>
internal sealed class ServiceForm : ObservableObject
{
    private readonly IServiceLink _link;
    private readonly UiThreads _threads;
    private readonly CultureInfo _culture;
    private readonly RelayCommand _save;
    private bool _isLoaded;
    private ChassisKind _chassis = MachineProfile.DefaultLaptop.Chassis;
    private PsuTier _psuTier = MachineProfile.DefaultLaptop.PsuTier;
    private string _ramSticks = "";
    private bool _ramIsDdr5;
    private string _ssdCount = "";
    private string _hddCount = "";
    private string _fanCount = "";
    private string _panelInches = "";
    private string _externalMonitors = "";
    private bool _includeMonitors;
    private string _monitorWatts = "";
    private string _extrasWatts = "";
    private string _cpuTdp = "";
    private string _gpuTdp = "";
    private string _idleMinutes = "";
    private string _sampleInterval = "1";
    private string _rawHours = "";
    private string _historyYears = "";
    private string? _message;

    public ServiceForm(IServiceLink link, UiThreads threads, CultureInfo culture)
    {
        _link = link;
        _threads = threads;
        _culture = culture;
        _save = new RelayCommand(() => _ = SaveAsync(), () => IsLoaded);
    }

    /// <summary>Raised on the UI thread once the service has taken the settings.</summary>
    public event Action? Saved;

    /// <summary>The service has sent its settings, so the form holds real values.</summary>
    public bool IsLoaded
    {
        get => _isLoaded;
        private set
        {
            if (SetProperty(ref _isLoaded, value)) _save.NotifyCanExecuteChanged();
        }
    }

    public ChassisKind Chassis
    {
        get => _chassis;
        set
        {
            if (SetProperty(ref _chassis, value)) OnPropertyChanged(nameof(IsDesktop));
        }
    }

    /// <summary>A desktop's power supply matters; a laptop's adapter is modelled instead.</summary>
    public bool IsDesktop => Chassis == ChassisKind.Desktop;

    public PsuTier PsuTier { get => _psuTier; set => SetProperty(ref _psuTier, value); }

    public string RamSticks { get => _ramSticks; set => SetProperty(ref _ramSticks, value); }

    public bool RamIsDdr5 { get => _ramIsDdr5; set => SetProperty(ref _ramIsDdr5, value); }

    public string SsdCount { get => _ssdCount; set => SetProperty(ref _ssdCount, value); }

    public string HddCount { get => _hddCount; set => SetProperty(ref _hddCount, value); }

    public string FanCount { get => _fanCount; set => SetProperty(ref _fanCount, value); }

    /// <summary>The built-in panel's diagonal in inches; 0 for none.</summary>
    public string PanelInches { get => _panelInches; set => SetProperty(ref _panelInches, value); }

    public string ExternalMonitors { get => _externalMonitors; set => SetProperty(ref _externalMonitors, value); }

    /// <summary>External monitors count only when the user says so: they have their own plugs.</summary>
    public bool IncludeMonitors { get => _includeMonitors; set => SetProperty(ref _includeMonitors, value); }

    public string MonitorWatts { get => _monitorWatts; set => SetProperty(ref _monitorWatts, value); }

    public string ExtrasWatts { get => _extrasWatts; set => SetProperty(ref _extrasWatts, value); }

    /// <summary>The processor's rated watts; blank uses the bundled table.</summary>
    public string CpuTdp { get => _cpuTdp; set => SetProperty(ref _cpuTdp, value); }

    /// <summary>The graphics card's rated watts; blank uses the bundled table.</summary>
    public string GpuTdp { get => _gpuTdp; set => SetProperty(ref _gpuTdp, value); }

    public string IdleMinutes { get => _idleMinutes; set => SetProperty(ref _idleMinutes, value); }

    /// <summary>"1" to "5" seconds, chosen with segmented buttons.</summary>
    public string SampleInterval { get => _sampleInterval; set => SetProperty(ref _sampleInterval, value); }

    public string RawHours { get => _rawHours; set => SetProperty(ref _rawHours, value); }

    public string HistoryYears { get => _historyYears; set => SetProperty(ref _historyYears, value); }

    public string? Message { get => _message; private set => SetProperty(ref _message, value); }

    public ICommand Save => _save;

    /// <summary>Fills the form from the service's settings.</summary>
    public void Load(ServiceSettings settings)
    {
        var p = settings.Profile;
        Chassis = p.Chassis;
        PsuTier = p.PsuTier;
        RamSticks = Whole(p.RamSticks);
        RamIsDdr5 = p.RamIsDdr5;
        SsdCount = Whole(p.SsdCount);
        HddCount = Whole(p.HddCount);
        FanCount = Whole(p.FanCount);
        PanelInches = Number(p.DisplayDiagonalInches);
        ExternalMonitors = Whole(p.ExternalMonitors);
        IncludeMonitors = p.IncludeMonitors;
        MonitorWatts = Number(p.MonitorWatts);
        ExtrasWatts = Number(p.ExtrasWatts);
        CpuTdp = p.CpuTdpOverrideW is { } cpu ? Number(cpu) : "";
        GpuTdp = p.GpuTdpOverrideW is { } gpu ? Number(gpu) : "";
        IdleMinutes = Whole((int)Math.Round(settings.IdleThresholdSeconds / 60.0));
        SampleInterval = Whole(settings.SampleIntervalSeconds);
        RawHours = Whole(settings.RawRetentionHours);
        HistoryYears = Whole(settings.HistoryRetentionYears);
        IsLoaded = true;
    }

    /// <summary>Sends the settings typed, and says whether the service took them.</summary>
    public async Task<bool> SaveAsync()
    {
        if (Read(out var problem) is not { } settings)
        {
            Message = problem;
            return false;
        }
        Message = "Saving…";
        var result = await _link.SetSettingsAsync(settings).ConfigureAwait(false);
        _threads.Post(() =>
        {
            Message = result.Succeeded ? "Saved." : result.Problem;
            if (result.Succeeded) Saved?.Invoke();
        });
        return result.Succeeded;
    }

    /// <summary>The settings the form describes, or null and the first problem.</summary>
    internal ServiceSettings? Read(out string? problem)
    {
        problem = !IsLoaded ? "The service hasn't sent its settings yet." : null;
        int ramSticks = 0, ssds = 0, hdds = 0, fans = 0, monitors = 0, idle = 0, interval = 0, rawHours = 0, years = 0;
        double panel = 0, monitorWatts = 0, extras = 0;
        double? cpu = null, gpu = null;
        _ = problem is null
            && Int(RamSticks, "the memory sticks", out ramSticks, ref problem) && Int(SsdCount, "the SSDs", out ssds, ref problem)
            && Int(HddCount, "the hard drives", out hdds, ref problem) && Int(FanCount, "the fans", out fans, ref problem)
            && Double(PanelInches, "the panel size in inches", out panel, ref problem)
            && Int(ExternalMonitors, "the external monitors", out monitors, ref problem)
            && Double(MonitorWatts, "a monitor's watts", out monitorWatts, ref problem)
            && Double(ExtrasWatts, "the extras in watts", out extras, ref problem)
            && Optional(CpuTdp, "the processor's rated watts", out cpu, ref problem)
            && Optional(GpuTdp, "the graphics card's rated watts", out gpu, ref problem)
            && Int(IdleMinutes, "the idle threshold in minutes", out idle, ref problem)
            && Int(SampleInterval, "the sample interval", out interval, ref problem)
            && Int(RawHours, "the hours of second-by-second history", out rawHours, ref problem)
            && Int(HistoryYears, "the years of minute-by-minute history", out years, ref problem);
        if (problem is null && idle is < 1 or > 30) problem = "The idle threshold is between 1 and 30 minutes.";
        if (problem is not null) return null;

        var settings = new ServiceSettings
        {
            Profile = new MachineProfile
            {
                Chassis = Chassis, PsuTier = PsuTier, RamSticks = ramSticks, RamIsDdr5 = RamIsDdr5, SsdCount = ssds, HddCount = hdds,
                FanCount = fans, DisplayDiagonalInches = panel, ExternalMonitors = monitors, IncludeMonitors = IncludeMonitors,
                MonitorWatts = monitorWatts, ExtrasWatts = extras, CpuTdpOverrideW = cpu, GpuTdpOverrideW = gpu,
            },
            IdleThresholdSeconds = idle * 60,
            SampleIntervalSeconds = interval,
            RawRetentionHours = rawHours,
            HistoryRetentionYears = years,
        };
        problem = settings.Validate();
        return problem is null ? settings : null;
    }

    private bool Int(string text, string what, out int value, ref string? problem)
    {
        if (int.TryParse(text, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, _culture, out value)) return true;
        problem = $"Type {what} as a whole number.";
        return false;
    }

    private bool Double(string text, string what, out double value, ref string? problem)
    {
        const NumberStyles plain = NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowDecimalPoint;
        if (double.TryParse(text, plain, _culture, out value) || double.TryParse(text, plain, CultureInfo.InvariantCulture, out value)) return true;
        problem = $"Type {what} as a number.";
        return false;
    }

    private bool Optional(string text, string what, out double? value, ref string? problem)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!Double(text, what, out var number, ref problem)) return false;
        value = number;
        return true;
    }

    private string Whole(int value) => value.ToString(_culture);

    private string Number(double value) => value.ToString("0.##", _culture);
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter "TariffFormTests|ServiceFormTests"`
Expected: `Passed! - Failed: 0, Passed: 21`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.App/Settings tests/PowerLedger.App.Tests/TariffFormTests.cs tests/PowerLedger.App.Tests/ServiceFormTests.cs
git commit -m "Add the tariff and service forms that Settings and the wizard share"
```

---

### Task 6: The Settings view model

**Files:**
- Create: `src/PowerLedger.App/Settings/SettingsViewModel.cs`
- Test: `tests/PowerLedger.App.Tests/SettingsViewModelTests.cs`

The Settings screen (spec §9) holds:
- The tariff form above the tariff history, newest first.
- The service form, with what was detected.
- The calibration status, with a reset that asks first.
- The App's own preferences: the CO₂ factor, the theme and starting with Windows. The theme and the Run entry apply when chosen; the typed factor applies on Save.
- An About section: the service's version and readings, each sensor source's state, the database's size, and the fact that there is no driver.

When it shows, it reads the service's settings and status, the tariffs and the detection, off the UI thread. After that it reads the status again every ten seconds while it shows. The service form is filled only when it shows and after a save, so a status refresh never overwrites what is being typed. Without the service, the App's own preferences still work, and the screen says so.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.App.Tests/SettingsViewModelTests.cs`
```csharp
using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.App.Tests;

public class SettingsViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 14, 30, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeLink _link = new();
    private readonly FakeMachineHistory _history = new();
    private readonly FakeUiSettings _ui = new();
    private readonly FakeTimeProvider _clock = new(Now);

    private SettingsViewModel Model() => new(_link, _history, _ui, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, "USD");

    [Fact]
    public void Showing_it_reads_the_service_the_tariffs_and_the_detection()
    {
        _link.Connect(true);
        var model = Model();
        model.Show();

        model.Service.IsLoaded.ShouldBeTrue();
        model.TariffHistory.ShouldBe(new[] { "From 1 Aug 2026 · $0.17 / kWh" });
        model.Detected.ShouldBe("Laptop · Core i7-1165G7 · GeForce MX330 · 2 × DDR4 · 1 SSD · 15.3 in panel · 1 display");
        model.Notice.ShouldBeNull();
        model.Calibration.ShouldBe("Learning on battery: 15m of 30m needed.");
        model.Sources.Select(s => s.Name).ShouldBe(new[] { "Processor energy meter", "Battery" });
        model.Sources[0].State.ShouldBe("working");
        model.ServiceState.ShouldBe("Service 0.1.0 · 10 readings since 1 Jan 1970 00:00");
        model.Database.ShouldBe("31 MB");
    }

    [Fact]
    public void Without_the_service_the_apps_own_preferences_still_work()
    {
        var model = Model();
        model.Show();

        model.Notice.ShouldBe("The service isn't running. Its settings appear when it starts; the App's own preferences below work now.");
        model.Service.IsLoaded.ShouldBeFalse();
        model.Theme = ThemeChoice.Dark;
        _ui.Changes.ShouldBe(new[] { "theme Dark" });
        model.Theme.ShouldBe(ThemeChoice.Dark);
    }

    [Fact]
    public void A_service_that_comes_up_while_settings_shows_fills_it()
    {
        var model = Model();
        model.Show();
        model.Notice.ShouldNotBeNull();

        _link.Connect(true);
        model.Service.IsLoaded.ShouldBeTrue();
        model.Notice.ShouldBeNull();
    }

    [Fact]
    public void A_co2_factor_is_read_in_the_users_culture_and_applied()
    {
        var model = Model();
        model.Co2.ShouldBe("0.40");

        model.Co2 = "0.23";
        model.SaveCo2.Execute(null);
        _ui.Changes.ShouldBe(new[] { "co2 0.23" });
        model.Co2Message.ShouldBe("Saved.");

        model.Co2 = "x";
        model.SaveCo2.Execute(null);
        model.Co2Message.ShouldBe("Type the kilograms of CO₂ per kWh as a number, like 0.40.");

        model.Co2 = "3";
        model.SaveCo2.Execute(null);
        model.Co2Message.ShouldBe("refused");
    }

    [Fact]
    public void Starting_with_windows_applies_when_ticked()
    {
        var model = Model();
        model.StartWithWindows.ShouldBeTrue();
        model.StartWithWindows = false;
        _ui.Changes.ShouldBe(new[] { "autostart False" });
        model.StartWithWindows.ShouldBeFalse();
    }

    [Fact]
    public async Task A_calibration_reset_asks_first()
    {
        _link.Connect(true);
        var model = Model();
        model.Show();

        model.ResetCalibration.Execute(null);
        model.ConfirmingReset.ShouldBeTrue();
        _link.Writes.ShouldBeEmpty();
        model.CancelReset.Execute(null);
        model.ConfirmingReset.ShouldBeFalse();

        model.ResetCalibration.Execute(null);
        await model.ConfirmResetAsync();
        _link.Writes.ShouldBe(new object[] { "reset" });
        model.ConfirmingReset.ShouldBeFalse();
        model.CalibrationMessage.ShouldBe("Calibration reset. The model learns again the next time the machine runs on battery.");
    }

    [Fact]
    public async Task A_saved_tariff_shows_in_the_history()
    {
        _link.Connect(true);
        var model = Model();
        model.Show();

        _history.TariffList!.Add(new Tariff(Now, 0.2m, "USD"));
        model.Tariff.Price = "0.20";
        await model.Tariff.SaveAsync();

        model.TariffHistory.First().ShouldBe("From 15 Sep 2026 · $0.20 / kWh");
    }

    [Fact]
    public void Run_setup_again_asks_the_shell()
    {
        var model = Model();
        var asked = 0;
        model.SetupRequested += () => asked++;
        model.RunSetup.Execute(null);
        asked.ShouldBe(1);
    }

    [Fact]
    public void The_status_is_read_again_every_ten_seconds_while_shown()
    {
        _link.Connect(true);
        var model = Model();
        model.Show();

        _link.Status = Statuses.Running() with { Ticks = 99 };
        _clock.Advance(SettingsViewModel.StatusEvery);
        model.ServiceState.ShouldContain("99 readings");

        model.Hide();
        _link.Status = Statuses.Running() with { Ticks = 5 };
        _clock.Advance(SettingsViewModel.StatusEvery * 2);
        model.ServiceState.ShouldContain("99 readings");
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.App.Tests --filter SettingsViewModelTests`
Expected: build error, `SettingsViewModel` not found.

- [x] **Step 3: Write the view model**

`src/PowerLedger.App/Settings/SettingsViewModel.cs`
```csharp
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLedger.Contracts;
using PowerLedger.Core;

namespace PowerLedger.App;

/// <summary>A sensor source as About lists it: what it is, whether it works here, and why not.</summary>
internal sealed record SourceLine(string Name, string State, string Detail);

/// <summary>
/// The Settings screen (spec §9): the tariff and its history, the machine profile with what was detected, sampling and
/// retention, calibration with a reset that asks first, the App's own preferences, and About. It reads when shown, and the
/// status again every ten seconds while shown, off the UI thread. The service form is filled when the screen shows and
/// after a save; when the service comes up while the screen shows, only an empty form is filled, so nothing typed is lost.
/// </summary>
internal sealed class SettingsViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan StatusEvery = TimeSpan.FromSeconds(10);

    private readonly IServiceLink _link;
    private readonly IMachineHistory _history;
    private readonly IUiSettings _ui;
    private readonly UiThreads _threads;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly CultureInfo _culture;
    private ITimer? _timer;
    private int _sampleSeconds = 1;
    private IReadOnlyList<string> _tariffHistory = [];
    private string _detected = "";
    private string _co2;
    private string? _co2Message;
    private string? _appMessage;
    private string _calibration = "";
    private bool _confirmingReset;
    private string? _calibrationMessage;
    private string _serviceState = "";
    private IReadOnlyList<SourceLine> _sources = [];
    private string _database = "";
    private string? _notice;

    public SettingsViewModel(
        IServiceLink link, IMachineHistory history, IUiSettings ui, UiThreads threads, TimeProvider clock, TimeZoneInfo zone,
        CultureInfo culture, string regionCurrency)
    {
        _link = link;
        _history = history;
        _ui = ui;
        _threads = threads;
        _clock = clock;
        _zone = zone;
        _culture = culture;
        _co2 = ui.Current.Co2KgPerKwh.ToString("0.00", culture);
        Tariff = new TariffForm(link, threads, clock, zone, culture, regionCurrency);
        Service = new ServiceForm(link, threads, culture);
        Tariff.Saved += ReadTariffs;
        Service.Saved += Show;
        SaveCo2 = new RelayCommand(ApplyCo2);
        ResetCalibration = new RelayCommand(() => ConfirmingReset = true);
        CancelReset = new RelayCommand(() => ConfirmingReset = false);
        ConfirmReset = new RelayCommand(() => _ = ConfirmResetAsync());
        RunSetup = new RelayCommand(() => SetupRequested?.Invoke());
        _link.ConnectionChanged += OnConnectionChanged;
    }

    /// <summary>"Run setup again": the shell shows the wizard.</summary>
    public event Action? SetupRequested;

    public TariffForm Tariff { get; }

    /// <summary>Every tariff, newest first: "From 1 Aug 2026 · $0.17 / kWh".</summary>
    public IReadOnlyList<string> TariffHistory { get => _tariffHistory; private set => SetProperty(ref _tariffHistory, value); }

    public ServiceForm Service { get; }

    /// <summary>What the service detected, in one line.</summary>
    public string Detected { get => _detected; private set => SetProperty(ref _detected, value); }

    /// <summary>Kilograms of CO₂ per kWh, as typed.</summary>
    public string Co2 { get => _co2; set => SetProperty(ref _co2, value); }

    public string? Co2Message { get => _co2Message; private set => SetProperty(ref _co2Message, value); }

    public ICommand SaveCo2 { get; }

    /// <summary>Applies when chosen.</summary>
    public ThemeChoice Theme
    {
        get => _ui.Current.Theme;
        set
        {
            if (value == _ui.Current.Theme) return;
            AppMessage = _ui.Choose(value);
            OnPropertyChanged();
        }
    }

    /// <summary>Applies when ticked.</summary>
    public bool StartWithWindows
    {
        get => _ui.StartsWithWindows;
        set
        {
            if (value == _ui.StartsWithWindows) return;
            AppMessage = _ui.StartWithWindows(value);
            OnPropertyChanged();
        }
    }

    /// <summary>Why a preference didn't stick, or null.</summary>
    public string? AppMessage { get => _appMessage; private set => SetProperty(ref _appMessage, value); }

    /// <summary>How far calibration has got (spec §5).</summary>
    public string Calibration { get => _calibration; private set => SetProperty(ref _calibration, value); }

    /// <summary>The reset was pressed once; it waits for Reset or Keep.</summary>
    public bool ConfirmingReset { get => _confirmingReset; private set => SetProperty(ref _confirmingReset, value); }

    public string? CalibrationMessage { get => _calibrationMessage; private set => SetProperty(ref _calibrationMessage, value); }

    public ICommand ResetCalibration { get; }

    public ICommand ConfirmReset { get; }

    public ICommand CancelReset { get; }

    /// <summary>"Service 0.1.0 · 12,345 readings since 15 Sep 2026 07:02".</summary>
    public string ServiceState { get => _serviceState; private set => SetProperty(ref _serviceState, value); }

    public IReadOnlyList<SourceLine> Sources { get => _sources; private set => SetProperty(ref _sources, value); }

    /// <summary>The database's size on disk.</summary>
    public string Database { get => _database; private set => SetProperty(ref _database, value); }

    /// <summary>Why the service's part is empty, or null.</summary>
    public string? Notice { get => _notice; private set => SetProperty(ref _notice, value); }

    public ICommand RunSetup { get; }

    /// <summary>The page is shown: read everything now, and the status every ten seconds until hidden. Call on the UI thread.</summary>
    public void Show()
    {
        _threads.Background(() => _ = ReadAllAsync(refill: true));
        _timer ??= _clock.CreateTimer(_ => _ = ReadStatusAsync(), null, StatusEvery, StatusEvery);
    }

    public void Hide()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        Tariff.Saved -= ReadTariffs;
        Service.Saved -= Show;
        _link.ConnectionChanged -= OnConnectionChanged;
        Hide();
    }

    /// <summary>The second press of the reset: sends it, and says what came of it.</summary>
    internal async Task ConfirmResetAsync()
    {
        var result = await _link.ResetCalibrationAsync().ConfigureAwait(false);
        _threads.Post(() =>
        {
            ConfirmingReset = false;
            CalibrationMessage = result.Succeeded
                ? "Calibration reset. The model learns again the next time the machine runs on battery."
                : result.Problem;
        });
        await ReadStatusAsync().ConfigureAwait(false);
    }

    /// <param name="refill">Fill the service form even when it holds values; a reconnect only fills an empty one.</param>
    private async Task ReadAllAsync(bool refill)
    {
        var settings = await _link.GetSettingsAsync().ConfigureAwait(false);
        var status = await _link.GetStatusAsync().ConfigureAwait(false);
        var tariffs = _history.Tariffs();
        var detected = _history.Detected();
        _threads.Post(() =>
        {
            if (settings is not null)
            {
                _sampleSeconds = settings.SampleIntervalSeconds;
                if (refill || !Service.IsLoaded) Service.Load(settings);
            }
            Notice = settings is null
                ? "The service isn't running. Its settings appear when it starts; the App's own preferences below work now."
                : null;
            ShowTariffs(tariffs);
            Detected = detected?.Summary(_culture) ?? "Nothing detected yet: the service detects the hardware when it starts.";
            ShowStatus(status);
        });
    }

    /// <summary>The service came up while the screen shows: read it. Raised on the link's thread.</summary>
    private void OnConnectionChanged(bool connected)
    {
        if (connected && _timer is not null) _threads.Background(() => _ = ReadAllAsync(refill: false));
    }

    private async Task ReadStatusAsync()
    {
        var status = await _link.GetStatusAsync().ConfigureAwait(false);
        _threads.Post(() => ShowStatus(status));
    }

    private void ReadTariffs()
    {
        var tariffs = _history.Tariffs();
        _threads.Post(() => ShowTariffs(tariffs));
    }

    private void ApplyCo2()
    {
        const NumberStyles plain = NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowDecimalPoint;
        if (!double.TryParse(Co2, plain, _culture, out var factor) && !double.TryParse(Co2, plain, CultureInfo.InvariantCulture, out factor))
        {
            Co2Message = "Type the kilograms of CO₂ per kWh as a number, like 0.40.";
            return;
        }
        Co2Message = _ui.UseCo2(factor) ?? "Saved.";
    }

    private void ShowTariffs(IReadOnlyList<Tariff>? tariffs)
        => TariffHistory = tariffs is null
            ? []
            : [.. tariffs.OrderByDescending(t => t.EffectiveFrom).Select(t =>
                $"From {TimeZoneInfo.ConvertTime(t.EffectiveFrom, _zone).ToString("d MMM yyyy", _culture)} · {Money.Rate(t.PricePerKwh, t.Currency, _culture)} / kWh")];

    private void ShowStatus(ServiceStatus? status)
    {
        if (status is null)
        {
            ServiceState = "The service isn't running.";
            Calibration = "Unknown while the service isn't running.";
            Sources = [];
            Database = Format.Missing;
            return;
        }
        var c = status.Calibration;
        var learned = Format.Duration(c.BatterySamples * _sampleSeconds / 3600.0);
        Calibration = c.TrustedBuckets > 0
            ? $"Learned from {learned} on battery; {c.TrustedBuckets.ToString(_culture)} of {c.Buckets.ToString(_culture)} brightness levels trusted."
            : $"Learning on battery: {learned} of {Format.Duration(c.SamplesNeeded * _sampleSeconds / 3600.0)} needed.";
        ServiceState = $"Service {status.Version.Split('+')[0]} · {status.Ticks.ToString("N0", _culture)} readings since "
                       + TimeZoneInfo.ConvertTime(status.StartedAt, _zone).ToString("d MMM yyyy HH:mm", _culture);
        Sources = [.. status.Sources.Select(Line)];
        var megabytes = status.DatabaseBytes / (1024.0 * 1024.0);
        Database = (megabytes < 10 ? megabytes.ToString("0.0", _culture) : megabytes.ToString("0", _culture)) + " MB";
    }

    private static SourceLine Line(SourceStatus source)
    {
        var name = source.Name switch
        {
            "energy-meter" => "Processor energy meter",
            "battery" => "Battery",
            "cpu-load" => "Processor load",
            "nvidia-gpu" => "NVIDIA graphics",
            "display" => "Display brightness",
            "activity" => "Display and lock state",
            _ => source.Name,
        };
        if (!source.Supported) return new SourceLine(name, "not on this machine", source.Unavailable ?? "");
        return source.Failures > 0
            ? new SourceLine(name, $"failing ({source.Failures})", source.LastError ?? "")
            : new SourceLine(name, "working", "");
    }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter SettingsViewModelTests`
Expected: `Passed! - Failed: 0, Passed: 9`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.App/Settings/SettingsViewModel.cs tests/PowerLedger.App.Tests/SettingsViewModelTests.cs
git commit -m "Add the Settings view model: tariff, machine, sampling, calibration, preferences and About"
```

---

### Task 7: The Settings screen

**Files:**
- Create: `src/PowerLedger.App/Settings/SettingsView.xaml`, `src/PowerLedger.App/Settings/SettingsView.xaml.cs`
- Modify: `src/PowerLedger.App/Controls/Converters.cs`, `src/PowerLedger.App/Theme/Styles.xaml`
- Modify: `src/PowerLedger.App/Shell/ShellViewModel.cs`, `src/PowerLedger.App/Shell/MainWindow.xaml`
- Modify: `src/PowerLedger.App/Tray/TrayIcon.cs`, `src/PowerLedger.App/App.xaml.cs`
- Modify: `tests/PowerLedger.App.Tests/ShellViewModelTests.cs`, `tests/PowerLedger.App.Tests/RenderingTests.cs`

The Settings screen is a single column of ledger-headed sections: tariff, machine, sampling and history, calibration, preferences, and About. Text boxes and tick boxes get palette styles, `Field` and `Tick`; choices use `Segment`. A message under each Save says what came of it, and `VisibleWhenText` hides a message that is empty.

The shell's last placeholder goes. The App builds `AppPreferences` over the store, the theme manager and the Run entry, and passes each new CO₂ factor to the three screens that use it. The tray's "Start with Windows" item reads the Run entry each time its menu opens, so a change made in Settings shows there too.

- [x] **Step 1: Write the failing test**

In `tests/PowerLedger.App.Tests/ShellViewModelTests.cs`, give `Shell()` a Settings screen:

```csharp
    private ShellViewModel Shell() => new(
        new NowViewModel(new FakeLink(), new FakeHistory(), UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, 0.4, () => { }),
        new BreakdownViewModel(_history, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English),
        new ReportViewModel(_history, new FakeSleep(), new FakeSaver(), _ => [], UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, 0.4),
        new SettingsViewModel(_link, new FakeMachineHistory(), new FakeUiSettings(), UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, "USD"),
        "0.1.0");
```

add the field `private readonly FakeLink _link = new();`, and replace `Settings_is_still_to_come` with:

```csharp
    [Fact]
    public void Settings_reads_the_service_while_it_shows()
    {
        _link.Connect(true);
        var shell = Shell();
        shell.Page = Page.Settings;
        shell.Current.ShouldBe(shell.Settings);
        shell.Settings.Service.IsLoaded.ShouldBeTrue();
    }
```

- [x] **Step 2: Run it to verify it fails**

Run: `dotnet build tests/PowerLedger.App.Tests`
Expected: build error, `ShellViewModel` takes four arguments.

- [x] **Step 3: Write the screen and wire it in**

In `src/PowerLedger.App/Controls/Converters.cs`, add after `VisibleWhen`:

```csharp
/// <summary>Shows an element while its text has something in it, so an empty message takes no room.</summary>
internal sealed class VisibleWhenText : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
```

In `src/PowerLedger.App/Theme/Styles.xaml`, add `<local:VisibleWhenText x:Key="VisibleWhenText" />` after the `HiddenWhen` converter, and before the closing `</ResourceDictionary>`:

```xml
    <!-- A typed value: the number face in a thin frame that turns amber while it has the keyboard. -->
    <Style x:Key="Field" TargetType="TextBox">
        <Setter Property="FontFamily" Value="{StaticResource Font.Numbers}" />
        <Setter Property="FontSize" Value="12.5" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.Ink}" />
        <Setter Property="CaretBrush" Value="{DynamicResource Brush.Ink}" />
        <Setter Property="Background" Value="{DynamicResource Brush.Panel}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.LineStrong}" />
        <Setter Property="Padding" Value="6,4" />
        <Setter Property="VerticalAlignment" Value="Center" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="TextBox">
                    <Border x:Name="Frame" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="3">
                        <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}" Focusable="False" VerticalAlignment="Center" />
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsKeyboardFocused" Value="True">
                            <Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource Brush.Amber}" />
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Opacity" Value="0.5" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- A yes or no: a small square that fills amber with a tick. -->
    <Style x:Key="Tick" TargetType="CheckBox">
        <Setter Property="Foreground" Value="{DynamicResource Brush.Ink2}" />
        <Setter Property="FontFamily" Value="{StaticResource Font.Ui}" />
        <Setter Property="FontSize" Value="13" />
        <Setter Property="Cursor" Value="Hand" />
        <Setter Property="VerticalAlignment" Value="Center" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="CheckBox">
                    <StackPanel Orientation="Horizontal" Background="Transparent">
                        <Border x:Name="Box" Width="14" Height="14" BorderBrush="{DynamicResource Brush.LineStrong}" BorderThickness="1" CornerRadius="2" VerticalAlignment="Center">
                            <Path x:Name="Mark" Data="M2.5,7 L5.5,10 L11.5,3.5" Stroke="{DynamicResource Brush.OnAmber}" StrokeThickness="1.8" Visibility="Collapsed" />
                        </Border>
                        <ContentPresenter Margin="8,0,0,0" VerticalAlignment="Center" />
                    </StackPanel>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter Property="Foreground" Value="{DynamicResource Brush.Ink}" />
                        </Trigger>
                        <Trigger Property="IsKeyboardFocused" Value="True">
                            <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource Brush.Amber}" />
                        </Trigger>
                        <Trigger Property="IsChecked" Value="True">
                            <Setter TargetName="Box" Property="Background" Value="{DynamicResource Brush.Amber}" />
                            <Setter TargetName="Box" Property="BorderBrush" Value="{DynamicResource Brush.Amber}" />
                            <Setter TargetName="Mark" Property="Visibility" Value="Visible" />
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter Property="Opacity" Value="0.5" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
```

`src/PowerLedger.App/Settings/SettingsView.xaml`
```xml
<UserControl x:Class="PowerLedger.App.SettingsView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:local="clr-namespace:PowerLedger.App"
             xmlns:contracts="clr-namespace:PowerLedger.Contracts;assembly=PowerLedger.Contracts">
    <UserControl.Resources>
        <Style x:Key="Label" TargetType="TextBlock" BasedOn="{StaticResource Text.Secondary}">
            <Setter Property="Width" Value="230" />
            <Setter Property="VerticalAlignment" Value="Center" />
        </Style>
        <Style x:Key="Row" TargetType="StackPanel">
            <Setter Property="Orientation" Value="Horizontal" />
            <Setter Property="Margin" Value="0,5" />
        </Style>
        <Style x:Key="Section" TargetType="Border" BasedOn="{StaticResource LedgerHead}">
            <Setter Property="Margin" Value="0,28,0,8" />
        </Style>
        <Style x:Key="Note" TargetType="TextBlock" BasedOn="{StaticResource Text.Muted}">
            <Setter Property="TextWrapping" Value="Wrap" />
            <Setter Property="Margin" Value="0,4,0,0" />
        </Style>
        <Style x:Key="Said" TargetType="TextBlock" BasedOn="{StaticResource Note}">
            <Setter Property="Foreground" Value="{DynamicResource Brush.Amber}" />
        </Style>
        <Style x:Key="Choice" TargetType="RadioButton" BasedOn="{StaticResource Segment}">
            <Setter Property="Margin" Value="0,0,6,0" />
        </Style>
        <Style x:Key="Aside" TargetType="TextBlock" BasedOn="{StaticResource Text.Muted}">
            <Setter Property="VerticalAlignment" Value="Center" />
            <Setter Property="Margin" Value="10,0,0,0" />
        </Style>
    </UserControl.Resources>

    <ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled" Focusable="False">
        <StackPanel Margin="26,22,26,18">
            <Border Style="{StaticResource Banner}" Visibility="{Binding Notice, Converter={StaticResource VisibleWhenText}}">
                <TextBlock Style="{StaticResource Text.Body}" TextWrapping="Wrap" Text="{Binding Notice}" />
            </Border>

            <Border Style="{StaticResource LedgerHead}"><TextBlock Style="{StaticResource Text.Eyebrow}" Text="TARIFF" /></Border>
            <ItemsControl ItemsSource="{Binding TariffHistory}" Margin="0,8,0,6" Focusable="False">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <TextBlock Style="{StaticResource Text.Number}" FontSize="12.5" Margin="0,2" Text="{Binding}" />
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
            <WrapPanel DataContext="{Binding Tariff}">
                <TextBlock Style="{StaticResource Text.Secondary}" Text="Price per kWh" VerticalAlignment="Center" Margin="0,0,8,0" />
                <TextBox Style="{StaticResource Field}" Width="90" Text="{Binding Price, UpdateSourceTrigger=PropertyChanged}" AutomationProperties.Name="Price per kWh" />
                <TextBlock Style="{StaticResource Text.Secondary}" Text="Currency" VerticalAlignment="Center" Margin="16,0,8,0" />
                <TextBox Style="{StaticResource Field}" Width="56" MaxLength="3" CharacterCasing="Upper" Text="{Binding Currency, UpdateSourceTrigger=PropertyChanged}" AutomationProperties.Name="Currency" />
                <TextBlock Style="{StaticResource Text.Secondary}" Text="From" VerticalAlignment="Center" Margin="16,0,8,0" />
                <DatePicker Width="132" SelectedDate="{Binding From}" AutomationProperties.Name="First day of the tariff" />
                <Button Style="{StaticResource Quiet}" Content="Save tariff" Command="{Binding Save}" Margin="16,0,0,0" />
            </WrapPanel>
            <TextBlock Style="{StaticResource Said}" DataContext="{Binding Tariff}" Text="{Binding Message}" Visibility="{Binding Message, Converter={StaticResource VisibleWhenText}}" />
            <TextBlock Style="{StaticResource Note}" Text="Energy already recorded is priced again from the day a tariff starts, so a tariff may start in the past." />

            <Border Style="{StaticResource Section}"><TextBlock Style="{StaticResource Text.Eyebrow}" Text="MACHINE" /></Border>
            <TextBlock Style="{StaticResource Text.Muted}" TextWrapping="Wrap" Margin="0,0,0,6" Text="{Binding Detected, StringFormat=Detected: {0}}" />
            <StackPanel DataContext="{Binding Service}" IsEnabled="{Binding IsLoaded}">
                <StackPanel Style="{StaticResource Row}">
                    <TextBlock Style="{StaticResource Label}" Text="Chassis" />
                    <RadioButton Style="{StaticResource Choice}" Content="Laptop"
                                 IsChecked="{Binding Chassis, Converter={StaticResource Is}, ConverterParameter={x:Static contracts:ChassisKind.Laptop}}" />
                    <RadioButton Style="{StaticResource Choice}" Content="Desktop"
                                 IsChecked="{Binding Chassis, Converter={StaticResource Is}, ConverterParameter={x:Static contracts:ChassisKind.Desktop}}" />
                </StackPanel>
                <StackPanel Style="{StaticResource Row}" Visibility="{Binding IsDesktop, Converter={StaticResource VisibleWhen}}">
                    <TextBlock Style="{StaticResource Label}" Text="Power supply rating" />
                    <RadioButton Style="{StaticResource Choice}" Content="White" IsChecked="{Binding PsuTier, Converter={StaticResource Is}, ConverterParameter={x:Static contracts:PsuTier.White}}" />
                    <RadioButton Style="{StaticResource Choice}" Content="Bronze" IsChecked="{Binding PsuTier, Converter={StaticResource Is}, ConverterParameter={x:Static contracts:PsuTier.Bronze}}" />
                    <RadioButton Style="{StaticResource Choice}" Content="Silver" IsChecked="{Binding PsuTier, Converter={StaticResource Is}, ConverterParameter={x:Static contracts:PsuTier.Silver}}" />
                    <RadioButton Style="{StaticResource Choice}" Content="Gold" IsChecked="{Binding PsuTier, Converter={StaticResource Is}, ConverterParameter={x:Static contracts:PsuTier.Gold}}" />
                    <RadioButton Style="{StaticResource Choice}" Content="Platinum" IsChecked="{Binding PsuTier, Converter={StaticResource Is}, ConverterParameter={x:Static contracts:PsuTier.Platinum}}" />
                    <RadioButton Style="{StaticResource Choice}" Content="Titanium" IsChecked="{Binding PsuTier, Converter={StaticResource Is}, ConverterParameter={x:Static contracts:PsuTier.Titanium}}" />
                </StackPanel>
                <StackPanel Style="{StaticResource Row}">
                    <TextBlock Style="{StaticResource Label}" Text="Memory sticks" />
                    <TextBox Style="{StaticResource Field}" Width="56" Text="{Binding RamSticks, UpdateSourceTrigger=PropertyChanged}" AutomationProperties.Name="Memory sticks" />
                    <CheckBox Style="{StaticResource Tick}" Content="DDR5" Margin="14,0,0,0" IsChecked="{Binding RamIsDdr5}" />
                </StackPanel>
                <StackPanel Style="{StaticResource Row}">
                    <TextBlock Style="{StaticResource Label}" Text="SSDs · hard drives · fans" />
                    <TextBox Style="{StaticResource Field}" Width="56" Text="{Binding SsdCount, UpdateSourceTrigger=PropertyChanged}" AutomationProperties.Name="SSDs" />
                    <TextBox Style="{StaticResource Field}" Width="56" Margin="8,0,0,0" Text="{Binding HddCount, UpdateSourceTrigger=PropertyChanged}" AutomationProperties.Name="Hard drives" />
                    <TextBox Style="{StaticResource Field}" Width="56" Margin="8,0,0,0" Text="{Binding FanCount, UpdateSourceTrigger=PropertyChanged}" AutomationProperties.Name="Fans" />
                </StackPanel>
                <StackPanel Style="{StaticResource Row}">
                    <TextBlock Style="{StaticResource Label}" Text="Built-in panel, inches" />
                    <TextBox Style="{StaticResource Field}" Width="56" Text="{Binding PanelInches, UpdateSourceTrigger=PropertyChanged}" AutomationProperties.Name="Built-in panel, inches" />
                    <TextBlock Style="{StaticResource Aside}" Text="0 for none" />
                </StackPanel>
                <StackPanel Style="{StaticResource Row}">
                    <TextBlock Style="{StaticResource Label}" Text="External monitors" />
                    <TextBox Style="{StaticResource Field}" Width="56" Text="{Binding ExternalMonitors, UpdateSourceTrigger=PropertyChanged}" AutomationProperties.Name="External monitors" />
                    <CheckBox Style="{StaticResource Tick}" Content="Count them, at" Margin="14,0,8,0" IsChecked="{Binding IncludeMonitors}" />
                    <TextBox Style="{StaticResource Field}" Width="56" Text="{Binding MonitorWatts, UpdateSourceTrigger=PropertyChanged}" AutomationProperties.Name="Watts per monitor" />
                    <TextBlock Style="{StaticResource Aside}" Text="W each" />
                </StackPanel>
                <StackPanel Style="{StaticResource Row}">
                    <TextBlock Style="{StaticResource Label}" Text="Extras such as lights and pumps, W" />
                    <TextBox Style="{StaticResource Field}" Width="56" Text="{Binding ExtrasWatts, UpdateSourceTrigger=PropertyChanged}" AutomationProperties.Name="Extras in watts" />
                </StackPanel>
                <StackPanel Style="{StaticResource Row}">
                    <TextBlock Style="{StaticResource Label}" Text="Processor · graphics rated power, W" />
                    <TextBox Style="{StaticResource Field}" Width="56" Text="{Binding CpuTdp, UpdateSourceTrigger=PropertyChanged}" AutomationProperties.Name="Processor rated power" />
                    <TextBox Style="{StaticResource Field}" Width="56" Margin="8,0,0,0" Text="{Binding GpuTdp, UpdateSourceTrigger=PropertyChanged}" AutomationProperties.Name="Graphics rated power" />
                    <TextBlock Style="{StaticResource Aside}" Text="blank: from PowerLedger's table" />
                </StackPanel>

                <Border Style="{StaticResource Section}"><TextBlock Style="{StaticResource Text.Eyebrow}" Text="SAMPLING AND HISTORY" /></Border>
                <StackPanel Style="{StaticResource Row}">
                    <TextBlock Style="{StaticResource Label}" Text="Idle after, minutes" />
                    <TextBox Style="{StaticResource Field}" Width="56" Text="{Binding IdleMinutes, UpdateSourceTrigger=PropertyChanged}" AutomationProperties.Name="Idle after, minutes" />
                    <TextBlock Style="{StaticResource Aside}" Text="1 to 30" />
                </StackPanel>
                <StackPanel Style="{StaticResource Row}">
                    <TextBlock Style="{StaticResource Label}" Text="Sample every, seconds" />
                    <RadioButton Style="{StaticResource Choice}" Content="1" IsChecked="{Binding SampleInterval, Converter={StaticResource Is}, ConverterParameter=1}" />
                    <RadioButton Style="{StaticResource Choice}" Content="2" IsChecked="{Binding SampleInterval, Converter={StaticResource Is}, ConverterParameter=2}" />
                    <RadioButton Style="{StaticResource Choice}" Content="3" IsChecked="{Binding SampleInterval, Converter={StaticResource Is}, ConverterParameter=3}" />
                    <RadioButton Style="{StaticResource Choice}" Content="4" IsChecked="{Binding SampleInterval, Converter={StaticResource Is}, ConverterParameter=4}" />
                    <RadioButton Style="{StaticResource Choice}" Content="5" IsChecked="{Binding SampleInterval, Converter={StaticResource Is}, ConverterParameter=5}" />
                </StackPanel>
                <StackPanel Style="{StaticResource Row}">
                    <TextBlock Style="{StaticResource Label}" Text="Keep every reading for, hours" />
                    <TextBox Style="{StaticResource Field}" Width="56" Text="{Binding RawHours, UpdateSourceTrigger=PropertyChanged}" AutomationProperties.Name="Hours of every reading" />
                    <TextBlock Style="{StaticResource Aside}" Text="24 to 168" />
                </StackPanel>
                <StackPanel Style="{StaticResource Row}">
                    <TextBlock Style="{StaticResource Label}" Text="Keep minute totals for, years" />
                    <TextBox Style="{StaticResource Field}" Width="56" Text="{Binding HistoryYears, UpdateSourceTrigger=PropertyChanged}" AutomationProperties.Name="Years of minute totals" />
                    <TextBlock Style="{StaticResource Aside}" Text="1 to 5; hourly totals are kept for good" />
                </StackPanel>
                <Button Style="{StaticResource Quiet}" Content="Save settings" Command="{Binding Save}" HorizontalAlignment="Left" Margin="0,10,0,0" />
                <TextBlock Style="{StaticResource Said}" Text="{Binding Message}" Visibility="{Binding Message, Converter={StaticResource VisibleWhenText}}" />
            </StackPanel>

            <Border Style="{StaticResource Section}"><TextBlock Style="{StaticResource Text.Eyebrow}" Text="CALIBRATION" /></Border>
            <TextBlock Style="{StaticResource Text.Body}" TextWrapping="Wrap" Text="{Binding Calibration}" />
            <StackPanel Orientation="Horizontal" Margin="0,10,0,0" Visibility="{Binding ConfirmingReset, Converter={StaticResource HiddenWhen}}">
                <Button Style="{StaticResource Quiet}" Content="Reset calibration" Command="{Binding ResetCalibration}" />
            </StackPanel>
            <StackPanel Orientation="Horizontal" Margin="0,10,0,0" Visibility="{Binding ConfirmingReset, Converter={StaticResource VisibleWhen}}">
                <TextBlock Style="{StaticResource Text.Body}" Text="Forget what was learned on battery?" VerticalAlignment="Center" Margin="0,0,12,0" />
                <Button Style="{StaticResource Quiet}" Content="Reset" Command="{Binding ConfirmReset}" Margin="0,0,8,0" />
                <Button Style="{StaticResource Quiet}" Content="Keep" Command="{Binding CancelReset}" />
            </StackPanel>
            <TextBlock Style="{StaticResource Said}" Text="{Binding CalibrationMessage}" Visibility="{Binding CalibrationMessage, Converter={StaticResource VisibleWhenText}}" />

            <Border Style="{StaticResource Section}"><TextBlock Style="{StaticResource Text.Eyebrow}" Text="PREFERENCES" /></Border>
            <StackPanel Style="{StaticResource Row}">
                <TextBlock Style="{StaticResource Label}" Text="CO₂ per kWh, kg" />
                <TextBox Style="{StaticResource Field}" Width="70" Text="{Binding Co2, UpdateSourceTrigger=PropertyChanged}" AutomationProperties.Name="Kilograms of CO2 per kWh" />
                <Button Style="{StaticResource Quiet}" Content="Save" Command="{Binding SaveCo2}" Margin="12,0,0,0" />
            </StackPanel>
            <TextBlock Style="{StaticResource Note}" Text="Use your electricity supplier's figure for its grid; 0.40 is the world average." />
            <TextBlock Style="{StaticResource Said}" Text="{Binding Co2Message}" Visibility="{Binding Co2Message, Converter={StaticResource VisibleWhenText}}" />
            <StackPanel Style="{StaticResource Row}" Margin="0,12,0,5">
                <TextBlock Style="{StaticResource Label}" Text="Theme" />
                <RadioButton Style="{StaticResource Choice}" Content="Like Windows" IsChecked="{Binding Theme, Converter={StaticResource Is}, ConverterParameter={x:Static local:ThemeChoice.System}}" />
                <RadioButton Style="{StaticResource Choice}" Content="Dark" IsChecked="{Binding Theme, Converter={StaticResource Is}, ConverterParameter={x:Static local:ThemeChoice.Dark}}" />
                <RadioButton Style="{StaticResource Choice}" Content="Light" IsChecked="{Binding Theme, Converter={StaticResource Is}, ConverterParameter={x:Static local:ThemeChoice.Light}}" />
            </StackPanel>
            <StackPanel Style="{StaticResource Row}">
                <TextBlock Style="{StaticResource Label}" Text="Start with Windows" />
                <CheckBox Style="{StaticResource Tick}" Content="In the tray, when you sign in" IsChecked="{Binding StartWithWindows}" />
            </StackPanel>
            <TextBlock Style="{StaticResource Said}" Text="{Binding AppMessage}" Visibility="{Binding AppMessage, Converter={StaticResource VisibleWhenText}}" />

            <Border Style="{StaticResource Section}"><TextBlock Style="{StaticResource Text.Eyebrow}" Text="ABOUT" /></Border>
            <TextBlock Style="{StaticResource Text.Body}" Text="{Binding ServiceState}" />
            <TextBlock Style="{StaticResource Text.Secondary}" Margin="0,4,0,0" Text="{Binding Database, StringFormat=Database {0}}" />
            <TextBlock Style="{StaticResource Text.Secondary}" Margin="0,4,0,8" TextWrapping="Wrap" Text="No driver: readings come from interfaces Windows already provides." />
            <ItemsControl ItemsSource="{Binding Sources}" Focusable="False">
                <ItemsControl.ItemTemplate>
                    <DataTemplate DataType="{x:Type local:SourceLine}">
                        <Border BorderBrush="{DynamicResource Brush.Line}" BorderThickness="0,0,0,1" Padding="0,6">
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="230" />
                                    <ColumnDefinition Width="170" />
                                    <ColumnDefinition Width="*" />
                                </Grid.ColumnDefinitions>
                                <TextBlock Style="{StaticResource Text.Body}" Text="{Binding Name}" />
                                <TextBlock Grid.Column="1" Style="{StaticResource Text.Secondary}" Text="{Binding State}" />
                                <TextBlock Grid.Column="2" Style="{StaticResource Text.Muted}" TextWrapping="Wrap" Text="{Binding Detail}" />
                            </Grid>
                        </Border>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
            <Button Style="{StaticResource Quiet}" Content="Run setup again" Command="{Binding RunSetup}" HorizontalAlignment="Left" Margin="0,16,0,0" />
        </StackPanel>
    </ScrollViewer>
</UserControl>
```

`src/PowerLedger.App/Settings/SettingsView.xaml.cs`
```csharp
using System.Windows.Controls;

namespace PowerLedger.App;

/// <summary>The Settings screen's layout (spec §9); everything it shows comes from <see cref="SettingsViewModel"/>.</summary>
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
}
```

`src/PowerLedger.App/Shell/ShellViewModel.cs`
```csharp
using CommunityToolkit.Mvvm.ComponentModel;

namespace PowerLedger.App;

/// <summary>The four screens of spec §9's rail.</summary>
internal enum Page
{
    Now,
    Breakdown,
    Report,
    Settings,
}

/// <summary>The window: which page shows, the screens, and the version in the title bar.</summary>
internal sealed class ShellViewModel(
    NowViewModel now, BreakdownViewModel breakdown, ReportViewModel report, SettingsViewModel settings, string version) : ObservableObject
{
    private Page _page = Page.Now;

    public NowViewModel Now { get; } = now;

    public BreakdownViewModel Breakdown { get; } = breakdown;

    public ReportViewModel Report { get; } = report;

    public SettingsViewModel Settings { get; } = settings;

    public string Version { get; } = version;

    /// <summary>The page shown. A screen that reads history or the service reads while it shows and stops when it does not.</summary>
    public Page Page
    {
        get => _page;
        set
        {
            if (!SetProperty(ref _page, value)) return;
            if (value == Page.Breakdown) Breakdown.Show();
            else Breakdown.Hide();
            if (value == Page.Report) Report.Show();
            else Report.Hide();
            if (value == Page.Settings) Settings.Show();
            else Settings.Hide();
            OnPropertyChanged(nameof(Current));
        }
    }

    public object Current => Page switch
    {
        Page.Now => Now,
        Page.Breakdown => Breakdown,
        Page.Report => Report,
        _ => Settings,
    };
}
```

In `src/PowerLedger.App/Shell/MainWindow.xaml`, replace the `PlaceholderViewModel` data template with:

```xml
        <DataTemplate DataType="{x:Type local:SettingsViewModel}">
            <local:SettingsView />
        </DataTemplate>
```

In `src/PowerLedger.App/Tray/TrayIcon.cs`, add after the menu is built:

```csharp
        menu.Opening += (_, _) =>
        {
            if (startWithWindows.Checked != autostart.IsEnabled) startWithWindows.Checked = autostart.IsEnabled;   // Settings may have changed it
        };
```

In `src/PowerLedger.App/App.xaml.cs`:
- Add the fields `private AppPreferences? _preferences;` and `private SettingsViewModel? _settings;`.
- Load the preferences through a kept store: `var store = new UiPreferencesStore(UiPreferencesStore.DefaultPath);` then `var preferences = store.Load();`.
- Make `var autostart = new StartWithWindows(Environment.ProcessPath!);` and pass it to the `TrayIcon` in place of the new one there.
- Replace `_shell = new ShellViewModel(_now, _breakdown, _report, version);` with:

```csharp
        _preferences = new AppPreferences(store, preferences, choice => _theme.Choose(choice), UseCo2, autostart);
        _settings = new SettingsViewModel(_link, history, _preferences, threads, TimeProvider.System, zone, culture, RegionCurrency());
        _shell = new ShellViewModel(_now, _breakdown, _report, _settings, version);
```

- Add `_settings?.Dispose();` after `_report?.Dispose();` in `ExitUi`.
- Add these methods:

```csharp
    /// <summary>A new CO₂ factor from Settings reaches every screen that shows CO₂, and the monthly reports.</summary>
    private void UseCo2(double factor)
    {
        if (_now is not null) _now.Co2KgPerKwh = factor;
        if (_report is not null) _report.Co2KgPerKwh = factor;
        if (_monthly is not null) _monthly.Co2KgPerKwh = factor;
    }

    /// <summary>The currency a new tariff starts in: the region's.</summary>
    private static string RegionCurrency()
    {
        try
        {
            return RegionInfo.CurrentRegion.ISOCurrencySymbol;
        }
        catch (ArgumentException)
        {
            return "USD";
        }
    }
```

In `tests/PowerLedger.App.Tests/RenderingTests.cs`, add a page, `(Page.Settings, "settings", _ => { }, shell => new SettingsView { DataContext = shell.Settings })`. Make the shell with `SettingsScreen()` before the version, and add:

```csharp
    /// <summary>Settings against a running service, with a tariff and this laptop's detection.</summary>
    private static SettingsViewModel SettingsScreen()
    {
        var link = new FakeLink();
        link.Connect(true);
        return new SettingsViewModel(link, new FakeMachineHistory(), new FakeUiSettings(), UiThreads.Inline, new FakeTimeProvider(Now),
            TimeZoneInfo.Utc, English, "USD");
    }
```

- [x] **Step 4: Run tests, and look**

Run: `dotnet test tests/PowerLedger.App.Tests --filter "Category!=UI"`
Expected: all pass.

Run: `dotnet test tests/PowerLedger.App.Tests --filter "Category=UI"` and open `settings-Dark-full.png` and `settings-Light-full.png`.
Expected: the tariff history and form, the machine section with "Detected: Laptop · Core i7-1165G7 · …", the sampling segments with "1" chosen, the calibration line, the preferences with the theme and start-with-Windows choices, and About listing the two sources as working.

- [x] **Step 5: Commit**

```bash
git add -A src/PowerLedger.App tests/PowerLedger.App.Tests
git commit -m "Add the Settings screen and apply its changes at once"
```

---

### Task 8: The first-run wizard's view model

**Files:**
- Create: `src/PowerLedger.App/Setup/WizardViewModel.cs`
- Test: `tests/PowerLedger.App.Tests/WizardViewModelTests.cs`

Spec §9's wizard has three steps.
- **Tariff.** The region's currency, no suggested price (spec §16 leaves the source open), and "later" when nothing is typed.
- **Machine.** What was detected, above the service form, so the user can correct the chassis, the power supply, the monitors and the extras.
- **Readings.** What measured, calibrated and estimated mean, and which of them this machine will show, from the service's sources.

A step saves before moving on, and stays put when the save fails, with the form's message saying why. Without the service the machine can't be saved, so that step says so and moves on: Settings has it later. Finishing is remembered in `ui.json`.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.App.Tests/WizardViewModelTests.cs`
```csharp
using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

public class WizardViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 14, 30, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeLink _link = new();
    private readonly FakeMachineHistory _history = new();
    private readonly FakeUiSettings _ui = new();
    private readonly FakeTimeProvider _clock = new(Now);

    private WizardViewModel Model()
    {
        var model = new WizardViewModel(_link, _history, _ui, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, "EUR");
        model.Start();
        return model;
    }

    [Fact]
    public void It_starts_at_the_tariff_in_the_regions_currency_and_reads_the_machine()
    {
        _link.Connect(true);
        var model = Model();

        model.Step.ShouldBe(SetupStep.Tariff);
        model.IsFirst.ShouldBeTrue();
        model.Tariff.Currency.ShouldBe("EUR");
        model.Machine.IsLoaded.ShouldBeTrue();
        model.Detected.ShouldStartWith("Laptop · Core i7-1165G7");
        model.Readings.ShouldStartWith("This machine has a battery and a processor energy meter.");
    }

    [Fact]
    public async Task An_empty_tariff_is_later_and_a_typed_one_is_saved_first()
    {
        _link.Connect(true);
        var later = Model();
        await later.NextAsync();
        later.Step.ShouldBe(SetupStep.Machine);
        _link.Writes.ShouldBeEmpty();

        var typed = Model();
        typed.Tariff.Price = "0.30";
        await typed.NextAsync();
        typed.Step.ShouldBe(SetupStep.Machine);
        _link.Writes.Single().ShouldBeOfType<(decimal, string, DateTimeOffset?)>();
    }

    [Fact]
    public async Task A_refused_step_stays_put()
    {
        _link.Connect(true);
        _link.Answer = new WriteResult("The price per kWh must be zero or more.");
        var model = Model();
        model.Tariff.Price = "0.30";
        await model.NextAsync();

        model.Step.ShouldBe(SetupStep.Tariff);
        model.Tariff.Message.ShouldBe("The price per kWh must be zero or more.");
    }

    [Fact]
    public async Task The_machine_is_saved_before_the_readings()
    {
        _link.Connect(true);
        var model = Model();
        await model.NextAsync();
        model.Machine.Chassis = ChassisKind.Desktop;
        await model.NextAsync();

        model.Step.ShouldBe(SetupStep.Readings);
        model.IsLast.ShouldBeTrue();
        ((ServiceSettings)_link.Writes.Single()).Profile.Chassis.ShouldBe(ChassisKind.Desktop);
    }

    [Fact]
    public async Task Without_the_service_the_machine_step_says_so_and_moves_on()
    {
        var model = Model();
        await model.NextAsync();
        await model.NextAsync();

        model.Step.ShouldBe(SetupStep.Readings);
        model.Message.ShouldBe("The service isn't running, so the machine wasn't saved. Settings has it once the service starts.");
        model.Readings.ShouldStartWith("The service isn't running yet.");
    }

    [Fact]
    public async Task Back_goes_back_a_step()
    {
        _link.Connect(true);
        var model = Model();
        await model.NextAsync();
        await model.NextAsync();
        model.Back.Execute(null);
        model.Step.ShouldBe(SetupStep.Machine);
        model.Back.Execute(null);
        model.Step.ShouldBe(SetupStep.Tariff);
    }

    [Fact]
    public void Finishing_is_remembered()
    {
        var model = Model();
        var finished = 0;
        model.Finished += () => finished++;
        model.Finish.Execute(null);

        _ui.Current.FirstRunDone.ShouldBeTrue();
        finished.ShouldBe(1);
    }

    [Fact]
    public void A_service_that_comes_up_after_the_wizard_fills_the_machine()
    {
        var model = Model();
        model.Machine.IsLoaded.ShouldBeFalse();
        model.Machine.Chassis.ShouldBe(ChassisKind.Laptop);

        _link.Connect(true);
        model.Machine.IsLoaded.ShouldBeTrue();
        model.Readings.ShouldStartWith("This machine has a battery");
    }

    [Fact]
    public void A_machine_without_sensors_is_told_its_readings_are_estimated()
        => WizardViewModel.ReadingsFor(Statuses.Running(energyMeter: false, battery: false))
            .ShouldBe("This machine has no power sensors PowerLedger can read, so its readings are estimated from load and the machine profile.");
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.App.Tests --filter WizardViewModelTests`
Expected: build error, `WizardViewModel` and `SetupStep` not found.

- [x] **Step 3: Write the wizard**

`src/PowerLedger.App/Setup/WizardViewModel.cs`
```csharp
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>The first-run wizard's steps (spec §9).</summary>
internal enum SetupStep
{
    Tariff,
    Machine,
    Readings,
}

/// <summary>
/// The first-run wizard (spec §9): the tariff, the detected hardware to confirm, and what measured, calibrated and
/// estimated mean on this machine. A step saves before the wizard moves on and stays put when the save fails; an empty
/// tariff means later. Without the service the machine can't be saved, so that step says so and moves on. The service
/// may come up after the wizard does, as at the first start after installing, so the wizard reads again when it
/// connects. Finishing is remembered in ui.json.
/// </summary>
internal sealed class WizardViewModel : ObservableObject, IDisposable
{
    private readonly IServiceLink _link;
    private readonly IMachineHistory _history;
    private readonly IUiSettings _ui;
    private readonly UiThreads _threads;
    private readonly CultureInfo _culture;
    private SetupStep _step;
    private string _detected = "";
    private string _readings = "";
    private string? _message;
    private bool _moving;

    public WizardViewModel(
        IServiceLink link, IMachineHistory history, IUiSettings ui, UiThreads threads, TimeProvider clock, TimeZoneInfo zone,
        CultureInfo culture, string regionCurrency)
    {
        _link = link;
        _history = history;
        _ui = ui;
        _threads = threads;
        _culture = culture;
        Tariff = new TariffForm(link, threads, clock, zone, culture, regionCurrency);
        Machine = new ServiceForm(link, threads, culture);
        Next = new RelayCommand(() => _ = NextAsync());
        Back = new RelayCommand(() => Step = Step == SetupStep.Readings ? SetupStep.Machine : SetupStep.Tariff);
        Finish = new RelayCommand(FinishSetup);
        _link.ConnectionChanged += OnConnectionChanged;
    }

    /// <summary>Raised when the user finishes; the shell shows the Now screen.</summary>
    public event Action? Finished;

    public SetupStep Step
    {
        get => _step;
        private set
        {
            if (!SetProperty(ref _step, value)) return;
            OnPropertyChanged(nameof(IsFirst));
            OnPropertyChanged(nameof(IsLast));
        }
    }

    public bool IsFirst => Step == SetupStep.Tariff;

    public bool IsLast => Step == SetupStep.Readings;

    public TariffForm Tariff { get; }

    public ServiceForm Machine { get; }

    /// <summary>What the service detected, in one line.</summary>
    public string Detected { get => _detected; private set => SetProperty(ref _detected, value); }

    /// <summary>Which qualities this machine's readings will have.</summary>
    public string Readings { get => _readings; private set => SetProperty(ref _readings, value); }

    /// <summary>Why a step moved on without saving, or null.</summary>
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }

    public ICommand Next { get; }

    public ICommand Back { get; }

    public ICommand Finish { get; }

    /// <summary>Begins at the tariff and reads what the service knows, off the UI thread. Call on the UI thread.</summary>
    public void Start()
    {
        Step = SetupStep.Tariff;
        Message = null;
        _threads.Background(() => _ = ReadAsync(refill: true));
    }

    public void Dispose() => _link.ConnectionChanged -= OnConnectionChanged;

    /// <summary>Saves this step, and moves on when that worked. Call on the UI thread.</summary>
    internal async Task NextAsync()
    {
        if (_moving) return;
        _moving = true;
        var step = Step;
        var saved = step switch
        {
            SetupStep.Tariff => Tariff.IsEmpty || await Tariff.SaveAsync().ConfigureAwait(false),
            SetupStep.Machine => !Machine.IsLoaded || await Machine.SaveAsync().ConfigureAwait(false),
            _ => true,
        };
        _threads.Post(() =>
        {
            _moving = false;
            if (!saved) return;
            if (step == SetupStep.Machine && !Machine.IsLoaded)
            {
                Message = "The service isn't running, so the machine wasn't saved. Settings has it once the service starts.";
            }
            Step = step == SetupStep.Tariff ? SetupStep.Machine : SetupStep.Readings;
        });
    }

    /// <summary>What this machine's readings will be, from the service's sources.</summary>
    internal static string ReadingsFor(ServiceStatus? status)
    {
        if (status is null) return "The service isn't running yet. Once it is, each reading on the Now screen shows its quality.";
        bool Has(string name) => status.Sources.Any(s => s.Name == name && s.Supported);
        const string battery = "On battery its readings are measured; plugged in, they are calibrated once the model has learned from battery time, and estimated until then.";
        if (Has("battery") && Has("energy-meter")) return "This machine has a battery and a processor energy meter. " + battery;
        if (Has("battery")) return "This machine has a battery. " + battery;
        if (Has("energy-meter"))
            return "This machine reports its processor's energy, so the processor is measured; the rest is estimated from the machine profile, since only a battery shows the whole machine's draw.";
        return "This machine has no power sensors PowerLedger can read, so its readings are estimated from load and the machine profile.";
    }

    /// <param name="refill">Fill the machine form even when it holds values; a reconnect only fills an empty one.</param>
    private async Task ReadAsync(bool refill)
    {
        var settings = await _link.GetSettingsAsync().ConfigureAwait(false);
        var status = await _link.GetStatusAsync().ConfigureAwait(false);
        var detected = _history.Detected();
        _threads.Post(() =>
        {
            if (settings is not null && (refill || !Machine.IsLoaded)) Machine.Load(settings);
            Detected = detected?.Summary(_culture) ?? "The service hasn't detected this machine yet; it does when it starts.";
            Readings = ReadingsFor(status);
        });
    }

    /// <summary>The service came up after the wizard did: read what it knows. Raised on the link's thread.</summary>
    private void OnConnectionChanged(bool connected)
    {
        if (connected) _threads.Background(() => _ = ReadAsync(refill: false));
    }

    private void FinishSetup()
    {
        _ui.FinishFirstRun();
        Finished?.Invoke();
    }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter WizardViewModelTests`
Expected: `Passed! - Failed: 0, Passed: 9`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.App/Setup tests/PowerLedger.App.Tests/WizardViewModelTests.cs
git commit -m "Add the first-run wizard's view model: tariff, machine, and what the readings mean"
```

---

### Task 9: The wizard on screen, on the first run

**Files:**
- Create: `src/PowerLedger.App/Setup/WizardView.xaml`, `src/PowerLedger.App/Setup/WizardView.xaml.cs`
- Modify: `src/PowerLedger.App/Shell/ShellViewModel.cs`, `src/PowerLedger.App/Shell/MainWindow.xaml`, `src/PowerLedger.App/App.xaml.cs`
- Modify: `tests/PowerLedger.App.Tests/ShellViewModelTests.cs`, `tests/PowerLedger.App.Tests/RenderingTests.cs`

While setup runs, the window shows the wizard in place of the rail and the screens. The App starts setup when the window is first shown and the first run is not done yet. That covers the installer opening the App, and a user who opens the window later from the tray. Finishing returns to the Now screen, and Settings' "Run setup again" starts it again.

- [x] **Step 1: Write the failing tests**

In `tests/PowerLedger.App.Tests/ShellViewModelTests.cs`, add the fields `private readonly FakeUiSettings _ui = new();` and `private readonly FakeMachineHistory _machine = new();`, and make the shell with a wizard:

```csharp
    private ShellViewModel Shell() => new(
        new NowViewModel(new FakeLink(), new FakeHistory(), UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, 0.4, () => { }),
        new BreakdownViewModel(_history, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English),
        new ReportViewModel(_history, new FakeSleep(), new FakeSaver(), _ => [], UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, 0.4),
        new SettingsViewModel(_link, _machine, _ui, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, "USD"),
        new WizardViewModel(_link, _machine, _ui, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, "USD"),
        "0.1.0");
```

and append inside the class:

```csharp
    [Fact]
    public void Setup_takes_the_window_until_it_is_finished()
    {
        var shell = Shell();
        shell.Page = Page.Report;
        shell.BeginSetup();
        shell.IsSetup.ShouldBeTrue();
        shell.Current.ShouldBe(shell.Wizard);

        shell.Wizard.Finish.Execute(null);
        shell.IsSetup.ShouldBeFalse();
        shell.Page.ShouldBe(Page.Now);
        shell.Current.ShouldBe(shell.Now);
        _ui.Current.FirstRunDone.ShouldBeTrue();
    }

    [Fact]
    public void Settings_can_run_setup_again()
    {
        var shell = Shell();
        shell.Page = Page.Settings;
        shell.Settings.RunSetup.Execute(null);
        shell.IsSetup.ShouldBeTrue();
        shell.Wizard.Step.ShouldBe(SetupStep.Tariff);
    }
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet build tests/PowerLedger.App.Tests`
Expected: build error, `ShellViewModel` takes five arguments.

- [x] **Step 3: Write the screen and start it on the first run**

`src/PowerLedger.App/Setup/WizardView.xaml`
```xml
<UserControl x:Class="PowerLedger.App.WizardView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:local="clr-namespace:PowerLedger.App"
             xmlns:contracts="clr-namespace:PowerLedger.Contracts;assembly=PowerLedger.Contracts">
    <UserControl.Resources>
        <Style x:Key="Step" TargetType="TextBlock" BasedOn="{StaticResource Text.Eyebrow}">
            <Setter Property="Foreground" Value="{DynamicResource Brush.Ink3}" />
            <Setter Property="Margin" Value="0,0,18,0" />
        </Style>
        <Style x:Key="Label" TargetType="TextBlock" BasedOn="{StaticResource Text.Secondary}">
            <Setter Property="Width" Value="250" />
            <Setter Property="VerticalAlignment" Value="Center" />
        </Style>
        <Style x:Key="Row" TargetType="StackPanel">
            <Setter Property="Orientation" Value="Horizontal" />
            <Setter Property="Margin" Value="0,5" />
        </Style>
        <Style x:Key="Choice" TargetType="RadioButton" BasedOn="{StaticResource Segment}">
            <Setter Property="Margin" Value="0,0,6,0" />
        </Style>
        <Style x:Key="Said" TargetType="TextBlock" BasedOn="{StaticResource Text.Muted}">
            <Setter Property="Foreground" Value="{DynamicResource Brush.Amber}" />
            <Setter Property="TextWrapping" Value="Wrap" />
            <Setter Property="Margin" Value="0,8,0,0" />
        </Style>
        <Style x:Key="Prose" TargetType="TextBlock" BasedOn="{StaticResource Text.Body}">
            <Setter Property="TextWrapping" Value="Wrap" />
            <Setter Property="LineHeight" Value="20" />
            <Setter Property="Margin" Value="0,0,0,12" />
        </Style>
    </UserControl.Resources>

    <ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled" Focusable="False">
        <StackPanel Margin="48,36,48,24" MaxWidth="700" HorizontalAlignment="Left">
            <TextBlock Style="{StaticResource Text.Eyebrow}" Foreground="{DynamicResource Brush.Amber}" Text="SET UP POWERLEDGER" />
            <StackPanel Orientation="Horizontal" Margin="0,14,0,0">
                <TextBlock Text="1 · TARIFF">
                    <TextBlock.Style>
                        <Style TargetType="TextBlock" BasedOn="{StaticResource Step}">
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding Step}" Value="Tariff"><Setter Property="Foreground" Value="{DynamicResource Brush.Ink}" /></DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </TextBlock.Style>
                </TextBlock>
                <TextBlock Text="2 · MACHINE">
                    <TextBlock.Style>
                        <Style TargetType="TextBlock" BasedOn="{StaticResource Step}">
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding Step}" Value="Machine"><Setter Property="Foreground" Value="{DynamicResource Brush.Ink}" /></DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </TextBlock.Style>
                </TextBlock>
                <TextBlock Text="3 · READINGS">
                    <TextBlock.Style>
                        <Style TargetType="TextBlock" BasedOn="{StaticResource Step}">
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding Step}" Value="Readings"><Setter Property="Foreground" Value="{DynamicResource Brush.Ink}" /></DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </TextBlock.Style>
                </TextBlock>
            </StackPanel>
            <Border Style="{StaticResource LedgerHead}" Margin="0,8,0,18" />

            <!-- 1: the tariff -->
            <StackPanel Visibility="{Binding Step, Converter={StaticResource VisibleWhenStep}, ConverterParameter={x:Static local:SetupStep.Tariff}}">
                <TextBlock Style="{StaticResource Prose}" FontSize="16" Text="What do you pay for electricity?" />
                <TextBlock Style="{StaticResource Prose}" Foreground="{DynamicResource Brush.Ink2}"
                           Text="The price per kWh is on your bill. PowerLedger multiplies it by the energy each hour used. Leave it blank to see only kWh for now; Settings can add it later." />
                <StackPanel DataContext="{Binding Tariff}">
                <StackPanel Style="{StaticResource Row}">
                    <TextBlock Style="{StaticResource Label}" Text="Price per kWh" />
                    <TextBox Style="{StaticResource Field}" Width="90" Text="{Binding Price, UpdateSourceTrigger=PropertyChanged}" AutomationProperties.Name="Price per kWh" />
                </StackPanel>
                <StackPanel Style="{StaticResource Row}">
                    <TextBlock Style="{StaticResource Label}" Text="Currency" />
                    <TextBox Style="{StaticResource Field}" Width="56" MaxLength="3" CharacterCasing="Upper" Text="{Binding Currency, UpdateSourceTrigger=PropertyChanged}" AutomationProperties.Name="Currency" />
                </StackPanel>
                <StackPanel Style="{StaticResource Row}">
                    <TextBlock Style="{StaticResource Label}" Text="Since" />
                    <DatePicker Width="132" SelectedDate="{Binding From}" AutomationProperties.Name="First day of the tariff" />
                </StackPanel>
                <TextBlock Style="{StaticResource Said}" Text="{Binding Message}" Visibility="{Binding Message, Converter={StaticResource VisibleWhenText}}" />
                </StackPanel>
            </StackPanel>

            <!-- 2: the machine -->
            <StackPanel Visibility="{Binding Step, Converter={StaticResource VisibleWhenStep}, ConverterParameter={x:Static local:SetupStep.Machine}}">
                <TextBlock Style="{StaticResource Prose}" FontSize="16" Text="Is this your machine?" />
                <TextBlock Style="{StaticResource Prose}" Foreground="{DynamicResource Brush.Ink2}" Text="{Binding Detected, StringFormat=Detected: {0}}" />
                <StackPanel DataContext="{Binding Machine}" IsEnabled="{Binding IsLoaded}">
                    <StackPanel Style="{StaticResource Row}">
                        <TextBlock Style="{StaticResource Label}" Text="Chassis" />
                        <RadioButton Style="{StaticResource Choice}" Content="Laptop"
                                     IsChecked="{Binding Chassis, Converter={StaticResource Is}, ConverterParameter={x:Static contracts:ChassisKind.Laptop}}" />
                        <RadioButton Style="{StaticResource Choice}" Content="Desktop"
                                     IsChecked="{Binding Chassis, Converter={StaticResource Is}, ConverterParameter={x:Static contracts:ChassisKind.Desktop}}" />
                    </StackPanel>
                    <StackPanel Style="{StaticResource Row}" Visibility="{Binding IsDesktop, Converter={StaticResource VisibleWhen}}">
                        <TextBlock Style="{StaticResource Label}" Text="Power supply rating" />
                        <RadioButton Style="{StaticResource Choice}" Content="White" IsChecked="{Binding PsuTier, Converter={StaticResource Is}, ConverterParameter={x:Static contracts:PsuTier.White}}" />
                        <RadioButton Style="{StaticResource Choice}" Content="Bronze" IsChecked="{Binding PsuTier, Converter={StaticResource Is}, ConverterParameter={x:Static contracts:PsuTier.Bronze}}" />
                        <RadioButton Style="{StaticResource Choice}" Content="Silver" IsChecked="{Binding PsuTier, Converter={StaticResource Is}, ConverterParameter={x:Static contracts:PsuTier.Silver}}" />
                        <RadioButton Style="{StaticResource Choice}" Content="Gold" IsChecked="{Binding PsuTier, Converter={StaticResource Is}, ConverterParameter={x:Static contracts:PsuTier.Gold}}" />
                        <RadioButton Style="{StaticResource Choice}" Content="Platinum" IsChecked="{Binding PsuTier, Converter={StaticResource Is}, ConverterParameter={x:Static contracts:PsuTier.Platinum}}" />
                        <RadioButton Style="{StaticResource Choice}" Content="Titanium" IsChecked="{Binding PsuTier, Converter={StaticResource Is}, ConverterParameter={x:Static contracts:PsuTier.Titanium}}" />
                    </StackPanel>
                    <StackPanel Style="{StaticResource Row}">
                        <TextBlock Style="{StaticResource Label}" Text="External monitors" />
                        <TextBox Style="{StaticResource Field}" Width="56" Text="{Binding ExternalMonitors, UpdateSourceTrigger=PropertyChanged}" AutomationProperties.Name="External monitors" />
                        <CheckBox Style="{StaticResource Tick}" Content="Count them, at" Margin="14,0,8,0" IsChecked="{Binding IncludeMonitors}" />
                        <TextBox Style="{StaticResource Field}" Width="56" Text="{Binding MonitorWatts, UpdateSourceTrigger=PropertyChanged}" AutomationProperties.Name="Watts per monitor" />
                        <TextBlock Style="{StaticResource Text.Muted}" Text="W each" VerticalAlignment="Center" Margin="10,0,0,0" />
                    </StackPanel>
                    <StackPanel Style="{StaticResource Row}">
                        <TextBlock Style="{StaticResource Label}" Text="Extras such as lights and pumps, W" />
                        <TextBox Style="{StaticResource Field}" Width="56" Text="{Binding ExtrasWatts, UpdateSourceTrigger=PropertyChanged}" AutomationProperties.Name="Extras in watts" />
                    </StackPanel>
                    <TextBlock Style="{StaticResource Text.Muted}" TextWrapping="Wrap" Margin="0,8,0,0"
                               Text="External monitors have their own plugs, so they count only if you tick the box. The rest of the machine, memory and drives among them, is in Settings." />
                    <TextBlock Style="{StaticResource Said}" Text="{Binding Message}" Visibility="{Binding Message, Converter={StaticResource VisibleWhenText}}" />
                </StackPanel>
            </StackPanel>

            <!-- 3: the readings -->
            <StackPanel Visibility="{Binding Step, Converter={StaticResource VisibleWhenStep}, ConverterParameter={x:Static local:SetupStep.Readings}}">
                <TextBlock Style="{StaticResource Prose}" FontSize="16" Text="Measured, calibrated or estimated" />
                <TextBlock Style="{StaticResource Prose}" Text="Every reading says how it was known." />
                <TextBlock Style="{StaticResource Prose}" Foreground="{DynamicResource Brush.Ink2}"
                           Text="Measured: on battery, Windows reports how fast the battery drains, which is the whole machine's power." />
                <TextBlock Style="{StaticResource Prose}" Foreground="{DynamicResource Brush.Ink2}"
                           Text="Calibrated: plugged in, a model uses the processor's and graphics card's load with a baseline it learned on battery. Expect it within 10%." />
                <TextBlock Style="{StaticResource Prose}" Foreground="{DynamicResource Brush.Ink2}"
                           Text="Estimated: without a battery to learn from, the model works from load and the machine profile alone. Expect it within 20%." />
                <TextBlock Style="{StaticResource Prose}" Text="{Binding Readings}" />
            </StackPanel>

            <TextBlock Style="{StaticResource Said}" Text="{Binding Message}" Visibility="{Binding Message, Converter={StaticResource VisibleWhenText}}" />
            <StackPanel Orientation="Horizontal" Margin="0,24,0,0">
                <Button Style="{StaticResource Quiet}" Content="Back" Command="{Binding Back}" Margin="0,0,8,0"
                        Visibility="{Binding IsFirst, Converter={StaticResource HiddenWhen}}" />
                <Button Style="{StaticResource Quiet}" Content="Next" Command="{Binding Next}"
                        Visibility="{Binding IsLast, Converter={StaticResource HiddenWhen}}" />
                <Button Style="{StaticResource Quiet}" Content="Start using PowerLedger" Command="{Binding Finish}"
                        Visibility="{Binding IsLast, Converter={StaticResource VisibleWhen}}" />
            </StackPanel>
        </StackPanel>
    </ScrollViewer>
</UserControl>
```

`src/PowerLedger.App/Setup/WizardView.xaml.cs`
```csharp
using System.Windows.Controls;

namespace PowerLedger.App;

/// <summary>The first-run wizard's layout (spec §9); everything it shows comes from <see cref="WizardViewModel"/>.</summary>
public partial class WizardView : UserControl
{
    public WizardView() => InitializeComponent();
}
```

In `src/PowerLedger.App/Controls/Converters.cs`, add after `VisibleWhenText`:

```csharp
/// <summary>Shows an element while a value equals the converter parameter: a wizard step's panel while that step is current.</summary>
internal sealed class VisibleWhenStep : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => Equals(value, parameter) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
```

and in `src/PowerLedger.App/Theme/Styles.xaml` add `<local:VisibleWhenStep x:Key="VisibleWhenStep" />` after `VisibleWhenText`.

`src/PowerLedger.App/Shell/ShellViewModel.cs`
```csharp
using CommunityToolkit.Mvvm.ComponentModel;

namespace PowerLedger.App;

/// <summary>The four screens of spec §9's rail.</summary>
internal enum Page
{
    Now,
    Breakdown,
    Report,
    Settings,
}

/// <summary>The window: which page shows, the screens, the first-run wizard while it runs, and the version in the title bar.</summary>
internal sealed class ShellViewModel : ObservableObject
{
    private Page _page = Page.Now;
    private bool _isSetup;

    public ShellViewModel(
        NowViewModel now, BreakdownViewModel breakdown, ReportViewModel report, SettingsViewModel settings, WizardViewModel wizard, string version)
    {
        Now = now;
        Breakdown = breakdown;
        Report = report;
        Settings = settings;
        Wizard = wizard;
        Version = version;
        Wizard.Finished += EndSetup;
        Settings.SetupRequested += BeginSetup;
    }

    public NowViewModel Now { get; }

    public BreakdownViewModel Breakdown { get; }

    public ReportViewModel Report { get; }

    public SettingsViewModel Settings { get; }

    public WizardViewModel Wizard { get; }

    public string Version { get; }

    /// <summary>The page shown. A screen that reads history or the service reads while it shows and stops when it does not.</summary>
    public Page Page
    {
        get => _page;
        set
        {
            if (!SetProperty(ref _page, value)) return;
            ShowPage();
            OnPropertyChanged(nameof(Current));
        }
    }

    /// <summary>The wizard has the window: the rail and the screens wait until it is finished.</summary>
    public bool IsSetup
    {
        get => _isSetup;
        private set
        {
            if (!SetProperty(ref _isSetup, value)) return;
            ShowPage();
            OnPropertyChanged(nameof(Current));
        }
    }

    public object Current => IsSetup ? Wizard : Page switch
    {
        Page.Now => Now,
        Page.Breakdown => Breakdown,
        Page.Report => Report,
        _ => Settings,
    };

    /// <summary>Shows the first-run wizard from its first step.</summary>
    public void BeginSetup()
    {
        Wizard.Start();
        IsSetup = true;
    }

    /// <summary>The wizard is done: back to the Now screen.</summary>
    public void EndSetup()
    {
        _page = Page.Now;
        OnPropertyChanged(nameof(Page));
        IsSetup = false;
    }

    /// <summary>Lets only the screen on show read, and none while the wizard runs.</summary>
    private void ShowPage()
    {
        var shown = IsSetup ? (Page?)null : Page;
        if (shown == Page.Breakdown) Breakdown.Show();
        else Breakdown.Hide();
        if (shown == Page.Report) Report.Show();
        else Report.Hide();
        if (shown == Page.Settings) Settings.Show();
        else Settings.Hide();
    }
}
```

In `src/PowerLedger.App/Shell/MainWindow.xaml`, add after the `SettingsViewModel` data template:

```xml
        <DataTemplate DataType="{x:Type local:WizardViewModel}">
            <local:WizardView />
        </DataTemplate>
```

Hide the rail during setup by giving the rail's `Border` (the one in `Grid.Column="0"`) `Visibility="{Binding IsSetup, Converter={StaticResource HiddenWhen}}"`, and let the content take both columns then by replacing

```xml
                <ContentControl Grid.Column="1" Content="{Binding Current}" Focusable="False" />
```

with

```xml
                <ContentControl Content="{Binding Current}" Focusable="False">
                    <ContentControl.Style>
                        <Style TargetType="ContentControl">
                            <Setter Property="Grid.Column" Value="1" />
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding IsSetup}" Value="True">
                                    <Setter Property="Grid.Column" Value="0" />
                                    <Setter Property="Grid.ColumnSpan" Value="2" />
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </ContentControl.Style>
                </ContentControl>
```

In `src/PowerLedger.App/App.xaml.cs`, add the field `private WizardViewModel? _wizard;`, add `_wizard?.Dispose();` after `_settings?.Dispose();` in `ExitUi`, and replace the shell's construction with:

```csharp
        _wizard = new WizardViewModel(_link, history, _preferences, threads, TimeProvider.System, zone, culture, RegionCurrency());
        _shell = new ShellViewModel(_now, _breakdown, _report, _settings, _wizard, version);
```

In `ShowWindow`, start setup the first time the window shows before the first run is done, before `_window.Show();`:

```csharp
        if (_preferences is { Current.FirstRunDone: false } && !_shell.IsSetup) _shell.BeginSetup();   // spec §9: the first window is the wizard
```

In `tests/PowerLedger.App.Tests/RenderingTests.cs`, add a page, `(Page.Now, "wizard", shell => shell.BeginSetup(), shell => new WizardView { DataContext = shell.Wizard })`. Call `if (shell.IsSetup) shell.EndSetup();` after each page's window closes. Make the shell with a wizard:

```csharp
    private static WizardViewModel WizardScreen()
    {
        var link = new FakeLink();
        link.Connect(true);
        return new WizardViewModel(link, new FakeMachineHistory(), new FakeUiSettings(), UiThreads.Inline, new FakeTimeProvider(Now),
            TimeZoneInfo.Utc, English, "USD");
    }
```

- [x] **Step 4: Run tests, and look**

Run: `dotnet test tests/PowerLedger.App.Tests --filter "Category!=UI"`
Expected: all pass.

Run: `dotnet test tests/PowerLedger.App.Tests --filter "Category=UI"` and open `wizard-Dark.png` and `wizard-Light.png`.
Expected: the window without its rail, "SET UP POWERLEDGER" in amber, "1 · TARIFF" lit, the price, currency ("USD") and date fields, and a Next button.

- [x] **Step 5: Commit**

```bash
git add -A src/PowerLedger.App tests/PowerLedger.App.Tests
git commit -m "Show the first-run wizard in the window until it is finished, and let Settings run it again"
```

---

### Task 10: See it, and finish

**Files:**
- Modify: `src/PowerLedger.App/History/RangeHistory.cs`, `src/PowerLedger.App/History/HistoryReader.cs`, `src/PowerLedger.App/Report/ReportData.cs`
- Modify: `tests/PowerLedger.App.Tests/Reports.cs`, `tests/PowerLedger.App.Tests/ReportDataTests.cs`
- Modify: `docs/superpowers/specs/2026-09-08-powerledger-design.md`, this plan

- [x] **Step 1: Draw everything, and look**

Run: `dotnet test tests/PowerLedger.App.Tests --filter "Category=UI"` and open `settings-Dark-full.png`, `settings-Light-full.png`, `wizard-Dark.png` and `wizard-Light.png` in `%TEMP%\powerledger-renders`.
Expected:
- Settings shows the tariff history and form, the machine section under "Detected: …", the sampling segments, the calibration line, the preferences, and About with its sources.
- The wizard fills the window without the rail, with "1 · TARIFF" lit.

- [x] **Step 2: A real run**

Start the development service and the App against it, as in Plan D2. Drive them with UI Automation, typing into text boxes by their automation names and pressing buttons by their text:
- **The wizard.** It shows at once, because `ui.json` has no `FirstRunDone`. A price typed on the first step reaches the service's tariffs table from that day's local midnight. The machine step saves the profile, and the last step reads "This machine has a battery and a processor energy meter…". Finishing writes `FirstRunDone` to `ui.json` and shows the Now screen.
- **Settings.** It shows the service's settings, the tariff history, and the region's currency (PKR on this laptop) for a new tariff. "Save tariff" answers "Saved: $0.17 / kWh from 15 Sep 2026." and the history gains the line. "Run setup again" brings the wizard back.
- **Report.** It prices the month from the new tariff.

Delete `%LOCALAPPDATA%\PowerLedger\ui.json` afterwards if the run created it.

The run showed three problems. The code in Tasks 5, 6, 8 and 9 already carries the fixes for the first two, and Step 3 makes the third:

| Seen | Fix |
|---|---|
| The window, and so the wizard, opens before the pipe connects, so the machine step never filled. Its form showed the enums' zeros, Desktop and White, as if chosen. | The wizard, and Settings while it shows, read the service again when it connects, filling only an empty form so nothing typed is lost. The form starts at the default profile's values. |
| The wizard's labels cut off "Extras such as lights and pumps, W". | The labels are 250 px wide. |
| The Report's cost note gave the average price: "€0.2501 / kWh" for a few watt-hours, and too low for any range where the tariff began part-way, which is every new user's first month. | The note gives the tariff in force at the end of the range, and the day it began when that was inside the range: "€0.25 / kWh from 15 Sep". |

- [x] **Step 3: Quote the tariff in force**

In `src/PowerLedger.App/History/RangeHistory.cs`, give the report the tariff:

```csharp
/// <summary>A range's totals, days and chart buckets, from one set of reads so they agree, and the tariff in force at its end.</summary>
internal sealed record RangeReport(
    DateRange Range, RangeTotals Totals, IReadOnlyList<DayTotals> Days, IReadOnlyList<Aggregate> Series, Tariff? Tariff = null);
```

In `src/PowerLedger.App/History/HistoryReader.cs`, read it with the range:

```csharp
            var (totals, days) = queries.Report(range.From, range.To, zone);
            var tariff = new TariffRepository(database).Schedule().At(range.To > range.From ? range.To.AddTicks(-1) : range.From);
            return new RangeReport(range, totals, days, queries.Series(range.From, range.To, range.Bucket), tariff);
```

In `src/PowerLedger.App/Report/ReportData.cs`, pass `CostNoteOf(t, report.Tariff, report.Range, zone, culture)` and replace `CostNoteOf` with:

```csharp
    /// <summary>The tariff in force at the range's end, and the day it started when that was inside the range, since
    /// energy before then was priced otherwise or not at all; or why there is no single price.</summary>
    private static string CostNoteOf(RangeTotals t, Tariff? tariff, DateRange range, TimeZoneInfo zone, CultureInfo culture)
    {
        if (t.Currency is null || tariff is null) return "no tariff set";
        if (t.CostIsPartial) return "partial: energy priced in an earlier currency is left out";
        var rate = Money.Rate(tariff.PricePerKwh, tariff.Currency, culture) + " / kWh";
        return tariff.EffectiveFrom > range.From
            ? $"{rate} from {TimeZoneInfo.ConvertTime(tariff.EffectiveFrom, zone).ToString("d MMM", culture)}"
            : rate;
    }
```

In `tests/PowerLedger.App.Tests/Reports.cs`, give `Typical` a tariff that began before the range: `new RangeReport(range, Totals(range, kwh), days, series, new Tariff(range.From.AddDays(-30), 0.17m, "USD"))`. In `tests/PowerLedger.App.Tests/ReportDataTests.cs`, expect `"$0.17 / kWh"` where D2 expected the average, and add:

```csharp
    [Fact]
    public void A_tariff_that_began_inside_the_range_says_from_when()
    {
        var range = Ranges.ThisMonth(Now, Utc, English);
        var data = Month(r => r with { Tariff = new Tariff(new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero), 0.25m, "USD") });
        data.CostNote.ShouldBe("$0.25 / kWh from 5 Sep");
    }
```

- [x] **Step 4: Verify everything**

Run: `dotnet build -c Release`, then `dotnet test` for each test project, with and without `--filter "Category!=Hardware&Category!=UI"`.
Expected: 0 warnings, and:

| Project | All | CI filter |
|---|---|---|
| Core | 110 | 110 |
| Storage | 47 | 47 |
| Sensors | 99 | 94 |
| Service | 127 | 125 |
| App | 224 | 222 |

- [x] **Step 5: Update the spec**

In `docs/superpowers/specs/2026-09-08-powerledger-design.md`:
- §8: the server check.
- §9: what Settings holds and how it saves.
- §9: the wizard's steps, the region's currency without a suggested rate, when it shows, and its reading again on connect.
- §9: the Report's cost note.
- §11: changes go only to the installed service.

- [x] **Step 6: Commit**

```bash
git add docs src/PowerLedger.App tests/PowerLedger.App.Tests
git commit -m "Complete Plan D3: Settings, the first-run wizard, and changes over the pipe"
```

**Rules Plan E must follow.** These are contracts the code relies on and the installer must keep:

- **Paths.** Install both programs, published for win-x64 (framework-dependent, spec §13), under `C:\Program Files\PowerLedger\`.
- **The service's name.** Register it as exactly `PowerLedger`, the name `InstalledServiceCheck.ServiceName` reads. Its `ImagePath` must be the quoted path to `PowerLedger.Service.exe`; the App compares the pipe server's executable with it. Start it automatically as LocalSystem, with `scripts/dev-service.ps1`'s recovery actions.
- **The data folder.** Leave `C:\ProgramData\PowerLedger` to the service. It creates the folder with its ACL and refuses one another account owns, so an elevated installer that made the folder first would stop it. On uninstall, ask whether to keep the folder.
- **Start with Windows.** It is on by default, and the value is `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\PowerLedger` = `"<path>\PowerLedger.exe" --tray`. The installer runs elevated, so it must write this, or start the App that writes it, as the user who ran it, not the elevating account.
- **The first window.** After installing, start the App as that user without `--tray`: its first window is the wizard.
- **Uninstall.** Stop and delete the service, remove the Run entry, and leave `%LOCALAPPDATA%\PowerLedger` and the Documents reports alone.

**Known gaps, left for later:**

- The CO₂ country table and suggested tariffs wait for the owner to choose their sources (spec §16).
- The drop-down calendar of the date pickers keeps Windows' light look in the dark theme.
- Settings fills the service's form each time it shows, so values typed and not saved are lost when leaving the page.
- The bundled fonts are not in the repository yet; downloading them needs the owner's go-ahead.

---

## Self-review against the spec

| Spec | Where |
|---|---|
| §8 `setSettings`, `setTariff`, `resetCalibration` from the App | Tasks 2, 5, 6 |
| §8 and §11: changes go only to the installed service (Plan C's rule) | Tasks 1, 2 |
| §9 Settings: tariff with history, CO₂ factor, machine profile, idle threshold, sample interval, retention, calibration status and reset, theme, start with Windows, About | Tasks 3, 4, 5, 6, 7 |
| §9 First-run wizard: tariff, confirm detected hardware, measured against estimated | Tasks 3, 8, 9 |
| D2's rules: the CO₂ factor and theme at run time, a new tariff on the next refresh, Settings reading while it shows | Tasks 4, 6, 7 |
| §16: the CO₂ table and tariff suggestions stay open | Decisions, Task 10 |
