# PowerLedger Plan D1 — App shell and the Now screen — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the first half of `PowerLedger.App`: the tray icon with the live watts, the window in the "Meter & Ledger" design, and the Now screen fed live by the service's pipe and from the database read-only.

**Architecture:** One `PipeServiceLink` owns the connection to the service: it subscribes, passes frames on, answers requests by id, reports idle time every five seconds and reconnects with backoff. One `HistoryReader` reads the service's database read-only. `NowViewModel` joins the two into immutable panel records that the view binds to, replaced whole on each reading, so the screen never shows half an update. Charts and the meter are small `FrameworkElement`s drawn with `DrawingContext`, whose geometry lives in pure functions that are tested.

**Tech Stack:** WPF on .NET 10 (`net10.0-windows`), `CommunityToolkit.Mvvm` 8.4.2, WinForms `NotifyIcon` for the tray, Plan A's `PowerLedger.Storage` read-only, Plan C's pipe contracts. Tests: xUnit, Shouldly, `Microsoft.Extensions.TimeProvider.Testing` 10.10.0. Spec: `docs/superpowers/specs/2026-09-08-powerledger-design.md` §3, §8, §9.

**Git rule (from the owner):** commit locally after every task. Never add a remote, push, or create a GitHub repo.

**Scope note:** Plan D is split in two so each half ships working software. D1 (this plan) is the shell: tray, window, theme, the pipe client, history reading and the Now screen. The rail shows Breakdown, Report and Settings, which say they arrive in the next build. D2 adds those three screens, the first-run wizard, exports and the monthly PDF. Plan E is the installer.

---

## What already exists

`main` holds Plans A, B and C. The App builds on:

- `PowerLedger.Contracts`: `PipeProtocol.PipeName` (`PowerLedger.v1`), `MessageChannel` (newline-delimited JSON over a stream, 64 KB lines, serialised writes), the message kinds (`SubscribeRequest`, `GetStatusRequest`, `GetSettingsRequest`, `ReportActivityRequest`, `OkReply`, `ErrorReply`, `StatusReply`, `SettingsReply`), `ReadingFrame` with `DisplayBandW` and `RestBandW`, `ServiceStatus`, `ServiceSettings`, `Quality`.
- `PowerLedger.Storage`: `SqliteDatabase(path, readOnly: true)`, `ReportQueries.Report(from, to, zone)` and `Totals(from, to)` returning `RangeTotals` and `DayTotals`, `AggregateRepository.ReadMinutes`, `TariffRepository.Schedule().At(now)`, `InventoryRepository.Latest()`.
- `PowerLedger.Core`: `Aggregate`, `Tariff`, `Co2.DefaultKgPerKwh`.
- The service: `\\.\pipe\PowerLedger.v1`, data in `C:\ProgramData\PowerLedger\power.db`; a development run takes `--data <folder> --pipe PowerLedger.dev`.

Plan C's rules for Plan D that D1 must honour: send `reportActivity` every 5 s; correlate replies by id, since frames arrive between a request and its reply; reconnect with backoff from 1 s to 30 s and subscribe again; open the database read-only and show the service as not running when that fails. The server check before sending settings belongs to D2, which is the first to send any.

## Decisions made while planning

- **No WPF-UI and no LiveCharts2.** The "Meter & Ledger" design is bespoke: graphite panels, hairlines, tick rulers, one amber accent. WPF-UI's Fluent styles would be overridden almost everywhere, and LiveCharts2 brings SkiaSharp's native libraries for four simple drawings. The window uses WPF's own `WindowChrome`; the meter, budget bar, sparkline and day chart are `FrameworkElement`s drawn with `DrawingContext`, as spec §9 already asked for the meter and the budget bar. The spec's stack line is updated in Task 15.
- **Fonts fall back to Windows' own until the bundled ones arrive.** Spec §9 names Archivo, Archivo Narrow and Martian Mono, bundled as static TTFs. Downloading them needs the owner's go-ahead, so the font resources name them first and fall back to Bahnschrift (in every Windows 10 since 1709) and Cascadia Mono, then Consolas. Dropping the TTFs into `src/PowerLedger.App/Fonts/` later switches the App over with no code change.
- **The tray icon is WinForms' `NotifyIcon`.** WPF has none. `UseWindowsForms` brings WinForms' global usings, which collide with WPF names such as `Application` and `Color`, so a build target removes them.
- **Panels are immutable records replaced whole.** `NowViewModel` exposes `Live`, `Today`, `Month`, `Chart` and `Status` records rather than forty properties. Each reading replaces `Live`, each history read replaces the rest, and the view never shows a mix of two readings.
- **Everything the view shows is a string made in the view model**, with the user's culture, except the numbers the drawings need. The view has no converters beyond visibility and page selection, and the tests read exactly what the user sees.
- **The service-down state waits three seconds.** At start the link has not connected yet; the banner appears only if it still has not after `ConnectGrace`.
- **A rendering test draws the Now screen to PNG** in both themes from fake data, so the design can be checked without clicking through a live window.

## File structure

```
src/PowerLedger.App/
  PowerLedger.App.csproj        WinExe, net10.0-windows, WPF + WinForms, assembly name PowerLedger
  app.manifest                  per-monitor DPI awareness, Windows 10 and 11
  App.xaml, App.xaml.cs         composition: single instance, theme, link, history, view models, tray, window
  AppOptions.cs                 --pipe, --data, --tray
  Formatting/Format.cs          watts, kWh, durations, kilograms, percents
  Formatting/Money.cs           money and prices in the tariff's currency, the user's way
  Preferences/UiPreferences.cs  ui.json: theme and CO2 factor
  Theme/ThemeManager.cs         theme rules, system theme, palette swapping
  Theme/Palette.Dark.xaml       instrument palette (spec §9 tokens)
  Theme/Palette.Light.xaml      bench-sheet palette
  Theme/Styles.xaml             fonts, text styles, rail, badges, ledger rows, banners, caption buttons
  Service/ServiceLink.cs        IServiceLink, IIdleSource, LastInputIdleSource, PipeServiceLink
  History/DaySlots.cs           today's five-minute slots from minute rows
  History/HistoryReader.cs      IHistory, HistorySnapshot, MachineNames, HistoryReader
  Now/LiveWindow.cs             the last 60 s of readings
  Now/MeterRange.cs             the meter's full scale and ticks
  Now/Budget.cs                 the four bands and their shares
  Now/MonthOutlook.cs           month to date, projection, days that stood out
  Now/Panels.cs                 the records the Now screen binds to
  Now/NowViewModel.cs           the Now screen
  Now/NowView.xaml(.cs)         its layout
  Controls/Instrument.cs        base element: brushes, fonts, text drawing
  Controls/Geometry.cs          pure geometry: scale x, spark range, ruler steps, chart scale
  Controls/MeterScale.cs, BudgetBar.cs, Sparkline.cs, DayChart.cs, LiveReadout.cs
  Controls/QualityBadge.cs, LedgerRow.cs   templated in Styles.xaml
  Controls/Converters.cs        visibility and page selection
  Shell/ShellViewModel.cs       pages, the placeholder for D2's screens
  Shell/MainWindow.xaml(.cs)    chrome, rail, content, status bar
  Tray/TrayIcon.cs              NotifyIcon with the watts drawn as text
  Tray/StartWithWindows.cs      the HKCU Run entry
  Tray/SingleInstance.cs        one App per session
  Tray/ServiceStarter.cs        "Start service" through UAC
tests/PowerLedger.App.Tests/
  PowerLedger.App.Tests.csproj  net10.0-windows, WPF
  Frames.cs, Statuses.cs, WaitFor.cs, FakeService.cs, FakeLink.cs, FakeHistory.cs
  FormatTests.cs, UiPreferencesTests.cs, ThemeRulesTests.cs, ServiceLinkTests.cs, DaySlotsTests.cs,
  HistoryReaderTests.cs, NowMathTests.cs, NowViewModelTests.cs, GeometryTests.cs, TrayTests.cs,
  AppOptionsTests.cs, RenderingTests.cs
```

**Rules.** The App never writes the database (spec §3). Nothing in a view model touches WPF types, so view models are tested without a dispatcher; the UI thread is reached only through the `post` action the App passes in. A drawing control holds no logic beyond calling `Geometry` and drawing the result.

---

### Task 1: Scaffold the App projects

**Files:**
- Create: `src/PowerLedger.App/PowerLedger.App.csproj`
- Create: `src/PowerLedger.App/app.manifest`
- Create: `src/PowerLedger.App/App.xaml`
- Create: `src/PowerLedger.App/App.xaml.cs`
- Create: `tests/PowerLedger.App.Tests/PowerLedger.App.Tests.csproj`
- Modify: `PowerLedger.sln`

- [ ] **Step 1: Write the project files**

`src/PowerLedger.App/PowerLedger.App.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <AssemblyName>PowerLedger</AssemblyName>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <SatelliteResourceLanguages>en</SatelliteResourceLanguages>
    <!-- WFO0003 wants DPI awareness set through WinForms' startup API, which a WPF app never calls; WPF reads it from the manifest. -->
    <NoWarn>$(NoWarn);WFO0003</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\PowerLedger.Contracts\PowerLedger.Contracts.csproj" />
    <ProjectReference Include="..\PowerLedger.Core\PowerLedger.Core.csproj" />
    <ProjectReference Include="..\PowerLedger.Storage\PowerLedger.Storage.csproj" />
  </ItemGroup>

  <ItemGroup>
    <Resource Include="Fonts\*.ttf" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="PowerLedger.App.Tests" />
  </ItemGroup>

  <!-- WinForms is here only for the tray icon; its global usings would collide with WPF's Application, Color and more. -->
  <Target Name="DropWinFormsGlobalUsings" BeforeTargets="GenerateGlobalUsings">
    <ItemGroup>
      <Using Remove="System.Drawing" />
      <Using Remove="System.Windows.Forms" />
    </ItemGroup>
  </Target>

</Project>
```

`src/PowerLedger.App/app.manifest`
```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="PowerLedger.App"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="asInvoker" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <!-- Windows 10 and 11 -->
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
    </application>
  </compatibility>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
</assembly>
```

`src/PowerLedger.App/App.xaml`
```xml
<Application x:Class="PowerLedger.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown">
    <Application.Resources />
</Application>
```

`src/PowerLedger.App/App.xaml.cs`
```csharp
using System.Windows;

namespace PowerLedger.App;

/// <summary>The tray App (spec §9). Task 13 composes it; until then it starts and stops.</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Shutdown();
    }
}
```

`tests/PowerLedger.App.Tests/PowerLedger.App.Tests.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
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
    <ProjectReference Include="..\..\src\PowerLedger.App\PowerLedger.App.csproj" />
    <ProjectReference Include="..\..\src\PowerLedger.Contracts\PowerLedger.Contracts.csproj" />
    <ProjectReference Include="..\..\src\PowerLedger.Core\PowerLedger.Core.csproj" />
    <ProjectReference Include="..\..\src\PowerLedger.Storage\PowerLedger.Storage.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Add both projects to the solution**

```bash
dotnet sln PowerLedger.sln add src/PowerLedger.App/PowerLedger.App.csproj --solution-folder src
dotnet sln PowerLedger.sln add tests/PowerLedger.App.Tests/PowerLedger.App.Tests.csproj --solution-folder tests
```

- [ ] **Step 3: Build**

Run: `dotnet build -c Release`
Expected: `Build succeeded.` with 0 warnings. `src/PowerLedger.App/obj/Release/net10.0-windows/PowerLedger.GlobalUsings.g.cs` names neither `System.Drawing` nor `System.Windows.Forms`.

- [ ] **Step 4: Commit**

```bash
git add src/PowerLedger.App tests/PowerLedger.App.Tests PowerLedger.sln
git commit -m "Add the App project and its tests"
```

---

### Task 2: How numbers and money are written

**Files:**
- Create: `src/PowerLedger.App/Formatting/Format.cs`
- Create: `src/PowerLedger.App/Formatting/Money.cs`
- Test: `tests/PowerLedger.App.Tests/FormatTests.cs`

Spec §9: every number in a tabular numeral font, so columns line up like a bill; spec §10: money is `decimal` end to end, in the tariff's ISO 4217 currency, formatted with `CultureInfo`. Money keeps the user's way of writing numbers and borrows only the currency's symbol, so a US reader sees "€0.05" for a euro tariff, not a German "0,05 €".

- [ ] **Step 1: Write the failing tests**

`tests/PowerLedger.App.Tests/FormatTests.cs`
```csharp
using System.Globalization;
using Shouldly;

namespace PowerLedger.App.Tests;

