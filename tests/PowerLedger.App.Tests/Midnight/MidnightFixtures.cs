using System.Globalization;
using System.Windows;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.App.Tests;

/// <summary>What the Midnight tests draw: a Tuesday's readings and the chart built from them, the shell's screens as
/// RenderingTests builds them for Classic, the Midnight window over them, and the palette put in place.</summary>
internal static class MidnightFixtures
{
    /// <summary>The shell with every screen on the same Tuesday afternoon, on the Dashboard.</summary>
    public static ShellViewModel Shell(FakeSaver saver, Updater? updates = null, FakeUiSettings? ui = null)
    {
        var now = NowScreen();
        var shell = new ShellViewModel(now, BreakdownScreen(), ReportScreen(saver), HouseholdScreen(), SettingsScreen(ui), WizardScreen(), "0.8.0", updates,
            DashboardScreen(now));
        shell.Page = Page.Dashboard;
        return shell;
    }

    /// <summary>
    /// The Dashboard over today's five-minute readings and the hourly ones for the longer ranges, with the totals of the Now
    /// screen's day (0.284 kWh), an average day of 0.3 kWh over the last 31, and a day before whose parts split differently,
    /// so each trend says something of its own.
    /// </summary>
    public static DashboardViewModel DashboardScreen(NowViewModel now)
    {
        var day = new DateTimeOffset(Now.Date, TimeSpan.Zero);
        var history = new FakeRangeHistory
        {
            Answer = range =>
            {
                var report = range.Title switch
                {
                    "Today" => Reports.Typical(range, 0.284),
                    "Last 31 days" => Reports.Typical(range, 31 * 0.3),
                    "Before" => Reports.Typical(range, 0.26),
                    _ => Reports.Typical(range),
                };
                if (range.Title == "Before")
                {
                    var parts = report.Totals;
                    report = report with { Totals = parts with { CpuKwh = parts.CpuKwh * 0.85, GpuKwh = parts.GpuKwh * 1.35, RestKwh = parts.RestKwh * 1.08 } };
                }
                return report with { Series = range.Bucket == TimeSpan.FromMinutes(5) ? DaySeries(day) : WeekSeries(range) };
            },
        };
        var summary = new FakeHistory { Snapshot = Snapshots.Typical(Now, DaySeries(day)), First = Now.AddDays(-40) };
        return new DashboardViewModel(now, history, summary, new FakeTimeProvider(Now), TimeZoneInfo.Utc, English, UiThreads.Inline);
    }