public class FormatTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    [Fact]
    public void Live_watts_have_one_decimal_and_never_go_negative()
    {
        Format.Watts(34.23, Invariant).ShouldBe("34.2");
        Format.Watts(-0.4, Invariant).ShouldBe("0.0");
        Format.Watts(double.NaN, Invariant).ShouldBe("–");
        Format.Watts(34.23, German).ShouldBe("34,2");
    }

    [Fact]
    public void Energy_keeps_three_significant_figures()
    {
        Format.Kwh(0.2841, Invariant).ShouldBe("0.284");
        Format.Kwh(2.744, Invariant).ShouldBe("2.74");
        Format.Kwh(10.32, Invariant).ShouldBe("10.3");
        Format.Kwh(274.4, Invariant).ShouldBe("274");
    }

    [Fact]
    public void Durations_read_as_hours_and_minutes()
    {
        Format.Duration(7 + 5 / 60.0).ShouldBe("7h 05m");
        Format.Duration(0.75).ShouldBe("45m");
        Format.Duration(0).ShouldBe("0m");
        Format.Duration(double.NaN).ShouldBe("0m");
    }

    [Fact]
    public void Kilograms_percents_and_whole_watts()
    {
        Format.Kg(0.1079, Invariant).ShouldBe("0.11");
        Format.Percent(0.427, Invariant).ShouldBe("43%");
        Format.Percent(1.2, Invariant).ShouldBe("100%");
        Format.WholeWatts(41.4, Invariant).ShouldBe("41");
    }

    [Fact]
    public void Money_uses_the_currency_symbol_and_the_reader_s_number_style()
    {
        Money.Format(0.0483m, "USD", English).ShouldBe("$0.05");
        Money.Format(0.0483m, "EUR", English).ShouldBe("€0.05");
        Money.Format(1234.5m, "USD", German).Replace(' ', ' ').ShouldBe("1.234,50 $");   // ICU puts a no-break space there
    }

    [Fact]
    public void A_currency_without_a_known_symbol_is_written_with_its_code()
        => Money.Format(5m, "XXX", English).ShouldBe("5.00 XXX");

    [Fact]
    public void A_price_keeps_the_decimals_it_needs_up_to_four()
    {
        Money.Rate(0.17m, "USD", English).ShouldBe("$0.17");
        Money.Rate(0.125m, "USD", English).ShouldBe("$0.125");
        Money.Rate(0.12345m, "USD", English).ShouldBe("$0.1235");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.App.Tests --filter FormatTests`
Expected: build error, `Format` not found.

- [ ] **Step 3: Write the formatters**

`src/PowerLedger.App/Formatting/Format.cs`
```csharp
using System.Globalization;

namespace PowerLedger.App;

/// <summary>How every number on screen is written (spec §9). Each method takes the culture to write in; the App passes the
/// user's. Nothing negative or undefined reaches the screen: a dash stands in for a value that does not exist.</summary>
internal static class Format
{
    public const string Missing = "–";

    /// <summary>The live reading, one decimal: "34.2".</summary>
    public static string Watts(double watts, CultureInfo culture)
        => double.IsFinite(watts) ? Math.Max(0, watts).ToString("0.0", culture) : Missing;

    /// <summary>Averages, peaks, watt-hours and scale labels, whole: "41".</summary>
    public static string WholeWatts(double watts, CultureInfo culture)
        => double.IsFinite(watts) ? Math.Round(Math.Max(0, watts)).ToString("0", culture) : Missing;

    /// <summary>Energy to three significant figures: 0.284, 2.74, 27.4, 274.</summary>
    public static string Kwh(double kwh, CultureInfo culture)
    {
        if (!double.IsFinite(kwh)) return Missing;
        kwh = Math.Max(0, kwh);
        var pattern = kwh < 1 ? "0.000" : kwh < 10 ? "0.00" : kwh < 100 ? "0.0" : "0";
        return kwh.ToString(pattern, culture);
    }

    /// <summary>A length of time given in hours: "7h 05m", "45m", "0m".</summary>
    public static string Duration(double hours)
    {
        var minutes = double.IsFinite(hours) ? (long)Math.Round(Math.Max(0, hours) * 60) : 0;
        return minutes >= 60
            ? (minutes / 60).ToString(CultureInfo.InvariantCulture) + "h " + (minutes % 60).ToString("00", CultureInfo.InvariantCulture) + "m"
            : minutes.ToString(CultureInfo.InvariantCulture) + "m";
    }

    /// <summary>Kilograms, two decimals: "0.11".</summary>
    public static string Kg(double kg, CultureInfo culture)
        => double.IsFinite(kg) ? Math.Max(0, kg).ToString("0.00", culture) : Missing;

    /// <summary>A share of a whole, as a whole percent: "43%".</summary>
    public static string Percent(double share, CultureInfo culture)
        => double.IsFinite(share) ? Math.Round(Math.Clamp(share, 0, 1) * 100).ToString("0", culture) + "%" : Missing;
}
```

`src/PowerLedger.App/Formatting/Money.cs`
```csharp
using System.Collections.Concurrent;
using System.Globalization;

namespace PowerLedger.App;

/// <summary>Money in the tariff's currency (spec §10), written the way the reader's culture writes money but with the
/// currency's own symbol. A currency no culture knows is written with its ISO code after the amount.</summary>
internal static class Money
{
    private static readonly ConcurrentDictionary<string, string?> Symbols = new(StringComparer.Ordinal);

    public static string Format(decimal amount, string currency, CultureInfo culture, int decimals = 2)
    {
        if (SymbolFor(currency) is not { } symbol)
        {
            return amount.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), culture) + " " + currency;
        }
        var numbers = (NumberFormatInfo)culture.NumberFormat.Clone();
        numbers.CurrencySymbol = symbol;
        numbers.CurrencyDecimalDigits = decimals;
        return amount.ToString("C", numbers);
    }

    /// <summary>A price per kWh: two decimals, or up to four when the price needs them ("$0.17", "$0.125").</summary>
    public static string Rate(decimal price, string currency, CultureInfo culture)
    {
        var decimals = 2;
        while (decimals < 4 && decimal.Round(price, decimals) != price) decimals++;
        return Format(decimal.Round(price, decimals, MidpointRounding.AwayFromZero), currency, culture, decimals);
    }

    /// <summary>
    /// The symbol for an ISO 4217 code, or null when no region uses it. The reader's own region wins when it uses the
    /// currency, then the currency's home country (the code's first two letters, as with USD and US), then any region.
    /// </summary>
    internal static string? SymbolFor(string currency) => Symbols.GetOrAdd(currency, static code =>
    {
        if (RegionInfo.CurrentRegion.ISOCurrencySymbol == code) return RegionInfo.CurrentRegion.CurrencySymbol;
        if (Region(code[..Math.Min(2, code.Length)]) is { } home && home.ISOCurrencySymbol == code) return home.CurrencySymbol;
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            if (Region(culture.Name) is { } region && region.ISOCurrencySymbol == code) return region.CurrencySymbol;
        }
        return null;
    });

    private static RegionInfo? Region(string name)
    {
        try
        {
            return new RegionInfo(name);
        }
        catch (ArgumentException)
        {
            return null;   // not a region, or a culture with no region of its own
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter FormatTests`
Expected: `Passed! - Failed: 0, Passed: 7`.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.App/Formatting tests/PowerLedger.App.Tests/FormatTests.cs
git commit -m "Write numbers and money the user's way, in the tariff's currency"
```

---

### Task 3: UI preferences

**Files:**
- Create: `src/PowerLedger.App/Preferences/UiPreferences.cs`
- Test: `tests/PowerLedger.App.Tests/UiPreferencesTests.cs`

Spec §9: UI-only preferences live in `%LOCALAPPDATA%\PowerLedger\ui.json`; everything else is service-owned. D1 needs two: the theme choice and the CO₂ factor, which the App alone uses because the report is the App's. A damaged file is not an error: the App starts from the defaults. Saving writes a temporary file and moves it into place, so a crash never leaves half a file.

- [ ] **Step 1: Write the failing tests**

`tests/PowerLedger.App.Tests/UiPreferencesTests.cs`
```csharp
using System.IO;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class UiPreferencesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"powerledger-ui-{Guid.NewGuid():N}");

    private string File => Path.Combine(_folder, "ui.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void With_no_file_the_defaults_apply()
    {
        var preferences = new UiPreferencesStore(File).Load();
        preferences.Theme.ShouldBe(ThemeChoice.System);
        preferences.Co2KgPerKwh.ShouldBe(0.40);
    }

    [Fact]
    public void Preferences_survive_a_save_and_a_load()
    {
        var store = new UiPreferencesStore(File);
        store.Save(new UiPreferences { Theme = ThemeChoice.Light, Co2KgPerKwh = 0.23 });
        store.Load().ShouldBe(new UiPreferences { Theme = ThemeChoice.Light, Co2KgPerKwh = 0.23 });
        System.IO.File.ReadAllText(File).ShouldContain("\"Light\"");
    }

    [Fact]
    public void A_damaged_file_gives_the_defaults()
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, "{ not json");
        new UiPreferencesStore(File).Load().ShouldBe(UiPreferences.Default);
    }

    [Fact]
    public void A_CO2_factor_out_of_range_falls_back_to_the_default()
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, """{ "Theme": "Dark", "Co2KgPerKwh": 55 }""");
        new UiPreferencesStore(File).Load().ShouldBe(new UiPreferences { Theme = ThemeChoice.Dark });
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.App.Tests --filter UiPreferencesTests`
Expected: build error, `UiPreferencesStore` not found.

- [ ] **Step 3: Write the preferences**

`src/PowerLedger.App/Preferences/UiPreferences.cs`
```csharp
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PowerLedger.Core;

namespace PowerLedger.App;

/// <summary>Which palette to use: Windows' app mode, or one of the two forced.</summary>
internal enum ThemeChoice
{
    System,
    Dark,
    Light,
}

/// <summary>What only the App cares about (spec §9).</summary>
internal sealed record UiPreferences
{
    /// <summary>Grid intensity at or above this is not a real grid; the value is refused.</summary>
    public const double MaxCo2KgPerKwh = 2;

    public ThemeChoice Theme { get; init; } = ThemeChoice.System;

    /// <summary>Kilograms of CO₂ per kWh used for every CO₂ figure; spec §9's default is the world average.</summary>
    public double Co2KgPerKwh { get; init; } = Co2.DefaultKgPerKwh;

    public static UiPreferences Default { get; } = new();

    /// <summary>The same preferences with anything out of range put back to its default.</summary>
    public UiPreferences Sanitised() => this with
    {
        Theme = Enum.IsDefined(Theme) ? Theme : ThemeChoice.System,
        Co2KgPerKwh = double.IsFinite(Co2KgPerKwh) && Co2KgPerKwh >= 0 && Co2KgPerKwh < MaxCo2KgPerKwh ? Co2KgPerKwh : Co2.DefaultKgPerKwh,
    };
}

/// <summary>Reads and writes ui.json. Reading never fails: a missing or damaged file gives the defaults.</summary>
internal sealed class UiPreferencesStore(string path)
{
    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerLedger", "ui.json");

    public UiPreferences Load()
    {
        try
        {
            if (!File.Exists(path)) return UiPreferences.Default;
            return (JsonSerializer.Deserialize(File.ReadAllText(path), UiJson.Default.UiPreferences) ?? UiPreferences.Default).Sanitised();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return UiPreferences.Default;
        }
    }

    public void Save(UiPreferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(preferences, UiJson.Default.UiPreferences));
        File.Move(temporary, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(UiPreferences))]
internal sealed partial class UiJson : JsonSerializerContext;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter UiPreferencesTests`
Expected: `Passed! - Failed: 0, Passed: 4`.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.App/Preferences tests/PowerLedger.App.Tests/UiPreferencesTests.cs
git commit -m "Keep the App's own preferences in ui.json"
```

---

### Task 4: The two palettes and the theme that picks between them

**Files:**
- Create: `src/PowerLedger.App/Theme/Palette.Dark.xaml`
- Create: `src/PowerLedger.App/Theme/Palette.Light.xaml`
- Create: `src/PowerLedger.App/Theme/ThemeManager.cs`
- Test: `tests/PowerLedger.App.Tests/ThemeRulesTests.cs`

Spec §9's tokens, one brush each, with the same keys in both palettes so every view names a token and never a colour. Both themes follow Windows' app mode by default and can be forced. The palette is swapped in the application's merged dictionaries, and every view refers to it with `DynamicResource`, so a switch repaints at once. `Brush.AmberSoft` is the amber at 14 % for the sparkline's fill.

- [ ] **Step 1: Write the failing test**

`tests/PowerLedger.App.Tests/ThemeRulesTests.cs`
```csharp
using Shouldly;

namespace PowerLedger.App.Tests;

public class ThemeRulesTests
{
    [Theory]
    [InlineData(ThemeChoice.System, false, Theme.Dark)]
    [InlineData(ThemeChoice.System, true, Theme.Light)]
    [InlineData(ThemeChoice.Dark, true, Theme.Dark)]
    [InlineData(ThemeChoice.Light, false, Theme.Light)]
    public void The_choice_wins_and_system_follows_windows(ThemeChoice choice, bool windowsUsesLight, Theme expected)
        => ThemeRules.Resolve(choice, windowsUsesLight).ShouldBe(expected);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PowerLedger.App.Tests --filter ThemeRulesTests`
Expected: build error, `ThemeRules` not found.

- [ ] **Step 3: Write the palettes and the manager**

`src/PowerLedger.App/Theme/Palette.Dark.xaml`
```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- "Instrument": spec §9 dark tokens. -->
    <SolidColorBrush x:Key="Brush.Ground" Color="#1B1D1A" />
    <SolidColorBrush x:Key="Brush.Panel" Color="#222522" />
    <SolidColorBrush x:Key="Brush.Raised" Color="#2B2F2B" />
    <SolidColorBrush x:Key="Brush.Line" Color="#363B36" />
    <SolidColorBrush x:Key="Brush.LineStrong" Color="#4B514B" />
    <SolidColorBrush x:Key="Brush.Ink" Color="#ECE9DF" />
    <SolidColorBrush x:Key="Brush.Ink2" Color="#A6A99E" />
    <SolidColorBrush x:Key="Brush.Ink3" Color="#71766F" />
    <SolidColorBrush x:Key="Brush.Amber" Color="#F2B233" />
    <SolidColorBrush x:Key="Brush.AmberSoft" Color="#24F2B233" />
    <SolidColorBrush x:Key="Brush.OnAmber" Color="#1B1D1A" />
    <SolidColorBrush x:Key="Brush.Measured" Color="#8FCB8B" />
    <SolidColorBrush x:Key="Brush.Calibrated" Color="#8FB6D4" />
    <SolidColorBrush x:Key="Brush.Estimated" Color="#B9AE93" />
    <SolidColorBrush x:Key="Brush.PartCpu" Color="#E39B3B" />
    <SolidColorBrush x:Key="Brush.PartGpu" Color="#6F97C4" />
    <SolidColorBrush x:Key="Brush.PartDisplay" Color="#B5C46A" />
    <SolidColorBrush x:Key="Brush.PartRest" Color="#6E736D" />
</ResourceDictionary>
```

`src/PowerLedger.App/Theme/Palette.Light.xaml`
```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- "Bench sheet": spec §9 light tokens. -->
    <SolidColorBrush x:Key="Brush.Ground" Color="#E4E6E0" />
    <SolidColorBrush x:Key="Brush.Panel" Color="#EFF0EC" />
    <SolidColorBrush x:Key="Brush.Raised" Color="#F8F8F5" />
    <SolidColorBrush x:Key="Brush.Line" Color="#CDD0C8" />
    <SolidColorBrush x:Key="Brush.LineStrong" Color="#AEB2A9" />
    <SolidColorBrush x:Key="Brush.Ink" Color="#1D1F1B" />
    <SolidColorBrush x:Key="Brush.Ink2" Color="#575B53" />
    <SolidColorBrush x:Key="Brush.Ink3" Color="#858980" />
    <SolidColorBrush x:Key="Brush.Amber" Color="#A2680C" />
    <SolidColorBrush x:Key="Brush.AmberSoft" Color="#24A2680C" />
    <SolidColorBrush x:Key="Brush.OnAmber" Color="#FFFFFF" />
    <SolidColorBrush x:Key="Brush.Measured" Color="#3E7E43" />
    <SolidColorBrush x:Key="Brush.Calibrated" Color="#35678A" />
    <SolidColorBrush x:Key="Brush.Estimated" Color="#7C7355" />
    <SolidColorBrush x:Key="Brush.PartCpu" Color="#C4761C" />
    <SolidColorBrush x:Key="Brush.PartGpu" Color="#4A76A6" />
    <SolidColorBrush x:Key="Brush.PartDisplay" Color="#7C8A2E" />
    <SolidColorBrush x:Key="Brush.PartRest" Color="#8E928A" />
</ResourceDictionary>
```

`src/PowerLedger.App/Theme/ThemeManager.cs`
```csharp
using System.Windows;
using Microsoft.Win32;

namespace PowerLedger.App;

internal enum Theme
{
    Dark,
    Light,
}

/// <summary>Spec §9: both themes follow the system by default and can be forced in Settings.</summary>
internal static class ThemeRules
{
    public static Theme Resolve(ThemeChoice choice, bool windowsUsesLight) => choice switch
    {
        ThemeChoice.Dark => Theme.Dark,
        ThemeChoice.Light => Theme.Light,
        _ => windowsUsesLight ? Theme.Light : Theme.Dark,
    };
}

/// <summary>
/// Keeps the application's palette in step with the choice and, for System, with Windows' app mode. The palette is the
/// first merged dictionary; everything else refers to its brushes with DynamicResource, so a swap repaints at once.
/// </summary>
internal sealed class ThemeManager : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly Application _application;
    private ThemeChoice _choice;
    private ResourceDictionary? _palette;

    public ThemeManager(Application application, ThemeChoice choice)
    {
        _application = application;
        _choice = choice;
        Apply();
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
    }

    public Theme Current { get; private set; }

    public void Choose(ThemeChoice choice)
    {
        _choice = choice;
        Apply();
    }

    /// <summary>Windows' app mode; dark when it cannot be read.</summary>
    public static bool WindowsUsesLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
    }

    /// <summary>A palette as a fresh dictionary, for the application or for a test that draws a view.</summary>
    public static ResourceDictionary Palette(Theme theme)
        => new() { Source = new Uri($"pack://application:,,,/PowerLedger;component/Theme/Palette.{theme}.xaml", UriKind.Absolute) };

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;

    private void OnPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color) _application.Dispatcher.InvokeAsync(Apply);
    }

    private void Apply()
    {
        var theme = ThemeRules.Resolve(_choice, WindowsUsesLight());
        if (_palette is not null && theme == Current) return;
        var palette = Palette(theme);
        var merged = _application.Resources.MergedDictionaries;
        if (_palette is not null) merged.Remove(_palette);
        merged.Insert(0, palette);
        _palette = palette;
        Current = theme;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PowerLedger.App.Tests --filter ThemeRulesTests`
Expected: `Passed! - Failed: 0, Passed: 4`.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.App/Theme tests/PowerLedger.App.Tests/ThemeRulesTests.cs
git commit -m "Add the instrument and bench-sheet palettes, following Windows by default"
```

---

### Task 5: The link to the service

**Files:**
- Create: `src/PowerLedger.App/Service/ServiceLink.cs`
- Create: `tests/PowerLedger.App.Tests/Frames.cs`
- Create: `tests/PowerLedger.App.Tests/Statuses.cs`
- Create: `tests/PowerLedger.App.Tests/WaitFor.cs`
- Create: `tests/PowerLedger.App.Tests/FakeService.cs`
- Test: `tests/PowerLedger.App.Tests/ServiceLinkTests.cs`

Spec §8 and Plan C's rules. The link connects, subscribes, and from then on one read loop takes every message: frames go to `FrameReceived`, replies complete the request that carries their id. Idle time from `GetLastInputInfo` goes every five seconds, because the service in session 0 cannot see input. When the connection ends the link says so, fails whatever was waiting, and tries again after 1, 2, 4, 8, 16 and then every 30 seconds. The test service is a real named pipe on a unique name, so the framing, ids and reconnects are the real ones; only the clock is fake.

- [ ] **Step 1: Write the test helpers and the failing tests**

`tests/PowerLedger.App.Tests/Frames.cs`
```csharp
using PowerLedger.Contracts;

namespace PowerLedger.App.Tests;

/// <summary>Readings as the service would push them.</summary>
internal static class Frames
{
    public static ReadingFrame At(
        DateTimeOffset at, double totalW = 34.2, Quality quality = Quality.Measured,
        double cpu = 14.6, double gpu = 4.1, double display = 4.0, bool gpuMeasured = false, double? gpuLoad = 0.31)
        => new(at, 1, totalW, quality,
               new ComponentWatts(cpu, gpu, display, 0, 0, 0, 0, 0, 0, totalW - cpu - gpu - display),
               CpuMeasured: true, GpuMeasured: gpuMeasured,
               OnBattery: quality == Quality.Measured, DisplayOn: true, UserIdle: false, SessionLocked: false,
               CpuLoad: 0.3, GpuLoad: gpuLoad, Brightness: 0.6, Suspect: false);
}
```

`tests/PowerLedger.App.Tests/Statuses.cs`
```csharp
using PowerLedger.Contracts;

namespace PowerLedger.App.Tests;

/// <summary>Service statuses as the service would report them.</summary>
internal static class Statuses
{
    public static ServiceStatus Running(bool energyMeter = true, bool battery = true) => new(
        "0.1.0+b688a18", DateTimeOffset.UnixEpoch, 10,
        [
            new SourceStatus("energy-meter", energyMeter, energyMeter ? null : "this machine publishes no processor power rails", 0, null),
            new SourceStatus("battery", battery, battery ? null : "no battery fitted", 0, null),
        ],
        SuspectTicks: 0, SensorRestarts: 0, new CalibrationStatus(900, 1800, 0, 12), "hash",
        DatabaseBytes: 31L * 1024 * 1024, WriteProblem: null, DatabaseNotice: null, Last: null);
}
```

`tests/PowerLedger.App.Tests/WaitFor.cs`
```csharp
namespace PowerLedger.App.Tests;

/// <summary>Waits in real time for something another thread does, with a timeout so a broken test fails instead of hanging.</summary>
internal static class WaitFor
{
    public static async Task True(Func<bool> condition, int timeoutMs = 5000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("The condition did not become true in time.");
            await Task.Delay(10);
        }
    }
}
```

`tests/PowerLedger.App.Tests/FakeService.cs`
```csharp
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using PowerLedger.Contracts;

namespace PowerLedger.App.Tests;

/// <summary>
/// A stand-in for the service's end of the pipe: it answers subscribe, status, settings and activity reports, pushes
/// frames on demand, and can stop and start again like a restarting service.
/// </summary>
internal sealed class FakeService(string name) : IAsyncDisposable
{
    private CancellationTokenSource _stop = new();
    private Task _serving = Task.CompletedTask;
    private volatile MessageChannel? _client;

    public ConcurrentQueue<PipeRequest> Requests { get; } = new();

    public ServiceStatus Status { get; set; } = Statuses.Running();

    public bool HasClient => _client is not null;

    public void Start()
    {
        _stop = new CancellationTokenSource();
        _serving = ServeAsync(_stop.Token);
    }

    public async Task StopAsync()
    {
        if (_stop.IsCancellationRequested) return;
        await _stop.CancelAsync();
        try
        {
            await _serving;
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task PushAsync(PipeMessage message)
    {
        await WaitFor.True(() => HasClient);
        await _client!.WriteAsync(message);
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task ServeAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(stop);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync();
                return;
            }

            var channel = new MessageChannel(server);
            await using (channel)
            {
                _client = channel;
                try
                {
                    while (await channel.ReadAsync(stop) is PipeRequest request)
                    {
                        Requests.Enqueue(request);
                        await channel.WriteAsync(Reply(request), stop);
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    // The client left, or the test stopped the service.
                }
                finally
                {
                    _client = null;
                }
            }
        }
    }

    private PipeMessage Reply(PipeRequest request) => request switch
    {
        GetStatusRequest r => new StatusReply(r.Id, Status),
        GetSettingsRequest r => new SettingsReply(r.Id, ServiceSettings.Default),
        _ => new OkReply(request.Id),
    };
}
```

`tests/PowerLedger.App.Tests/ServiceLinkTests.cs`
```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class ServiceLinkTests : IAsyncLifetime
{
    private readonly string _name = $"PowerLedger.app-test.{Guid.NewGuid():N}";
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UnixEpoch);
    private readonly ConcurrentQueue<ReadingFrame> _frames = new();
    private readonly ConcurrentQueue<bool> _changes = new();
    private readonly FakeService _service;
    private readonly PipeServiceLink _link;

    public ServiceLinkTests()
    {
        _service = new FakeService(_name);
        _link = new PipeServiceLink(_name, new FixedIdle(42), _clock);
        _link.FrameReceived += _frames.Enqueue;
        _link.ConnectionChanged += _changes.Enqueue;
    }

    public async Task InitializeAsync()
    {
        _service.Start();
        _link.Start();
        await WaitFor.True(() => _link.IsConnected);
    }

    public async Task DisposeAsync()
    {
        await _link.DisposeAsync();
        await _service.DisposeAsync();
    }

    [Fact]
    public async Task Once_connected_the_link_has_subscribed_and_passes_frames_on()
    {
        _service.Requests.OfType<SubscribeRequest>().Count().ShouldBe(1);
        await _service.PushAsync(Frames.At(DateTimeOffset.UnixEpoch, totalW: 12));
        await WaitFor.True(() => !_frames.IsEmpty);
        _frames.Single().TotalW.ShouldBe(12);
        _changes.ToArray().ShouldBe(new[] { true });
    }

    [Fact]
    public async Task Status_and_settings_come_back_for_the_request_that_asked()
    {
        (await _link.GetStatusAsync()).ShouldNotBeNull().Version.ShouldBe("0.1.0+b688a18");
        (await _link.GetSettingsAsync()).ShouldBe(ServiceSettings.Default);
    }

    [Fact]
    public async Task Idle_time_is_reported_on_connecting_and_every_five_seconds()
    {
        await WaitFor.True(() => _service.Requests.OfType<ReportActivityRequest>().Any());
        _service.Requests.OfType<ReportActivityRequest>().Single().IdleSeconds.ShouldBe(42);
        _clock.Advance(PipeServiceLink.ActivityEvery);
        await WaitFor.True(() => _service.Requests.OfType<ReportActivityRequest>().Count() == 2);
    }

    [Fact]
    public async Task When_the_service_goes_away_the_link_says_so_and_comes_back_by_itself()
    {
        await _service.StopAsync();
        await WaitFor.True(() => !_link.IsConnected);
        (await _link.GetStatusAsync()).ShouldBeNull();

        _service.Start();
        await WaitFor.True(() =>
        {
            _clock.Advance(TimeSpan.FromSeconds(1));   // lets the backoff run out, however far it has got
            return _link.IsConnected;
        });
        _changes.ToArray().ShouldBe(new[] { true, false, true });
        _service.Requests.OfType<SubscribeRequest>().Count().ShouldBe(2);
    }

    private sealed class FixedIdle(double seconds) : IIdleSource
    {
        public double IdleSeconds() => seconds;
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.App.Tests --filter ServiceLinkTests`
Expected: build error, `PipeServiceLink` not found.

- [ ] **Step 3: Write the link**

`src/PowerLedger.App/Service/ServiceLink.cs`
```csharp
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>What the App needs from the service (spec §8). Events are raised on a background thread; a handler must not
/// throw and must hand its work to the UI thread itself.</summary>
internal interface IServiceLink : IAsyncDisposable
{
    /// <summary>A reading the service pushed, once a sample interval while connected.</summary>
    event Action<ReadingFrame>? FrameReceived;

    /// <summary>True once connected and subscribed; false when the connection is lost.</summary>
    event Action<bool>? ConnectionChanged;

    bool IsConnected { get; }

    void Start();

    /// <summary>Null when not connected, or when the service did not answer in time.</summary>
    Task<ServiceStatus?> GetStatusAsync(CancellationToken cancel = default);

    /// <summary>Null when not connected, or when the service did not answer in time.</summary>
    Task<ServiceSettings?> GetSettingsAsync(CancellationToken cancel = default);
}

/// <summary>Seconds since the last keyboard or mouse input in this session.</summary>
internal interface IIdleSource
{
    double IdleSeconds();
}

/// <summary>The App runs in the user's session, so GetLastInputInfo describes the person at the keyboard. The service, in
/// session 0, cannot see that, which is why the App reports it (Plan C's rules for Plan D).</summary>
internal sealed class LastInputIdleSource : IIdleSource
{
    public double IdleSeconds()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.Time) / 1000.0 : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}

/// <summary>
/// The App's end of \\.\pipe\PowerLedger.v1. It connects, subscribes, passes frames on, answers requests by id and
/// reports idle time every five seconds. When the service goes away it tries again after 1, 2, 4, 8, 16 and then every
/// 30 seconds (spec §8), and subscribes again once back.
/// </summary>
internal sealed class PipeServiceLink(string pipeName, IIdleSource idle, TimeProvider clock) : IServiceLink
{
    public static readonly TimeSpan ActivityEvery = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan[] Backoff = [.. new[] { 1, 2, 4, 8, 16, 30 }.Select(s => TimeSpan.FromSeconds(s))];
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<PipeMessage>> _pending = new();
    private long _lastId;
    private volatile MessageChannel? _channel;
    private Task? _run;

    public event Action<ReadingFrame>? FrameReceived;

    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _channel is not null;

    public void Start() => _run ??= Task.Run(() => RunAsync(_stop.Token));

    public async Task<ServiceStatus?> GetStatusAsync(CancellationToken cancel = default)
        => await SendAsync(new GetStatusRequest(NextId()), cancel).ConfigureAwait(false) is StatusReply reply ? reply.Status : null;

    public async Task<ServiceSettings?> GetSettingsAsync(CancellationToken cancel = default)
        => await SendAsync(new GetSettingsRequest(NextId()), cancel).ConfigureAwait(false) is SettingsReply reply ? reply.Settings : null;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_run is not null) await _run.ConfigureAwait(false);
        _stop.Dispose();
    }

    private long NextId() => Interlocked.Increment(ref _lastId);

    private async Task RunAsync(CancellationToken stop)
    {
        var failures = 0;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await stream.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, stop).ConfigureAwait(false);
                failures = 0;
                await ServeAsync(stream, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException or PipeProtocolException)
            {
                // Not running, busy, or gone: try again after the backoff.
            }

            try
            {
                await Task.Delay(Backoff[Math.Min(failures, Backoff.Length - 1)], clock, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            failures++;
        }
    }

    /// <summary>One connection, from subscribing to its end.</summary>
    private async Task ServeAsync(Stream stream, CancellationToken stop)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(stop);
        await using var channel = new MessageChannel(stream);
        var reading = ReadAsync(channel, connection.Token);
        Task? reporting = null;
        try
        {
            if (await RequestAsync(channel, new SubscribeRequest(NextId()), connection.Token).ConfigureAwait(false) is not OkReply) return;
            _channel = channel;
            ConnectionChanged?.Invoke(true);
            reporting = ReportActivityAsync(channel, connection.Token);
            await Task.WhenAny(reading, reporting).ConfigureAwait(false);
        }
        finally
        {
            var connected = _channel is not null;
            _channel = null;
            await connection.CancelAsync().ConfigureAwait(false);
            FailPending();
            await Quietly(reading).ConfigureAwait(false);
            if (reporting is not null) await Quietly(reporting).ConfigureAwait(false);
            if (connected) ConnectionChanged?.Invoke(false);
        }
    }

    /// <summary>Every message the service sends, until the connection ends: frames go out, replies find their request.</summary>
    private async Task ReadAsync(MessageChannel channel, CancellationToken cancel)
    {
        while (await channel.ReadAsync(cancel).ConfigureAwait(false) is { } message)
        {
            switch (message)
            {
                case ReadingFrame frame:
                    FrameReceived?.Invoke(frame);
                    break;
                case OkReply reply:
                    Answer(reply.Id, reply);
                    break;
                case StatusReply reply:
                    Answer(reply.Id, reply);
                    break;
                case SettingsReply reply:
                    Answer(reply.Id, reply);
                    break;
                case ErrorReply { Id: { } id } reply:
                    Answer(id, reply);
                    break;
                default:
                    return;   // an error with no id: the service is closing this connection
            }
        }
    }

    private async Task ReportActivityAsync(MessageChannel channel, CancellationToken cancel)
    {
        using var timer = new PeriodicTimer(ActivityEvery, clock);
        do
        {
            await RequestAsync(channel, new ReportActivityRequest(NextId(), idle.IdleSeconds()), cancel).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(cancel).ConfigureAwait(false));
    }

    private async Task<PipeMessage?> SendAsync(PipeRequest request, CancellationToken cancel)
    {
        if (_channel is not { } channel) return null;
        try
        {
            return await RequestAsync(channel, request, cancel).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or TimeoutException
                                      || (error is OperationCanceledException && !cancel.IsCancellationRequested))
        {
            return null;
        }
    }

    private async Task<PipeMessage> RequestAsync(MessageChannel channel, PipeRequest request, CancellationToken cancel)
    {
        var reply = new TaskCompletionSource<PipeMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = reply;
        try
        {
            await channel.WriteAsync(request, cancel).ConfigureAwait(false);
            return await reply.Task.WaitAsync(RequestTimeout, clock, cancel).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
        }
    }

    private void Answer(long id, PipeMessage reply)
    {
        if (_pending.TryRemove(id, out var waiting)) waiting.TrySetResult(reply);
    }

    private void FailPending()
    {
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var waiting)) waiting.TrySetException(new IOException("The connection to the service closed."));
        }
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException
                                          or PipeProtocolException or TimeoutException)
        {
            // The connection is over; how it ended does not matter here.
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter ServiceLinkTests`
Expected: `Passed! - Failed: 0, Passed: 4`.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.App/Service tests/PowerLedger.App.Tests/Frames.cs tests/PowerLedger.App.Tests/Statuses.cs tests/PowerLedger.App.Tests/WaitFor.cs tests/PowerLedger.App.Tests/FakeService.cs tests/PowerLedger.App.Tests/ServiceLinkTests.cs
git commit -m "Link the App to the service: subscribe, requests by id, idle reports, reconnect with backoff"
```

---

### Task 6: Today's five-minute slots

**Files:**
- Create: `src/PowerLedger.App/History/DaySlots.cs`
- Test: `tests/PowerLedger.App.Tests/DaySlotsTests.cs`

The Now screen's chart is today in five-minute slots stacked by band (spec §9). A slot's watts are its energy over the time the machine was on in it, so a half-asleep slot is not drawn at half height. A minute row's gap is the sleep that ended in that minute (Plan A), so it is laid backwards over the slots the sleep covered, stopping at midnight; that is where the chart hatches "asleep".

- [ ] **Step 1: Write the failing tests**

`tests/PowerLedger.App.Tests/DaySlotsTests.cs`
```csharp
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.App.Tests;

public class DaySlotsTests
{
    private static readonly DateTimeOffset Day = new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);

    private static Aggregate Minute(int minuteOfDay, double cpuWh = 0.2, double restWh = 0.3, double onSeconds = 60, double gapSeconds = 0)
        => Aggregate.Empty(Day.AddMinutes(minuteOfDay)) with
        {
            CpuWh = cpuWh, RestWh = restWh, EnergyWh = cpuWh + restWh, OnSeconds = onSeconds, GapSeconds = gapSeconds,
        };

    [Fact]
    public void A_slot_averages_its_minutes_over_the_time_the_machine_was_on()
    {
        var slots = DaySlots.Build([Minute(0), Minute(1)], Day, Day.AddMinutes(4));
        slots.Count.ShouldBe(1);
        slots[0].CpuW.ShouldBe(12, 1e-9);                          // 0.4 Wh over 120 s
        slots[0].RestW.ShouldBe(18, 1e-9);
        slots[0].TotalW.ShouldBe(30, 1e-9);
        slots[0].HasReadings.ShouldBeTrue();
    }

    [Fact]
    public void Slots_run_up_to_the_one_that_holds_now()
    {
        DaySlots.Build([], Day, Day.AddMinutes(60)).Count.ShouldBe(12);
        DaySlots.Build([], Day, Day.AddMinutes(61)).Count.ShouldBe(13);
        DaySlots.Build([], Day, Day).Count.ShouldBe(1);
    }

    [Fact]
    public void A_sleep_is_laid_backwards_over_the_slots_it_covered()
    {
        // Two hours asleep, closed by a tick in the minute that starts at 02:00, so the sleep ran from 00:01 to 02:01.
        var slots = DaySlots.Build([Minute(120, onSeconds: 30, gapSeconds: 7200)], Day, Day.AddMinutes(125));
        slots[0].AsleepSeconds.ShouldBe(240, 1e-9);
        slots.Skip(1).Take(23).ShouldAllBe(s => Math.Abs(s.AsleepSeconds - 300) < 1e-9);
        slots[24].AsleepSeconds.ShouldBe(60, 1e-9);
        slots[24].OnSeconds.ShouldBe(30);
    }

    [Fact]
    public void A_sleep_that_began_yesterday_stops_at_midnight()
    {
        var slots = DaySlots.Build([Minute(10, gapSeconds: 3600)], Day, Day.AddMinutes(15));
        slots.Sum(s => s.AsleepSeconds).ShouldBe(660, 1e-9);        // 00:00 to 00:11
    }

    [Fact]
    public void A_negative_rest_draws_as_zero()
    {
        var slot = DaySlots.Build([Minute(0, cpuWh: 0.5, restWh: -0.1)], Day, Day.AddMinutes(1))[0];
        slot.RestW.ShouldBe(-6, 1e-9);
        slot.TotalW.ShouldBe(30, 1e-9);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.App.Tests --filter DaySlotsTests`
Expected: build error, `DaySlots` not found.

- [ ] **Step 3: Write the slots**

`src/PowerLedger.App/History/DaySlots.cs`
```csharp
using PowerLedger.Core;

namespace PowerLedger.App;

/// <summary>Five minutes of today's chart. Watts are averages over the time the machine was on in the slot; AsleepSeconds is
/// how much of the slot it slept while the service ran.</summary>
internal sealed record DaySlot(DateTimeOffset Start, double CpuW, double GpuW, double DisplayW, double RestW, double OnSeconds, double AsleepSeconds)
{
    /// <summary>The height to draw. A negative rest, which measured mode shows when the parts over-report, counts as zero
    /// on every chart (spec §9).</summary>
    public double TotalW => CpuW + GpuW + DisplayW + Math.Max(0, RestW);

    public bool HasReadings => OnSeconds > 0;
}

internal static class DaySlots
{
    public static readonly TimeSpan Length = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    /// <summary>One slot per five minutes from <paramref name="dayStart"/> through the slot holding <paramref name="now"/>.</summary>
    public static IReadOnlyList<DaySlot> Build(IReadOnlyList<Aggregate> minutes, DateTimeOffset dayStart, DateTimeOffset now)
    {
        var count = Math.Max(1, (int)Math.Ceiling((now - dayStart) / Length));
        var cpu = new double[count];
        var gpu = new double[count];
        var display = new double[count];
        var rest = new double[count];
        var on = new double[count];
        var asleep = new double[count];

        foreach (var m in minutes)
        {
            var index = (int)Math.Floor((m.Start - dayStart) / Length);
            if (index >= 0 && index < count)
            {
                cpu[index] += m.CpuWh;
                gpu[index] += m.GpuWh;
                display[index] += m.DisplayWh;
                rest[index] += m.RestWh;
                on[index] += m.OnSeconds;
            }
            Lay(asleep, m.Start + Minute, m.GapSeconds, dayStart);
        }

        var slots = new DaySlot[count];
        for (var i = 0; i < count; i++)
        {
            slots[i] = new DaySlot(
                dayStart + i * Length, Watts(cpu[i], on[i]), Watts(gpu[i], on[i]), Watts(display[i], on[i]), Watts(rest[i], on[i]),
                on[i], Math.Min(asleep[i], Length.TotalSeconds));
        }
        return slots;
    }

    /// <summary>Spreads a sleep of <paramref name="seconds"/> that ended at <paramref name="end"/> backwards over the slots it covered.</summary>
    private static void Lay(double[] asleep, DateTimeOffset end, double seconds, DateTimeOffset dayStart)
    {
        var cursor = end;
        while (seconds > 0 && cursor > dayStart)
        {
            var index = (int)Math.Floor((cursor - dayStart - TimeSpan.FromTicks(1)) / Length);
            var slotStart = dayStart + index * Length;
            var taken = Math.Min(seconds, (cursor - slotStart).TotalSeconds);
            if (index < asleep.Length) asleep[index] += taken;
            seconds -= taken;
            cursor = slotStart;
        }
    }

    private static double Watts(double wh, double onSeconds) => onSeconds > 0 ? wh * 3600 / onSeconds : 0;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter DaySlotsTests`
Expected: `Passed! - Failed: 0, Passed: 5`.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.App/History/DaySlots.cs tests/PowerLedger.App.Tests/DaySlotsTests.cs
git commit -m "Cut today into five-minute slots, laying each sleep back over the time it covered"
```

---

### Task 7: History, read-only

**Files:**
- Create: `src/PowerLedger.App/History/HistoryReader.cs`
- Test: `tests/PowerLedger.App.Tests/HistoryReaderTests.cs`

Spec §3: the App never writes the database. It opens it read-only, which works while the service runs even though the App cannot write the `-wal` and `-shm` files (Plan C, verified with SQLite 3.51). One read gathers everything the Now screen needs: today and the month through Plan A's `ReportQueries`, today's slots from the minute rows, the tariff in force and the machine's names from the newest inventory. When the database cannot be opened the read returns null and the screen says the service is not running. Local midnight is the start of today; where a clock change skips midnight, the day starts at the first valid time after it.

- [ ] **Step 1: Write the failing tests**

`tests/PowerLedger.App.Tests/HistoryReaderTests.cs`
```csharp
using System.IO;
using PowerLedger.Core;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class HistoryReaderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 14, 30, 0, TimeSpan.Zero);
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"powerledger-app-{Guid.NewGuid():N}.db");
    private readonly SqliteDatabase _writer;

    public HistoryReaderTests() => _writer = SqliteDatabase.OpenAndMigrate(_path);

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

    private static Aggregate Minute(DateTimeOffset start, double wh = 0.5) => Aggregate.Empty(start) with
    {
        AvgW = wh * 60, MaxW = wh * 60, EnergyWh = wh, CpuWh = wh * 0.4, RestWh = wh * 0.6,
        OnSeconds = 60, MeasuredSeconds = 60, SampleCount = 60,
    };

    [Fact]
    public void One_read_gathers_today_the_month_the_slots_the_tariff_and_the_machine()
    {
        var minutes = new AggregateRepository(_writer);
        minutes.UpsertMinute(Minute(Now.AddMinutes(-30)));                   // 14:00 today
        minutes.UpsertMinute(Minute(Now.AddMinutes(-29)));                   // 14:01 today
        var earlier = Minute(new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero));
        minutes.UpsertMinute(earlier);
        minutes.UpsertHour(Downsampler.ToHour(earlier.Start, [earlier]));    // the service folds finished hours; a month reads them
        new TariffRepository(_writer).Add(new Tariff(Now.AddDays(-30), 0.17m, "USD"));
        new InventoryRepository(_writer).Upsert(new InventoryRecord("hash", Now,
            """{"CpuName":"11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz","GpuName":"NVIDIA GeForce MX330","DisplayDiagonalInches":15.3}"""));

        using var readOnly = new SqliteDatabase(_path, readOnly: true);
        var snapshot = new HistoryReader(readOnly).Read(Now, TimeZoneInfo.Utc).ShouldNotBeNull();

        snapshot.Today.EnergyKwh.ShouldBe(0.001, 1e-12);
        snapshot.Month.EnergyKwh.ShouldBe(0.0015, 1e-12);
        snapshot.MonthDays.Count.ShouldBe(2);
        snapshot.DayStart.ShouldBe(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        snapshot.TodaySlots.Count.ShouldBe(174);                              // 14.5 hours of five-minute slots
        snapshot.TodaySlots[168].CpuW.ShouldBe(12, 1e-9);                     // 14:00: 0.4 Wh of CPU over 120 s
        snapshot.Tariff.ShouldNotBeNull().PricePerKwh.ShouldBe(0.17m);
        snapshot.Machine.ShouldBe(new MachineNames("11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", "NVIDIA GeForce MX330", 15.3));
    }

    [Fact]
    public void A_database_that_cannot_be_opened_reads_as_nothing()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"powerledger-missing-{Guid.NewGuid():N}.db");
        using var database = new SqliteDatabase(missing, readOnly: true);
        new HistoryReader(database).Read(Now, TimeZoneInfo.Utc).ShouldBeNull();
    }

    [Fact]
    public void A_day_whose_midnight_a_clock_change_skips_starts_at_the_first_valid_time()
    {
        var springForwardAtMidnight = TimeZoneInfo.CreateCustomTimeZone("Test Midnight", TimeSpan.Zero, "Test Midnight", "Test", "Test Summer",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2020, 1, 1), new DateTime(2030, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 11, 1)),
        ]);
        var start = HistoryReader.LocalMidnight(new DateTime(2026, 3, 1), springForwardAtMidnight);
        start.ShouldBe(new DateTimeOffset(2026, 3, 1, 1, 0, 0, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void A_damaged_inventory_gives_no_names_rather_than_an_error()
        => HistoryReader.Names(new InventoryRecord("hash", Now, "{ not json")).ShouldBeNull();
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.App.Tests --filter HistoryReaderTests`
Expected: build error, `HistoryReader` not found.

- [ ] **Step 3: Write the reader**

`src/PowerLedger.App/History/HistoryReader.cs`
```csharp
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <param name="Cpu">The processor's name as Windows reports it.</param>
/// <param name="Gpu">The graphics adapter the service names, when there is one.</param>
/// <param name="DisplayDiagonalInches">The built-in panel's size; 0 when the machine has none.</param>
internal sealed record MachineNames(string? Cpu, string? Gpu, double DisplayDiagonalInches);

/// <summary>Everything the Now screen reads from history in one pass, so its parts always agree.</summary>
internal sealed record HistorySnapshot(
    RangeTotals Today, RangeTotals Month, IReadOnlyList<DayTotals> MonthDays, IReadOnlyList<DaySlot> TodaySlots,
    DateTimeOffset DayStart, Tariff? Tariff, MachineNames? Machine);

internal interface IHistory
{
    /// <summary>Null when the database cannot be read, as while the service is stopped.</summary>
    HistorySnapshot? Read(DateTimeOffset now, TimeZoneInfo zone);
}

/// <summary>The App's read-only view of the service's database (spec §3: the App never writes it).</summary>
internal sealed class HistoryReader(SqliteDatabase database) : IHistory
{
    public HistorySnapshot? Read(DateTimeOffset now, TimeZoneInfo zone)
    {
        try
        {
            var local = TimeZoneInfo.ConvertTime(now, zone);
            var dayStart = LocalMidnight(local.Date, zone);
            var monthStart = LocalMidnight(new DateTime(local.Year, local.Month, 1), zone);
            var queries = new ReportQueries(database);
            var today = queries.Totals(dayStart, now);
            var (month, days) = queries.Report(monthStart, now, zone);
            var slots = DaySlots.Build(new AggregateRepository(database).ReadMinutes(dayStart, now), dayStart, now);
            var tariff = new TariffRepository(database).Schedule().At(now);
            var machine = Names(new InventoryRepository(database).Latest());
            return new HistorySnapshot(today, month, days, slots, dayStart, tariff, machine);
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary>Midnight local time on <paramref name="date"/>, or the first valid time after it where a clock change skips midnight.</summary>
    internal static DateTimeOffset LocalMidnight(DateTime date, TimeZoneInfo zone)
    {
        var wall = date.Date;
        while (zone.IsInvalidTime(wall)) wall = wall.AddMinutes(15);
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }

    /// <summary>The names the budget rows show, from the newest inventory the service stored.</summary>
    internal static MachineNames? Names(InventoryRecord? record)
    {
        if (record is null) return null;
        try
        {
            using var json = JsonDocument.Parse(record.Json);
            var root = json.RootElement;
            var inches = root.TryGetProperty("DisplayDiagonalInches", out var size) && size.TryGetDouble(out var value) ? value : 0;
            return new MachineNames(Text(root, "CpuName"), Text(root, "GpuName"), inches);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter HistoryReaderTests`
Expected: `Passed! - Failed: 0, Passed: 4`.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.App/History/HistoryReader.cs tests/PowerLedger.App.Tests/HistoryReaderTests.cs
git commit -m "Read today, the month, the tariff and the machine from the database, read-only"
```

---

### Task 8: The Now screen's arithmetic

**Files:**
- Create: `src/PowerLedger.App/Now/LiveWindow.cs`
- Create: `src/PowerLedger.App/Now/MeterRange.cs`
- Create: `src/PowerLedger.App/Now/Budget.cs`
- Create: `src/PowerLedger.App/Now/MonthOutlook.cs`
- Test: `tests/PowerLedger.App.Tests/NowMathTests.cs`

Four small pure pieces. The live window keeps the last 60 s of readings for the sparkline, however often they arrive. The meter's full scale is the smallest round size at least 10 % above what it must show, so the needle never pins, with ticks that read cleanly at that size (spec §9's example is 0–150 W, majors every 25, minors every 5). The budget is spec §6's four bands with a negative rest shown as zero. The month outlook projects from the daily average over the days recorded, prices the projection and the idle waste at the month's own average price, and picks the lowest and highest complete days; today is not complete, so it never counts as either.

- [ ] **Step 1: Write the failing tests**

`tests/PowerLedger.App.Tests/NowMathTests.cs`
```csharp
using PowerLedger.Contracts;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.App.Tests;

public class NowMathTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 8, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_live_window_keeps_the_last_sixty_seconds()
    {
        var window = new LiveWindow(TimeSpan.FromSeconds(60));
        for (var s = 0; s <= 90; s++) window.Add(T0.AddSeconds(s), s);
        window.Values.Count.ShouldBe(61);
        window.Values[0].ShouldBe(30);
        window.Values[^1].ShouldBe(90);
    }

    [Fact]
    public void The_live_window_ignores_readings_that_are_not_numbers()
    {
        var window = new LiveWindow(TimeSpan.FromSeconds(60));
        window.Add(T0, double.NaN);
        window.Add(T0, -3);
        window.Values.ShouldBe(new[] { 0.0 });
    }

    [Theory]
    [InlineData(0, 25, 5, 1)]
    [InlineData(68, 75, 25, 5)]
    [InlineData(130, 150, 25, 5)]
    [InlineData(140, 200, 50, 10)]
    [InlineData(5000, 2000, 500, 100)]
    public void The_meter_scale_is_the_smallest_round_size_ten_percent_above_the_highest_value(double highest, double max, double major, double minor)
        => MeterRange.For(highest).ShouldBe(new MeterRange(max, major, minor));

    [Fact]
    public void Meter_ticks_run_from_zero_to_full_scale_with_labelled_majors()
    {
        var ticks = MeterRange.For(130).Ticks().ToList();
        ticks.Count.ShouldBe(31);
        ticks.Where(t => t.Major).Select(t => t.Value).ShouldBe(new double[] { 0, 25, 50, 75, 100, 125, 150 });
    }

    [Fact]
    public void The_budget_is_four_bands_and_a_negative_rest_counts_as_zero()
    {
        var shares = Budget.Of(Frames.At(T0, totalW: 34.2, cpu: 14.6, gpu: 4.1, display: 4.0));
        shares.Select(s => s.Part).ShouldBe(new[] { Part.Cpu, Part.Gpu, Part.Display, Part.Rest });
        shares[3].Watts.ShouldBe(11.5, 1e-9);
        shares.Sum(s => s.Share).ShouldBe(1, 1e-9);

        var over = Budget.Of(Frames.At(T0, totalW: 20, cpu: 14.6, gpu: 4.1, display: 4.0));
        over[3].Watts.ShouldBe(0);
        over.Sum(s => s.Share).ShouldBe(1, 1e-9);
    }

    [Fact]
    public void The_month_projects_from_the_daily_average_and_prices_it_at_the_month_s_own_rate()
    {
        var days = Enumerable.Range(1, 8).Select(d => Day(d, 0.3 + d * 0.01)).ToList();
        var month = Month(energyKwh: days.Sum(d => d.EnergyKwh), cost: 0.50m, idleOnKwh: 0.15, idleOffKwh: 0.06);
        var outlook = MonthOutlook.From(month, days, new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));

        outlook.DailyAverageKwh.ShouldNotBeNull().ShouldBe(month.EnergyKwh / 7.5, 1e-9);
        outlook.ProjectedKwh.ShouldNotBeNull().ShouldBe(month.EnergyKwh / 7.5 * 30, 1e-9);
        var price = 0.50m / (decimal)month.EnergyKwh;
        outlook.ProjectedCost.ShouldBe(decimal.Round((decimal)outlook.ProjectedKwh!.Value * price, 2));
        outlook.IdleWasteKwh.ShouldBe(0.21, 1e-9);
        outlook.Lowest!.Day.ShouldBe(new DateOnly(2026, 9, 1));
        outlook.Highest!.Day.ShouldBe(new DateOnly(2026, 9, 7));             // the 8th is today, not yet complete
        outlook.NextReport.ShouldBe(new DateOnly(2026, 10, 1));
        outlook.DaysRecorded.ShouldBe(8);
    }

    [Fact]
    public void Before_a_full_day_is_recorded_there_is_no_projection()
    {
        var outlook = MonthOutlook.From(Month(energyKwh: 0.2, cost: 0.03m), [Day(8, 0.2)], new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        outlook.DailyAverageKwh.ShouldBeNull();
        outlook.ProjectedKwh.ShouldBeNull();
        outlook.Lowest.ShouldBeNull();
    }

    private static DayTotals Day(int day, double kwh)
        => new(new DateOnly(2026, 9, day), kwh, 0, "USD", false, OnHours: 8, PeakW: 60, IdleOnKwh: 0, IdleOffKwh: 0);

    private static RangeTotals Month(double energyKwh, decimal cost, double idleOnKwh = 0, double idleOffKwh = 0) => new(
        T0, T0, energyKwh, cost, "USD", false, AvgW: 30, PeakW: 68, PeakAt: T0,
        OnHours: 60, IdleOnHours: 1, IdleOffHours: 0.5, AsleepHours: 50, UnmonitoredHours: 0,
        CpuKwh: 0, GpuKwh: 0, DisplayKwh: 0, RestKwh: 0, idleOnKwh, idleOffKwh,
        MeasuredShare: 0.62, CalibratedShare: 0.2, EstimatedShare: 0.18);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.App.Tests --filter NowMathTests`
Expected: build error, `LiveWindow` not found.

- [ ] **Step 3: Write the four pieces**

`src/PowerLedger.App/Now/LiveWindow.cs`
```csharp
namespace PowerLedger.App;

/// <summary>The readings of the last <paramref name="span"/>, oldest first, for the sparkline (spec §9: "Last 60 s").</summary>
internal sealed class LiveWindow(TimeSpan span)
{
    private readonly Queue<(DateTimeOffset At, double Watts)> _points = new();

    public IReadOnlyList<double> Values => [.. _points.Select(p => p.Watts)];

    public void Add(DateTimeOffset at, double watts)
    {
        if (!double.IsFinite(watts)) return;
        _points.Enqueue((at, Math.Max(0, watts)));
        while (at - _points.Peek().At > span) _points.Dequeue();
    }

    public void Clear() => _points.Clear();
}
```

`src/PowerLedger.App/Now/MeterRange.cs`
```csharp
namespace PowerLedger.App;

/// <summary>The meter's full scale (spec §9 MeterScale): the smallest round size above what it must show, with ticks that
/// read cleanly at that size.</summary>
internal readonly record struct MeterRange(double Max, double Major, double Minor)
{
    private static readonly MeterRange[] Sizes =
    [
        new(25, 5, 1), new(50, 10, 2), new(75, 25, 5), new(100, 25, 5), new(150, 25, 5), new(200, 50, 10),
        new(300, 50, 10), new(400, 100, 20), new(600, 100, 20), new(800, 200, 40), new(1000, 250, 50),
        new(1500, 250, 50), new(2000, 500, 100),
    ];

    /// <summary>A scale whose top is at least 10 % above <paramref name="highest"/>, so the needle never pins.</summary>
    public static MeterRange For(double highest)
    {
        var need = double.IsFinite(highest) ? Math.Max(0, highest) * 1.1 : 0;
        foreach (var size in Sizes)
        {
            if (size.Max >= need) return size;
        }
        return Sizes[^1];
    }

    /// <summary>Every tick from 0 to Max; the majors carry labels.</summary>
    public IEnumerable<(double Value, bool Major)> Ticks()
    {
        var count = (int)Math.Round(Max / Minor);
        for (var i = 0; i <= count; i++)
        {
            var value = i * Minor;
            yield return (value, Math.Abs(value / Major - Math.Round(value / Major)) < 1e-9);
        }
    }
}
```

`src/PowerLedger.App/Now/Budget.cs`
```csharp
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>Spec §6's four energy bands.</summary>
internal enum Part
{
    Cpu,
    Gpu,
    Display,
    Rest,
}

/// <param name="Watts">The band's watts, never negative.</param>
/// <param name="Share">Its share of the four bands together.</param>
internal sealed record BudgetShare(Part Part, double Watts, double Share);

internal static class Budget
{
    /// <summary>The four bands for one reading. A negative rest, which measured mode shows when the parts over-report,
    /// counts as zero here as on every chart (spec §9).</summary>
    public static IReadOnlyList<BudgetShare> Of(ReadingFrame frame)
    {
        (Part Part, double Watts)[] bands =
        [
            (Part.Cpu, frame.Components.Cpu), (Part.Gpu, frame.Components.Gpu),
            (Part.Display, frame.DisplayBandW), (Part.Rest, frame.RestBandW),
        ];
        var clean = bands.Select(b => (b.Part, Watts: double.IsFinite(b.Watts) ? Math.Max(0, b.Watts) : 0)).ToList();
        var total = clean.Sum(b => b.Watts);
        return [.. clean.Select(b => new BudgetShare(b.Part, b.Watts, total > 0 ? b.Watts / total : 0))];
    }
}
```

`src/PowerLedger.App/Now/MonthOutlook.cs`
```csharp
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>
/// The month-to-date ledger (spec §9, Now screen). The daily average runs over the days recorded, from the first of them,
/// so a machine that joined mid-month is not averaged over days it was not watched; there is none until a full day has
/// passed. The projection and the idle waste are priced at the month's own average price, which follows any tariff
/// change. Lowest and highest are complete days only.
/// </summary>
internal sealed record MonthOutlook(
    double EnergyKwh, decimal Cost, string? Currency, bool CostIsPartial, double MeasuredShare,
    double? DailyAverageKwh, double? ProjectedKwh, decimal? ProjectedCost,
    double IdleWasteKwh, decimal IdleWasteCost,
    DayTotals? Lowest, DayTotals? Highest, int DaysRecorded, DateOnly NextReport)
{
    public static MonthOutlook From(RangeTotals month, IReadOnlyList<DayTotals> days, DateTimeOffset localNow)
    {
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var price = month.EnergyKwh > 0 ? month.Cost / (decimal)month.EnergyKwh : 0m;

        double? average = null;
        double? projected = null;
        decimal? projectedCost = null;
        if (days.Count > 0)
        {
            var elapsed = (localNow.DateTime - days[0].Day.ToDateTime(TimeOnly.MinValue)).TotalDays;
            if (elapsed >= 1)
            {
                average = month.EnergyKwh / elapsed;
                projected = average * DateTime.DaysInMonth(today.Year, today.Month);
                projectedCost = decimal.Round((decimal)projected.Value * price, 2);
            }
        }

        var complete = days.Where(d => d.Day < today && d.OnHours > 0).ToList();
        var idle = month.IdleOnKwh + month.IdleOffKwh;
        return new MonthOutlook(
            month.EnergyKwh, month.Cost, month.Currency, month.CostIsPartial, month.MeasuredShare,
            average, projected, projectedCost,
            idle, decimal.Round((decimal)idle * price, 2),
            complete.MinBy(d => d.EnergyKwh), complete.MaxBy(d => d.EnergyKwh), days.Count,
            new DateOnly(today.Year, today.Month, 1).AddMonths(1));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter NowMathTests`
Expected: `Passed! - Failed: 0, Passed: 11`.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.App/Now/LiveWindow.cs src/PowerLedger.App/Now/MeterRange.cs src/PowerLedger.App/Now/Budget.cs src/PowerLedger.App/Now/MonthOutlook.cs tests/PowerLedger.App.Tests/NowMathTests.cs
git commit -m "Add the Now screen's arithmetic: the last minute, the meter scale, the budget and the month"
```

---

### Task 9: The Now screen's view model

**Files:**
- Create: `src/PowerLedger.App/Now/Panels.cs`
- Create: `src/PowerLedger.App/Now/NowViewModel.cs`
- Create: `tests/PowerLedger.App.Tests/FakeLink.cs`
- Create: `tests/PowerLedger.App.Tests/FakeHistory.cs`
- Create: `tests/PowerLedger.App.Tests/Snapshots.cs`
- Test: `tests/PowerLedger.App.Tests/NowViewModelTests.cs`

Spec §9's Now screen, as immutable panels. A reading replaces `Live`; a history read replaces `Today`, `Month` and `Chart`; a status poll replaces `Status`. Every string is made here with the user's culture, so the tests read exactly what the user sees. Readings arrive on the link's thread and are posted to the UI thread; history and status are read off it. `UiThreads` carries both hops, and the tests pass one that runs everything inline, so nothing in them races. Spec §9's states: "Collecting…" until today has a folded minute, "Service not running" once the link has not connected within `ConnectGrace`, and an honest banner when the machine has neither an energy meter nor a battery.

- [ ] **Step 1: Write the fakes and the failing tests**

`tests/PowerLedger.App.Tests/FakeLink.cs`
```csharp
using PowerLedger.Contracts;

namespace PowerLedger.App.Tests;

/// <summary>A service link the test drives by hand.</summary>
internal sealed class FakeLink : IServiceLink
{
    public event Action<ReadingFrame>? FrameReceived;

    public event Action<bool>? ConnectionChanged;

    public bool IsConnected { get; private set; }

    public ServiceStatus? Status { get; set; } = Statuses.Running();

    public ServiceSettings? Settings { get; set; } = ServiceSettings.Default;

    public void Start()
    {
    }

    public void Push(ReadingFrame frame) => FrameReceived?.Invoke(frame);

    public void Connect(bool connected)
    {
        IsConnected = connected;
        ConnectionChanged?.Invoke(connected);
    }

    public Task<ServiceStatus?> GetStatusAsync(CancellationToken cancel = default) => Task.FromResult(IsConnected ? Status : null);

    public Task<ServiceSettings?> GetSettingsAsync(CancellationToken cancel = default) => Task.FromResult(IsConnected ? Settings : null);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

`tests/PowerLedger.App.Tests/FakeHistory.cs`
```csharp
namespace PowerLedger.App.Tests;

/// <summary>History that answers with whatever snapshot the test sets.</summary>
internal sealed class FakeHistory : IHistory
{
    public HistorySnapshot? Snapshot { get; set; }

    public HistorySnapshot? Read(DateTimeOffset now, TimeZoneInfo zone) => Snapshot;
}
```

`tests/PowerLedger.App.Tests/Snapshots.cs`
```csharp
using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.App.Tests;

/// <summary>History as the mockup shows it: a Tuesday afternoon eight days into September.</summary>
internal static class Snapshots
{
    public static HistorySnapshot Typical(DateTimeOffset now, IReadOnlyList<DaySlot>? slots = null)
    {
        var dayStart = new DateTimeOffset(now.Date, TimeSpan.Zero);
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var today = new RangeTotals(
            From: dayStart, To: now, EnergyKwh: 0.284, Cost: 0.0483m, Currency: "USD", CostIsPartial: false,
            AvgW: 41, PeakW: 68, PeakAt: dayStart.AddHours(14),
            OnHours: 7 + 5 / 60.0, IdleOnHours: 0.75, IdleOffHours: 0, AsleepHours: 7.5, UnmonitoredHours: 0,
            CpuKwh: 0.12, GpuKwh: 0.03, DisplayKwh: 0.028, RestKwh: 0.106, IdleOnKwh: 0.011, IdleOffKwh: 0,
            MeasuredShare: 0.62, CalibratedShare: 0.2, EstimatedShare: 0.18);
        var month = today with { From = monthStart, EnergyKwh = 2.74, Cost = 0.47m, IdleOnKwh = 0.15, IdleOffKwh = 0.06 };
        var days = Enumerable.Range(1, now.Day)
            .Select(d => new DayTotals(new DateOnly(now.Year, now.Month, d), 0.2 + d % 4 * 0.1, 0.05m, "USD", false, 8, 60, 0.01, 0))
            .ToList();
        return new HistorySnapshot(
            today, month, days, slots ?? DaySlots.Build([], dayStart, now), dayStart,
            new Tariff(now.AddDays(-30), 0.17m, "USD"),
            new MachineNames("11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", "NVIDIA GeForce MX330", 15.3));
    }
}
```

`tests/PowerLedger.App.Tests/NowViewModelTests.cs`
```csharp
using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

public class NowViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 7, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeLink _link = new();
    private readonly FakeHistory _history = new();
    private int _starts;

    private NowViewModel Model()
        => new(_link, _history, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, co2KgPerKwh: 0.38, startService: () => _starts++);

    [Fact]
    public void A_reading_fills_the_live_panel()
    {
        var model = Model();
        _link.Push(Frames.At(Now, totalW: 34.2));

        model.Live.Watts.ShouldBe(34.2);
        model.Live.Eyebrow.ShouldBe("Live · battery discharge · 14:32:07");
        model.Live.Quality.ShouldBe(Quality.Measured);
        model.Live.QualityNote.ShouldBe("Windows battery report · 1 s samples");
        model.Live.Spark.ShouldBe(new[] { 34.2 });
        model.Live.Budget.Select(r => r.Name).ShouldBe(new[] { "CPU package", "GPU", "Display", "Rest of system" });
        model.Live.Budget[0].Watts.ShouldBe("14.6 W");
        model.Live.Budget[0].Percent.ShouldBe("43%");
        model.Live.BudgetTotalW.ShouldBe(34.2, 1e-9);
        model.TrayTooltip.ShouldStartWith("34.2 W · Measured");
    }

    [Fact]
    public void Budget_rows_say_what_each_part_is_and_how_it_was_known()
    {
        var model = Model();
        _history.Snapshot = Snapshots.Typical(Now);
        model.RefreshHistory();
        _link.Push(Frames.At(Now));

        model.Live.Budget[0].Detail.ShouldBe("Core i7-1165G7 · 30% load · measured");
        model.Live.Budget[1].Detail.ShouldBe("GeForce MX330 · 31% load · modelled");
        model.Live.Budget[2].Detail.ShouldBe("15.3 in · brightness 60%");
        model.Live.Budget[3].Detail.ShouldBe("RAM, SSD, board, radios · measured remainder");
    }

    [Fact]
    public void A_card_windows_has_switched_off_says_so()
    {
        var model = Model();
        _link.Push(Frames.At(Now, gpu: 0, gpuMeasured: true, gpuLoad: 0));
        model.Live.Budget[1].Detail.ShouldBe("switched off");
    }

    [Fact]
    public void History_fills_today_s_ledger_the_way_the_user_reads_it()
    {
        var model = Model();
        _history.Snapshot = Snapshots.Typical(Now);
        model.RefreshHistory();

        model.Today.Date.ShouldBe("Tue 8 Sep");
        model.Today.Energy.ShouldBe("0.284");
        model.Today.Cost.ShouldBe("$0.05");
        model.Today.Tariff.ShouldBe("at $0.17 / kWh");
        model.Today.Average.ShouldBe("41");
        model.Today.Peak.ShouldBe("68");
        model.Today.PeakAt.ShouldBe("at 14:00");
        model.Today.On.ShouldBe("7h 05m");
        model.Today.Idle.ShouldBe("45m");
        model.Today.IdleWasted.ShouldBe("11 Wh wasted");
        model.Today.Asleep.ShouldBe("7h 30m");
        model.Today.Co2.ShouldBe("0.11");
        model.Today.Co2Factor.ShouldBe("0.38 kg / kWh grid");
        model.IsCollecting.ShouldBeFalse();
    }

    [Fact]
    public void History_fills_the_month_s_ledger()
    {
        var model = Model();
        _history.Snapshot = Snapshots.Typical(Now);
        model.RefreshHistory();

        model.Month.Name.ShouldBe("September");
        model.Month.Summary.ShouldBe("8 days · 62% measured");
        model.Month.Energy.ShouldBe("2.74");
        model.Month.Cost.ShouldBe("$0.47");
        model.Month.Projected.ShouldBe("$1.85");
        model.Month.ProjectedEnergy.ShouldBe("10.8 kWh");
        model.Month.DailyAverage.ShouldBe("0.360 kWh");
        model.Month.IdleWaste.ShouldBe("0.210 kWh");
        model.Month.NextReport.ShouldBe("1 Oct");
    }

    [Fact]
    public void The_meter_marks_today_s_average_and_the_higher_of_history_s_peak_and_the_live_one()
    {
        var model = Model();
        _history.Snapshot = Snapshots.Typical(Now);
        model.RefreshHistory();
        _link.Push(Frames.At(Now, totalW: 90));

        model.Live.PeakW.ShouldBe(90);
        model.Live.AverageW.ShouldBe(41);
        model.Live.Meter.Max.ShouldBe(100);
    }

    [Fact]
    public void Until_today_has_a_folded_minute_the_ledgers_are_collecting()
    {
        var model = Model();
        model.IsCollecting.ShouldBeTrue();
        var empty = Snapshots.Typical(Now);
        _history.Snapshot = empty with { Today = empty.Today with { OnHours = 0 } };
        model.RefreshHistory();
        model.IsCollecting.ShouldBeTrue();
    }

    [Fact]
    public void Without_the_service_the_screen_says_so_after_the_grace_period_and_offers_to_start_it()
    {
        var model = Model();
        model.Start();
        model.Connection.ShouldBe(Connection.Connecting);
        _clock.Advance(NowViewModel.ConnectGrace);
        model.IsServiceDown.ShouldBeTrue();
        model.Status.State.ShouldBe("Service not running");

        model.StartService.Execute(null);
        _starts.ShouldBe(1);

        _link.Connect(true);
        model.IsServiceDown.ShouldBeFalse();
        model.Status.State.ShouldBe("Service running");
        model.Status.Service.ShouldBe("Service 0.1.0");
        model.Status.Sampling.ShouldBe("Sampling 1 s");
        model.Status.Calibration.ShouldBe("Calibrating · 15m of 30m on battery");
        model.Status.Database.ShouldBe("Database 31 MB");
    }

    [Fact]
    public void With_no_energy_meter_and_no_battery_the_screen_says_so_honestly()
    {
        _link.Status = Statuses.Running(energyMeter: false, battery: false);
        var model = Model();
        _link.Connect(true);
        model.IsSensorless.ShouldBeTrue();
    }

    [Fact]
    public void Losing_the_service_clears_the_live_panel()
    {
        var model = Model();
        _link.Connect(true);
        _link.Push(Frames.At(Now));
        _link.Connect(false);
        model.Live.ShouldBe(LivePanel.Waiting);
        model.IsServiceDown.ShouldBeTrue();
    }

    [Theory]
    [InlineData("11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", "Core i7-1165G7")]
    [InlineData("AMD Ryzen 7 5800U with Radeon Graphics", "Ryzen 7 5800U")]
    [InlineData("NVIDIA GeForce MX330", "GeForce MX330")]
    [InlineData("Intel(R) Core(TM) Ultra 7 155H", "Core Ultra 7 155H")]
    public void Names_lose_the_trademarks_the_maker_and_the_clock_speed(string full, string expected)
        => NowViewModel.ShortName(full).ShouldBe(expected);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.App.Tests --filter NowViewModelTests`
Expected: build error, `NowViewModel` not found.

- [ ] **Step 3: Write the panels and the view model**

`src/PowerLedger.App/Now/Panels.cs`
```csharp
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>Whether the App can reach the service.</summary>
internal enum Connection
{
    Connecting,
    Connected,
    Down,
}

/// <param name="Post">Runs an action on the UI thread.</param>
/// <param name="Background">Runs an action off the UI thread.</param>
internal sealed record UiThreads(Action<Action> Post, Action<Action> Background)
{
    /// <summary>Everything at once on the caller's thread, for tests.</summary>
    public static UiThreads Inline { get; } = new(action => action(), action => action());
}

/// <summary>One row of the power budget: a band, what it is, and its share now.</summary>
internal sealed record BudgetRow(Part Part, string Name, string Detail, string Watts, string Percent, double Share);

/// <summary>The live half of the Now screen, replaced whole on every reading.</summary>
internal sealed record LivePanel(
    double Watts, string Eyebrow, Quality? Quality, string QualityNote, IReadOnlyList<double> Spark,
    MeterRange Meter, double AverageW, double PeakW, IReadOnlyList<BudgetRow> Budget, double BudgetTotalW)
{
    public static LivePanel Waiting { get; } = new(0, "Live · waiting for the service", null, "", [], MeterRange.For(0), 0, 0, [], 0);
}

/// <summary>Today's ledger, as the user reads it.</summary>
internal sealed record TodayLedger(
    string Date, string Energy, string Cost, string Tariff, string Average, string Peak, string PeakAt,
    string On, string Idle, string IdleWasted, string Asleep, string Co2, string Co2Factor)
{
    public static TodayLedger Empty { get; } = new("", "–", "–", "", "–", "–", "", "–", "–", "", "–", "–", "");
}

/// <summary>The month-to-date ledger, as the user reads it.</summary>
internal sealed record MonthLedger(
    string Name, string Summary, string Energy, string Cost, string Projected, string ProjectedEnergy, string DailyAverage,
    string IdleWaste, string IdleWasteCost, string Lowest, string LowestDate, string Highest, string HighestDate, string NextReport)
{
    public static MonthLedger Empty { get; } = new("", "", "–", "–", "–", "", "–", "–", "", "–", "", "–", "", "");
}

/// <summary>Today's chart and its legend.</summary>
internal sealed record DayChartModel(
    IReadOnlyList<DaySlot> Slots, DateTimeOffset DayStart, DateTimeOffset Now,
    string CpuWh, string GpuWh, string DisplayWh, string RestWh)
{
    public static DayChartModel Empty { get; } = new([], default, default, "–", "–", "–", "–");
}

/// <summary>The status bar and the rail's footer.</summary>
internal sealed record StatusLine(bool Running, string Service, string Sampling, string Calibration, string Database, string? Notice)
{
    public static StatusLine Down { get; } = new(false, "", "", "", "", null);

    public string State => Running ? "Service running" : "Service not running";
}
```

`src/PowerLedger.App/Now/NowViewModel.cs`
```csharp
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLedger.Contracts;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>
/// The Now screen (spec §9): the live reading and its quality, the last minute, the meter, the power budget, today's and
/// the month's ledgers, and today's chart. Readings arrive on the link's thread and are posted to the UI thread; history
/// is read every minute and the status every ten seconds, both off the UI thread.
/// </summary>
internal sealed partial class NowViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan HistoryEvery = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan StatusEvery = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ConnectGrace = TimeSpan.FromSeconds(3);

    private readonly IServiceLink _link;
    private readonly IHistory _history;
    private readonly UiThreads _threads;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly CultureInfo _culture;
    private readonly double _co2KgPerKwh;
    private readonly LiveWindow _window = new(TimeSpan.FromSeconds(60));
    private ITimer? _historyTimer;
    private ITimer? _statusTimer;
    private ITimer? _graceTimer;
    private HistorySnapshot? _snapshot;
    private ServiceSettings? _settings;
    private ReadingFrame? _last;
    private double _livePeak;
    private DateOnly _liveDay;

    private LivePanel _live = LivePanel.Waiting;
    private TodayLedger _today = TodayLedger.Empty;
    private MonthLedger _month = MonthLedger.Empty;
    private DayChartModel _chart = DayChartModel.Empty;
    private StatusLine _status = StatusLine.Down;
    private Connection _connection = Connection.Connecting;
    private bool _isSensorless;
    private bool _isCollecting = true;

    public NowViewModel(
        IServiceLink link, IHistory history, UiThreads threads, TimeProvider clock, TimeZoneInfo zone, CultureInfo culture,
        double co2KgPerKwh, Action startService)
    {
        _link = link;
        _history = history;
        _threads = threads;
        _clock = clock;
        _zone = zone;
        _culture = culture;
        _co2KgPerKwh = co2KgPerKwh;
        StartService = new RelayCommand(startService);
        _link.FrameReceived += OnFrame;
        _link.ConnectionChanged += OnConnectionChanged;
    }

    public LivePanel Live { get => _live; private set => SetProperty(ref _live, value); }

    public TodayLedger Today { get => _today; private set => SetProperty(ref _today, value); }

    public MonthLedger Month { get => _month; private set => SetProperty(ref _month, value); }

    public DayChartModel Chart { get => _chart; private set => SetProperty(ref _chart, value); }

    public StatusLine Status { get => _status; private set => SetProperty(ref _status, value); }

    public Connection Connection
    {
        get => _connection;
        private set
        {
            if (SetProperty(ref _connection, value)) OnPropertyChanged(nameof(IsServiceDown));
        }
    }

    public bool IsServiceDown => Connection == Connection.Down;

    public bool IsSensorless { get => _isSensorless; private set => SetProperty(ref _isSensorless, value); }

    public bool IsCollecting { get => _isCollecting; private set => SetProperty(ref _isCollecting, value); }

    /// <summary>"Start service" on the banner (spec §9: a Start button, through UAC).</summary>
    public ICommand StartService { get; }

    /// <summary>The newest reading, for the tray icon.</summary>
    public ReadingFrame? Last => _last;

    /// <summary>The tray's tooltip: now and today (spec §9).</summary>
    public string TrayTooltip => _last is { } frame
        ? $"{Format.Watts(frame.TotalW, _culture)} W · {frame.Quality}\nToday {Today.Energy} kWh · {Today.Cost}"
        : IsServiceDown ? "PowerLedger · service not running" : "PowerLedger · waiting for the service";

    /// <summary>Starts the timers; the first history read happens at once. Call once, on the UI thread.</summary>
    public void Start()
    {
        _historyTimer = _clock.CreateTimer(_ => RefreshHistory(), null, TimeSpan.Zero, HistoryEvery);
        _statusTimer = _clock.CreateTimer(_ => _ = PollAsync(), null, StatusEvery, StatusEvery);
        _graceTimer = _clock.CreateTimer(_ => _threads.Post(EndGrace), null, ConnectGrace, Timeout.InfiniteTimeSpan);
        if (_link.IsConnected) OnConnectionChanged(true);
    }

    /// <summary>Reads history and hands it to the UI thread. Called off the UI thread.</summary>
    internal void RefreshHistory()
    {
        var snapshot = _history.Read(_clock.GetUtcNow(), _zone);
        _threads.Post(() => ApplyHistory(snapshot));
    }

    /// <summary>Asks the service for its status, and for its settings when they are not known yet.</summary>
    internal async Task PollAsync()
    {
        if (!_link.IsConnected) return;
        var status = await _link.GetStatusAsync().ConfigureAwait(false);
        var settings = _settings ?? await _link.GetSettingsAsync().ConfigureAwait(false);
        _threads.Post(() => ApplyStatus(status, settings));
    }

    public void Dispose()
    {
        _link.FrameReceived -= OnFrame;
        _link.ConnectionChanged -= OnConnectionChanged;
        _historyTimer?.Dispose();
        _statusTimer?.Dispose();
        _graceTimer?.Dispose();
    }

    private void OnFrame(ReadingFrame frame) => _threads.Post(() => ApplyFrame(frame));

    private void OnConnectionChanged(bool connected) => _threads.Post(() =>
    {
        Connection = connected ? Connection.Connected : Connection.Down;
        if (connected)
        {
            _settings = null;
            _threads.Background(() =>
            {
                RefreshHistory();
                _ = PollAsync();
            });
            return;
        }
        _last = null;
        _window.Clear();
        Live = LivePanel.Waiting;
        Status = StatusLine.Down;
        OnPropertyChanged(nameof(Last));
        OnPropertyChanged(nameof(TrayTooltip));
    });

    private void EndGrace()
    {
        if (Connection != Connection.Connecting) return;
        Connection = Connection.Down;
        OnPropertyChanged(nameof(TrayTooltip));
    }

    private void ApplyFrame(ReadingFrame frame)
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(frame.Timestamp, _zone).DateTime);
        if (day != _liveDay)
        {
            _liveDay = day;
            _livePeak = 0;
        }
        _livePeak = Math.Max(_livePeak, double.IsFinite(frame.TotalW) ? frame.TotalW : 0);
        _window.Add(frame.Timestamp, frame.TotalW);
        _last = frame;
        RebuildLive();
        OnPropertyChanged(nameof(Last));
    }

    private void RebuildLive()
    {
        if (_last is not { } frame)
        {
            Live = LivePanel.Waiting;
            return;
        }
        var local = TimeZoneInfo.ConvertTime(frame.Timestamp, _zone);
        var today = _snapshot?.Today;
        var peak = Math.Max(today?.PeakW ?? 0, _livePeak);
        var average = today is { OnHours: > 0 } ? today.AvgW : frame.TotalW;
        var spark = _window.Values;
        var shares = Budget.Of(frame);
        Live = new LivePanel(
            frame.TotalW,
            $"Live · {Source(frame.Quality)} · {local.ToString("HH:mm:ss", _culture)}",
            frame.Quality, Note(frame.Quality), spark,
            MeterRange.For(Math.Max(peak, spark.Count > 0 ? spark.Max() : 0)),
            average, peak,
            [.. shares.Select(share => Row(share, frame))],
            shares.Sum(share => share.Watts));
        OnPropertyChanged(nameof(TrayTooltip));
    }

    private void ApplyHistory(HistorySnapshot? snapshot)
    {
        _snapshot = snapshot;
        IsCollecting = snapshot is null || snapshot.Today.OnHours <= 0;
        if (snapshot is null)
        {
            Today = TodayLedger.Empty;
            Month = MonthLedger.Empty;
            Chart = DayChartModel.Empty;
        }
        else
        {
            var now = _clock.GetUtcNow();
            var local = TimeZoneInfo.ConvertTime(now, _zone);
            Today = TodayOf(snapshot, local);
            Month = MonthOf(snapshot, local);
            Chart = new DayChartModel(
                snapshot.TodaySlots, snapshot.DayStart, now,
                Wh(snapshot.Today.CpuKwh), Wh(snapshot.Today.GpuKwh), Wh(snapshot.Today.DisplayKwh), Wh(snapshot.Today.RestKwh));
        }
        RebuildLive();
    }

    private void ApplyStatus(ServiceStatus? status, ServiceSettings? settings)
    {
        if (settings is not null) _settings = settings;
        if (status is null) return;
        IsSensorless = !Supported(status, "energy-meter") && !Supported(status, "battery");
        var interval = _settings?.SampleIntervalSeconds ?? 1;
        var learned = Format.Duration(status.Calibration.BatterySamples * interval / 3600.0);
        var needed = Format.Duration(status.Calibration.SamplesNeeded * interval / 3600.0);
        Status = new StatusLine(
            true,
            $"Service {status.Version.Split('+')[0]}",
            $"Sampling {interval.ToString(_culture)} s",
            status.Calibration.TrustedBuckets > 0 ? $"Calibration {learned} on battery" : $"Calibrating · {learned} of {needed} on battery",
            $"Database {Megabytes(status.DatabaseBytes)}",
            status.WriteProblem ?? status.DatabaseNotice);
        if (_last is not null) RebuildLive();
    }

    private TodayLedger TodayOf(HistorySnapshot snapshot, DateTimeOffset local)
    {
        var t = snapshot.Today;
        var tariff = snapshot.Tariff;
        return new TodayLedger(
            local.ToString("ddd d MMM", _culture),
            Format.Kwh(t.EnergyKwh, _culture),
            t.Currency is { } currency ? Money.Format(t.Cost, currency, _culture) : Format.Missing,
            tariff is null ? "no tariff set" : $"at {Money.Rate(tariff.PricePerKwh, tariff.Currency, _culture)} / kWh" + (t.CostIsPartial ? " · partial" : ""),
            Format.WholeWatts(t.AvgW, _culture),
            Format.WholeWatts(t.PeakW, _culture),
            t.PeakAt is { } at ? "at " + TimeZoneInfo.ConvertTime(at, _zone).ToString("HH:mm", _culture) : "",
            Format.Duration(t.OnHours),
            Format.Duration(t.IdleOnHours),
            $"{Format.WholeWatts(t.IdleOnKwh * 1000, _culture)} Wh wasted",
            Format.Duration(t.AsleepHours),
            Format.Kg(t.EnergyKwh * _co2KgPerKwh, _culture),
            $"{_co2KgPerKwh.ToString("0.00", _culture)} kg / kWh grid");
    }

    private MonthLedger MonthOf(HistorySnapshot snapshot, DateTimeOffset local)
    {
        var o = MonthOutlook.From(snapshot.Month, snapshot.MonthDays, local);
        string Cash(decimal? amount) => amount is { } value && o.Currency is { } currency ? Money.Format(value, currency, _culture) : Format.Missing;
        string DayEnergy(DayTotals? day) => day is null ? Format.Missing : Format.Kwh(day.EnergyKwh, _culture) + " kWh";
        string DayDate(DayTotals? day) => day is null ? "" : day.Day.ToString("ddd d MMM", _culture);
        return new MonthLedger(
            local.ToString("MMMM", _culture),
            $"{o.DaysRecorded.ToString(_culture)} {(o.DaysRecorded == 1 ? "day" : "days")} · {Format.Percent(o.MeasuredShare, _culture)} measured",
            Format.Kwh(o.EnergyKwh, _culture),
            Cash(o.Cost),
            Cash(o.ProjectedCost),
            o.ProjectedKwh is { } projected ? Format.Kwh(projected, _culture) + " kWh" : "after a full day",
            o.DailyAverageKwh is { } average ? Format.Kwh(average, _culture) + " kWh" : Format.Missing,
            Format.Kwh(o.IdleWasteKwh, _culture) + " kWh",
            o.Currency is null ? "" : Cash(o.IdleWasteCost),
            DayEnergy(o.Lowest), DayDate(o.Lowest), DayEnergy(o.Highest), DayDate(o.Highest),
            o.NextReport.ToString("d MMM", _culture));
    }

    private BudgetRow Row(BudgetShare share, ReadingFrame frame)
    {
        var machine = _snapshot?.Machine;
        var (name, detail) = share.Part switch
        {
            Part.Cpu => ("CPU package", Join(ShortName(machine?.Cpu), Load(frame.CpuLoad), frame.CpuMeasured ? "measured" : "modelled")),
            Part.Gpu => ("GPU", GpuDetail(machine?.Gpu, frame)),
            Part.Display => ("Display", DisplayDetail(machine, frame)),
            _ => ("Rest of system", Join("RAM, SSD, board, radios", Remainder(frame.Quality))),
        };
        return new BudgetRow(share.Part, name, detail, Format.Watts(share.Watts, _culture) + " W", Format.Percent(share.Share, _culture), share.Share);
    }

    private string GpuDetail(string? gpu, ReadingFrame frame)
    {
        var name = ShortName(gpu);
        if (frame.GpuMeasured && frame.Components.Gpu <= 0 && (frame.GpuLoad ?? 0) <= 0) return Join(name, "switched off");
        if (!frame.GpuMeasured && frame.GpuLoad is null && frame.Components.Gpu <= 0) return "no discrete GPU the service can read";
        return Join(name, Load(frame.GpuLoad), frame.GpuMeasured ? "measured" : "modelled");
    }

    private string DisplayDetail(MachineNames? machine, ReadingFrame frame)
    {
        if (!frame.DisplayOn) return "display off";
        var size = machine is { DisplayDiagonalInches: > 0 } ? machine.DisplayDiagonalInches.ToString("0.#", _culture) + " in" : null;
        var brightness = frame.Brightness is { } b ? "brightness " + Format.Percent(b, _culture) : null;
        var text = Join(size, brightness);
        return text.Length > 0 ? text : "monitors included in the profile";
    }

    private string? Load(double? load) => load is { } value ? Format.Percent(value, _culture) + " load" : null;

    private string Wh(double kwh) => Format.WholeWatts(kwh * 1000, _culture) + " Wh";

    private string Megabytes(long bytes)
    {
        var mb = bytes / (1024.0 * 1024.0);
        return (mb < 10 ? mb.ToString("0.0", _culture) : mb.ToString("0", _culture)) + " MB";
    }

    private string Note(Quality quality) => quality switch
    {
        Quality.Measured => $"Windows battery report · {(_settings?.SampleIntervalSeconds ?? 1).ToString(_culture)} s samples",
        Quality.Calibrated => "Model with a baseline learned on battery · ±10%",
        _ => "Model from the sensors and the machine profile · ±20%",
    };

    private static string Source(Quality quality) => quality switch
    {
        Quality.Measured => "battery discharge",
        Quality.Calibrated => "calibrated model",
        _ => "estimate",
    };

    private static string Remainder(Quality quality) => quality switch
    {
        Quality.Measured => "measured remainder",
        Quality.Calibrated => "learned baseline",
        _ => "estimated",
    };

    private static bool Supported(ServiceStatus status, string source) => status.Sources.Any(s => s.Name == source && s.Supported);

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>"11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz" becomes "Core i7-1165G7".</summary>
    internal static string? ShortName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return Spaces().Replace(Noise().Replace(name, " "), " ").Trim();
    }

    [GeneratedRegex(@"\((R|TM|C)\)|@.*$|\b\d+(st|nd|rd|th) Gen\b|\bIntel\b|\bAMD\b|\bNVIDIA\b|\bProcessor\b|\bCPU\b|\bwith Radeon Graphics\b|\b\d+-Core\b", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter NowViewModelTests`
Expected: `Passed! - Failed: 0, Passed: 14`.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.App/Now/Panels.cs src/PowerLedger.App/Now/NowViewModel.cs tests/PowerLedger.App.Tests/FakeLink.cs tests/PowerLedger.App.Tests/FakeHistory.cs tests/PowerLedger.App.Tests/Snapshots.cs tests/PowerLedger.App.Tests/NowViewModelTests.cs
git commit -m "Add the Now screen's view model: live panel, ledgers, chart, status and states"
```

---

### Task 10: The drawn controls

**Files:**
- Create: `src/PowerLedger.App/Controls/Geometry.cs`
- Create: `src/PowerLedger.App/Controls/Instrument.cs`
- Create: `src/PowerLedger.App/Controls/MeterScale.cs`
- Create: `src/PowerLedger.App/Controls/BudgetBar.cs`
- Create: `src/PowerLedger.App/Controls/Sparkline.cs`
- Create: `src/PowerLedger.App/Controls/DayChart.cs`
- Create: `src/PowerLedger.App/Controls/LiveReadout.cs`
- Test: `tests/PowerLedger.App.Tests/GeometryTests.cs`

Spec §9 asks for `MeterScale` and `BudgetBar` drawn with `DrawingContext`; the sparkline and the day chart are drawn the same way, following the mockup. Each control is thin: it calls `Geometry` for every number and draws the result, so what can be wrong is tested without a window. Brushes and the numeral font come from styles (Task 11), set with `DynamicResource`, so a theme switch repaints. The live readout settles from zero over 900 ms when it first shows a value, unless Windows' client-area animation is off (spec §9 motion), and then follows each reading without animating.

- [ ] **Step 1: Write the failing tests**

`tests/PowerLedger.App.Tests/GeometryTests.cs`
```csharp
using Shouldly;

namespace PowerLedger.App.Tests;

public class GeometryTests
{
    [Fact]
    public void A_value_sits_along_the_scale_and_is_clamped_to_its_ends()
    {
        Geometry.ScaleX(75, 150, 10, 590).ShouldBe(300);
        Geometry.ScaleX(-5, 150, 10, 590).ShouldBe(10);
        Geometry.ScaleX(400, 150, 10, 590).ShouldBe(590);
        Geometry.ScaleX(double.NaN, 150, 10, 590).ShouldBe(10);
    }

    [Theory]
    [InlineData(34.2, 7, 5)]
    [InlineData(68, 4, 20)]
    [InlineData(3, 3, 1)]
    [InlineData(900, 4, 500)]
    public void Steps_are_round(double span, int ticks, double expected) => Geometry.NiceStep(span, ticks).ShouldBe(expected);

    [Fact]
    public void The_day_chart_clears_its_tallest_slot_in_four_round_steps()
    {
        Geometry.ChartScale(68).ShouldBe((80.0, 20.0));
        Geometry.ChartScale(3).ShouldBe((20.0, 5.0));
    }

    [Fact]
    public void The_sparkline_range_leaves_room_and_carries_round_gridlines()
    {
        var (low, high, grid) = Geometry.SparkRange([30, 34, 38]);
        low.ShouldBe(28, 1e-9);
        high.ShouldBe(40, 1e-9);
        grid.ShouldBe(new double[] { 30, 35 });
    }

    [Fact]
    public void Spark_points_spread_across_the_width_with_the_newest_at_the_right()
    {
        var points = Geometry.SparkPoints([10, 20, 30], left: 0, right: 100, top: 0, bottom: 50, low: 10, high: 30);
        points.Select(p => p.X).ShouldBe(new double[] { 0, 50, 100 });
        points.Select(p => p.Y).ShouldBe(new double[] { 50, 25, 0 });
        Geometry.SparkPoints([20], 0, 100, 0, 50, 10, 30).Single().X.ShouldBe(100);
    }

    [Fact]
    public void Stacked_layers_rise_from_rest_to_cpu_and_negative_rest_stays_at_zero()
    {
        DaySlot[] slots =
        [
            new(DateTimeOffset.UnixEpoch, CpuW: 10, GpuW: 2, DisplayW: 4, RestW: 6, OnSeconds: 300, AsleepSeconds: 0),
            new(DateTimeOffset.UnixEpoch, CpuW: 20, GpuW: 0, DisplayW: 4, RestW: -3, OnSeconds: 300, AsleepSeconds: 0),
        ];
        var tops = Geometry.StackTops(slots);
        tops.Rest.ShouldBe(new double[] { 6, 0 });
        tops.Display.ShouldBe(new double[] { 10, 4 });
        tops.Gpu.ShouldBe(new double[] { 12, 4 });
        tops.Cpu.ShouldBe(new double[] { 22, 24 });
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.App.Tests --filter GeometryTests`
Expected: build error, `Geometry` not found.

- [ ] **Step 3: Write the geometry and the controls**

`src/PowerLedger.App/Controls/Geometry.cs`
```csharp
using System.Windows;

namespace PowerLedger.App;

/// <summary>Every number the drawn controls need, as pure functions.</summary>
internal static class Geometry
{
    /// <summary>Where <paramref name="value"/> sits on a scale from 0 to <paramref name="max"/> drawn from left to right, clamped to the ends.</summary>
    public static double ScaleX(double value, double max, double left, double right)
        => max <= 0 || !double.IsFinite(value) ? left : left + (right - left) * Math.Clamp(value / max, 0, 1);

    /// <summary>A step of 1, 2 or 5 times a power of ten that gives about <paramref name="ticks"/> ticks over <paramref name="span"/>.</summary>
    public static double NiceStep(double span, int ticks)
    {
        if (!(span > 0) || ticks <= 0) return 1;
        var raw = span / ticks;
        var power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var fraction = raw / power;
        var nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10;
        return nice * power;
    }

    /// <summary>The day chart's top and gridline step: round steps that clear the tallest slot, never under 20 W.</summary>
    public static (double Max, double Step) ChartScale(double tallest)
    {
        var need = double.IsFinite(tallest) ? Math.Max(tallest, 20) : 20;
        var step = NiceStep(need, 4);
        return (step * Math.Ceiling(need / step), step);
    }

    /// <summary>The sparkline's range: the values with room above and below, and the round gridlines inside it.</summary>
    public static (double Low, double High, IReadOnlyList<double> Grid) SparkRange(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return (0, 10, [5]);
        var min = values.Min();
        var max = values.Max();
        var pad = Math.Max(2, (max - min) * 0.2);
        var low = Math.Max(0, min - pad);
        var high = max + pad;
        var step = NiceStep(high - low, 3);
        var grid = new List<double>();
        for (var line = Math.Ceiling(low / step) * step; line < high; line += step)
        {
            if (line > low) grid.Add(line);
        }
        return (low, high, grid);
    }

    /// <summary>The sparkline's points, spread across the width with the newest at the right edge.</summary>
    public static IReadOnlyList<Point> SparkPoints(IReadOnlyList<double> values, double left, double right, double top, double bottom, double low, double high)
    {
        var points = new Point[values.Count];
        var step = values.Count > 1 ? (right - left) / (values.Count - 1) : 0;
        var span = high > low ? high - low : 1;
        for (var i = 0; i < values.Count; i++)
        {
            var x = values.Count > 1 ? left + i * step : right;
            points[i] = new Point(x, bottom - (bottom - top) * Math.Clamp((values[i] - low) / span, 0, 1));
        }
        return points;
    }

    /// <summary>The top of each band in each slot, stacked from rest at the bottom to CPU at the top (spec §9). A negative rest stays at zero.</summary>
    public static (double[] Rest, double[] Display, double[] Gpu, double[] Cpu) StackTops(IReadOnlyList<DaySlot> slots)
    {
        var rest = new double[slots.Count];
        var display = new double[slots.Count];
        var gpu = new double[slots.Count];
        var cpu = new double[slots.Count];
        for (var i = 0; i < slots.Count; i++)
        {
            rest[i] = Math.Max(0, slots[i].RestW);
            display[i] = rest[i] + Math.Max(0, slots[i].DisplayW);
            gpu[i] = display[i] + Math.Max(0, slots[i].GpuW);
            cpu[i] = gpu[i] + Math.Max(0, slots[i].CpuW);
        }
        return (rest, display, gpu, cpu);
    }
}
```

`src/PowerLedger.App/Controls/Instrument.cs`
```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>
/// What every drawn control shares (spec §9): hairline, label, accent and band brushes, the numeral font, and text
/// drawing. Styles set the brushes from the palette with DynamicResource, so a theme switch repaints.
/// </summary>
internal abstract class Instrument : FrameworkElement
{
    public static readonly DependencyProperty LineBrushProperty = BrushProperty(nameof(LineBrush));
    public static readonly DependencyProperty StrongLineBrushProperty = BrushProperty(nameof(StrongLineBrush));
    public static readonly DependencyProperty LabelBrushProperty = BrushProperty(nameof(LabelBrush));
    public static readonly DependencyProperty InkBrushProperty = BrushProperty(nameof(InkBrush));
    public static readonly DependencyProperty AccentBrushProperty = BrushProperty(nameof(AccentBrush));
    public static readonly DependencyProperty AccentSoftBrushProperty = BrushProperty(nameof(AccentSoftBrush));
    public static readonly DependencyProperty CpuBrushProperty = BrushProperty(nameof(CpuBrush));
    public static readonly DependencyProperty GpuBrushProperty = BrushProperty(nameof(GpuBrush));
    public static readonly DependencyProperty DisplayBrushProperty = BrushProperty(nameof(DisplayBrush));
    public static readonly DependencyProperty RestBrushProperty = BrushProperty(nameof(RestBrush));
    public static readonly DependencyProperty NumberFontProperty = DependencyProperty.Register(
        nameof(NumberFont), typeof(FontFamily), typeof(Instrument),
        new FrameworkPropertyMetadata(new FontFamily("Cascadia Mono, Consolas"), FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }

    public Brush StrongLineBrush { get => (Brush)GetValue(StrongLineBrushProperty); set => SetValue(StrongLineBrushProperty, value); }

    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    public Brush InkBrush { get => (Brush)GetValue(InkBrushProperty); set => SetValue(InkBrushProperty, value); }

    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    public Brush AccentSoftBrush { get => (Brush)GetValue(AccentSoftBrushProperty); set => SetValue(AccentSoftBrushProperty, value); }

    public Brush CpuBrush { get => (Brush)GetValue(CpuBrushProperty); set => SetValue(CpuBrushProperty, value); }

    public Brush GpuBrush { get => (Brush)GetValue(GpuBrushProperty); set => SetValue(GpuBrushProperty, value); }

    public Brush DisplayBrush { get => (Brush)GetValue(DisplayBrushProperty); set => SetValue(DisplayBrushProperty, value); }

    public Brush RestBrush { get => (Brush)GetValue(RestBrushProperty); set => SetValue(RestBrushProperty, value); }

    public FontFamily NumberFont { get => (FontFamily)GetValue(NumberFontProperty); set => SetValue(NumberFontProperty, value); }

    /// <summary>A dependency property that repaints the control when it changes.</summary>
    protected static DependencyProperty Register<T>(string name, T defaultValue, Type owner)
        => DependencyProperty.Register(name, typeof(T), owner, new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    protected FormattedText Text(string text, double size, Brush brush, FontWeight? weight = null) => new(
        text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface(NumberFont, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
        size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>Draws text with its top at <paramref name="y"/>, placed by <paramref name="align"/> around <paramref name="x"/>.</summary>
    protected void DrawText(DrawingContext dc, string text, double x, double y, Brush brush, TextAlignment align = TextAlignment.Left, double size = 10)
    {
        var formatted = Text(text, size, brush);
        var left = align switch
        {
            TextAlignment.Center => x - formatted.Width / 2,
            TextAlignment.Right => x - formatted.Width,
            _ => x,
        };
        dc.DrawText(formatted, new Point(left, y));
    }

    protected static Pen Line(Brush brush, double thickness = 1, DashStyle? dash = null) => new(brush, thickness) { DashStyle = dash ?? DashStyles.Solid };

    protected Size Fixed(Size available, double height)
        => new(double.IsInfinity(available.Width) ? 600 : available.Width, height);

    private static DependencyProperty BrushProperty(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(Instrument), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
}
```

`src/PowerLedger.App/Controls/MeterScale.cs`
```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>The meter (spec §9): a tick scale from 0 to full scale, average and peak marks above it, and the amber needle at the live reading.</summary>
internal sealed class MeterScale : Instrument
{
    public static readonly DependencyProperty RangeProperty = Register(nameof(Range), MeterRange.For(0), typeof(MeterScale));
    public static readonly DependencyProperty ValueProperty = Register(nameof(Value), 0.0, typeof(MeterScale));
    public static readonly DependencyProperty AverageProperty = Register(nameof(Average), 0.0, typeof(MeterScale));
    public static readonly DependencyProperty PeakProperty = Register(nameof(Peak), 0.0, typeof(MeterScale));

    private const double Baseline = 28;
    private const double Inset = 10;

    public MeterRange Range { get => (MeterRange)GetValue(RangeProperty); set => SetValue(RangeProperty, value); }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public double Average { get => (double)GetValue(AverageProperty); set => SetValue(AverageProperty, value); }

    public double Peak { get => (double)GetValue(PeakProperty); set => SetValue(PeakProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => Fixed(availableSize, 52);

    protected override void OnRender(DrawingContext dc)
    {
        var range = Range;
        double left = Inset, right = ActualWidth - Inset;
        var tick = Line(StrongLineBrush);
        dc.DrawLine(tick, new Point(left, Baseline), new Point(right, Baseline));
        foreach (var (value, major) in range.Ticks())
        {
            var x = Geometry.ScaleX(value, range.Max, left, right);
            dc.DrawLine(tick, new Point(x, Baseline), new Point(x, Baseline + (major ? 11 : 5)));
            if (major) DrawText(dc, Format.WholeWatts(value, CultureInfo.CurrentCulture), x, Baseline + 13, LabelBrush, TextAlignment.Center);
        }

        Mark(dc, Average, "avg", range, left, right);
        Mark(dc, Peak, "peak", range, left, right);

        var needle = Geometry.ScaleX(Value, range.Max, left, right);
        var shape = new StreamGeometry();
        using (var g = shape.Open())
        {
            g.BeginFigure(new Point(needle - 5, Baseline - 10), isFilled: true, isClosed: true);
            g.LineTo(new Point(needle + 5, Baseline - 10), isStroked: false, isSmoothJoin: false);
            g.LineTo(new Point(needle, Baseline - 1), isStroked: false, isSmoothJoin: false);
        }
        shape.Freeze();
        dc.DrawGeometry(AccentBrush, null, shape);
    }

    private void Mark(DrawingContext dc, double value, string label, MeterRange range, double left, double right)
    {
        if (!(value > 0)) return;
        var x = Geometry.ScaleX(value, range.Max, left, right);
        dc.DrawLine(Line(LabelBrush), new Point(x, Baseline - 7), new Point(x, Baseline));
        DrawText(dc, $"{label} {Format.WholeWatts(value, CultureInfo.CurrentCulture)}", x, 3, LabelBrush, TextAlignment.Center);
    }
}
```

`src/PowerLedger.App/Controls/BudgetBar.cs`
```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>The power budget (spec §9 BudgetBar): one segment per band in proportion to its watts, 2 px apart, over a watt ruler that ends at the total.</summary>
internal sealed class BudgetBar : Instrument
{
    public static readonly DependencyProperty RowsProperty = Register<IReadOnlyList<BudgetRow>>(nameof(Rows), [], typeof(BudgetBar));
    public static readonly DependencyProperty TotalProperty = Register(nameof(Total), 0.0, typeof(BudgetBar));

    private const double BarHeight = 14;
    private const double Gap = 2;
    private const double RulerTop = BarHeight + 8;

    public IReadOnlyList<BudgetRow> Rows { get => (IReadOnlyList<BudgetRow>)GetValue(RowsProperty); set => SetValue(RowsProperty, value); }

    public double Total { get => (double)GetValue(TotalProperty); set => SetValue(TotalProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => Fixed(availableSize, RulerTop + 22);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var shown = Rows.Where(r => r.Share > 0).ToList();
        var room = width - Gap * Math.Max(0, shown.Count - 1);
        var x = 0.0;
        foreach (var row in shown)
        {
            var segment = room * row.Share;
            dc.DrawRectangle(BrushFor(row.Part), null, new Rect(x, 0, Math.Max(0, segment), BarHeight));
            x += segment + Gap;
        }

        var tick = Line(StrongLineBrush);
        dc.DrawLine(tick, new Point(0, RulerTop), new Point(width, RulerTop));
        if (!(Total > 0)) return;
        var totalLabel = Text($"{Format.Watts(Total, CultureInfo.CurrentCulture)} W", 10, LabelBrush);
        dc.DrawText(totalLabel, new Point(width - totalLabel.Width, RulerTop + 8));
        var step = Geometry.NiceStep(Total, 7);
        var index = 0;
        for (var value = 0.0; value <= Total + 1e-9; value += step, index++)
        {
            var at = Geometry.ScaleX(value, Total, 0, width);
            var labelled = index % 2 == 0;
            dc.DrawLine(tick, new Point(at, RulerTop), new Point(at, RulerTop + (labelled ? 8 : 4)));
            if (labelled && at < width - totalLabel.Width - 12)
            {
                DrawText(dc, Format.WholeWatts(value, CultureInfo.CurrentCulture), at, RulerTop + 8, LabelBrush, index == 0 ? TextAlignment.Left : TextAlignment.Center);
            }
        }
    }

    private Brush BrushFor(Part part) => part switch
    {
        Part.Cpu => CpuBrush,
        Part.Gpu => GpuBrush,
        Part.Display => DisplayBrush,
        _ => RestBrush,
    };
}
```

`src/PowerLedger.App/Controls/Sparkline.cs`
```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>The last minute (spec §9): an amber line over a faint amber fill, with round gridlines labelled in watts and a dot at the newest reading.</summary>
internal sealed class Sparkline : Instrument
{
    public static readonly DependencyProperty ValuesProperty = Register<IReadOnlyList<double>>(nameof(Values), [], typeof(Sparkline));

    private const double ControlHeight = 84;
    private const double Top = 10;
    private const double Bottom = 74;
    private const double Inset = 8;

    public IReadOnlyList<double> Values { get => (IReadOnlyList<double>)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => Fixed(availableSize, ControlHeight);

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        double left = Inset, right = ActualWidth - Inset;
        var (low, high, grid) = Geometry.SparkRange(values);
        var gridPen = Line(LineBrush);
        foreach (var watts in grid)
        {
            var y = Bottom - (Bottom - Top) * (watts - low) / (high - low);
            dc.DrawLine(gridPen, new Point(left, y), new Point(right, y));
            DrawText(dc, Format.WholeWatts(watts, CultureInfo.CurrentCulture), right, y - 13, LabelBrush, TextAlignment.Right);
        }
        if (values.Count == 0) return;

        var points = Geometry.SparkPoints(values, left, right, Top, Bottom, low, high);
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(points[0], isFilled: false, isClosed: false);
            for (var i = 1; i < points.Count; i++) g.LineTo(points[i], isStroked: true, isSmoothJoin: true);
        }
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(points[0].X, Bottom), isFilled: true, isClosed: true);
            foreach (var point in points) g.LineTo(point, isStroked: false, isSmoothJoin: false);
            g.LineTo(new Point(points[^1].X, Bottom), isStroked: false, isSmoothJoin: false);
        }
        line.Freeze();
        area.Freeze();
        dc.DrawGeometry(AccentSoftBrush, null, area);
        dc.DrawGeometry(null, new Pen(AccentBrush, 1.6) { LineJoin = PenLineJoin.Round }, line);
        dc.DrawEllipse(AccentBrush, null, points[^1], 3.5, 3.5);
    }
}
```

`src/PowerLedger.App/Controls/DayChart.cs`
```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>
/// Today's chart (spec §9): five-minute slots stacked by band from rest at the bottom to CPU at the top, hatched where the
/// machine slept, a dashed amber line at now, and a dot at the day's peak.
/// </summary>
internal sealed class DayChart : Instrument
{
    public static readonly DependencyProperty SlotsProperty = Register<IReadOnlyList<DaySlot>>(nameof(Slots), [], typeof(DayChart));
    public static readonly DependencyProperty NowProperty = Register(nameof(Now), default(DateTimeOffset), typeof(DayChart));

    private const double Left = 36;
    private const double RightInset = 14;
    private const double Top = 16;
    private const double Bottom = 222;
    private const int SlotsPerDay = 288;

    public IReadOnlyList<DaySlot> Slots { get => (IReadOnlyList<DaySlot>)GetValue(SlotsProperty); set => SetValue(SlotsProperty, value); }

    public DateTimeOffset Now { get => (DateTimeOffset)GetValue(NowProperty); set => SetValue(NowProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => Fixed(availableSize, 250);

    protected override void OnRender(DrawingContext dc)
    {
        var slots = Slots;
        var right = ActualWidth - RightInset;
        double X(double slot) => Left + (right - Left) * slot / SlotsPerDay;
        var (max, step) = Geometry.ChartScale(slots.Count > 0 ? slots.Max(s => s.TotalW) : 0);
        double Y(double watts) => Bottom - (Bottom - Top) * Math.Clamp(watts / max, 0, 1);

        var grid = Line(LineBrush);
        for (var watts = 0.0; watts <= max + 1e-9; watts += step)
        {
            dc.DrawLine(grid, new Point(Left, Y(watts)), new Point(right, Y(watts)));
            DrawText(dc, Format.WholeWatts(watts, CultureInfo.CurrentCulture), Left - 8, Y(watts) - 7, LabelBrush, TextAlignment.Right);
        }
        for (var hour = 0; hour <= 24; hour += 6)
        {
            var align = hour == 0 ? TextAlignment.Left : hour == 24 ? TextAlignment.Right : TextAlignment.Center;
            DrawText(dc, $"{hour:00}:00", X(hour * 12), Bottom + 6, LabelBrush, align);
        }
        if (slots.Count == 0) return;

        DrawAsleep(dc, slots, X);
        var tops = Geometry.StackTops(slots);
        var zero = new double[slots.Count];
        Band(dc, zero, tops.Rest, RestBrush, X, Y);
        Band(dc, tops.Rest, tops.Display, DisplayBrush, X, Y);
        Band(dc, tops.Display, tops.Gpu, GpuBrush, X, Y);
        Band(dc, tops.Gpu, tops.Cpu, CpuBrush, X, Y);

        var elapsed = Now == default ? slots.Count : (Now - slots[0].Start) / DaySlots.Length;
        var nowX = X(Math.Clamp(elapsed, 0, SlotsPerDay));
        dc.DrawLine(Line(AccentBrush, 1, new DashStyle([3, 3], 0)), new Point(nowX, Top), new Point(nowX, Bottom));
        DrawText(dc, "now", nowX + 5, Top - 2, AccentBrush);

        var peak = Enumerable.Range(0, slots.Count).MaxBy(i => slots[i].TotalW);
        if (slots[peak].TotalW > 0)
        {
            var at = new Point(X(peak + 0.5), Y(slots[peak].TotalW));
            dc.DrawEllipse(InkBrush, null, at, 2.5, 2.5);
            DrawText(dc, $"peak {Format.WholeWatts(slots[peak].TotalW, CultureInfo.CurrentCulture)} W", at.X - 6, at.Y - 18, LabelBrush, TextAlignment.Right);
        }
    }

    private void DrawAsleep(DrawingContext dc, IReadOnlyList<DaySlot> slots, Func<double, double> x)
    {
        var hatch = new DrawingBrush(new GeometryDrawing(null, Line(StrongLineBrush), new LineGeometry(new Point(0, 0), new Point(0, 6))))
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 6, 6), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 6, 6), ViewboxUnits = BrushMappingMode.Absolute, Transform = new RotateTransform(45),
        };
        var i = 0;
        while (i < slots.Count)
        {
            if (slots[i].AsleepSeconds <= 0)
            {
                i++;
                continue;
            }
            var start = i;
            double seconds = 0;
            while (i < slots.Count && slots[i].AsleepSeconds > 0) seconds += slots[i++].AsleepSeconds;
            var rect = new Rect(new Point(x(start), Top), new Point(x(i), Bottom));
            dc.DrawRectangle(hatch, null, rect);
            var label = Text($"asleep · {Format.Duration(seconds / 3600)}", 10, LabelBrush);
            if (label.Width + 8 < rect.Width) dc.DrawText(label, new Point(rect.Left + (rect.Width - label.Width) / 2, Top + 6));
        }
    }

    private static void Band(DrawingContext dc, double[] lower, double[] upper, Brush brush, Func<double, double> x, Func<double, double> y)
    {
        var shape = new StreamGeometry();
        using (var g = shape.Open())
        {
            g.BeginFigure(new Point(x(0), y(upper[0])), isFilled: true, isClosed: true);
            for (var i = 0; i < upper.Length; i++)
            {
                g.LineTo(new Point(x(i), y(upper[i])), isStroked: false, isSmoothJoin: false);
                g.LineTo(new Point(x(i + 1), y(upper[i])), isStroked: false, isSmoothJoin: false);
            }
            for (var i = lower.Length - 1; i >= 0; i--)
            {
                g.LineTo(new Point(x(i + 1), y(lower[i])), isStroked: false, isSmoothJoin: false);
                g.LineTo(new Point(x(i), y(lower[i])), isStroked: false, isSmoothJoin: false);
            }
        }
        shape.Freeze();
        dc.DrawGeometry(brush, null, shape);
    }
}
```

`src/PowerLedger.App/Controls/LiveReadout.cs`
```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PowerLedger.App;

/// <summary>
/// The big live number (spec §9): light numerals with a smaller "W". It settles from zero over 900 ms the first time it
/// shows a value, unless Windows' client-area animation is off, and after that follows each reading without animating.
/// </summary>
internal sealed class LiveReadout : Instrument
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(LiveReadout), new FrameworkPropertyMetadata(0.0, OnValueChanged));
    public static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        nameof(Shown), typeof(double), typeof(LiveReadout),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty NumberSizeProperty = Register(nameof(NumberSize), 84.0, typeof(LiveReadout));

    private bool _settled;

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public double Shown { get => (double)GetValue(ShownProperty); set => SetValue(ShownProperty, value); }

    public double NumberSize { get => (double)GetValue(NumberSizeProperty); set => SetValue(NumberSizeProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        var number = Number();
        var unit = Unit();
        return new Size(number.WidthIncludingTrailingWhitespace + 8 + unit.Width, number.Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var number = Number();
        var unit = Unit();
        dc.DrawText(number, new Point(0, 0));
        dc.DrawText(unit, new Point(number.WidthIncludingTrailingWhitespace + 8, number.Baseline - unit.Baseline));
    }

    private FormattedText Number() => Text(Format.Watts(Shown, CultureInfo.CurrentCulture), NumberSize, InkBrush, FontWeights.Light);

    private FormattedText Unit() => Text("W", NumberSize * 0.31, LabelBrush);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var readout = (LiveReadout)d;
        var value = (double)e.NewValue;
        if (!readout._settled && value > 0 && SystemParameters.ClientAreaAnimation)
        {
            readout._settled = true;
            readout.BeginAnimation(ShownProperty, new DoubleAnimation(0, value, TimeSpan.FromMilliseconds(900))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
            return;
        }
        if (value > 0) readout._settled = true;
        readout.BeginAnimation(ShownProperty, null);
        readout.Shown = value;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter GeometryTests`
Expected: `Passed! - Failed: 0, Passed: 9`.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.App/Controls tests/PowerLedger.App.Tests/GeometryTests.cs
git commit -m "Draw the meter, budget bar, sparkline, day chart and live readout from tested geometry"
```

---

### Task 11: Styles, the window and the Now screen's layout

**Files:**
- Create: `src/PowerLedger.App/Theme/Styles.xaml`
- Create: `src/PowerLedger.App/Controls/Converters.cs`
- Create: `src/PowerLedger.App/Controls/QualityBadge.cs`
- Create: `src/PowerLedger.App/Controls/LedgerRow.cs`
- Create: `src/PowerLedger.App/Shell/ShellViewModel.cs`
- Create: `src/PowerLedger.App/Shell/MainWindow.xaml`
- Create: `src/PowerLedger.App/Shell/MainWindow.xaml.cs`
- Create: `src/PowerLedger.App/Now/NowView.xaml`
- Create: `src/PowerLedger.App/Now/NowView.xaml.cs`
- Modify: `src/PowerLedger.App/App.xaml`

The mockup `docs/design/mockup-1-now-screen.html` in WPF. The window draws its own title bar with `WindowChrome`, keeping Windows' resizing and snapping; a maximised window is inset by the resize border, which Windows otherwise pushes off screen. The rail's LEDs light amber for the page shown. Quality is told by colour and by form (spec §9): a solid dot and solid frame for Measured, a half dot for Calibrated, a hollow dot and dashed frame for Estimated. Ledger rows run a dotted leader from label to value. Views use `DynamicResource` for every palette brush and `StaticResource` only for styles and fonts, which never change. The layout is proven by Task 14's rendering test; this task's check is the build.

- [ ] **Step 1: Write the styles and the small controls**

`src/PowerLedger.App/Theme/Styles.xaml`
```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="clr-namespace:PowerLedger.App">

    <!-- Spec §9 type: the bundled faces first, then Windows' own. -->
    <FontFamily x:Key="Font.Ui">/Fonts/#Archivo, Bahnschrift, Segoe UI</FontFamily>
    <FontFamily x:Key="Font.Narrow">/Fonts/#Archivo Narrow, Bahnschrift</FontFamily>
    <FontFamily x:Key="Font.Numbers">/Fonts/#Martian Mono, Cascadia Mono, Consolas</FontFamily>
    <FontFamily x:Key="Font.Glyphs">Segoe Fluent Icons, Segoe MDL2 Assets</FontFamily>

    <local:VisibleWhen x:Key="VisibleWhen" />
    <local:VisibleWhen x:Key="HiddenWhen" Invert="True" />
    <local:PageIs x:Key="PageIs" />
    <local:UpperCase x:Key="Upper" />

    <Style x:Key="Text.Body" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource Font.Ui}" />
        <Setter Property="FontSize" Value="13.5" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.Ink}" />
    </Style>
    <Style x:Key="Text.Secondary" TargetType="TextBlock" BasedOn="{StaticResource Text.Body}">
        <Setter Property="Foreground" Value="{DynamicResource Brush.Ink2}" />
    </Style>
    <Style x:Key="Text.Muted" TargetType="TextBlock" BasedOn="{StaticResource Text.Body}">
        <Setter Property="FontSize" Value="12" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.Ink3}" />
    </Style>
    <Style x:Key="Text.Eyebrow" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource Font.Narrow}" />
        <Setter Property="FontStretch" Value="SemiCondensed" />
        <Setter Property="FontWeight" Value="Medium" />
        <Setter Property="FontSize" Value="11" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.Ink2}" />
    </Style>
    <Style x:Key="Text.Number" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{StaticResource Font.Numbers}" />
        <Setter Property="FontSize" Value="13.5" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.Ink}" />
    </Style>

    <!-- The drawn controls take the palette here, so views never repeat it. -->
    <Style x:Key="Instrument" TargetType="local:Instrument">
        <Setter Property="LineBrush" Value="{DynamicResource Brush.Line}" />
        <Setter Property="StrongLineBrush" Value="{DynamicResource Brush.LineStrong}" />
        <Setter Property="LabelBrush" Value="{DynamicResource Brush.Ink3}" />
        <Setter Property="InkBrush" Value="{DynamicResource Brush.Ink}" />
        <Setter Property="AccentBrush" Value="{DynamicResource Brush.Amber}" />
        <Setter Property="AccentSoftBrush" Value="{DynamicResource Brush.AmberSoft}" />
        <Setter Property="CpuBrush" Value="{DynamicResource Brush.PartCpu}" />
        <Setter Property="GpuBrush" Value="{DynamicResource Brush.PartGpu}" />
        <Setter Property="DisplayBrush" Value="{DynamicResource Brush.PartDisplay}" />
        <Setter Property="RestBrush" Value="{DynamicResource Brush.PartRest}" />
        <Setter Property="NumberFont" Value="{StaticResource Font.Numbers}" />
    </Style>
    <Style TargetType="local:MeterScale" BasedOn="{StaticResource Instrument}" />
    <Style TargetType="local:BudgetBar" BasedOn="{StaticResource Instrument}" />
    <Style TargetType="local:Sparkline" BasedOn="{StaticResource Instrument}" />
    <Style TargetType="local:DayChart" BasedOn="{StaticResource Instrument}" />
    <Style TargetType="local:LiveReadout" BasedOn="{StaticResource Instrument}">
        <Setter Property="LabelBrush" Value="{DynamicResource Brush.Ink2}" />
    </Style>

    <!-- Quality by colour and by form: solid, half, dashed. -->
    <Style TargetType="local:QualityBadge">
        <Setter Property="Foreground" Value="{DynamicResource Brush.Measured}" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="local:QualityBadge">
                    <Grid x:Name="Root" Height="22">
                        <Rectangle x:Name="Frame" RadiusX="2" RadiusY="2" StrokeThickness="1" Stroke="{TemplateBinding Foreground}" />
                        <StackPanel Orientation="Horizontal" Margin="7,0,8,0" VerticalAlignment="Center">
                            <Grid Width="8" Height="8" VerticalAlignment="Center">
                                <Ellipse x:Name="Dot" Stroke="{TemplateBinding Foreground}" StrokeThickness="1" Fill="{TemplateBinding Foreground}" />
                                <Path x:Name="Half" Data="M4,0 A4,4 0 0 0 4,8 Z" Fill="{TemplateBinding Foreground}" Visibility="Collapsed" />
                            </Grid>
                            <TextBlock x:Name="Label" Margin="6,0,0,0" Text="MEASURED" VerticalAlignment="Center"
                                       Style="{StaticResource Text.Eyebrow}" Foreground="{TemplateBinding Foreground}" />
                        </StackPanel>
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="Quality" Value="{x:Null}">
                            <Setter TargetName="Root" Property="Visibility" Value="Collapsed" />
                        </Trigger>
                        <Trigger Property="Quality" Value="Calibrated">
                            <Setter Property="Foreground" Value="{DynamicResource Brush.Calibrated}" />
                            <Setter TargetName="Dot" Property="Fill" Value="Transparent" />
                            <Setter TargetName="Half" Property="Visibility" Value="Visible" />
                            <Setter TargetName="Label" Property="Text" Value="CALIBRATED ±10%" />
                        </Trigger>
                        <Trigger Property="Quality" Value="Estimated">
                            <Setter Property="Foreground" Value="{DynamicResource Brush.Estimated}" />
                            <Setter TargetName="Dot" Property="Fill" Value="Transparent" />
                            <Setter TargetName="Frame" Property="StrokeDashArray" Value="3 2" />
                            <Setter TargetName="Label" Property="Text" Value="ESTIMATED ±20%" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- A ledger line: label, dotted leader, value and a small note. -->
    <Style TargetType="local:LedgerRow">
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="local:LedgerRow">
                    <Grid x:Name="Row" Margin="0,7">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="Auto" />
                            <ColumnDefinition Width="*" MinWidth="16" />
                            <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>
                        <TextBlock Grid.Column="0" Style="{StaticResource Text.Secondary}" Text="{TemplateBinding Key}" VerticalAlignment="Bottom" />
                        <Line Grid.Column="1" X1="0" X2="1" Stretch="Fill" VerticalAlignment="Bottom" Margin="10,0,10,4"
                              Stroke="{DynamicResource Brush.LineStrong}" StrokeThickness="1" StrokeDashArray="1 3" />
                        <StackPanel Grid.Column="2" Orientation="Horizontal" VerticalAlignment="Bottom">
                            <TextBlock x:Name="Value" Style="{StaticResource Text.Number}" Text="{TemplateBinding Value}" VerticalAlignment="Bottom" />
                            <TextBlock x:Name="Note" Style="{StaticResource Text.Muted}" Margin="6,0,0,1" Text="{TemplateBinding Note}" VerticalAlignment="Bottom" />
                        </StackPanel>
                    </Grid>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsHero" Value="True">
                            <Setter TargetName="Row" Property="Margin" Value="0,12,0,8" />
                            <Setter TargetName="Value" Property="FontSize" Value="26" />
                            <Setter TargetName="Value" Property="FontWeight" Value="Light" />
                            <Setter TargetName="Note" Property="FontSize" Value="13" />
                        </Trigger>
                        <Trigger Property="Note" Value="">
                            <Setter TargetName="Note" Property="Visibility" Value="Collapsed" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- The rail: text with an LED that lights amber for the page shown. -->
    <Style x:Key="Rail" TargetType="RadioButton">
        <Setter Property="Foreground" Value="{DynamicResource Brush.Ink2}" />
        <Setter Property="FontFamily" Value="{StaticResource Font.Ui}" />
        <Setter Property="FontSize" Value="13" />
        <Setter Property="Cursor" Value="Hand" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="RadioButton">
                    <Border Background="Transparent" Padding="18,9">
                        <StackPanel Orientation="Horizontal">
                            <Ellipse x:Name="Led" Width="7" Height="7" StrokeThickness="1" Stroke="{DynamicResource Brush.Ink3}" Fill="Transparent" VerticalAlignment="Center" />
                            <ContentPresenter Margin="10,0,0,0" VerticalAlignment="Center" />
                        </StackPanel>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter Property="Foreground" Value="{DynamicResource Brush.Ink}" />
                        </Trigger>
                        <Trigger Property="IsChecked" Value="True">
                            <Setter Property="Foreground" Value="{DynamicResource Brush.Ink}" />
                            <Setter TargetName="Led" Property="Fill" Value="{DynamicResource Brush.Amber}" />
                            <Setter TargetName="Led" Property="Stroke" Value="{DynamicResource Brush.Amber}" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Title-bar buttons, flat until hovered. -->
    <Style x:Key="Caption" TargetType="Button">
        <Setter Property="Width" Value="46" />
        <Setter Property="Height" Value="37" />
        <Setter Property="FontFamily" Value="{StaticResource Font.Glyphs}" />
        <Setter Property="FontSize" Value="10" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.Ink2}" />
        <Setter Property="Background" Value="Transparent" />
        <Setter Property="Focusable" Value="False" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border x:Name="Face" Background="{TemplateBinding Background}">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Face" Property="Background" Value="{DynamicResource Brush.Raised}" />
                            <Setter Property="Foreground" Value="{DynamicResource Brush.Ink}" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
    <Style x:Key="Caption.Close" TargetType="Button" BasedOn="{StaticResource Caption}">
        <Style.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
                <Setter Property="Background" Value="#C42B1C" />
                <Setter Property="Foreground" Value="White" />
            </Trigger>
        </Style.Triggers>
    </Style>

    <!-- A plain bordered button for banners. -->
    <Style x:Key="Quiet" TargetType="Button">
        <Setter Property="FontFamily" Value="{StaticResource Font.Ui}" />
        <Setter Property="FontSize" Value="12.5" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.Ink}" />
        <Setter Property="Cursor" Value="Hand" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border x:Name="Face" Background="Transparent" BorderBrush="{DynamicResource Brush.LineStrong}" BorderThickness="1" CornerRadius="3" Padding="11,5">
                        <ContentPresenter />
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Face" Property="Background" Value="{DynamicResource Brush.Raised}" />
                        </Trigger>
                        <Trigger Property="IsKeyboardFocused" Value="True">
                            <Setter TargetName="Face" Property="BorderBrush" Value="{DynamicResource Brush.Amber}" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <Style x:Key="Banner" TargetType="Border">
        <Setter Property="Background" Value="{DynamicResource Brush.Raised}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.LineStrong}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="CornerRadius" Value="3" />
        <Setter Property="Padding" Value="14,10" />
        <Setter Property="Margin" Value="0,0,0,18" />
    </Style>

    <!-- A ledger's heading: two labels over a strong hairline. -->
    <Style x:Key="LedgerHead" TargetType="Border">
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.LineStrong}" />
        <Setter Property="BorderThickness" Value="0,0,0,1" />
        <Setter Property="Padding" Value="0,0,0,8" />
    </Style>
</ResourceDictionary>
```

`src/PowerLedger.App/Controls/Converters.cs`
```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PowerLedger.App;

/// <summary>True shows, false collapses; <see cref="Invert"/> turns it round.</summary>
internal sealed class VisibleWhen : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Checks a rail button when the shell shows its page, and shows its page when it is checked.</summary>
internal sealed class PageIs : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Equals(value, parameter);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? parameter : Binding.DoNothing;
}

/// <summary>Spec §9's labels are uppercase; the view model keeps them in sentence case.</summary>
internal sealed class UpperCase : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value as string)?.ToUpper(culture) ?? string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
```

`src/PowerLedger.App/Controls/QualityBadge.cs`
```csharp
using System.Windows;
using System.Windows.Controls;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>The quality label (spec §9), templated in Styles.xaml; hidden while there is no reading.</summary>
internal sealed class QualityBadge : Control
{
    public static readonly DependencyProperty QualityProperty = DependencyProperty.Register(
        nameof(Quality), typeof(Quality?), typeof(QualityBadge), new PropertyMetadata(null));

    public Quality? Quality { get => (Quality?)GetValue(QualityProperty); set => SetValue(QualityProperty, value); }
}
```

`src/PowerLedger.App/Controls/LedgerRow.cs`
```csharp
using System.Windows;
using System.Windows.Controls;