    /// <summary>
    /// A Midnight window over <paramref name="shell"/> in <paramref name="theme"/>'s Midnight palette, off screen, unactivated, with a switcher
    /// and a theme manager of its own. The palette is merged into the window, not the application, so tests on other threads
    /// pumping the same dispatcher can't swap it under the render; and the Classic palette the theme manager puts into the
    /// application on construction is taken out again, so it can't shadow the one the Classic render tests keep there. Call on the UI thread.
    /// </summary>
    public static MidnightWindow Window(ShellViewModel shell, Theme theme = Theme.Dark, Updater? updates = null, Action? feedback = null)
    {
        var app = Application.Current.Resources.MergedDictionaries;
        var before = app.Count;
        var manager = new ThemeManager(Application.Current, theme == Theme.Dark ? ThemeChoice.Dark : ThemeChoice.Light);
        if (app.Count == before + 1) app.RemoveAt(0);
        var looks = new LookSwitcher(_ => throw new InvalidOperationException("No switch in a render."), manager, _ => { }, _ => { });
        var window = new MidnightWindow(shell, looks, manager, updates ?? IdleUpdates(), feedback ?? (() => { }))
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
        };
        window.Resources.MergedDictionaries.Add(ThemeManager.Palette(Look.Midnight, theme));
        window.Closed += (_, _) => manager.Dispose();
        return window;
    }

    /// <summary>0.9.0 downloaded and ready to install, so the update card shows with its Restart button.</summary>
    public static Updater ReadyUpdate()
    {
        var updater = Updates(new FakeFeed { Latest = UpdaterTests.Release("0.9.0") });
        updater.CheckAsync().GetAwaiter().GetResult();
        return updater;
    }

    /// <summary>An updater that has not looked yet, so the update card stays hidden.</summary>
    public static Updater IdleUpdates() => Updates(new FakeFeed());

    private static Updater Updates(FakeFeed feed)
        => new(feed, new FakeDownloader(), new FakeSetup(), new FakeCost(), new FakeUiSettings(), UiThreads.Inline,
            new FakeTimeProvider(Now), TimeZoneInfo.Utc, English, new Version(0, 8, 0), (_, _) => { }, _ => { });

    /// <summary>The Now screen as RenderingTests draws it: on battery with two external monitors, a minute of readings, today's history.</summary>
    public static NowViewModel NowScreen()
    {
        var link = new FakeLink { Status = Statuses.WithMonitors(Statuses.Dell, Statuses.Portable) };
        var ownPlug = Statuses.Dell.WattsNow;
        var monitors = ownPlug + Statuses.Portable.WattsNow;
        var history = new FakeHistory();
        var model = new NowViewModel(link, history, UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc, English,
            co2KgPerKwh: 0.38, startService: () => { });
        link.Connect(true);
        history.Snapshot = Snapshots.Typical(Now, DaySeries(new DateTimeOffset(Now.Date, TimeSpan.Zero)));
        model.RefreshHistory();
        var seed = 11;
        var watts = 33.0;
        for (var s = 59; s >= 0; s--)
        {
            seed = (seed * 9301 + 49297) % 233280;
            watts = Math.Clamp(watts + (seed / 233280.0 - 0.5) * 3, 27, 41);
            link.Push(Frames.At(Now.AddSeconds(-s), totalW: watts + ownPlug, cpu: watts * 0.43, gpu: watts * 0.12, display: 4.0, monitors: monitors));
        }
        return model;
    }

    public static BreakdownViewModel BreakdownScreen()
    {
        var link = new FakeLink();
        link.Connect(true);
        var history = new FakeRangeHistory { Answer = range => Reports.Typical(range) with { Series = WeekSeries(range) } };
        return new BreakdownViewModel(link, history, UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc, English);
    }

    public static ReportViewModel ReportScreen(FakeSaver saver)
    {
        var history = new FakeRangeHistory { Answer = Reports.Typical };
        return new ReportViewModel(new FakeLink(), history, new FakeHouseholdHistory(), new FakeSleep(), saver, _ => [], UiThreads.Inline,
            new FakeTimeProvider(Now), TimeZoneInfo.Utc, English, 0.38);
    }

    /// <summary>A household of two PCs: this desktop, and a laptop last seen three days ago.</summary>
    public static HouseholdViewModel HouseholdScreen()
    {
        var link = new FakeLink
        {
            Status = Statuses.Running() with { Household = new HouseholdStatus("hh1", "aaaa", "Desktop-1", ChassisKind.Desktop, true, [], null) },
        };
        link.Connect(true);
        var history = new FakeHouseholdHistory
        {
            Answer = _ => new HouseholdSnapshot(
                new HouseholdRangeTotals(1.62, [new CurrencyCost("USD", 0.28m)], []),
                new HouseholdRangeTotals(11.4, [new CurrencyCost("USD", 1.94m)], []),
                new HouseholdRangeTotals(46.8, [new CurrencyCost("USD", 7.96m)], [new DeviceEnergy("aaaa", 34.2), new DeviceEnergy("bbbb", 12.6)]),
                [
                    new HouseholdMemberRow("aaaa", "Desktop-1", ChassisKind.Desktop, Now.AddDays(-40), null, Now.AddMinutes(-2)),
                    new HouseholdMemberRow("bbbb", "Laptop-2", ChassisKind.Laptop, Now.AddDays(-20), null, Now.AddDays(-3)),
                ]),
        };
        var model = new HouseholdViewModel(link, history, UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc, English, FakeAccount.Model(link));
        model.Show();   // the members come from the household history when the page shows; the header wants them from the start
        model.Hide();
        return model;
    }

    /// <summary>Settings over <paramref name="ui"/>, by default preferences in the Midnight look, as a Midnight window has them.</summary>
    public static SettingsViewModel SettingsScreen(FakeUiSettings? ui = null)
    {
        var link = new FakeLink();
        link.Status = Statuses.WithMonitors(Statuses.Dell, Statuses.Portable) with { PowerDevices = [Statuses.Ups, Statuses.PowerSupply] };
        link.Connect(true);
        ui ??= new FakeUiSettings { Current = UiPreferences.Default with { Look = Look.Midnight, LookIntroduced = true } };   // the new look's banner told once already
        return new SettingsViewModel(link, new FakeMachineHistory(), ui, UiThreads.Inline, new FakeTimeProvider(Now),
            TimeZoneInfo.Utc, English, "USD");
    }

    public static WizardViewModel WizardScreen()
    {
        var link = new FakeLink { Status = Statuses.WithMonitors() };
        link.Connect(true);
        return new WizardViewModel(link, new FakeMachineHistory(), new FakeUiSettings(), UiThreads.Inline, new FakeTimeProvider(Now),
            TimeZoneInfo.Utc, English, "USD");
    }

    /// <summary>Hourly aggregates over a range: asleep overnight, working days, quiet evenings.</summary>
    public static IReadOnlyList<Aggregate> WeekSeries(DateRange range)
    {
        var series = new List<Aggregate>();
        var seed = 5;
        double Noise()
        {
            seed = (seed * 9301 + 49297) % 233280;
            return seed / 233280.0 - 0.5;
        }
        for (var start = range.From; start < range.To; start += range.Bucket)
        {
            var hour = start.Hour;
            var (watts, on) = hour switch
            {
                < 7 or 23 => (0.0, 0.0),
                7 => (16 + Noise() * 2, 1800.0),
                < 12 => (36 + Noise() * 10, 3600.0),
                < 13 => (18 + Noise() * 3, 3600.0),
                < 18 => (42 + Noise() * 14, 3600.0),
                _ => (15 + Noise() * 3, 3600.0),
            };
            var wh = watts * on / 3600;
            series.Add(Aggregate.Empty(start) with
            {
                EnergyWh = wh, CpuWh = wh * 0.45, GpuWh = wh * 0.1, DisplayWh = 4 * on / 3600, RestWh = wh * 0.45 - 4 * on / 3600,
                OnSeconds = on, GapSeconds = 3600 - on,
            });
        }
        return series;
    }

    public static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 7, TimeSpan.Zero);
    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>Today's range in UTC, as the Dashboard's 1D pill reads it.</summary>
    public static DateRange Today() => Ranges.Today(Now, TimeZoneInfo.Utc, English);

    /// <summary>Today's chart in watts: asleep until 07:30, a working morning, an idle patch, a peak at 14:00, on to now.</summary>
    public static ChartModel DayChart() => Charts.Build(Today(), DaySeries(Today().From), ChartUnit.Watts, TimeZoneInfo.Utc, English);

    /// <summary>Five-minute aggregates from midnight to now, with a sleep gap and a shape a person recognises as a day.</summary>
    public static IReadOnlyList<Aggregate> DaySeries(DateTimeOffset dayStart)
    {
        var series = new List<Aggregate>();
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
            var start = dayStart.AddMinutes(5 * i);
            series.Add(total <= 0
                ? Aggregate.Empty(start) with { GapSeconds = 300 }
                : Aggregate.Empty(start) with
                {
                    CpuWh = total * 0.45 / 12, GpuWh = total * 0.11 / 12, DisplayWh = 4 / 12.0, RestWh = (total * 0.44 - 4) / 12,
                    EnergyWh = total / 12, OnSeconds = 300,
                });
        }
        return series;
    }
}