namespace PowerLedger.App;

/// <summary>One line of a ledger (spec §9: dotted leaders between labels and values), templated in Styles.xaml.</summary>
internal sealed class LedgerRow : Control
{
    public static readonly DependencyProperty KeyProperty = Text(nameof(Key));
    public static readonly DependencyProperty ValueProperty = Text(nameof(Value));
    public static readonly DependencyProperty NoteProperty = Text(nameof(Note));
    public static readonly DependencyProperty IsHeroProperty = DependencyProperty.Register(
        nameof(IsHero), typeof(bool), typeof(LedgerRow), new PropertyMetadata(false));

    public string Key { get => (string)GetValue(KeyProperty); set => SetValue(KeyProperty, value); }

    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public string Note { get => (string)GetValue(NoteProperty); set => SetValue(NoteProperty, value); }

    /// <summary>The first line of a ledger, set large.</summary>
    public bool IsHero { get => (bool)GetValue(IsHeroProperty); set => SetValue(IsHeroProperty, value); }

    private static DependencyProperty Text(string name)
        => DependencyProperty.Register(name, typeof(string), typeof(LedgerRow), new PropertyMetadata(string.Empty));
}
```

- [ ] **Step 2: Write the shell**

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

/// <summary>A screen Plan D2 builds; until then it says so.</summary>
internal sealed record PlaceholderViewModel(string Title, string Text);

/// <summary>The window: which page shows, the Now screen, and the version in the title bar.</summary>
internal sealed class ShellViewModel(NowViewModel now, string version) : ObservableObject
{
    private static readonly Dictionary<Page, PlaceholderViewModel> Placeholders = new()
    {
        [Page.Breakdown] = new("Breakdown", "Power by component over any range arrives in the next build."),
        [Page.Report] = new("Report", "The energy bill, comparisons and exports arrive in the next build."),
        [Page.Settings] = new("Settings", "Tariff, machine profile and preferences arrive in the next build."),
    };

    private Page _page = Page.Now;

    public NowViewModel Now { get; } = now;

    public string Version { get; } = version;

    public Page Page
    {
        get => _page;
        set
        {
            if (SetProperty(ref _page, value)) OnPropertyChanged(nameof(Current));
        }
    }

    public object Current => Page == Page.Now ? Now : Placeholders[Page];
}
```

`src/PowerLedger.App/Shell/MainWindow.xaml`
```xml
<Window x:Class="PowerLedger.App.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:local="clr-namespace:PowerLedger.App"
        Title="PowerLedger" Width="1180" Height="900" MinWidth="880" MinHeight="600"
        WindowStartupLocation="CenterScreen" Background="{DynamicResource Brush.Panel}"
        UseLayoutRounding="True" SnapsToDevicePixels="True" TextOptions.TextFormattingMode="Display">
    <WindowChrome.WindowChrome>
        <WindowChrome CaptionHeight="38" ResizeBorderThickness="6" GlassFrameThickness="0" CornerRadius="0" UseAeroCaptionButtons="False" />
    </WindowChrome.WindowChrome>
    <Window.Resources>
        <DataTemplate DataType="{x:Type local:NowViewModel}">
            <local:NowView />
        </DataTemplate>
        <DataTemplate DataType="{x:Type local:PlaceholderViewModel}">
            <StackPanel Margin="26,22" MaxWidth="520" HorizontalAlignment="Left">
                <TextBlock Style="{StaticResource Text.Eyebrow}" Text="{Binding Title, Converter={StaticResource Upper}}" />
                <TextBlock Style="{StaticResource Text.Secondary}" Margin="0,10,0,0" TextWrapping="Wrap" Text="{Binding Text}" />
            </StackPanel>
        </DataTemplate>
    </Window.Resources>

    <Border BorderBrush="{DynamicResource Brush.LineStrong}">
        <Border.Style>
            <Style TargetType="Border">
                <Setter Property="BorderThickness" Value="1" />
                <Style.Triggers>
                    <!-- Windows pushes a maximised window's resize border off screen; keep the content on it. -->
                    <DataTrigger Binding="{Binding WindowState, RelativeSource={RelativeSource AncestorType=Window}}" Value="Maximized">
                        <Setter Property="BorderThickness" Value="0" />
                        <Setter Property="Margin" Value="7" />
                    </DataTrigger>
                </Style.Triggers>
            </Style>
        </Border.Style>
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="38" />
                <RowDefinition Height="*" />
                <RowDefinition Height="Auto" />
            </Grid.RowDefinitions>

            <Border Grid.Row="0" Background="{DynamicResource Brush.Ground}" BorderBrush="{DynamicResource Brush.Line}" BorderThickness="0,0,0,1">
                <DockPanel>
                    <StackPanel DockPanel.Dock="Right" Orientation="Horizontal">
                        <Button Style="{StaticResource Caption}" Content="&#xE921;" ToolTip="Minimize" Click="MinimizeClick" WindowChrome.IsHitTestVisibleInChrome="True" />
                        <Button Style="{StaticResource Caption}" Content="&#xE922;" ToolTip="Maximize" Click="MaximizeClick" WindowChrome.IsHitTestVisibleInChrome="True" />
                        <Button Style="{StaticResource Caption.Close}" Content="&#xE8BB;" ToolTip="Close to the tray" Click="CloseClick" WindowChrome.IsHitTestVisibleInChrome="True" />
                    </StackPanel>
                    <StackPanel Orientation="Horizontal" Margin="14,0,0,0" VerticalAlignment="Center">
                        <Grid Width="12" Height="12" VerticalAlignment="Center">
                            <Border BorderBrush="{DynamicResource Brush.Amber}" BorderThickness="1.5" CornerRadius="2" />
                            <Line X1="5" Y1="9.5" X2="8.5" Y2="3.5" Stroke="{DynamicResource Brush.Amber}" StrokeThickness="1.5" />
                        </Grid>
                        <TextBlock Margin="9,0,0,0" Style="{StaticResource Text.Body}" FontSize="13" FontWeight="SemiBold" Text="PowerLedger" VerticalAlignment="Center" />
                        <TextBlock Margin="9,1,0,0" Style="{StaticResource Text.Muted}" FontFamily="{StaticResource Font.Numbers}" FontSize="11" Text="{Binding Version}" VerticalAlignment="Center" />
                    </StackPanel>
                </DockPanel>
            </Border>

            <Grid Grid.Row="1">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="168" />
                    <ColumnDefinition Width="*" />
                </Grid.ColumnDefinitions>
                <Border Grid.Column="0" Background="{DynamicResource Brush.Ground}" BorderBrush="{DynamicResource Brush.Line}" BorderThickness="0,0,1,0">
                    <DockPanel Margin="0,14,0,0">
                        <Border DockPanel.Dock="Bottom" BorderBrush="{DynamicResource Brush.Line}" BorderThickness="0,1,0,0" Padding="18,12">
                            <StackPanel Orientation="Horizontal">
                                <Ellipse Width="6" Height="6" VerticalAlignment="Center">
                                    <Ellipse.Style>
                                        <Style TargetType="Ellipse">
                                            <Setter Property="Fill" Value="{DynamicResource Brush.Ink3}" />
                                            <Style.Triggers>
                                                <DataTrigger Binding="{Binding Now.Status.Running}" Value="True">
                                                    <Setter Property="Fill" Value="{DynamicResource Brush.Measured}" />
                                                </DataTrigger>
                                            </Style.Triggers>
                                        </Style>
                                    </Ellipse.Style>
                                </Ellipse>
                                <TextBlock Margin="7,0,0,0" Style="{StaticResource Text.Muted}" FontSize="11" Text="{Binding Now.Status.State}" />
                            </StackPanel>
                        </Border>
                        <StackPanel>
                            <RadioButton Style="{StaticResource Rail}" GroupName="Rail" Content="Now"
                                         IsChecked="{Binding Page, Converter={StaticResource PageIs}, ConverterParameter={x:Static local:Page.Now}}" />
                            <RadioButton Style="{StaticResource Rail}" GroupName="Rail" Content="Breakdown"
                                         IsChecked="{Binding Page, Converter={StaticResource PageIs}, ConverterParameter={x:Static local:Page.Breakdown}}" />
                            <RadioButton Style="{StaticResource Rail}" GroupName="Rail" Content="Report"
                                         IsChecked="{Binding Page, Converter={StaticResource PageIs}, ConverterParameter={x:Static local:Page.Report}}" />
                            <RadioButton Style="{StaticResource Rail}" GroupName="Rail" Content="Settings"
                                         IsChecked="{Binding Page, Converter={StaticResource PageIs}, ConverterParameter={x:Static local:Page.Settings}}" />
                        </StackPanel>
                    </DockPanel>
                </Border>
                <ContentControl Grid.Column="1" Content="{Binding Current}" Focusable="False" />
            </Grid>

            <Border Grid.Row="2" Background="{DynamicResource Brush.Ground}" BorderBrush="{DynamicResource Brush.Line}" BorderThickness="0,1,0,0" Padding="16,7">
                <WrapPanel>
                    <WrapPanel.Resources>
                        <Style TargetType="TextBlock" BasedOn="{StaticResource Text.Secondary}">
                            <Setter Property="FontSize" Value="11" />
                            <Setter Property="Margin" Value="0,0,20,0" />
                        </Style>
                    </WrapPanel.Resources>
                    <TextBlock Text="{Binding Now.Status.Sampling}" />
                    <TextBlock Text="{Binding Now.Status.Calibration}" />
                    <TextBlock Text="{Binding Now.Status.Database}" />
                    <TextBlock Text="{Binding Now.Status.Service}" />
                    <TextBlock Text="{Binding Now.Status.Notice}" Foreground="{DynamicResource Brush.Amber}" />
                </WrapPanel>
            </Border>
        </Grid>
    </Border>
</Window>
```

`src/PowerLedger.App/Shell/MainWindow.xaml.cs`
```csharp
using System.Windows;

namespace PowerLedger.App;

/// <summary>The App's window (spec §9). Closing it hides it to the tray; the App decides that in Task 13.</summary>
public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
```

- [ ] **Step 3: Write the Now screen's layout**

`src/PowerLedger.App/Now/NowView.xaml`
```xml
<UserControl x:Class="PowerLedger.App.NowView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:local="clr-namespace:PowerLedger.App">
    <ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled" Focusable="False">
        <StackPanel Margin="26,22,26,18">

            <Border Style="{StaticResource Banner}" Visibility="{Binding IsServiceDown, Converter={StaticResource VisibleWhen}}">
                <DockPanel>
                    <Button DockPanel.Dock="Right" Style="{StaticResource Quiet}" Content="Start service" Command="{Binding StartService}" Margin="16,0,0,0" />
                    <TextBlock Style="{StaticResource Text.Body}" TextWrapping="Wrap" VerticalAlignment="Center"
                               Text="Service not running. History stays readable; live readings return when the service starts." />
                </DockPanel>
            </Border>
            <Border Style="{StaticResource Banner}" Visibility="{Binding IsSensorless, Converter={StaticResource VisibleWhen}}">
                <TextBlock Style="{StaticResource Text.Body}" TextWrapping="Wrap"
                           Text="Power sensors aren't available here. Readings are estimated from load and the machine profile; uptime and idle time are still logged." />
            </Border>

            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="1.15*" />
                    <ColumnDefinition Width="34" />
                    <ColumnDefinition Width="*" />
                </Grid.ColumnDefinitions>

                <StackPanel Grid.Column="0">
                    <TextBlock Style="{StaticResource Text.Eyebrow}" Text="{Binding Live.Eyebrow, Converter={StaticResource Upper}}" />
                    <local:LiveReadout Value="{Binding Live.Watts}" Margin="0,6,0,10" HorizontalAlignment="Left" />
                    <StackPanel Orientation="Horizontal">
                        <local:QualityBadge Quality="{Binding Live.Quality}" VerticalAlignment="Center" />
                        <TextBlock Style="{StaticResource Text.Muted}" Margin="12,0,0,0" VerticalAlignment="Center" Text="{Binding Live.QualityNote}" />
                    </StackPanel>
                    <local:MeterScale Margin="0,16,0,14" Range="{Binding Live.Meter}" Value="{Binding Live.Watts}"
                                      Average="{Binding Live.AverageW}" Peak="{Binding Live.PeakW}" />
                    <TextBlock Style="{StaticResource Text.Eyebrow}" Text="LAST 60 S" />
                    <local:Sparkline Margin="0,6,0,0" Values="{Binding Live.Spark}" />
                </StackPanel>

                <StackPanel Grid.Column="2">
                    <TextBlock Style="{StaticResource Text.Eyebrow}" Text="POWER BUDGET · NOW" />
                    <local:BudgetBar Margin="0,12,0,10" Rows="{Binding Live.Budget}" Total="{Binding Live.BudgetTotalW}" />
                    <ItemsControl ItemsSource="{Binding Live.Budget}" Focusable="False">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate DataType="{x:Type local:BudgetRow}">
                                <Border BorderBrush="{DynamicResource Brush.Line}" BorderThickness="0,1,0,0" Padding="0,8">
                                    <Grid>
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="10" />
                                            <ColumnDefinition Width="*" />
                                            <ColumnDefinition Width="Auto" />
                                            <ColumnDefinition Width="48" />
                                        </Grid.ColumnDefinitions>
                                        <Rectangle Grid.Column="0" Width="10" Height="10" RadiusX="1" RadiusY="1" VerticalAlignment="Top" Margin="0,4,0,0">
                                            <Rectangle.Style>
                                                <Style TargetType="Rectangle">
                                                    <Setter Property="Fill" Value="{DynamicResource Brush.PartRest}" />
                                                    <Style.Triggers>
                                                        <DataTrigger Binding="{Binding Part}" Value="Cpu"><Setter Property="Fill" Value="{DynamicResource Brush.PartCpu}" /></DataTrigger>
                                                        <DataTrigger Binding="{Binding Part}" Value="Gpu"><Setter Property="Fill" Value="{DynamicResource Brush.PartGpu}" /></DataTrigger>
                                                        <DataTrigger Binding="{Binding Part}" Value="Display"><Setter Property="Fill" Value="{DynamicResource Brush.PartDisplay}" /></DataTrigger>
                                                    </Style.Triggers>
                                                </Style>
                                            </Rectangle.Style>
                                        </Rectangle>
                                        <StackPanel Grid.Column="1" Margin="12,0,12,0">
                                            <TextBlock Style="{StaticResource Text.Body}" Text="{Binding Name}" />
                                            <TextBlock Style="{StaticResource Text.Muted}" FontSize="11" Text="{Binding Detail}" TextTrimming="CharacterEllipsis" />
                                        </StackPanel>
                                        <TextBlock Grid.Column="2" Style="{StaticResource Text.Number}" Text="{Binding Watts}" />
                                        <TextBlock Grid.Column="3" Style="{StaticResource Text.Number}" FontSize="12" Foreground="{DynamicResource Brush.Ink3}"
                                                   Text="{Binding Percent}" HorizontalAlignment="Right" Margin="0,1,0,0" />
                                    </Grid>
                                </Border>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </StackPanel>
            </Grid>

            <Grid Margin="0,26,0,0">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="34" />
                    <ColumnDefinition Width="*" />
                </Grid.ColumnDefinitions>

                <StackPanel Grid.Column="0">
                    <Border Style="{StaticResource LedgerHead}">
                        <DockPanel>
                            <TextBlock DockPanel.Dock="Right" Style="{StaticResource Text.Eyebrow}" Text="{Binding Today.Date, Converter={StaticResource Upper}}" />
                            <TextBlock Style="{StaticResource Text.Eyebrow}" Text="TODAY" />
                        </DockPanel>
                    </Border>
                    <TextBlock Style="{StaticResource Text.Muted}" Margin="0,10,0,0" Text="Collecting… first numbers in about a minute."
                               Visibility="{Binding IsCollecting, Converter={StaticResource VisibleWhen}}" />
                    <StackPanel Visibility="{Binding IsCollecting, Converter={StaticResource HiddenWhen}}">
                        <local:LedgerRow IsHero="True" Key="Energy" Value="{Binding Today.Energy}" Note="kWh" />
                        <local:LedgerRow Key="Cost" Value="{Binding Today.Cost}" Note="{Binding Today.Tariff}" />
                        <local:LedgerRow Key="Average" Value="{Binding Today.Average}" Note="W" />
                        <local:LedgerRow Key="Peak" Value="{Binding Today.Peak}" Note="{Binding Today.PeakAt}" />
                        <local:LedgerRow Key="On" Value="{Binding Today.On}" />
                        <local:LedgerRow Key="Idle, display on" Value="{Binding Today.Idle}" Note="{Binding Today.IdleWasted}" />
                        <local:LedgerRow Key="Asleep" Value="{Binding Today.Asleep}" />
                        <local:LedgerRow Key="CO₂" Value="{Binding Today.Co2}" Note="{Binding Today.Co2Factor}" />
                    </StackPanel>
                </StackPanel>

                <StackPanel Grid.Column="2">
                    <Border Style="{StaticResource LedgerHead}">
                        <DockPanel>
                            <TextBlock DockPanel.Dock="Right" Style="{StaticResource Text.Eyebrow}" Text="{Binding Month.Summary, Converter={StaticResource Upper}}" />
                            <TextBlock Style="{StaticResource Text.Eyebrow}" Text="{Binding Month.Name, Converter={StaticResource Upper}}" />
                        </DockPanel>
                    </Border>
                    <StackPanel Visibility="{Binding IsCollecting, Converter={StaticResource HiddenWhen}}">
                        <local:LedgerRow IsHero="True" Key="Energy" Value="{Binding Month.Energy}" Note="kWh" />
                        <local:LedgerRow Key="Cost" Value="{Binding Month.Cost}" />
                        <local:LedgerRow Key="Projected month" Value="{Binding Month.Projected}" Note="{Binding Month.ProjectedEnergy}" />
                        <local:LedgerRow Key="Daily average" Value="{Binding Month.DailyAverage}" />
                        <local:LedgerRow Key="Idle waste" Value="{Binding Month.IdleWaste}" Note="{Binding Month.IdleWasteCost}" />
                        <local:LedgerRow Key="Lowest day" Value="{Binding Month.Lowest}" Note="{Binding Month.LowestDate}" />
                        <local:LedgerRow Key="Highest day" Value="{Binding Month.Highest}" Note="{Binding Month.HighestDate}" />
                        <local:LedgerRow Key="Next report" Value="{Binding Month.NextReport}" Note="PDF to Documents" />
                    </StackPanel>
                </StackPanel>
            </Grid>

            <StackPanel Margin="0,26,0,0">
                <DockPanel Margin="0,0,0,8">
                    <TextBlock DockPanel.Dock="Right" Style="{StaticResource Text.Eyebrow}" Text="WATTS" />
                    <TextBlock Style="{StaticResource Text.Eyebrow}" Text="TODAY · 5-MIN · STACKED BY COMPONENT" />
                </DockPanel>
                <local:DayChart Slots="{Binding Chart.Slots}" Now="{Binding Chart.Now}" />
                <WrapPanel Margin="0,8,0,0">
                    <WrapPanel.Resources>
                        <Style TargetType="TextBlock" BasedOn="{StaticResource Text.Secondary}">
                            <Setter Property="FontSize" Value="12" />
                            <Setter Property="VerticalAlignment" Value="Center" />
                        </Style>
                        <Style TargetType="Rectangle">
                            <Setter Property="Width" Value="10" />
                            <Setter Property="Height" Value="10" />
                            <Setter Property="Margin" Value="0,0,7,0" />
                        </Style>
                    </WrapPanel.Resources>
                    <Rectangle Fill="{DynamicResource Brush.PartCpu}" />
                    <TextBlock Text="CPU " /><TextBlock Style="{StaticResource Text.Number}" FontSize="12" Text="{Binding Chart.CpuWh}" Margin="0,0,22,0" />
                    <Rectangle Fill="{DynamicResource Brush.PartGpu}" />
                    <TextBlock Text="GPU " /><TextBlock Style="{StaticResource Text.Number}" FontSize="12" Text="{Binding Chart.GpuWh}" Margin="0,0,22,0" />
                    <Rectangle Fill="{DynamicResource Brush.PartDisplay}" />
                    <TextBlock Text="Display " /><TextBlock Style="{StaticResource Text.Number}" FontSize="12" Text="{Binding Chart.DisplayWh}" Margin="0,0,22,0" />
                    <Rectangle Fill="{DynamicResource Brush.PartRest}" />
                    <TextBlock Text="Rest " /><TextBlock Style="{StaticResource Text.Number}" FontSize="12" Text="{Binding Chart.RestWh}" />
                </WrapPanel>
            </StackPanel>
        </StackPanel>
    </ScrollViewer>
</UserControl>
```

`src/PowerLedger.App/Now/NowView.xaml.cs`
```csharp
using System.Windows.Controls;

namespace PowerLedger.App;

/// <summary>The Now screen's layout (spec §9); everything it shows comes from <see cref="NowViewModel"/>.</summary>
public partial class NowView : UserControl
{
    public NowView() => InitializeComponent();
}
```

`src/PowerLedger.App/App.xaml`
```xml
<Application x:Class="PowerLedger.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <!-- The palette is inserted first by ThemeManager at start. -->
                <ResourceDictionary Source="Theme/Styles.xaml" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

- [ ] **Step 4: Build**

Run: `dotnet build -c Release`
Expected: `Build succeeded.` with 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.App/Theme/Styles.xaml src/PowerLedger.App/Controls/Converters.cs src/PowerLedger.App/Controls/QualityBadge.cs src/PowerLedger.App/Controls/LedgerRow.cs src/PowerLedger.App/Shell src/PowerLedger.App/Now/NowView.xaml src/PowerLedger.App/Now/NowView.xaml.cs src/PowerLedger.App/App.xaml
git commit -m "Lay out the window and the Now screen in the Meter and Ledger design"
```

---

### Task 12: The tray icon, start with Windows, and one App per session

**Files:**
- Create: `src/PowerLedger.App/Tray/TrayIcon.cs`
- Create: `src/PowerLedger.App/Tray/StartWithWindows.cs`
- Create: `src/PowerLedger.App/Tray/SingleInstance.cs`
- Create: `src/PowerLedger.App/Tray/ServiceStarter.cs`
- Test: `tests/PowerLedger.App.Tests/TrayTests.cs`

Spec §9's tray: the icon shows the live watts as text and redraws only when the rounded value changes; the tooltip gives now and today; the menu opens the window, toggles start with Windows (the HKCU Run entry the installer sets), and exits the UI while the service keeps logging. A second start of the App asks the first to show its window and exits. "Start service" runs `sc start` elevated, so Windows asks through UAC; declining is not an error. The icon's text rule, the Run entry and the single-instance handshake are tested; the drawing is seen in Task 14.

- [ ] **Step 1: Write the failing tests**

`tests/PowerLedger.App.Tests/TrayTests.cs`
```csharp
using Microsoft.Win32;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class TrayTests : IDisposable
{
    private readonly string _key = $@"Software\PowerLedger.Tests.{Guid.NewGuid():N}";

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_key, throwOnMissingSubKey: false);

    [Theory]
    [InlineData(null, "–")]
    [InlineData(34.4, "34")]
    [InlineData(999.4, "999")]
    [InlineData(1499.0, "1k")]
    [InlineData(2600.0, "3k")]
    public void The_icon_shows_whole_watts_and_thousands_as_k(double? watts, string expected)
        => TrayGlyph.Text(TrayGlyph.Round(watts)).ShouldBe(expected);

    [Fact]
    public void Start_with_windows_writes_and_removes_the_run_entry()
    {
        var autostart = new StartWithWindows(@"C:\Program Files\PowerLedger\PowerLedger.exe", _key);
        autostart.IsEnabled.ShouldBeFalse();

        autostart.Set(true);
        autostart.IsEnabled.ShouldBeTrue();
        using (var key = Registry.CurrentUser.OpenSubKey(_key)!)
        {
            key.GetValue("PowerLedger").ShouldBe("\"C:\\Program Files\\PowerLedger\\PowerLedger.exe\" --tray");
        }

        autostart.Set(false);
        autostart.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task A_second_start_finds_the_first_and_asks_it_to_show_itself()
    {
        var name = $"PowerLedger.test.{Guid.NewGuid():N}";
        using var first = new SingleInstance(name);
        using var second = new SingleInstance(name);
        first.IsFirst.ShouldBeTrue();
        second.IsFirst.ShouldBeFalse();

        var shown = 0;
        first.OnShowRequested(() => Interlocked.Increment(ref shown));
        second.SignalFirst();
        await WaitFor.True(() => Volatile.Read(ref shown) == 1);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.App.Tests --filter TrayTests`
Expected: build error, `TrayGlyph` not found.

- [ ] **Step 3: Write the tray pieces**

`src/PowerLedger.App/Tray/TrayIcon.cs`
```csharp
using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PowerLedger.App;

/// <summary>What the tray icon says: whole watts, thousands as "k", a dash without a reading.</summary>
internal static class TrayGlyph
{
    public static int? Round(double? watts) => watts is { } w && double.IsFinite(w) ? (int)Math.Round(Math.Max(0, w)) : null;

    public static string Text(int? watts) => watts switch
    {
        null => "–",
        < 1000 => watts.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => Math.Round(watts.Value / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture) + "k",
    };
}

/// <summary>
/// The tray icon (spec §9). It draws the live watts as text and redraws only when the rounded value changes; the tooltip
/// gives now and today; the menu opens the window, toggles start with Windows, and exits the UI while the service keeps
/// logging. Call it on the UI thread.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private static readonly Color Amber = Color.FromArgb(0xF2, 0xB2, 0x33);

    private readonly NotifyIcon _icon;
    private Icon? _current;
    private int? _shown;
    private bool _drawn;

    public TrayIcon(Action open, Action exit, StartWithWindows autostart)
    {
        var startWithWindows = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = autostart.IsEnabled };
        startWithWindows.CheckedChanged += (_, _) => autostart.Set(startWithWindows.Checked);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => open());
        menu.Items.Add(startWithWindows);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit UI", null, (_, _) => exit());
        _icon = new NotifyIcon { ContextMenuStrip = menu, Text = "PowerLedger", Visible = true };
        _icon.DoubleClick += (_, _) => open();
        Show(null, "PowerLedger · waiting for the service");
    }

    /// <summary>Updates the tooltip, and the icon when the rounded watts changed.</summary>
    public void Show(double? watts, string tooltip)
    {
        _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        var rounded = TrayGlyph.Round(watts);
        if (_drawn && rounded == _shown) return;
        _drawn = true;
        _shown = rounded;
        var next = Render(TrayGlyph.Text(rounded));
        _icon.Icon = next;
        _current?.Dispose();
        _current = next;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }

    /// <summary>The text in amber, as large as the small-icon size allows.</summary>
    private static Icon Render(string text)
    {
        var size = SystemInformation.SmallIconSize;
        using var bitmap = new Bitmap(size.Width, size.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);
            var em = size.Height * (text.Length <= 2 ? 0.78f : 0.62f);
            using var font = new Font("Bahnschrift", em, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Amber);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(text, font, brush, new RectangleF(-2, 0, size.Width + 4, size.Height), format);
        }
        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
```

`src/PowerLedger.App/Tray/StartWithWindows.cs`
```csharp
using Microsoft.Win32;

namespace PowerLedger.App;

/// <summary>The HKCU Run entry that starts the App with Windows, in the tray (spec §9: on by default, set by the installer).</summary>
internal sealed class StartWithWindows(string exePath, string keyPath = StartWithWindows.RunKey)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PowerLedger";

    public string Command => $"\"{exePath}\" --tray";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
    }

    public void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
```

`src/PowerLedger.App/Tray/SingleInstance.cs`
```csharp
namespace PowerLedger.App;

/// <summary>One App per session: a second start signals the first to show its window, then exits.</summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private RegisteredWaitHandle? _wait;

    public SingleInstance(string name = "PowerLedger.App")
    {
        _mutex = new Mutex(initiallyOwned: true, $@"Local\{name}", out var created);
        IsFirst = created;
        _show = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Show");
    }

    public bool IsFirst { get; }

    /// <summary>Asks the first instance to show itself.</summary>
    public void SignalFirst() => _show.Set();

    /// <summary>Runs <paramref name="show"/> on a pool thread each time a later start asks.</summary>
    public void OnShowRequested(Action show)
        => _wait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => show(), null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        _wait?.Unregister(null);
        _show.Dispose();
        if (IsFirst) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
```

`src/PowerLedger.App/Tray/ServiceStarter.cs`
```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace PowerLedger.App;

/// <summary>"Start service" on the Now screen's banner (spec §9). Starting a service needs an administrator, so Windows
/// asks through UAC; a user who declines has changed nothing, and nothing more is said.</summary>
internal static class ServiceStarter
{
    public static void Start()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), "start PowerLedger")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Win32Exception)
        {
            // The UAC prompt was declined.
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter TrayTests`
Expected: `Passed! - Failed: 0, Passed: 7`.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.App/Tray tests/PowerLedger.App.Tests/TrayTests.cs
git commit -m "Add the tray icon with live watts, start with Windows, and one App per session"
```

---

### Task 13: Compose the App

**Files:**
- Create: `src/PowerLedger.App/AppOptions.cs`
- Modify: `src/PowerLedger.App/App.xaml.cs`
- Test: `tests/PowerLedger.App.Tests/AppOptionsTests.cs`

The App's start: claim the session (a second start shows the first and exits), read `ui.json`, apply the theme, open the database read-only (Plan A's rule: one `SqliteDatabase` for the App's lifetime), start the link and the Now screen, add the tray icon, and show the window unless started with `--tray`, as the Run entry does. `--pipe` and `--data` reach a development service, matching the service's own switches. Closing the window hides it; "Exit UI" leaves the service logging.

- [ ] **Step 1: Write the failing test**

`tests/PowerLedger.App.Tests/AppOptionsTests.cs`
```csharp
using System.IO;
using Shouldly;

namespace PowerLedger.App.Tests;

public class AppOptionsTests
{
    [Fact]
    public void With_no_switches_the_App_reaches_the_installed_service()
    {
        var options = AppOptions.Parse([]);
        options.PipeName.ShouldBe("PowerLedger.v1");
        options.DatabasePath.ShouldBe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PowerLedger", "power.db"));
        options.StartInTray.ShouldBeFalse();
    }

    [Fact]
    public void Switches_reach_a_development_service_and_start_in_the_tray()
    {
        var options = AppOptions.Parse(["--pipe", "PowerLedger.dev", "--data", @"C:\Temp\pl-run", "--tray"]);
        options.PipeName.ShouldBe("PowerLedger.dev");
        options.DatabasePath.ShouldBe(@"C:\Temp\pl-run\power.db");
        options.StartInTray.ShouldBeTrue();
    }

    [Fact]
    public void A_switch_without_its_value_is_ignored()
        => AppOptions.Parse(["--pipe"]).PipeName.ShouldBe("PowerLedger.v1");
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PowerLedger.App.Tests --filter AppOptionsTests`
Expected: build error, `AppOptions` not found.

- [ ] **Step 3: Write the options and the composition**

`src/PowerLedger.App/AppOptions.cs`
```csharp
using System.IO;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <param name="PipeName">The service's pipe; --pipe names a development service's.</param>
/// <param name="DataFolder">Where the service keeps power.db; --data names a development run's folder.</param>
/// <param name="StartInTray">--tray: start with only the tray icon, as the Run entry does.</param>
internal sealed record AppOptions(string PipeName, string DataFolder, bool StartInTray)
{
    public static string DefaultDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PowerLedger");

    public string DatabasePath => Path.Combine(DataFolder, "power.db");

    public static AppOptions Parse(IReadOnlyList<string> args)
    {
        var pipe = PipeProtocol.PipeName;
        var data = DefaultDataFolder;
        var tray = false;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--pipe" when i + 1 < args.Count:
                    pipe = args[++i];
                    break;
                case "--data" when i + 1 < args.Count:
                    data = args[++i];
                    break;
                case "--tray":
                    tray = true;
                    break;
            }
        }
        return new AppOptions(pipe, data, tray);
    }
}
```

`src/PowerLedger.App/App.xaml.cs`
```csharp
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>The tray App (spec §9): one per session, living in the tray, with a window on demand.</summary>
public partial class App : Application
{
    private SingleInstance? _instance;
    private ThemeManager? _theme;
    private SqliteDatabase? _database;
    private PipeServiceLink? _link;
    private NowViewModel? _now;
    private ShellViewModel? _shell;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new SingleInstance();
        if (!_instance.IsFirst)
        {
            _instance.SignalFirst();
            Shutdown();
            return;
        }

        var options = AppOptions.Parse(e.Args);
        var preferences = new UiPreferencesStore(UiPreferencesStore.DefaultPath).Load();
        _theme = new ThemeManager(this, preferences.Theme);
        _database = new SqliteDatabase(options.DatabasePath, readOnly: true);
        _link = new PipeServiceLink(options.PipeName, new LastInputIdleSource(), TimeProvider.System);
        var threads = new UiThreads(action => Dispatcher.InvokeAsync(action), action => Task.Run(action));
        _now = new NowViewModel(
            _link, new HistoryReader(_database), threads, TimeProvider.System, TimeZoneInfo.Local, CultureInfo.CurrentCulture,
            preferences.Co2KgPerKwh, ServiceStarter.Start);
        _shell = new ShellViewModel(_now, Version());
        _tray = new TrayIcon(ShowWindow, ExitUi, new StartWithWindows(Environment.ProcessPath!));
        _now.PropertyChanged += OnNowChanged;
        _instance.OnShowRequested(() => Dispatcher.InvokeAsync(ShowWindow));

        _link.Start();
        _now.Start();
        if (!options.StartInTray) ShowWindow();
    }

    private void OnNowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NowViewModel.TrayTooltip)) _tray?.Show(_now?.Last?.TotalW, _now?.TrayTooltip ?? "PowerLedger");
    }

    private void ShowWindow()
    {
        if (_exiting || _shell is null) return;
        if (_window is null)
        {
            _window = new MainWindow { DataContext = _shell };
            _window.Closing += (_, args) =>
            {
                if (_exiting) return;
                args.Cancel = true;      // closing hides to the tray; the service keeps logging either way
                _window.Hide();
            };
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private async void ExitUi()
    {
        _exiting = true;
        try
        {
            _window?.Close();
            _tray?.Dispose();
            if (_now is not null)
            {
                _now.PropertyChanged -= OnNowChanged;
                _now.Dispose();
            }
            if (_link is not null) await _link.DisposeAsync();
            _theme?.Dispose();
            _database?.Dispose();
            _instance?.Dispose();
        }
        finally
        {
            Shutdown();
        }
    }

    private static string Version()
        => (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];
}
```

- [ ] **Step 4: Run the tests and the App**

Run: `dotnet test tests/PowerLedger.App.Tests --filter AppOptionsTests`
Expected: `Passed! - Failed: 0, Passed: 3`.

Run: `dotnet build -c Release`
Expected: `Build succeeded.` with 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/PowerLedger.App/AppOptions.cs src/PowerLedger.App/App.xaml.cs tests/PowerLedger.App.Tests/AppOptionsTests.cs
git commit -m "Compose the App: one per session, theme, history, link, Now screen, tray and window"
```

---

### Task 14: See it

**Files:**
- Create: `tests/PowerLedger.App.Tests/RenderingTests.cs`

The unit tests prove the numbers and the words; this task proves the look. A test draws the whole window, rail, Now screen and status bar, from fake data shaped like the mockup's Tuesday afternoon, in both themes, to PNGs in the temp folder. It runs on its own STA thread with the only `Application` in the test process, and it pumps the dispatcher long enough for the live readout to settle before drawing. It needs a desktop session, so it carries `Category=UI` and CI skips it with the hardware tests. Then the App runs for real against a development service.

- [ ] **Step 1: Write the rendering test**

`tests/PowerLedger.App.Tests/RenderingTests.cs`
```csharp
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Draws the window with the Now screen, in both themes, to PNGs for a person to look at.</summary>
[Trait("Category", "UI")]
public class RenderingTests
{
    public static readonly string Folder = Path.Combine(Path.GetTempPath(), "powerledger-renders");
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 7, TimeSpan.Zero);

    [Fact]
    public void The_window_draws_the_now_screen_in_both_themes()
    {
        Directory.CreateDirectory(Folder);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Render();
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();

        foreach (var theme in new[] { Theme.Dark, Theme.Light })
        {
            new FileInfo(Path.Combine(Folder, $"now-{theme}.png")).Length.ShouldBeGreaterThan(30_000);
        }
    }

    private static void Render()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PowerLedger;component/Theme/Styles.xaml", UriKind.Absolute),
        });
        var shell = new ShellViewModel(Model(), "0.1.0");
        ResourceDictionary? palette = null;
        foreach (var theme in new[] { Theme.Dark, Theme.Light })
        {
            if (palette is not null) application.Resources.MergedDictionaries.Remove(palette);
            palette = ThemeManager.Palette(theme);
            application.Resources.MergedDictionaries.Insert(0, palette);

            var window = new MainWindow
            {
                DataContext = shell, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
            };
            window.Show();
            Pump(TimeSpan.FromMilliseconds(1200));   // the live readout settles over 900 ms
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(Folder, $"now-{theme}.png"))) png.Save(file);
            window.Close();
        }
    }

    /// <summary>A Tuesday afternoon eight days into September: asleep until 07:30, a working morning, an idle patch, a peak at 14:00.</summary>
    private static NowViewModel Model()
    {
        var link = new FakeLink();
        var history = new FakeHistory();
        var model = new NowViewModel(link, history, UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc,
            CultureInfo.GetCultureInfo("en-US"), co2KgPerKwh: 0.38, startService: () => { });
        link.Connect(true);
        history.Snapshot = Snapshots.Typical(Now, Slots());
        model.RefreshHistory();
        var seed = 11;
        var watts = 33.0;
        for (var s = 59; s >= 0; s--)
        {
            seed = (seed * 9301 + 49297) % 233280;
            watts = Math.Clamp(watts + (seed / 233280.0 - 0.5) * 3, 27, 41);
            link.Push(Frames.At(Now.AddSeconds(-s), totalW: watts, cpu: watts * 0.43, gpu: watts * 0.12, display: 4.0));
        }
        return model;
    }

    private static IReadOnlyList<DaySlot> Slots()
    {
        var dayStart = new DateTimeOffset(Now.Date, TimeSpan.Zero);
        var slots = new List<DaySlot>();
        var seed = 7;
        double Noise()
        {
            seed = (seed * 9301 + 49297) % 233280;
            return seed / 233280.0 - 0.5;
        }
        for (var i = 0; i < 175; i++)
        {
            var total = i switch
            {
                < 90 => 0,
                < 108 => 14 + Noise() * 3,
                < 150 => 38 + 10 * Math.Sin((i - 108) / 42.0 * Math.PI) + Noise() * 8,
                < 159 => 15 + Noise() * 2,
                168 => 68,
                _ => 44 + Noise() * 6,
            };
            var start = dayStart + i * DaySlots.Length;
            slots.Add(total <= 0
                ? new DaySlot(start, 0, 0, 0, 0, 0, 300)
                : new DaySlot(start, total * 0.45, total * 0.11, 4, total * 0.44 - 4, 300, 0));
        }
        return slots;
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
```

- [ ] **Step 2: Run it and look at both pictures**

Run: `dotnet test tests/PowerLedger.App.Tests --filter RenderingTests`
Expected: `Passed! - Failed: 0, Passed: 1`, and `%TEMP%\powerledger-renders\now-Dark.png` and `now-Light.png` exist.

Open both PNGs and compare them with `docs/design/mockup-1-now-screen.html` in a browser. Check: the amber is spent only on the live number's needle, the sparkline and the now line; hairlines, not shadows; the quality badge is a solid green frame with a solid dot; the ledgers' dotted leaders line up; nothing overlaps or clips at 1180 × 900. Fix what does not match in `Styles.xaml`, `NowView.xaml` or a control, and run again.

- [ ] **Step 3: Run the App against a development service**

Build, then start a development service in one terminal and the App in another:

```bash
dotnet build -c Release
src/PowerLedger.Service/bin/Release/net10.0-windows/PowerLedger.Service.exe --data "$TEMP/pl-app" --pipe PowerLedger.dev
src/PowerLedger.App/bin/Release/net10.0-windows/PowerLedger.exe --data "$TEMP/pl-app" --pipe PowerLedger.dev
```

Expected: the tray icon shows whole watts; the window's number ticks once a second with a Measured badge on battery or Estimated on mains; the budget rows name the processor and the graphics card; the status bar says "Service running" once the first status poll lands. After a minute the ledgers leave "Collecting…". Stop the service: within three seconds the banner says the service is not running, and the tooltip says so. Start it again: within 30 seconds the readings return by themselves.

- [ ] **Step 4: Measure the App's memory**

With the window open for a minute, from PowerShell:

```powershell
$p = Get-Process PowerLedger
$private = (Get-Counter "\Process(PowerLedger)\Working Set - Private").CounterSamples[0].CookedValue
"{0:N1} MB private working set, {1:N1} MB working set" -f ($private / 1MB), ($p.WorkingSet64 / 1MB)
```

Expected: under 120 MB (spec §12's App gate, read as private working set like the service's). Record both numbers. Close the App with the tray menu's "Exit UI".

- [ ] **Step 5: Commit**

```bash
git add tests/PowerLedger.App.Tests/RenderingTests.cs
git commit -m "Draw the window in both themes to pictures, and run the App for real"
```

---

### Task 15: Final verification, the spec, and the handoff to D2

- [ ] **Step 1: Clean build with warnings as errors**

Run: `dotnet build -c Release`
Expected: `Build succeeded.` and `0 Warning(s)`.

- [ ] **Step 2: Full test run**

Run: `dotnet test -c Release`
Expected: Core `Passed: 110`, Storage `Passed: 41`, Sensors `Passed: 99`, Service `Passed: 127`, App `Passed: 73`, no failures.

- [ ] **Step 3: Confirm CI can skip the hardware and UI tests**

Run: `dotnet test -c Release --filter "Category!=Hardware&Category!=UI"`
Expected: App `Passed: 72` (the rendering test excluded), Service `Passed: 125`, Sensors `Passed: 94`, Core and Storage unchanged.

- [ ] **Step 4: Bring the spec in line**

In `docs/superpowers/specs/2026-09-08-powerledger-design.md`:

§9 Stack, replace the paragraph with:

```markdown
WPF on .NET 10 with its own `WindowChrome`, `CommunityToolkit.Mvvm`, QuestPDF for PDF. The meter, the budget bar, the sparkline and the charts are lightweight controls drawn with `DrawingContext`, whose geometry is tested as pure functions; WPF-UI's Fluent styles and LiveCharts2's SkiaSharp were dropped in Plan D1 because the design overrides the first almost everywhere and the second is far larger than four simple drawings need. The tray icon is WinForms' `NotifyIcon`, which also shows the monthly report's notification. One ViewModel per screen; no logic in code-behind.
```

§9, append to the Type bullet: ` Until the TTFs are added to src/PowerLedger.App/Fonts, every font reference falls back to Windows' own: Bahnschrift for text, Cascadia Mono then Consolas for numbers.`

§12, replace the **App** line with: `- **App**: ViewModel and geometry unit tests; the pipe client against a real pipe; history against a temp database; a rendering test that draws the window in both themes to PNG (Category UI, skipped in CI). A manual QA checklist per screen for v1.`

§13, replace `CI (GitHub Actions): build, non-hardware tests, installer artifact.` with `CI (GitHub Actions): build, tests outside the Hardware and UI categories, installer artifact.`

§14, in the third-party licence line, remove `WPF-UI (MIT), LiveCharts2 (MIT), `.

- [ ] **Step 5: Record what D2 builds on**

D2 (Breakdown, Report, Settings, the wizard, exports and the monthly PDF) uses:

| Piece | For |
|---|---|
| `IServiceLink`, `PipeServiceLink` | add `SetSettingsAsync`, `SetTariffAsync` and `ResetCalibrationAsync` beside `GetStatusAsync`, each returning the `ErrorReply` message to show as it is |
| `HistoryReader`, `IHistory`, `DaySlots` | the Breakdown and Report screens read ranges the same way, read-only |
| `Format`, `Money` | every number and amount on the new screens |
| `UiPreferences`, `UiPreferencesStore`, `ThemeManager.Choose` | the Settings screen's theme and CO₂ factor |
| `ShellViewModel` | replace each `PlaceholderViewModel` with the screen's view model |
| `Styles.xaml`, the palettes, `Instrument`, `Geometry`, `LedgerRow`, `QualityBadge` | the design system for the new screens |
| `TrayIcon` | the monthly report's notification, as a balloon tip |

**Rules D2 must follow.** These are contracts the types cannot enforce:

- Check the server before sending settings or tariffs (Plan C's rule): `GetNamedPipeServerProcessId` on the pipe's handle, and the process's image must be the installed service's. Plan E fixes that path; until then a development run skips the check only when started with `--pipe`.
- A view model never touches WPF types and reaches the UI thread only through `UiThreads`, so the tests keep running without a dispatcher.
- Views use `DynamicResource` for palette brushes and `StaticResource` only for styles and fonts.
- The CO₂ factor and the theme change from Settings at run time: `NowViewModel` takes the factor at construction, so D2 gives it a way to change it, and saves `ui.json` through `UiPreferencesStore`.
- Move Plan A's report arithmetic (`Summarise`, `Days`) into Core before computing totals in memory (Plan A's follow-up, still open).
- Give the drawn controls `AutomationProperties.Name` text, so a screen reader can say what the meter and the charts show.

**Known gaps, left for D2 or later:**

- The bundled fonts are not in the repository yet; downloading them needs the owner's go-ahead.
- The window does not remember its size and position.
- The custom maximise button does not open Windows 11's snap-layout flyout; snapping by dragging works.
- At 16 px the tray icon's three digits are small; the tooltip carries the exact value.

- [ ] **Step 6: Commit**

```bash
git add docs/superpowers/specs/2026-09-08-powerledger-design.md docs/superpowers/plans
git commit -m "Complete Plan D1: the tray, the window and the Now screen"
```

---

## Self-review against the spec

| Spec section | Covered by |
|---|---|
| §3 the App never writes the database; one ViewModel per screen, no logic in code-behind | Tasks 7, 9, 11 |
| §8 subscribe, status and settings requests by id, reconnect with backoff 1 s to 30 s, activity reports | Task 5 |
| §9 tray: live watts as text, redraw on change, tooltip, Open / Start with Windows / Exit UI | Tasks 12, 13 |
| §9 Now screen: live watts and quality, 60 s sparkline, meter with average and peak, today and month ledgers, budget bar and rows, today's stacked chart | Tasks 6–11 |
| §9 states: collecting, service down with Start, sensorless banner | Task 9 |
| §9 visual direction: tokens, both themes following the system, fonts, amber for the live reading only, quality by colour and form, hairlines, dotted leaders, LEDs, the settle animation and reduced motion | Tasks 4, 10, 11, 14 |
| §9 UI-only preferences in ui.json | Task 3 |
| §10 money in decimal with ISO 4217 codes, formatted with the culture | Task 2 |
| §12 App tests, performance gate for the App | Tasks 2–14 |
| §9 Breakdown, Report, Settings, the wizard, exports, monthly PDF | **Plan D2**, by design |

Names were checked across tasks: `Part` and `BudgetShare` come from Task 8 and are used by Tasks 9, 10 and 11; `BudgetRow`, `LivePanel`, `TodayLedger`, `MonthLedger`, `DayChartModel`, `StatusLine`, `Connection` and `UiThreads` from Task 9; `DaySlot` and `DaySlots.Length` from Task 6, used by Tasks 7, 10 and 14; `Theme`, `ThemeRules` and `ThemeManager.Palette` from Task 4, used by Task 14; `TrayGlyph` and `StartWithWindows` from Task 12, used by Task 13.
