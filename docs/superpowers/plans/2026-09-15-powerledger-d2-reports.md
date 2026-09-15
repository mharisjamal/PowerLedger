# PowerLedger Plan D2 — Breakdown, Report, exports and the monthly PDF — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the App's history screens. Breakdown shows power by component over today, 7 days, 30 days, a month or a custom range, in watts or watt-hours. Report shows the energy bill for a range with its comparisons, idle waste and a saving suggestion. Both get CSV, PNG and PDF exports, and the App writes a monthly PDF to Documents on its own.

**Architecture:** Storage gains one query, `ReportQueries.Series`, so a chart and its totals read the same rows. Short ranges now read minute rows throughout, which makes them exact to the minute. The App reads a `RangeReport` for a `DateRange` through `IRangeHistory`, read-only like the Now screen. The Now screen's day chart becomes a general `StackedChart` that every chart shares. Exports are pure where they can be (CSV lines, and a `ReportData` record the screen, the PDF and the monthly job all read) and thin where they cannot (the Save dialog, drawing a PNG). The monthly reports are a small scheduler that decides which finished months lack a PDF and writes them.

**Tech Stack:** as Plan D1, plus QuestPDF 2026.9.0 (Community licence) for PDF. Windows' sleep and display timeouts come from `powrprof`'s `PowerReadACValueIndex` and `PowerReadDCValueIndex`. Spec: `docs/superpowers/specs/2026-09-08-powerledger-design.md` §6, §7, §9.

**Git rule (from the owner):** commit locally after every task. Never add a remote, push, or create a GitHub repo.

**Scope note:** Settings, the first-run wizard and every request that changes the service belong to Plan D3; the rail's Settings page keeps D1's placeholder. Plan E is the installer.

---

## What already exists

`main` holds Plans A to D1. D2 builds on D1's `HistoryReader`, `Format`, `Money`, `UiThreads`, `Instrument`, `Geometry`, `LedgerRow`, `Styles.xaml`, the palettes and `ShellViewModel`, and on Plan A's `ReportQueries`, `AggregateRepository`, `RawSampleRepository`, `RangeTotals`, `DayTotals`, `Comparisons` and `Co2`.

D1 set rules for D2, and this plan honours them:
- View models never touch WPF types and reach the UI thread only through `UiThreads`.
- Views use `DynamicResource` for palette brushes.
- The CO₂ factor comes from `ui.json`.
- The drawn controls say what they show to a screen reader.

Two D1 rules wait for D3, the first plan to need them: the server check before sending settings, and changing the CO₂ factor and theme at run time.

## Decisions made while planning

- **A short range reads minute rows throughout.** Plan A read a folded hour's row even where a range covered only part of that hour. A chart with five- or fifteen-minute buckets would then pile each hour into the bucket where the hour starts. A range of up to three days now reads its minutes, which makes it exact to the minute in every time zone. An hour row stands in only where retention has purged the minutes. Long ranges keep Plan A's rule: they read hour rows, and their cut at a local midnight moves to a UTC hour in a zone half an hour off UTC. Adjacent months still tile with no hour lost or counted twice.
- **Sleep is laid back in Storage.** `Series` knows each row's length, so it spreads every sleep backwards from the moment the machine woke over the buckets the sleep covered. D1's `DaySlots` did this for today's slots only; with `Series` in place it is deleted.
- **One chart control for every chart.** `StackedChart` draws any run of buckets across a fixed number of positions. Today's chart spans the whole day with the future left empty, and seven days span seven days. D1's `DayChart` becomes this control and is deleted.
- **Custom ranges use WPF's `DatePicker`,** styled at its text box and button. Restyling the drop-down calendar is a large template for little gain, so the calendar keeps Windows' own look. This is noted as a gap.
- **No CO₂ table and no tariff suggestions yet.** Spec §16 leaves their source open, so D2 invents no figures. CO₂ uses the factor in `ui.json` (default 0.40 kg/kWh, editable in D3).
- **PNG export splits between view and view model.** Drawing a visual to a stream is view mechanics, so the Report view does it in a click handler. The view model asks where to save and reports the outcome, as it does for PDF and CSV.
- **QuestPDF draws the PDF from a plain record.** `ReportData` holds the strings and numbers a report shows, so the screen, the PDF and the monthly job agree. The PDF's bars are QuestPDF layout rather than SVG or a canvas, so they need nothing QuestPDF might not render.
- **The monthly job looks back twelve finished months at most,** and skips a month with no readings at all. An App that was not run for a long time writes a year of reports at most, and never a stack of empty ones.
- **The drawn controls describe themselves.** `Instrument` gets an automation peer whose name comes from each control's `Describe()`. A screen reader then says "Power by component, today…" rather than nothing.

## File structure

```
src/PowerLedger.Storage/
  ReportQueries.cs              Modify: short ranges read minutes throughout; Series(from, to, bucket) with sleep laid back
  AggregateRepository.cs        Modify: FirstMinuteStart()
src/PowerLedger.App/
  History/Ranges.cs             DateRange and the presets; local-clock helpers; bucket names; the days a range covers
  History/RangeHistory.cs       IRangeHistory, RangeReport, ExportGrain
  History/CsvExport.cs          readings and rows as CSV lines
  History/RangePicker.cs        RangeChoice and the chosen range, shared by both history screens
  History/Bands.cs              the four bands of a range as table rows
  History/HistoryReader.cs      Modify: implements IRangeHistory; today's chart from Series
  History/DaySlots.cs           Delete
  Charts/ChartModel.cs          ChartUnit, ChartBucket, AxisTick, ChartModel
  Charts/Charts.cs              buckets in a unit, the time axis, the chart for a range
  Controls/StackedChart.cs      the stacked chart for any range
  Controls/DayChart.cs          Delete
  Controls/RangeBar.xaml(.cs)   the range buttons and custom dates both history screens share
  Controls/DailyBars.cs         one bar per day
  Controls/QualityBar.cs        the quality mix as one bar
  Controls/Instrument.cs        Modify: automation peer and Describe()
  Controls/Geometry.cs          Modify: StackTops over ChartBucket; ChartScale floor; LabelEvery
  Controls/Converters.cs        Modify: PageIs becomes ValueIs
  Controls/LiveReadout.cs, MeterScale.cs, Sparkline.cs, BudgetBar.cs   Modify: Describe()
  Breakdown/BreakdownViewModel.cs, Breakdown/BreakdownView.xaml(.cs)
  Report/SleepSettings.cs       Windows' sleep and display timeouts
  Report/IdleAdvice.cs          the saving suggestion
  Report/ReportData.cs          everything a report shows
  Report/ReportDocument.cs      the PDF
  Report/FileSaver.cs           IFileSaver and the Save dialog
  Report/ReportViewModel.cs, Report/ReportView.xaml(.cs)
  Report/MonthlyReports.cs      which months need a PDF, and writing them
  Now/Panels.cs, Now/NowViewModel.cs, Now/NowView.xaml   Modify: today's chart uses StackedChart
  Shell/ShellViewModel.cs, Shell/MainWindow.xaml, Tray/TrayIcon.cs, App.xaml.cs, Theme/Styles.xaml,
  Formatting/Format.cs, PowerLedger.App.csproj   Modify
tests/PowerLedger.Storage.Tests/ReportQueriesTests.cs    Modify
tests/PowerLedger.App.Tests/
  RangesTests.cs, CsvExportTests.cs, ChartTests.cs, RangePickerTests.cs, BreakdownViewModelTests.cs,
  SleepSettingsTests.cs, IdleAdviceTests.cs, ReportDataTests.cs, ReportDocumentTests.cs, ReportViewModelTests.cs,
  DescriptionTests.cs, ShellViewModelTests.cs, MonthlyReportsTests.cs,
  FakeRangeHistory.cs, FakeSaver.cs, FakeSleep.cs, Reports.cs, Sta.cs
  DaySlotsTests.cs              Delete
  GeometryTests.cs, HistoryReaderTests.cs, NowViewModelTests.cs, Snapshots.cs, RenderingTests.cs   Modify
```

---

### Task 1: Short ranges to the minute, and a series for charts

**Files:**
- Modify: `src/PowerLedger.Storage/ReportQueries.cs`
- Modify: `src/PowerLedger.Storage/AggregateRepository.cs`
- Test: `tests/PowerLedger.Storage.Tests/ReportQueriesTests.cs`

`Series` cuts a range into buckets from the rows the totals read, so a chart and its totals always agree (spec §7's `GetSeries`). A row is never split: it counts wholly in the bucket where it starts. That only works for buckets under an hour if the range reads minute rows, so a short range now reads its minutes throughout. A folded hour's row stands in only where retention has purged the minutes.

Each bucket's `GapSeconds` becomes the time the machine slept inside it. A sleep is stored in the row where the machine woke, and the on-time in that row came after the wake. So the sleep ended that long before the row did, and it is laid back from there over the buckets it covered. `FirstMinuteStart` tells the monthly reports where history begins.

- [x] **Step 1: Write the failing tests**

Append to `tests/PowerLedger.Storage.Tests/ReportQueriesTests.cs`, inside the class:

```csharp
    [Fact]
    public void A_short_range_reads_minutes_even_inside_a_folded_hour()
    {
        using var t = new TestDatabase();
        var agg = new AggregateRepository(t.Db);
        var hour = Enumerable.Range(0, 60).Select(i => Downsampler.ToMinute(Fixtures.T0.AddMinutes(i), Fixtures.Minute(i * 60, 60))).ToList();
        agg.UpsertHour(Downsampler.ToHour(Fixtures.T0, hour));
        foreach (var m in hour) agg.UpsertMinute(m);

        // Half the hour is in the range. Its minutes say so, where the hour row would have counted all of it.
        new ReportQueries(t.Db).Totals(Fixtures.T0.AddMinutes(30), Fixtures.T0.AddHours(1)).EnergyKwh.ShouldBe(30.0 / 1000, 1e-9);
    }

    [Fact]
    public void A_series_cuts_the_range_into_buckets_that_add_up_to_its_totals()
    {
        using var t = new TestDatabase();
        SeedThreeHours(t);
        var queries = new ReportQueries(t.Db);

        var series = queries.Series(Fixtures.T0, Fixtures.T0.AddHours(4), TimeSpan.FromHours(1));

        series.Select(b => b.Start).ShouldBe(Enumerable.Range(0, 4).Select(h => Fixtures.T0.AddHours(h)));
        series[0].EnergyWh.ShouldBe(30, 1e-9);                     // sixty minutes at 30 W
        series[0].AvgW.ShouldBe(30, 1e-9);
        series[2].EnergyWh.ShouldBe(60, 1e-9);                     // sixty minutes at 60 W
        series[3].EnergyWh.ShouldBe(0);
        series.Sum(b => b.EnergyWh).ShouldBe(queries.Totals(Fixtures.T0, Fixtures.T0.AddHours(4)).EnergyKwh * 1000, 1e-9);
    }

    [Fact]
    public void A_long_series_puts_each_hour_row_in_the_bucket_where_it_starts()
    {
        using var t = new TestDatabase();
        var agg = new AggregateRepository(t.Db);
        for (var d = 0; d < 5; d++)
            agg.UpsertHour(Downsampler.ToHour(Fixtures.T0.AddDays(d), [Downsampler.ToMinute(Fixtures.T0.AddDays(d), Fixtures.Minute(0, 100))]));

        var series = new ReportQueries(t.Db).Series(Fixtures.T0, Fixtures.T0.AddDays(5), TimeSpan.FromDays(1));
        series.Count.ShouldBe(5);
        series.ShouldAllBe(b => Math.Abs(b.EnergyWh - 100.0 / 60) < 1e-9);
    }

    [Fact]
    public void A_sleep_is_laid_back_over_the_buckets_it_covered()
    {
        // Two hours asleep, stored in the minute starting at 14:00 with thirty seconds on after the wake:
        // asleep from 12:00:30 to 14:00:30.
        using var t = new TestDatabase();
        new AggregateRepository(t.Db).UpsertMinute(Aggregate.Empty(Fixtures.T0.AddHours(2)) with { OnSeconds = 30, GapSeconds = 7200, SampleCount = 30 });

        var series = new ReportQueries(t.Db).Series(Fixtures.T0, Fixtures.T0.AddMinutes(125), TimeSpan.FromMinutes(5));
        series.Count.ShouldBe(25);
        series[0].GapSeconds.ShouldBe(270, 1e-9);
        series.Skip(1).Take(23).ShouldAllBe(b => Math.Abs(b.GapSeconds - 300) < 1e-9);
        series[24].GapSeconds.ShouldBe(30, 1e-9);
        series[24].OnSeconds.ShouldBe(30);
    }

    [Fact]
    public void A_sleep_that_began_before_the_range_is_cut_at_its_start()
    {
        using var t = new TestDatabase();
        new AggregateRepository(t.Db).UpsertMinute(Aggregate.Empty(Fixtures.T0.AddMinutes(10)) with { OnSeconds = 60, GapSeconds = 3600, SampleCount = 60 });

        var series = new ReportQueries(t.Db).Series(Fixtures.T0, Fixtures.T0.AddMinutes(15), TimeSpan.FromMinutes(5));
        series.Sum(b => b.GapSeconds).ShouldBe(600, 1e-9);        // 12:00 to 12:10
    }

    [Fact]
    public void The_first_minute_row_says_where_history_begins()
    {
        using var t = new TestDatabase();
        var repository = new AggregateRepository(t.Db);
        repository.FirstMinuteStart().ShouldBeNull();
        SeedThreeHours(t);
        repository.FirstMinuteStart().ShouldBe(Fixtures.T0);
    }
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.Storage.Tests --filter ReportQueriesTests`
Expected: build error, `Series` and `FirstMinuteStart` not found.

- [x] **Step 3: Read minutes for short ranges, and add the series**

`src/PowerLedger.Storage/ReportQueries.cs`
```csharp
using PowerLedger.Core;

namespace PowerLedger.Storage;

/// <summary>Read side of the report: range totals, daily bars and chart series, with cost priced at query time.</summary>
public sealed class ReportQueries(SqliteDatabase db)
{
    /// <summary>Ranges up to this length read minute rows; longer ranges read hour rows.</summary>
    public static readonly TimeSpan MinuteResolutionLimit = TimeSpan.FromDays(3);

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <summary>Totals and daily bars from one read of the rows and one read of the tariffs, so the two always agree.</summary>
    public (RangeTotals Totals, List<DayTotals> Days) Report(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        var window = Load(from, to);
        var schedule = new TariffRepository(db).Schedule();
        return (Summarise(window, schedule), Days(window.Rows, schedule, zone));
    }

    /// <summary>Totals for the range. The window is widened to whole rows of the resolution used, so no partial row is dropped.</summary>
    public RangeTotals Totals(DateTimeOffset from, DateTimeOffset to)
        => Summarise(Load(from, to), new TariffRepository(db).Schedule());

    /// <summary>One bucket per local calendar day that has rows, oldest first. Days are cut at local midnight; in a zone
    /// whose offset is not a whole number of hours, a range long enough to read hour rows shifts that cut to the enclosing UTC hour.</summary>
    public List<DayTotals> DailyBuckets(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
        => Days(Load(from, to).Rows, new TariffRepository(db).Schedule(), zone);

    /// <summary>
    /// The range cut into buckets of <paramref name="bucket"/> from <paramref name="from"/> (spec §7 GetSeries), made from
    /// the rows the totals read, so a chart and its totals agree. A row counts wholly in the bucket where it starts, so a
    /// bucket shorter than an hour wants a range short enough to read minute rows. AvgW is each bucket's energy over its
    /// on-time. GapSeconds is laid back: each sleep is spread backwards from the moment the machine woke over the buckets
    /// it covered, so a chart shows it where it happened, and no bucket holds more sleep than its own length.
    /// </summary>
    public List<Aggregate> Series(DateTimeOffset from, DateTimeOffset to, TimeSpan bucket)
    {
        var count = Math.Max(1, (int)Math.Ceiling((to - from) / bucket));
        var buckets = new Aggregate[count];
        var asleep = new double[count];
        for (var i = 0; i < count; i++) buckets[i] = Aggregate.Empty(from + i * bucket);

        var window = Load(from, to);
        var rows = window.Hours.Select(h => (Row: h, Length: Hour)).Concat(window.Minutes.Select(m => (Row: m, Length: Minute)));
        foreach (var (row, length) in rows)
        {
            var index = Math.Clamp((int)Math.Floor((row.Start - from) / bucket), 0, count - 1);
            buckets[index] = buckets[index].Plus(row with { GapSeconds = 0 });
            // A sleep is stored in the row where the machine woke, and the time on in that row came after the wake.
            var woke = row.Start + length - TimeSpan.FromSeconds(Math.Min(row.OnSeconds, length.TotalSeconds));
            LayBack(asleep, woke, row.GapSeconds, from, bucket);
        }
        return [.. buckets.Select((b, i) => b with
        {
            AvgW = b.OnSeconds > 0 ? b.EnergyWh * 3600 / b.OnSeconds : 0,
            GapSeconds = Math.Min(asleep[i], bucket.TotalSeconds),
        })];
    }

    /// <summary>
    /// The rows for a range, and the window they cover: the range widened to whole rows. A short range reads minute rows
    /// throughout, so it is exact to the minute; an hour row stands in only for an hour whose minutes retention has purged.
    /// A long range reads hour rows, which are kept forever, and minute rows only past the last hour the hourly job has
    /// folded, so the read stays bounded and an hour in progress is never lost. No hour is ever counted twice.
    /// </summary>
    private Window Load(DateTimeOffset from, DateTimeOffset to)
    {
        var repo = new AggregateRepository(db);
        var hourFrom = Floor(from, Hour);
        var hourTo = Ceiling(to, Hour);
        var minuteFrom = Floor(from, Minute);
        var minuteTo = Ceiling(to, Minute);

        if (to - from <= MinuteResolutionLimit)
        {
            var minutes = repo.ReadMinutes(minuteFrom, minuteTo);
            var covered = minutes.Select(m => Floor(m.Start, Hour)).ToHashSet();
            var purged = repo.ReadHours(hourFrom, hourTo).Where(h => !covered.Contains(h.Start)).ToList();
            return purged.Count > 0 ? new Window(hourFrom, hourTo, purged, minutes) : new Window(minuteFrom, minuteTo, [], minutes);
        }

        var hours = repo.ReadHours(hourFrom, hourTo);
        var tailFrom = Max(minuteFrom, hours.Count > 0 ? hours[^1].Start + Hour : hourTo - MinuteResolutionLimit);
        var tail = tailFrom < minuteTo ? repo.ReadMinutes(tailFrom, minuteTo) : [];
        return hours.Count > 0 ? new Window(hourFrom, hourTo, hours, tail) : new Window(minuteFrom, minuteTo, [], tail);
    }

    /// <summary>Spreads a sleep of <paramref name="seconds"/> that ended at <paramref name="end"/> backwards over the buckets
    /// it covered. Sleep before the range is dropped.</summary>
    private static void LayBack(double[] asleep, DateTimeOffset end, double seconds, DateTimeOffset from, TimeSpan bucket)
    {
        var cursor = end;
        while (seconds > 0 && cursor > from)
        {
            var index = (int)Math.Floor((cursor - from - TimeSpan.FromTicks(1)) / bucket);
            var start = from + index * bucket;
            var taken = Math.Min(seconds, (cursor - start).TotalSeconds);
            if (index < asleep.Length) asleep[index] += taken;
            seconds -= taken;
            cursor = start;
        }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static RangeTotals Summarise(Window w, TariffSchedule schedule)
    {
        double energy = 0, cpu = 0, gpu = 0, display = 0, rest = 0, idleOn = 0, idleOff = 0;
        double onS = 0, idleOnS = 0, idleOffS = 0, gapS = 0, measuredS = 0, calibratedS = 0, estimatedS = 0, peak = 0;
        DateTimeOffset? peakAt = null;
        foreach (var r in w.Rows)
        {
            energy += r.EnergyWh; cpu += r.CpuWh; gpu += r.GpuWh; display += r.DisplayWh; rest += r.RestWh;
            idleOn += r.IdleOnWh; idleOff += r.IdleOffWh;
            onS += r.OnSeconds; idleOnS += r.IdleOnSeconds; idleOffS += r.IdleOffSeconds; gapS += r.GapSeconds;
            measuredS += r.MeasuredSeconds; calibratedS += r.CalibratedSeconds; estimatedS += r.EstimatedSeconds;
            if (r.MaxW > peak) { peak = r.MaxW; peakAt = r.Start; }
        }

        var qualityS = measuredS + calibratedS + estimatedS;
        var onHours = onS / 3600.0;
        var asleepHours = gapS / 3600.0;
        var cost = schedule.Cost(w.Rows.Select(r => (r.Start, r.EnergyWh)));
        return new RangeTotals(
            From: w.From, To: w.To,
            EnergyKwh: energy / 1000,
            Cost: cost.Amount,
            Currency: cost.Currency,
            CostIsPartial: cost.Partial,
            AvgW: onHours > 0 ? energy / onHours : 0,
            PeakW: peak, PeakAt: peakAt,
            OnHours: onHours,
            IdleOnHours: idleOnS / 3600.0,
            IdleOffHours: idleOffS / 3600.0,
            AsleepHours: asleepHours,
            UnmonitoredHours: Math.Max(0, (w.To - w.From).TotalHours - onHours - asleepHours),
            CpuKwh: cpu / 1000, GpuKwh: gpu / 1000, DisplayKwh: display / 1000, RestKwh: rest / 1000,
            IdleOnKwh: idleOn / 1000, IdleOffKwh: idleOff / 1000,
            MeasuredShare: Share(measuredS, qualityS),
            CalibratedShare: Share(calibratedS, qualityS),
            EstimatedShare: Share(estimatedS, qualityS));
    }

    private static List<DayTotals> Days(List<Aggregate> rows, TariffSchedule schedule, TimeZoneInfo zone)
        => rows
            .GroupBy(r => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(r.Start, zone).DateTime))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var cost = schedule.Cost(g.Select(r => (r.Start, r.EnergyWh)));
                return new DayTotals(
                    g.Key,
                    EnergyKwh: g.Sum(r => r.EnergyWh) / 1000,
                    Cost: cost.Amount,
                    Currency: cost.Currency,
                    CostIsPartial: cost.Partial,
                    OnHours: g.Sum(r => r.OnSeconds) / 3600.0,
                    PeakW: g.Max(r => r.MaxW),
                    IdleOnKwh: g.Sum(r => r.IdleOnWh) / 1000,
                    IdleOffKwh: g.Sum(r => r.IdleOffWh) / 1000);
            })
            .ToList();

    private static DateTimeOffset Floor(DateTimeOffset t, TimeSpan unit) => t.AddTicks(-(t.Ticks % unit.Ticks));

    private static DateTimeOffset Ceiling(DateTimeOffset t, TimeSpan unit)
    {
        var remainder = t.Ticks % unit.Ticks;
        return remainder == 0 ? t : t.AddTicks(unit.Ticks - remainder);
    }

    private static double Share(double part, double whole) => whole > 0 ? part / whole : 0;

    /// <summary>The rows a range reads, hour rows and minute rows apart so a series knows each row's length.</summary>
    private sealed record Window(DateTimeOffset From, DateTimeOffset To, List<Aggregate> Hours, List<Aggregate> Minutes)
    {
        public List<Aggregate> Rows { get; } = [.. Hours.Concat(Minutes).OrderBy(r => r.Start)];
    }
}
```

In `src/PowerLedger.Storage/AggregateRepository.cs`, add after `LastHourStart`:

```csharp
    /// <summary>Start of the oldest minute row, or null when there are none: where history begins.</summary>
    public DateTimeOffset? FirstMinuteStart()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT MIN(start_ms) FROM {MinuteTable}";
        return cmd.ExecuteScalar() is long ms ? Rows.Time(ms) : null;
    }
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.Storage.Tests`
Expected: `Passed! - Failed: 0, Passed: 47`. Plan A's tests pass unchanged. The one that pairs an hour row with its own minutes reads the minutes now, and they hold the same energy.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.Storage/ReportQueries.cs src/PowerLedger.Storage/AggregateRepository.cs tests/PowerLedger.Storage.Tests/ReportQueriesTests.cs
git commit -m "Read short ranges to the minute, and add a series for charts with sleep laid back"
```

---

### Task 2: Date ranges

**Files:**
- Create: `src/PowerLedger.App/History/Ranges.cs`
- Modify: `src/PowerLedger.App/History/HistoryReader.cs`
- Test: `tests/PowerLedger.App.Tests/RangesTests.cs`

Every history screen shows a span of local days. A `DateRange` carries:
- `From` to `To`: the instants to read. `To` stops at now for a range that includes today.
- `Through`: where the chart ends, which is the end of the last day. The rest of today then shows empty, instead of the morning stretched across the chart.
- A title.
- The chart bucket that suits its length: five minutes for a day, fifteen for up to three days, an hour for a week, six hours for a month and a day beyond that.

Buckets under an hour are used only for ranges that read minute rows (Task 1), so a three-day range with a clock change in it, 73 hours long, gets hours. `At` turns a local wall-clock time into an instant. Where a clock change skips that time, it takes the first valid time after it, as D1's `HistoryReader.LocalMidnight` does, which now calls it.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.App.Tests/RangesTests.cs`
```csharp
using System.Globalization;
using Shouldly;

namespace PowerLedger.App.Tests;

public class RangesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void Today_runs_from_midnight_to_now_and_its_chart_to_midnight()
    {
        var today = Ranges.Today(Now, Utc, English);
        today.From.ShouldBe(new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero));
        today.To.ShouldBe(Now);
        today.Through.ShouldBe(new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero));
        today.Bucket.ShouldBe(TimeSpan.FromMinutes(5));
        today.Capacity.ShouldBe(288);
        today.Title.ShouldBe("Today");
    }

    [Fact]
    public void The_last_seven_days_include_today()
    {
        var week = Ranges.LastDays(7, Now, Utc, English);
        week.From.ShouldBe(new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero));
        week.To.ShouldBe(Now);
        week.Through.ShouldBe(new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero));
        week.Bucket.ShouldBe(TimeSpan.FromHours(1));
        week.Title.ShouldBe("Last 7 days");
    }

    [Fact]
    public void This_month_and_last_month()
    {
        var month = Ranges.ThisMonth(Now, Utc, English);
        month.From.ShouldBe(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        month.To.ShouldBe(Now);
        month.Through.ShouldBe(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        month.Title.ShouldBe("September 2026");

        var january = new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);
        var last = Ranges.LastMonth(january, Utc, English);
        last.From.ShouldBe(new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero));
        last.To.ShouldBe(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        last.Through.ShouldBe(last.To);
        last.Title.ShouldBe("December 2025");
        last.Bucket.ShouldBe(TimeSpan.FromHours(6));
    }

    [Fact]
    public void A_custom_range_covers_whole_days_and_stops_at_now()
    {
        var custom = Ranges.Days(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), Now, Utc, English);
        custom.From.ShouldBe(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        custom.To.ShouldBe(Now);
        custom.Through.ShouldBe(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        custom.Title.ShouldBe("1 Sep – 30 Sep 2026");

        Ranges.Days(new DateOnly(2026, 9, 5), new DateOnly(2026, 9, 2), Now, Utc, English).Title.ShouldBe("2 Sep – 5 Sep 2026");
        Ranges.Days(new DateOnly(2025, 12, 28), new DateOnly(2026, 1, 3), Now, Utc, English).Title.ShouldBe("28 Dec 2025 – 3 Jan 2026");
        Ranges.Days(new DateOnly(2026, 9, 3), new DateOnly(2026, 9, 3), Now, Utc, English).Title.ShouldBe("Thu 3 Sep 2026");
    }

    [Fact]
    public void A_range_wholly_in_the_future_is_empty()
    {
        var future = Ranges.Days(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5), Now, Utc, English);
        future.To.ShouldBe(future.From);
    }

    [Theory]
    [InlineData(24, 5)]
    [InlineData(72, 15)]
    [InlineData(73, 60)]
    [InlineData(7 * 24, 60)]
    [InlineData(31 * 24, 360)]
    [InlineData(90 * 24, 1440)]
    public void The_bucket_suits_the_length(int hours, int minutes)
        => Ranges.BucketFor(TimeSpan.FromHours(hours)).ShouldBe(TimeSpan.FromMinutes(minutes));

    [Fact]
    public void Buckets_have_names_for_headings()
    {
        Ranges.BucketName(TimeSpan.FromMinutes(5)).ShouldBe("5-min");
        Ranges.BucketName(TimeSpan.FromHours(1)).ShouldBe("hourly");
        Ranges.BucketName(TimeSpan.FromHours(6)).ShouldBe("6-hour");
        Ranges.BucketName(TimeSpan.FromDays(1)).ShouldBe("daily");
        Ranges.BucketLength(TimeSpan.FromMinutes(15)).ShouldBe("15 min");
        Ranges.BucketLength(TimeSpan.FromHours(1)).ShouldBe("hour");
        Ranges.BucketLength(TimeSpan.FromHours(6)).ShouldBe("6 hours");
        Ranges.BucketLength(TimeSpan.FromDays(1)).ShouldBe("day");
    }

    [Fact]
    public void A_local_time_a_clock_change_skips_moves_to_the_first_valid_time()
    {
        var europe = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        Ranges.At(new DateTime(2026, 3, 29, 2, 30, 0), europe).ShouldBe(new DateTimeOffset(2026, 3, 29, 3, 0, 0, TimeSpan.FromHours(2)));
        Ranges.At(new DateTime(2026, 3, 29, 6, 0, 0), europe).ShouldBe(new DateTimeOffset(2026, 3, 29, 6, 0, 0, TimeSpan.FromHours(2)));
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.App.Tests --filter RangesTests`
Expected: build error, `Ranges` not found.

- [x] **Step 3: Write the ranges**

`src/PowerLedger.App/History/Ranges.cs`
```csharp
using System.Globalization;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>A span of local days to show.</summary>
/// <param name="From">Where reading starts: a local midnight.</param>
/// <param name="To">Where reading stops: now, for a range that includes today.</param>
/// <param name="Through">Where the chart ends: the end of the range's last day, so the rest of today shows empty.</param>
/// <param name="Title">What the screen calls it.</param>
/// <param name="Bucket">The chart bucket that suits its length.</param>
internal sealed record DateRange(DateTimeOffset From, DateTimeOffset To, DateTimeOffset Through, string Title, TimeSpan Bucket)
{
    /// <summary>How many buckets the chart's width holds.</summary>
    public int Capacity => Math.Max(1, (int)Math.Ceiling((Through - From) / Bucket));
}

/// <summary>The ranges the history screens offer, and the local-clock arithmetic behind them.</summary>
internal static class Ranges
{
    public static DateRange Today(DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        var day = LocalDay(now, zone);
        return Build(day, day, now, zone, "Today");
    }

    /// <summary>The last <paramref name="days"/> days, today among them.</summary>
    public static DateRange LastDays(int days, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        var today = LocalDay(now, zone);
        return Build(today.AddDays(1 - days), today, now, zone, $"Last {days.ToString(culture)} days");
    }

    public static DateRange ThisMonth(DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        var today = LocalDay(now, zone);
        return Month(today.Year, today.Month, now, zone, culture);
    }

    public static DateRange LastMonth(DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        var today = LocalDay(now, zone);
        var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        return Month(first.Year, first.Month, now, zone, culture);
    }

    /// <summary>A calendar month, stopping at now while it is under way.</summary>
    public static DateRange Month(int year, int month, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        var first = new DateOnly(year, month, 1);
        return Build(first, first.AddMonths(1).AddDays(-1), now, zone, first.ToString("MMMM yyyy", culture));
    }

    /// <summary>Whole local days from one date to another, in either order, stopping at now.</summary>
    public static DateRange Days(DateOnly first, DateOnly last, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        if (last < first) (first, last) = (last, first);
        return Build(first, last, now, zone, Span(first, last, culture));
    }

    /// <summary>"Thu 3 Sep 2026", "1 Sep – 30 Sep 2026", "28 Dec 2025 – 3 Jan 2026".</summary>
    public static string Span(DateOnly first, DateOnly last, CultureInfo culture)
    {
        if (first == last) return first.ToString("ddd d MMM yyyy", culture);
        return first.Year == last.Year
            ? $"{first.ToString("d MMM", culture)} – {last.ToString("d MMM yyyy", culture)}"
            : $"{first.ToString("d MMM yyyy", culture)} – {last.ToString("d MMM yyyy", culture)}";
    }

    /// <summary>
    /// Five minutes for a day, fifteen for up to three days, an hour for a week, six hours for a month and a day beyond.
    /// A bucket under an hour needs minute rows, which only a range within <see cref="ReportQueries.MinuteResolutionLimit"/> reads.
    /// </summary>
    public static TimeSpan BucketFor(TimeSpan span)
    {
        if (span <= TimeSpan.FromHours(25)) return TimeSpan.FromMinutes(5);
        if (span <= ReportQueries.MinuteResolutionLimit) return TimeSpan.FromMinutes(15);
        if (span <= TimeSpan.FromHours(8 * 24 + 1)) return TimeSpan.FromHours(1);
        if (span <= TimeSpan.FromHours(32 * 24 + 1)) return TimeSpan.FromHours(6);
        return TimeSpan.FromDays(1);
    }

    /// <summary>A chart heading's name for its bucket: "5-min", "hourly", "6-hour", "daily".</summary>
    public static string BucketName(TimeSpan bucket) => bucket.TotalMinutes switch
    {
        < 60 => bucket.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + "-min",
        60 => "hourly",
        < 1440 => bucket.TotalHours.ToString("0", CultureInfo.InvariantCulture) + "-hour",
        _ => "daily",
    };

    /// <summary>What one bucket is, after "per": "15 min", "hour", "6 hours", "day".</summary>
    public static string BucketLength(TimeSpan bucket) => bucket.TotalMinutes switch
    {
        < 60 => bucket.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + " min",
        60 => "hour",
        < 1440 => bucket.TotalHours.ToString("0", CultureInfo.InvariantCulture) + " hours",
        _ => "day",
    };

    /// <summary>The local calendar day at <paramref name="instant"/>.</summary>
    public static DateOnly LocalDay(DateTimeOffset instant, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    /// <summary>Midnight local time at the start of <paramref name="day"/>.</summary>
    public static DateTimeOffset Midnight(DateOnly day, TimeZoneInfo zone) => At(day.ToDateTime(TimeOnly.MinValue), zone);

    /// <summary>The instant a local clock shows <paramref name="wall"/>, or the first valid time after it where a clock change skips it.</summary>
    public static DateTimeOffset At(DateTime wall, TimeZoneInfo zone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(wall)) wall = wall.AddMinutes(15);
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }

    private static DateRange Build(DateOnly first, DateOnly last, DateTimeOffset now, TimeZoneInfo zone, string title)
    {
        var from = Midnight(first, zone);
        var through = Midnight(last.AddDays(1), zone);
        var to = now < through ? now : through;
        return new DateRange(from, to < from ? from : to, through, title, BucketFor(through - from));
    }
}
```

In `src/PowerLedger.App/History/HistoryReader.cs`, replace `LocalMidnight` with:

```csharp
    /// <summary>Midnight local time on <paramref name="date"/>, or the first valid time after it where a clock change skips midnight.</summary>
    internal static DateTimeOffset LocalMidnight(DateTime date, TimeZoneInfo zone) => Ranges.At(date.Date, zone);
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter "RangesTests|HistoryReaderTests"`
Expected: `Passed! - Failed: 0, Passed: 17`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.App/History tests/PowerLedger.App.Tests/RangesTests.cs
git commit -m "Add the history screens' date ranges"
```

---

### Task 3: Reading a range, and CSV

**Files:**
- Create: `src/PowerLedger.App/History/RangeHistory.cs`
- Create: `src/PowerLedger.App/History/CsvExport.cs`
- Modify: `src/PowerLedger.App/History/HistoryReader.cs`
- Test: `tests/PowerLedger.App.Tests/CsvExportTests.cs`
- Modify: `tests/PowerLedger.App.Tests/HistoryReaderTests.cs`

`IRangeHistory` is the Breakdown and Report screens' view of the database. It offers:
- A range's totals, days and series, from one set of reads.
- The range as CSV at one of spec §9's three grains (raw, 1 minute, 1 hour).
- The first local day with history, for the monthly reports.

CSV is written with the invariant culture, UTC timestamps in ISO 8601 and one column per stored field, so a spreadsheet anywhere reads it the same way. Raw rows keep every modelled part, and rest is left negative where measured mode stored it so. The CSV holds the data as stored; clamping is for display.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.App.Tests/CsvExportTests.cs`
```csharp
using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.App.Tests;

public class CsvExportTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Raw_readings_are_one_line_each_with_every_part()
    {
        var reading = new Reading(T0, 1, 34.25, Quality.Measured,
            new Components(Cpu: 14.5, Gpu: 4, Display: 3, Ram: 0, Storage: 0, Board: 0, Extras: 0, Monitors: 1, PsuLoss: 0, Unattributed: 11.75),
            OnBattery: true, DisplayOn: true, UserIdle: false, SessionLocked: false, CpuLoad: 0.3, GpuLoad: null, Brightness: 0.6, Suspect: false);

        var lines = CsvExport.Raw([reading]);

        lines[0].ShouldBe("timestamp_utc,delta_s,total_w,quality,cpu_w,gpu_w,display_w,monitors_w,ram_w,storage_w,board_w,extras_w,psu_loss_w,unattributed_w,on_battery,display_on,user_idle,locked,cpu_load,gpu_load,brightness,suspect");
        lines[1].ShouldBe("2026-09-08T12:00:00Z,1,34.25,Measured,14.5,4,3,1,0,0,0,0,0,11.75,true,true,false,false,0.3,,0.6,false");
    }

    [Fact]
    public void Minute_and_hour_rows_are_one_line_each()
    {
        var row = Aggregate.Empty(T0) with { AvgW = 30, MaxW = 41.5, EnergyWh = 0.5, CpuWh = 0.2, RestWh = 0.3, OnSeconds = 60, SampleCount = 60, MeasuredSeconds = 60 };

        var lines = CsvExport.Rows([row]);

        lines[0].ShouldBe("start_utc,avg_w,max_w,energy_wh,cpu_wh,gpu_wh,display_wh,rest_wh,idle_on_wh,idle_off_wh,idle_on_s,idle_off_s,on_s,battery_s,gap_s,samples,measured_s,calibrated_s,estimated_s");
        lines[1].ShouldBe("2026-09-08T12:00:00Z,30,41.5,0.5,0.2,0,0,0.3,0,0,0,0,60,0,0,60,60,0,0");
    }
}
```

Append to `tests/PowerLedger.App.Tests/HistoryReaderTests.cs`, inside the class:

```csharp
    [Fact]
    public void A_range_reads_its_totals_days_and_series_together()
    {
        var minutes = new AggregateRepository(_writer);
        minutes.UpsertMinute(Minute(Now.AddMinutes(-30)));
        minutes.UpsertMinute(Minute(Now.AddMinutes(-29)));

        using var readOnly = new SqliteDatabase(_path, readOnly: true);
        var reader = new HistoryReader(readOnly);
        var range = Ranges.Today(Now, TimeZoneInfo.Utc, System.Globalization.CultureInfo.InvariantCulture);
        var report = reader.Read(range, TimeZoneInfo.Utc).ShouldNotBeNull();

        report.Range.ShouldBe(range);
        report.Totals.EnergyKwh.ShouldBe(0.001, 1e-12);
        report.Days.Count.ShouldBe(1);
        report.Series.Count.ShouldBe(174);                                   // 14.5 hours of five-minute buckets
        report.Series.Sum(b => b.EnergyWh).ShouldBe(1, 1e-9);
        reader.FirstDay(TimeZoneInfo.Utc).ShouldBe(new DateOnly(2026, 9, 15));
        reader.Csv(range, ExportGrain.Minute).ShouldNotBeNull().Count.ShouldBe(3);
        reader.Csv(range, ExportGrain.Raw).ShouldNotBeNull().Count.ShouldBe(1);   // no raw rows: the header alone
    }

    [Fact]
    public void A_range_over_a_database_that_cannot_be_opened_reads_as_nothing()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"powerledger-missing-{Guid.NewGuid():N}.db");
        using var database = new SqliteDatabase(missing, readOnly: true);
        var reader = new HistoryReader(database);
        var range = Ranges.Today(Now, TimeZoneInfo.Utc, System.Globalization.CultureInfo.InvariantCulture);
        reader.Read(range, TimeZoneInfo.Utc).ShouldBeNull();
        reader.Csv(range, ExportGrain.Hour).ShouldBeNull();
        reader.FirstDay(TimeZoneInfo.Utc).ShouldBeNull();
    }
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.App.Tests --filter "CsvExportTests|HistoryReaderTests"`
Expected: build error, `CsvExport` not found.

- [x] **Step 3: Write the range history and the CSV**

`src/PowerLedger.App/History/RangeHistory.cs`
```csharp
using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>A range's totals, days and chart buckets, from one set of reads so they agree.</summary>
internal sealed record RangeReport(DateRange Range, RangeTotals Totals, IReadOnlyList<DayTotals> Days, IReadOnlyList<Aggregate> Series);

/// <summary>Spec §9's CSV grains.</summary>
internal enum ExportGrain
{
    Raw,
    Minute,
    Hour,
}

/// <summary>The history screens' read-only view of the database. Every read returns null when the database cannot be opened.</summary>
internal interface IRangeHistory
{
    RangeReport? Read(DateRange range, TimeZoneInfo zone);

    /// <summary>The range as CSV lines, header first.</summary>
    IReadOnlyList<string>? Csv(DateRange range, ExportGrain grain);

    /// <summary>The first local day with any history.</summary>
    DateOnly? FirstDay(TimeZoneInfo zone);
}
```

`src/PowerLedger.App/History/CsvExport.cs`
```csharp
using System.Globalization;
using PowerLedger.Core;

namespace PowerLedger.App;

/// <summary>History as CSV lines (spec §9 exports): invariant culture, UTC timestamps in ISO 8601, one column per stored field.</summary>
internal static class CsvExport
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static IReadOnlyList<string> Raw(IReadOnlyList<Reading> readings)
    {
        var lines = new List<string>(readings.Count + 1)
        {
            "timestamp_utc,delta_s,total_w,quality,cpu_w,gpu_w,display_w,monitors_w,ram_w,storage_w,board_w,extras_w,psu_loss_w,unattributed_w," +
            "on_battery,display_on,user_idle,locked,cpu_load,gpu_load,brightness,suspect",
        };
        foreach (var r in readings)
        {
            var p = r.Components;
            lines.Add(string.Join(',',
                Time(r.Timestamp), N(r.DeltaSeconds), N(r.TotalW), r.Quality.ToString(),
                N(p.Cpu), N(p.Gpu), N(p.Display), N(p.Monitors), N(p.Ram), N(p.Storage), N(p.Board), N(p.Extras), N(p.PsuLoss), N(p.Unattributed),
                B(r.OnBattery), B(r.DisplayOn), B(r.UserIdle), B(r.SessionLocked), N(r.CpuLoad), N(r.GpuLoad), N(r.Brightness), B(r.Suspect)));
        }
        return lines;
    }

    public static IReadOnlyList<string> Rows(IReadOnlyList<Aggregate> rows)
    {
        var lines = new List<string>(rows.Count + 1)
        {
            "start_utc,avg_w,max_w,energy_wh,cpu_wh,gpu_wh,display_wh,rest_wh,idle_on_wh,idle_off_wh,idle_on_s,idle_off_s," +
            "on_s,battery_s,gap_s,samples,measured_s,calibrated_s,estimated_s",
        };
        foreach (var a in rows)
        {
            lines.Add(string.Join(',',
                Time(a.Start), N(a.AvgW), N(a.MaxW), N(a.EnergyWh), N(a.CpuWh), N(a.GpuWh), N(a.DisplayWh), N(a.RestWh),
                N(a.IdleOnWh), N(a.IdleOffWh), N(a.IdleOnSeconds), N(a.IdleOffSeconds),
                N(a.OnSeconds), N(a.BatterySeconds), N(a.GapSeconds), a.SampleCount.ToString(Invariant),
                N(a.MeasuredSeconds), N(a.CalibratedSeconds), N(a.EstimatedSeconds)));
        }
        return lines;
    }

    private static string Time(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Invariant);

    private static string N(double value) => double.IsFinite(value) ? value.ToString("0.######", Invariant) : "";

    private static string N(double? value) => value is { } v ? N(v) : "";

    private static string B(bool value) => value ? "true" : "false";
}
```

In `src/PowerLedger.App/History/HistoryReader.cs`, change the class declaration and add the three `IRangeHistory` members before `LocalMidnight`:

```csharp
/// <summary>The App's read-only view of the service's database (spec §3: the App never writes it).</summary>
internal sealed class HistoryReader(SqliteDatabase database) : IHistory, IRangeHistory
{
```

```csharp
    public RangeReport? Read(DateRange range, TimeZoneInfo zone)
    {
        try
        {
            var queries = new ReportQueries(database);
            var (totals, days) = queries.Report(range.From, range.To, zone);
            return new RangeReport(range, totals, days, queries.Series(range.From, range.To, range.Bucket));
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public IReadOnlyList<string>? Csv(DateRange range, ExportGrain grain)
    {
        try
        {
            var aggregates = new AggregateRepository(database);
            return grain switch
            {
                ExportGrain.Raw => CsvExport.Raw(new RawSampleRepository(database).Read(range.From, range.To)),
                ExportGrain.Minute => CsvExport.Rows(aggregates.ReadMinutes(range.From, range.To)),
                _ => CsvExport.Rows(aggregates.ReadHours(range.From, range.To)),
            };
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public DateOnly? FirstDay(TimeZoneInfo zone)
    {
        try
        {
            return new AggregateRepository(database).FirstMinuteStart() is { } first ? Ranges.LocalDay(first, zone) : null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter "CsvExportTests|HistoryReaderTests"`
Expected: `Passed! - Failed: 0, Passed: 8`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.App/History tests/PowerLedger.App.Tests/CsvExportTests.cs tests/PowerLedger.App.Tests/HistoryReaderTests.cs
git commit -m "Read a range's totals, days and series together, and write history as CSV"
```

---

### Task 4: One stacked chart for every range

**Files:**
- Create: `src/PowerLedger.App/Charts/ChartModel.cs`
- Create: `src/PowerLedger.App/Charts/Charts.cs`
- Create: `src/PowerLedger.App/Controls/StackedChart.cs`
- Modify: `src/PowerLedger.App/Controls/Geometry.cs`
- Modify: `src/PowerLedger.App/Controls/Instrument.cs`
- Modify: `src/PowerLedger.App/Formatting/Format.cs`
- Modify: `src/PowerLedger.App/History/HistoryReader.cs`
- Modify: `src/PowerLedger.App/Now/Panels.cs`
- Modify: `src/PowerLedger.App/Now/NowViewModel.cs`
- Modify: `src/PowerLedger.App/Now/NowView.xaml`
- Modify: `src/PowerLedger.App/Theme/Styles.xaml`
- Delete: `src/PowerLedger.App/Controls/DayChart.cs`, `src/PowerLedger.App/History/DaySlots.cs`, `tests/PowerLedger.App.Tests/DaySlotsTests.cs`
- Test: `tests/PowerLedger.App.Tests/ChartTests.cs`
- Modify: `tests/PowerLedger.App.Tests/GeometryTests.cs`, `HistoryReaderTests.cs`, `NowViewModelTests.cs`, `Snapshots.cs`, `RenderingTests.cs`

D1's day chart drew today's five-minute slots across 288 positions. `StackedChart` does the same for any range. It draws `ChartBucket`s in the chart's unit across `Capacity` positions, with the axis ticks a range asks for, a dashed line at now when the range includes it, and hatching where the machine slept through most of a bucket.

In watts, a bucket's height is its average while the machine was on, as D1's slots were. In watt-hours it is the bucket's energy. The pure parts are `Charts.Buckets`, `Charts.Ticks` and `Charts.Build`. Tick labels sit just right of their tick, so a day's label begins where the day does.

The Now screen moves onto the new control: its snapshot carries today's `DateRange` and `Series`, and `DaySlots` goes, since `Series` lays sleep back itself. `Instrument` gets an automation peer so each drawn control can tell a screen reader what it shows. The chart's sentence comes with its model.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.App.Tests/ChartTests.cs`
```csharp
using System.Globalization;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.App.Tests;

public class ChartTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static Aggregate Bucket(DateTimeOffset start, double cpuWh, double restWh, double onSeconds, double gapSeconds = 0)
        => Aggregate.Empty(start) with { CpuWh = cpuWh, RestWh = restWh, EnergyWh = cpuWh + restWh, OnSeconds = onSeconds, GapSeconds = gapSeconds };

    [Fact]
    public void A_bucket_in_watts_is_its_energy_over_its_time_on()
    {
        var bucket = Charts.Buckets([Bucket(Now, cpuWh: 0.4, restWh: 0.6, onSeconds: 120, gapSeconds: 30)], ChartUnit.Watts).Single();
        bucket.Cpu.ShouldBe(12, 1e-9);
        bucket.Rest.ShouldBe(18, 1e-9);
        bucket.Total.ShouldBe(30, 1e-9);
        bucket.AsleepSeconds.ShouldBe(30);
        Charts.Buckets([Bucket(Now, 0.4, 0.6, onSeconds: 0)], ChartUnit.Watts).Single().Total.ShouldBe(0);
    }

    [Fact]
    public void A_bucket_in_watt_hours_is_its_energy()
    {
        var bucket = Charts.Buckets([Bucket(Now, cpuWh: 0.4, restWh: 0.6, onSeconds: 120)], ChartUnit.WattHours).Single();
        bucket.Cpu.ShouldBe(0.4, 1e-9);
        bucket.Total.ShouldBe(1, 1e-9);
    }

    [Fact]
    public void A_negative_rest_draws_as_zero()
        => Charts.Buckets([Bucket(Now, cpuWh: 0.5, restWh: -0.1, onSeconds: 60)], ChartUnit.Watts).Single().Total.ShouldBe(30, 1e-9);

    [Fact]
    public void A_day_is_marked_every_six_hours()
        => Charts.Ticks(Ranges.Today(Now, Utc, English), Utc, English)
            .ShouldBe(new AxisTick[] { new(0, "00:00"), new(72, "06:00"), new(144, "12:00"), new(216, "18:00") });

    [Fact]
    public void A_week_is_marked_at_each_midnight()
    {
        var ticks = Charts.Ticks(Ranges.LastDays(7, Now, Utc, English), Utc, English);
        ticks.Select(t => t.At).ShouldBe(new double[] { 0, 24, 48, 72, 96, 120, 144 });
        ticks[0].Label.ShouldBe("Wed 2");
        ticks[^1].Label.ShouldBe("Tue 8");
    }

    [Fact]
    public void Thirty_days_are_marked_weekly_and_a_long_range_at_month_starts()
    {
        var month = Charts.Ticks(Ranges.LastDays(30, Now, Utc, English), Utc, English);
        month.Select(t => t.At).ShouldBe(new double[] { 0, 28, 56, 84, 112 });
        month.Select(t => t.Label).ShouldBe(new[] { "10 Aug", "17 Aug", "24 Aug", "31 Aug", "7 Sep" });

        var year = Charts.Ticks(Ranges.Days(new DateOnly(2025, 11, 15), new DateOnly(2026, 9, 8), Now, Utc, English), Utc, English);
        year.Select(t => t.Label).Take(3).ShouldBe(new[] { "Dec 2025", "Jan 2026", "Feb" });
    }

    [Fact]
    public void Ticks_follow_the_clock_on_a_day_that_springs_forward()
    {
        var europe = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var day = Ranges.Today(new DateTimeOffset(2026, 3, 29, 12, 0, 0, TimeSpan.Zero), europe, English);
        day.Capacity.ShouldBe(276);                                              // a 23-hour day
        Charts.Ticks(day, europe, English).Select(t => t.At).ShouldBe(new double[] { 0, 60, 132, 204 });
    }

    [Fact]
    public void Todays_chart_spans_the_day_and_marks_now()
    {
        var range = Ranges.Today(Now, Utc, English);
        var chart = Charts.Build(range, [Bucket(range.From, 0.5, 0.5, 300)], ChartUnit.Watts, Utc, English);

        chart.Capacity.ShouldBe(288);
        chart.Bucket.ShouldBe(TimeSpan.FromMinutes(5));
        chart.NowAt.ShouldNotBeNull().ShouldBe(174.4, 1e-9);
        chart.Description.ShouldBe("Today: power by component in watts, stacked from the rest of the system up to the CPU. Peak 12 W.");
    }

    [Fact]
    public void A_past_range_has_no_now_and_watt_hours_name_their_bucket()
    {
        var chart = Charts.Build(Ranges.LastMonth(Now, Utc, English), [], ChartUnit.WattHours, Utc, English);
        chart.NowAt.ShouldBeNull();
        chart.Description.ShouldBe("August 2026: power by component in watt-hours per 6 hours, stacked from the rest of the system up to the CPU. No readings in this range.");
    }

    [Fact]
    public void Scale_labels_carry_the_decimals_their_step_needs()
    {
        Format.Scale(40, 20, English).ShouldBe("40");
        Format.Scale(2.5, 0.5, English).ShouldBe("2.5");
        Format.Scale(0.25, 0.05, English).ShouldBe("0.25");
    }
}
```

In `tests/PowerLedger.App.Tests/GeometryTests.cs`, replace `Stacked_layers_rise_from_rest_to_cpu_and_negative_rest_stays_at_zero` with:

```csharp
    [Fact]
    public void Stacked_layers_rise_from_rest_to_cpu_and_negative_rest_stays_at_zero()
    {
        ChartBucket[] buckets =
        [
            new(Cpu: 10, Gpu: 2, Display: 4, Rest: 6, OnSeconds: 300, AsleepSeconds: 0),
            new(Cpu: 20, Gpu: 0, Display: 4, Rest: -3, OnSeconds: 300, AsleepSeconds: 0),
        ];
        var tops = Geometry.StackTops(buckets);
        tops.RestTop.ShouldBe(new double[] { 6, 0 });
        tops.DisplayTop.ShouldBe(new double[] { 10, 4 });
        tops.GpuTop.ShouldBe(new double[] { 12, 4 });
        tops.CpuTop.ShouldBe(new double[] { 22, 24 });
    }

    [Fact]
    public void A_chart_scale_never_drops_under_its_floor()
    {
        Geometry.ChartScale(3, floor: 1).ShouldBe((3.0, 1.0));
        Geometry.ChartScale(0.3, floor: 0.1).Max.ShouldBe(0.3, 1e-9);
    }
```

In `tests/PowerLedger.App.Tests/HistoryReaderTests.cs`, in `One_read_gathers_today_the_month_the_slots_the_tariff_and_the_machine`, replace the three lines about `DayStart` and `TodaySlots` with:

```csharp
        snapshot.TodayRange.From.ShouldBe(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        snapshot.TodaySeries.Count.ShouldBe(174);                             // 14.5 hours of five-minute buckets
        snapshot.TodaySeries[168].CpuWh.ShouldBe(0.4, 1e-9);                  // 14:00: two minutes of 0.2 Wh of CPU
```

Append to `tests/PowerLedger.App.Tests/NowViewModelTests.cs`, inside the class:

```csharp
    [Fact]
    public void Todays_chart_spans_the_day_and_its_legend_gives_each_bands_energy()
    {
        var model = Model();
        _history.Snapshot = Snapshots.Typical(Now);
        model.RefreshHistory();

        model.Chart.Capacity.ShouldBe(288);
        model.Chart.NowAt.ShouldNotBeNull().ShouldBe(174.42, 0.01);           // 14:32:07 in five-minute buckets
        model.Legend.ShouldBe(new ChartLegend("120 Wh", "30 Wh", "28 Wh", "106 Wh"));
    }
```

`tests/PowerLedger.App.Tests/Snapshots.cs`
```csharp
using System.Globalization;
using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.App.Tests;

/// <summary>History as the mockup shows it: a Tuesday afternoon eight days into September.</summary>
internal static class Snapshots
{
    public static HistorySnapshot Typical(DateTimeOffset now, IReadOnlyList<Aggregate>? series = null)
    {
        var day = Ranges.Today(now, TimeZoneInfo.Utc, CultureInfo.InvariantCulture);
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var today = new RangeTotals(
            From: day.From, To: now, EnergyKwh: 0.284, Cost: 0.0483m, Currency: "USD", CostIsPartial: false,
            AvgW: 41, PeakW: 68, PeakAt: day.From.AddHours(14),
            OnHours: 7 + 5 / 60.0, IdleOnHours: 0.75, IdleOffHours: 0, AsleepHours: 7.5, UnmonitoredHours: 0,
            CpuKwh: 0.12, GpuKwh: 0.03, DisplayKwh: 0.028, RestKwh: 0.106, IdleOnKwh: 0.011, IdleOffKwh: 0,
            MeasuredShare: 0.62, CalibratedShare: 0.2, EstimatedShare: 0.18);
        var month = today with { From = monthStart, EnergyKwh = 2.74, Cost = 0.47m, IdleOnKwh = 0.15, IdleOffKwh = 0.06 };
        var days = Enumerable.Range(1, now.Day)
            .Select(d => new DayTotals(new DateOnly(now.Year, now.Month, d), 0.2 + d % 4 * 0.1, 0.05m, "USD", false, 8, 60, 0.01, 0))
            .ToList();
        return new HistorySnapshot(
            today, month, days, day, series ?? [],
            new Tariff(now.AddDays(-30), 0.17m, "USD"),
            new MachineNames("11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", "NVIDIA GeForce MX330", 15.3));
    }
}
```

In `tests/PowerLedger.App.Tests/RenderingTests.cs`, add `using PowerLedger.Core;`, change `Snapshots.Typical(Now, Slots())` to `Snapshots.Typical(Now, Series())`, and replace `Slots()` with:

```csharp
    private static IReadOnlyList<Aggregate> Series()
    {
        var dayStart = new DateTimeOffset(Now.Date, TimeSpan.Zero);
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
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet build tests/PowerLedger.App.Tests`
Expected: build errors, `Charts`, `ChartBucket`, `ChartLegend` and `TodayRange` not found.

- [x] **Step 3: Write the chart model**

`src/PowerLedger.App/Charts/ChartModel.cs`
```csharp
namespace PowerLedger.App;

/// <summary>What a chart's height shows: average watts over the time on, or energy per bucket.</summary>
internal enum ChartUnit
{
    Watts,
    WattHours,
}

/// <summary>One bucket of a stacked chart in the chart's unit, with how long the machine was on and asleep in it.</summary>
internal sealed record ChartBucket(double Cpu, double Gpu, double Display, double Rest, double OnSeconds, double AsleepSeconds)
{
    /// <summary>The height to draw. A negative rest, which measured mode shows when the parts over-report, counts as zero
    /// on every chart (spec §9).</summary>
    public double Total => Cpu + Gpu + Display + Math.Max(0, Rest);
}

/// <param name="At">Where the tick sits, in buckets from the chart's left edge.</param>
/// <param name="Label">What it says, just right of the tick.</param>
internal readonly record struct AxisTick(double At, string Label);

/// <summary>
/// A stacked chart (spec §9): its buckets from the left edge, how many buckets the width holds and how long each is, the
/// time axis, where now falls when the range includes it, the unit, and a sentence that says what it shows.
/// </summary>
internal sealed record ChartModel(
    IReadOnlyList<ChartBucket> Buckets, int Capacity, TimeSpan Bucket, IReadOnlyList<AxisTick> Ticks, double? NowAt, ChartUnit Unit,
    string Description)
{
    public static ChartModel Empty { get; } = new([], 1, TimeSpan.FromMinutes(5), [], null, ChartUnit.Watts, "No readings yet.");
}
```

`src/PowerLedger.App/Charts/Charts.cs`
```csharp
using System.Globalization;
using PowerLedger.Core;

namespace PowerLedger.App;

/// <summary>Turns a range's series into what the stacked chart draws.</summary>
internal static class Charts
{
    /// <summary>The chart for a range: its buckets in the unit, the time axis, where now is, and what it shows in words.</summary>
    public static ChartModel Build(DateRange range, IReadOnlyList<Aggregate> series, ChartUnit unit, TimeZoneInfo zone, CultureInfo culture)
    {
        var buckets = Buckets(series, unit);
        double? nowAt = range.To < range.Through ? (range.To - range.From) / range.Bucket : null;
        var peak = buckets.Count > 0 ? buckets.Max(b => b.Total) : 0;
        var what = unit == ChartUnit.Watts ? "watts" : "watt-hours per " + Ranges.BucketLength(range.Bucket);
        var description = $"{range.Title}: power by component in {what}, stacked from the rest of the system up to the CPU. "
            + (peak > 0 ? $"Peak {Format.Scale(peak, Geometry.ChartScale(peak, Floor(unit)).Step, culture)} {Symbol(unit)}." : "No readings in this range.");
        return new ChartModel(buckets, range.Capacity, range.Bucket, Ticks(range, zone, culture), nowAt, unit, description);
    }

    /// <summary>Each bucket of a series in the unit. Watts are the bucket's energy over its time on; a bucket with no time on is zero.</summary>
    public static IReadOnlyList<ChartBucket> Buckets(IReadOnlyList<Aggregate> series, ChartUnit unit)
        => [.. series.Select(a =>
        {
            var scale = unit == ChartUnit.WattHours ? 1 : a.OnSeconds > 0 ? 3600 / a.OnSeconds : 0;
            return new ChartBucket(a.CpuWh * scale, a.GpuWh * scale, a.DisplayWh * scale, a.RestWh * scale, a.OnSeconds, a.GapSeconds);
        })];

    /// <summary>The lowest top a chart's scale may have: 20 W, or 1 Wh.</summary>
    public static double Floor(ChartUnit unit) => unit == ChartUnit.Watts ? 20 : 1;

    public static string Symbol(ChartUnit unit) => unit == ChartUnit.Watts ? "W" : "Wh";

    /// <summary>
    /// The time axis. A single day is marked every six hours. Longer ranges mark local midnights: every day up to eight
    /// days, every second day up to sixteen, every week up to eight weeks, every fortnight up to four months, and month
    /// starts beyond that.
    /// </summary>
    public static IReadOnlyList<AxisTick> Ticks(DateRange range, TimeZoneInfo zone, CultureInfo culture)
    {
        var first = Ranges.LocalDay(range.From, zone);
        var last = Ranges.LocalDay(range.Through.AddTicks(-1), zone);
        var days = last.DayNumber - first.DayNumber + 1;
        var ticks = new List<AxisTick>();
        if (days <= 1)
        {
            for (var hour = 0; hour < 24; hour += 6)
                ticks.Add(Tick(range, Ranges.At(first.ToDateTime(new TimeOnly(hour, 0)), zone), hour.ToString("00", CultureInfo.InvariantCulture) + ":00"));
            return ticks;
        }
        if (days > 120)
        {
            var month = new DateOnly(first.Year, first.Month, 1);
            if (month < first) month = month.AddMonths(1);
            for (; month <= last; month = month.AddMonths(1))
                ticks.Add(Tick(range, Ranges.Midnight(month, zone), month.ToString(month.Month == 1 || ticks.Count == 0 ? "MMM yyyy" : "MMM", culture)));
            return ticks;
        }
        var step = days <= 8 ? 1 : days <= 16 ? 2 : days <= 56 ? 7 : 14;
        for (var day = first; day <= last; day = day.AddDays(step))
            ticks.Add(Tick(range, Ranges.Midnight(day, zone), day.ToString(days <= 8 ? "ddd d" : "d MMM", culture)));
        return ticks;
    }

    private static AxisTick Tick(DateRange range, DateTimeOffset at, string label) => new((at - range.From) / range.Bucket, label);
}
```

In `src/PowerLedger.App/Formatting/Format.cs`, add after `Percent`:

```csharp
    /// <summary>A scale label, or a value read against the scale, with the decimals its step needs: "40", "2.5", "0.25".</summary>
    public static string Scale(double value, double step, CultureInfo culture)
        => double.IsFinite(value) ? Math.Max(0, value).ToString(step >= 1 ? "0" : step >= 0.1 ? "0.0" : "0.00", culture) : Missing;
```

In `src/PowerLedger.App/Controls/Geometry.cs`, replace `ChartScale` and `StackTops` with:

```csharp
    /// <summary>A chart's top and gridline step: round steps that clear the tallest bucket, never under <paramref name="floor"/>.</summary>
    public static (double Max, double Step) ChartScale(double tallest, double floor = 20)
    {
        var need = double.IsFinite(tallest) ? Math.Max(tallest, floor) : floor;
        var step = NiceStep(need, 4);
        return (step * Math.Ceiling(need / step - 1e-9), step);
    }
```

```csharp
    /// <summary>The top of each band in each bucket, stacked from rest at the bottom to CPU at the top (spec §9). A negative rest stays at zero.</summary>
    public static (double[] RestTop, double[] DisplayTop, double[] GpuTop, double[] CpuTop) StackTops(IReadOnlyList<ChartBucket> buckets)
    {
        var rest = new double[buckets.Count];
        var display = new double[buckets.Count];
        var gpu = new double[buckets.Count];
        var cpu = new double[buckets.Count];
        for (var i = 0; i < buckets.Count; i++)
        {
            rest[i] = Math.Max(0, buckets[i].Rest);
            display[i] = rest[i] + Math.Max(0, buckets[i].Display);
            gpu[i] = display[i] + Math.Max(0, buckets[i].Gpu);
            cpu[i] = gpu[i] + Math.Max(0, buckets[i].Cpu);
        }
        return (rest, display, gpu, cpu);
    }
```

- [x] **Step 4: Write the stacked chart, and let drawn controls describe themselves**

In `src/PowerLedger.App/Controls/Instrument.cs`, add `using System.Windows.Automation.Peers;`, and add before `Register<T>`:

```csharp
    /// <summary>What the control shows, in a sentence, for a screen reader.</summary>
    internal virtual string Describe() => string.Empty;

    /// <summary>To UI Automation a drawn control is a picture, named by the view or else by <see cref="Describe"/>.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new InstrumentPeer(this);
```

and before `BrushProperty`:

```csharp
    private sealed class InstrumentPeer(Instrument owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetClassNameCore() => Owner.GetType().Name;

        protected override string GetNameCore()
        {
            var name = base.GetNameCore();
            return string.IsNullOrEmpty(name) ? ((Instrument)Owner).Describe() : name;
        }
    }
```

`src/PowerLedger.App/Controls/StackedChart.cs`
```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>
/// The stacked chart for any range (spec §9): buckets stacked by band from rest at the bottom to CPU at the top, hatched
/// where the machine slept through most of a bucket, the time axis along the bottom, a dashed amber line at now, and a
/// dot at the peak.
/// </summary>
internal sealed class StackedChart : Instrument
{
    public static readonly DependencyProperty ModelProperty = Register(nameof(Model), ChartModel.Empty, typeof(StackedChart));

    private const double Left = 40;
    private const double RightInset = 14;
    private const double Top = 16;
    private const double AxisRoom = 26;

    public ChartModel Model { get => (ChartModel)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }

    internal override string Describe() => Model.Description;

    protected override Size MeasureOverride(Size availableSize) => Fixed(availableSize, 250);

    protected override void OnRender(DrawingContext dc)
    {
        var model = Model;
        var buckets = model.Buckets;
        var capacity = Math.Max(1, model.Capacity);
        var right = ActualWidth - RightInset;
        var bottom = ActualHeight - AxisRoom;
        double X(double at) => Left + (right - Left) * at / capacity;
        var (max, step) = Geometry.ChartScale(buckets.Count > 0 ? buckets.Max(b => b.Total) : 0, Charts.Floor(model.Unit));
        double Y(double value) => bottom - (bottom - Top) * Math.Clamp(value / max, 0, 1);
        var culture = CultureInfo.CurrentCulture;

        var grid = Line(LineBrush);
        for (var value = 0.0; value <= max + step / 1000; value += step)
        {
            dc.DrawLine(grid, new Point(Left, Y(value)), new Point(right, Y(value)));
            DrawText(dc, Format.Scale(value, step, culture), Left - 8, Y(value) - 7, LabelBrush, TextAlignment.Right);
        }
        for (var i = 0; i < model.Ticks.Count; i++)
        {
            var tick = model.Ticks[i];
            var x = X(tick.At);
            if (tick.At > 0 && tick.At < capacity) dc.DrawLine(grid, new Point(x, Top), new Point(x, bottom));
            var label = Text(tick.Label, 10, LabelBrush);
            var room = (i + 1 < model.Ticks.Count ? X(model.Ticks[i + 1].At) : right) - x;
            if (label.Width + 8 <= room) dc.DrawText(label, new Point(x + 4, bottom + 6));
        }
        if (buckets.Count == 0) return;

        DrawAsleep(dc, model, X, bottom);
        // Each band is filled down to zero, tallest first, so no two bands share an anti-aliased edge.
        var tops = Geometry.StackTops(buckets);
        Area(dc, tops.CpuTop, CpuBrush, X, Y);
        Area(dc, tops.GpuTop, GpuBrush, X, Y);
        Area(dc, tops.DisplayTop, DisplayBrush, X, Y);
        Area(dc, tops.RestTop, RestBrush, X, Y);

        if (model.NowAt is { } nowAt)
        {
            var nowX = X(Math.Clamp(nowAt, 0, capacity));
            dc.DrawLine(Line(AccentBrush, 1, new DashStyle([3, 3], 0)), new Point(nowX, Top), new Point(nowX, bottom));
            var now = Text("now", 10, AccentBrush);
            dc.DrawText(now, new Point(nowX + 5 + now.Width <= ActualWidth ? nowX + 5 : nowX - 5 - now.Width, Top - 2));
        }

        var peak = Enumerable.Range(0, buckets.Count).MaxBy(i => buckets[i].Total);
        if (buckets[peak].Total > 0)
        {
            var at = new Point(X(peak + 0.5), Y(buckets[peak].Total));
            dc.DrawEllipse(InkBrush, null, at, 2.5, 2.5);
            var label = Text($"peak {Format.Scale(buckets[peak].Total, step, culture)} {Charts.Symbol(model.Unit)}", 10, LabelBrush);
            dc.DrawText(label, new Point(at.X - 6 - label.Width >= Left ? at.X - 6 - label.Width : at.X + 6, at.Y - 18));
        }
    }

    /// <summary>Hatches each run of buckets the machine slept through most of, with how long it slept when there is room.</summary>
    private void DrawAsleep(DrawingContext dc, ChartModel model, Func<double, double> x, double bottom)
    {
        var buckets = model.Buckets;
        var most = model.Bucket.TotalSeconds / 2;
        var hatch = new DrawingBrush(new GeometryDrawing(null, Line(StrongLineBrush), new LineGeometry(new Point(0, 0), new Point(0, 6))))
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 6, 6), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 6, 6), ViewboxUnits = BrushMappingMode.Absolute, Transform = new RotateTransform(45),
        };
        var i = 0;
        while (i < buckets.Count)
        {
            if (buckets[i].AsleepSeconds < most)
            {
                i++;
                continue;
            }
            var start = i;
            double seconds = 0;
            while (i < buckets.Count && buckets[i].AsleepSeconds >= most) seconds += buckets[i++].AsleepSeconds;
            var rect = new Rect(new Point(x(start), Top), new Point(x(i), bottom));
            dc.DrawRectangle(hatch, null, rect);
            var label = Text($"asleep · {Format.Duration(seconds / 3600)}", 10, LabelBrush);
            if (label.Width + 8 < rect.Width) dc.DrawText(label, new Point(rect.Left + (rect.Width - label.Width) / 2, Top + 6));
        }
    }

    /// <summary>A band's top as a line through the bucket centres, filled down to zero.</summary>
    private static void Area(DrawingContext dc, double[] top, Brush brush, Func<double, double> x, Func<double, double> y)
    {
        var shape = new StreamGeometry();
        using (var g = shape.Open())
        {
            g.BeginFigure(new Point(x(0.5), y(0)), isFilled: true, isClosed: true);
            for (var i = 0; i < top.Length; i++) g.LineTo(new Point(x(i + 0.5), y(top[i])), isStroked: false, isSmoothJoin: false);
            g.LineTo(new Point(x(top.Length - 0.5), y(0)), isStroked: false, isSmoothJoin: false);
        }
        shape.Freeze();
        dc.DrawGeometry(brush, null, shape);
    }
}
```

- [x] **Step 5: Move the Now screen onto it**

`src/PowerLedger.App/History/HistoryReader.cs`
```csharp
using System.Globalization;
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
    RangeTotals Today, RangeTotals Month, IReadOnlyList<DayTotals> MonthDays, DateRange TodayRange, IReadOnlyList<Aggregate> TodaySeries,
    Tariff? Tariff, MachineNames? Machine);

internal interface IHistory
{
    /// <summary>Null when the database cannot be read, as while the service is stopped.</summary>
    HistorySnapshot? Read(DateTimeOffset now, TimeZoneInfo zone);
}

/// <summary>The App's read-only view of the service's database (spec §3: the App never writes it).</summary>
internal sealed class HistoryReader(SqliteDatabase database) : IHistory, IRangeHistory
{
    public HistorySnapshot? Read(DateTimeOffset now, TimeZoneInfo zone)
    {
        try
        {
            var day = Ranges.Today(now, zone, CultureInfo.InvariantCulture);
            var local = TimeZoneInfo.ConvertTime(now, zone);
            var monthStart = LocalMidnight(new DateTime(local.Year, local.Month, 1), zone);
            var queries = new ReportQueries(database);
            var today = queries.Totals(day.From, now);
            var (month, days) = queries.Report(monthStart, now, zone);
            var series = queries.Series(day.From, now, day.Bucket);
            var tariff = new TariffRepository(database).Schedule().At(now);
            var machine = Names(new InventoryRepository(database).Latest());
            return new HistorySnapshot(today, month, days, day, series, tariff, machine);
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public RangeReport? Read(DateRange range, TimeZoneInfo zone)
    {
        try
        {
            var queries = new ReportQueries(database);
            var (totals, days) = queries.Report(range.From, range.To, zone);
            return new RangeReport(range, totals, days, queries.Series(range.From, range.To, range.Bucket));
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public IReadOnlyList<string>? Csv(DateRange range, ExportGrain grain)
    {
        try
        {
            var aggregates = new AggregateRepository(database);
            return grain switch
            {
                ExportGrain.Raw => CsvExport.Raw(new RawSampleRepository(database).Read(range.From, range.To)),
                ExportGrain.Minute => CsvExport.Rows(aggregates.ReadMinutes(range.From, range.To)),
                _ => CsvExport.Rows(aggregates.ReadHours(range.From, range.To)),
            };
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public DateOnly? FirstDay(TimeZoneInfo zone)
    {
        try
        {
            return new AggregateRepository(database).FirstMinuteStart() is { } first ? Ranges.LocalDay(first, zone) : null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary>Midnight local time on <paramref name="date"/>, or the first valid time after it where a clock change skips midnight.</summary>
    internal static DateTimeOffset LocalMidnight(DateTime date, TimeZoneInfo zone) => Ranges.At(date.Date, zone);

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

In `src/PowerLedger.App/Now/Panels.cs`, replace `DayChartModel` with:

```csharp
/// <summary>Today's chart legend: each band's energy so far.</summary>
internal sealed record ChartLegend(string Cpu, string Gpu, string Display, string Rest)
{
    public static ChartLegend Empty { get; } = new("–", "–", "–", "–");
}
```

In `src/PowerLedger.App/Now/NowViewModel.cs`, replace the field `private DayChartModel _chart = DayChartModel.Empty;` with:

```csharp
    private ChartModel _chart = ChartModel.Empty;
    private ChartLegend _legend = ChartLegend.Empty;
```

replace the `Chart` property with:

```csharp
    /// <summary>Today's stacked chart (spec §9).</summary>
    public ChartModel Chart { get => _chart; private set => SetProperty(ref _chart, value); }

    public ChartLegend Legend { get => _legend; private set => SetProperty(ref _legend, value); }
```

and replace `ApplyHistory` with:

```csharp
    private void ApplyHistory(HistorySnapshot? snapshot)
    {
        _snapshot = snapshot;
        IsCollecting = snapshot is null || snapshot.Today.OnHours <= 0;
        if (snapshot is null)
        {
            Today = TodayLedger.Empty;
            Month = MonthLedger.Empty;
            Chart = ChartModel.Empty;
            Legend = ChartLegend.Empty;
        }
        else
        {
            var local = TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _zone);
            Today = TodayOf(snapshot, local);
            Month = MonthOf(snapshot, local);
            Chart = Charts.Build(snapshot.TodayRange, snapshot.TodaySeries, ChartUnit.Watts, _zone, _culture);
            Legend = new ChartLegend(Wh(snapshot.Today.CpuKwh), Wh(snapshot.Today.GpuKwh), Wh(snapshot.Today.DisplayKwh), Wh(snapshot.Today.RestKwh));
        }
        RebuildLive();
    }
```

In `src/PowerLedger.App/Now/NowView.xaml`, replace `<local:DayChart Slots="{Binding Chart.Slots}" Now="{Binding Chart.Now}" />` with:

```xml
                <local:StackedChart Model="{Binding Chart}" />
```

and in the legend below it replace `Chart.CpuWh`, `Chart.GpuWh`, `Chart.DisplayWh` and `Chart.RestWh` with `Legend.Cpu`, `Legend.Gpu`, `Legend.Display` and `Legend.Rest`.

In `src/PowerLedger.App/Theme/Styles.xaml`, replace `<Style TargetType="local:DayChart" BasedOn="{StaticResource Instrument}" />` with:

```xml
    <Style TargetType="local:StackedChart" BasedOn="{StaticResource Instrument}" />
```

Delete `src/PowerLedger.App/Controls/DayChart.cs`, `src/PowerLedger.App/History/DaySlots.cs` and `tests/PowerLedger.App.Tests/DaySlotsTests.cs`.

- [x] **Step 6: Run tests to verify they pass, and look**

Run: `dotnet test tests/PowerLedger.App.Tests --filter "Category!=UI"`
Expected: all pass, with the 5 `DaySlotsTests` gone and 12 tests added.

Run: `dotnet test tests/PowerLedger.App.Tests --filter "Category=UI"` and open `%TEMP%\powerledger-renders\now-Dark-full.png`.
Expected: today's chart as before. The six-hourly labels now sit just right of faint gridlines, and the night before 07:30 is hatched with "asleep · 7h 30m".

- [x] **Step 7: Commit**

```bash
git add -A src/PowerLedger.App tests/PowerLedger.App.Tests
git commit -m "Draw every chart with one stacked chart, and move the Now screen onto it"
```

---

### Task 5: The range picker and the Breakdown view model

**Files:**
- Create: `src/PowerLedger.App/History/RangePicker.cs`
- Create: `src/PowerLedger.App/Breakdown/BreakdownViewModel.cs`
- Test: `tests/PowerLedger.App.Tests/RangePickerTests.cs`
- Test: `tests/PowerLedger.App.Tests/BreakdownViewModelTests.cs`
- Create: `tests/PowerLedger.App.Tests/FakeRangeHistory.cs`, `tests/PowerLedger.App.Tests/Reports.cs`

Both history screens pick a range the same way, so `RangePicker` holds the choice and the custom days and resolves them to a `DateRange`. The custom days start as the last week. They change the range only while Custom is chosen. A date the picker clears keeps the last day.

`BreakdownViewModel` reads when its page is shown, when the range changes and every minute while shown. It reads off the UI thread, and a read that a newer one overtook is dropped. Switching between watts and watt-hours redraws the last read without reading again. The table gives each band's energy and its share of the four bands. Rest counts as zero there when measured mode put it below zero, and the footnote says so (spec §9).

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.App.Tests/Reports.cs`
```csharp
using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.App.Tests;

/// <summary>Range reports for view model tests: a range with readings in every bucket up to now, and an empty one.</summary>
internal static class Reports
{
    public static RangeTotals Totals(DateRange range, double kwh = 2.74) => new(
        From: range.From, To: range.To, EnergyKwh: kwh, Cost: (decimal)kwh * 0.17m, Currency: "USD", CostIsPartial: false,
        AvgW: 41, PeakW: 68, PeakAt: range.From.AddHours(14),
        OnHours: 60, IdleOnHours: 6, IdleOffHours: 2, AsleepHours: 50, UnmonitoredHours: 5,
        CpuKwh: kwh * 0.43, GpuKwh: kwh * 0.11, DisplayKwh: kwh * 0.1, RestKwh: kwh * 0.36,
        IdleOnKwh: kwh * 0.1, IdleOffKwh: kwh * 0.04, MeasuredShare: 0.62, CalibratedShare: 0.2, EstimatedShare: 0.18);

    public static RangeReport Typical(DateRange range) => Typical(range, 2.74);

    public static RangeReport Typical(DateRange range, double kwh)
    {
        var count = Math.Max(1, (int)Math.Ceiling((range.To - range.From) / range.Bucket));
        var perBucket = kwh * 1000 / count;
        var series = Enumerable.Range(0, count).Select(i => Aggregate.Empty(range.From + i * range.Bucket) with
        {
            EnergyWh = perBucket, CpuWh = perBucket * 0.43, GpuWh = perBucket * 0.11, DisplayWh = perBucket * 0.1, RestWh = perBucket * 0.36,
            OnSeconds = range.Bucket.TotalSeconds,
        }).ToList();
        var first = DateOnly.FromDateTime(range.From.UtcDateTime);
        var last = DateOnly.FromDateTime(range.To.AddTicks(-1).UtcDateTime);
        var dayCount = Math.Max(1, last.DayNumber - first.DayNumber + 1);
        var days = Enumerable.Range(0, dayCount)
            .Select(d => new DayTotals(first.AddDays(d), kwh / dayCount, 0.05m, "USD", false, 8, 60, 0.01, 0))
            .ToList();
        return new RangeReport(range, Totals(range, kwh), days, series);
    }

    public static RangeReport Empty(DateRange range) => new(
        range,
        Totals(range, 0) with
        {
            Cost = 0, AvgW = 0, PeakW = 0, PeakAt = null, OnHours = 0, IdleOnHours = 0, IdleOffHours = 0, AsleepHours = 0,
            UnmonitoredHours = (range.To - range.From).TotalHours, MeasuredShare = 0, CalibratedShare = 0, EstimatedShare = 0,
        },
        [], []);
}
```

`tests/PowerLedger.App.Tests/FakeRangeHistory.cs`
```csharp
namespace PowerLedger.App.Tests;

/// <summary>History for the range screens: answers with <see cref="Answer"/> and records what it was asked.</summary>
internal sealed class FakeRangeHistory : IRangeHistory
{
    public List<DateRange> Reads { get; } = [];

    public List<(DateRange Range, ExportGrain Grain)> Exports { get; } = [];

    public Func<DateRange, RangeReport?> Answer { get; set; } = Reports.Typical;

    public IReadOnlyList<string>? Lines { get; set; } = ["header", "row"];

    public DateOnly? First { get; set; }

    public RangeReport? Read(DateRange range, TimeZoneInfo zone)
    {
        Reads.Add(range);
        return Answer(range);
    }

    public IReadOnlyList<string>? Csv(DateRange range, ExportGrain grain)
    {
        Exports.Add((range, grain));
        return Lines;
    }

    public DateOnly? FirstDay(TimeZoneInfo zone) => First;
}
```

`tests/PowerLedger.App.Tests/RangePickerTests.cs`
```csharp
using System.Globalization;
using Shouldly;

namespace PowerLedger.App.Tests;

public class RangePickerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly DateOnly Today = new(2026, 9, 8);

    [Theory]
    [InlineData("Today", "Today")]
    [InlineData("SevenDays", "Last 7 days")]
    [InlineData("ThirtyDays", "Last 30 days")]
    [InlineData("ThisMonth", "September 2026")]
    [InlineData("LastMonth", "August 2026")]
    [InlineData("Custom", "2 Sep – 8 Sep 2026")]
    public void Each_choice_resolves_to_its_range(string choice, string title)
        => new RangePicker(Enum.Parse<RangeChoice>(choice), Today).Resolve(Now, TimeZoneInfo.Utc, English).Title.ShouldBe(title);

    [Fact]
    public void Custom_days_change_the_range_only_while_custom_is_chosen()
    {
        var picker = new RangePicker(RangeChoice.Today, Today);
        var changes = 0;
        picker.Changed += () => changes++;

        picker.From = new DateTime(2026, 9, 1);
        changes.ShouldBe(0);
        picker.Choice = RangeChoice.Custom;
        changes.ShouldBe(1);
        picker.IsCustom.ShouldBeTrue();
        picker.To = new DateTime(2026, 9, 3);
        changes.ShouldBe(2);
        picker.Resolve(Now, TimeZoneInfo.Utc, English).Title.ShouldBe("1 Sep – 3 Sep 2026");
    }

    [Fact]
    public void A_cleared_date_keeps_the_last_day()
    {
        var picker = new RangePicker(RangeChoice.Custom, Today);
        picker.From = null;
        picker.From.ShouldBe(new DateTime(2026, 9, 2));
    }
}
```

`tests/PowerLedger.App.Tests/BreakdownViewModelTests.cs`
```csharp
using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.App.Tests;

public class BreakdownViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeRangeHistory _history = new();

    private BreakdownViewModel Model(UiThreads? threads = null) => new(_history, threads ?? UiThreads.Inline, _clock, TimeZoneInfo.Utc, English);

    [Fact]
    public void It_shows_today_in_watts_when_the_page_opens()
    {
        var model = Model();
        _history.Reads.ShouldBeEmpty();                               // nothing is read until the page shows
        model.Show();

        _history.Reads.Single().Title.ShouldBe("Today");
        model.Heading.ShouldBe("Today · 5-min · stacked by component");
        model.UnitLabel.ShouldBe("Watts");
        model.Chart.Capacity.ShouldBe(288);
        model.Chart.Buckets.Count.ShouldBe(175);
        model.Message.ShouldBeNull();
    }

    [Fact]
    public void Watt_hours_redraw_the_last_read_without_reading_again()
    {
        var model = Model();
        model.Show();
        var watts = model.Chart.Buckets[0].Total;

        model.Unit = ChartUnit.WattHours;

        _history.Reads.Count.ShouldBe(1);
        model.UnitLabel.ShouldBe("Wh per 5 min");
        model.Chart.Unit.ShouldBe(ChartUnit.WattHours);
        model.Chart.Buckets[0].Total.ShouldBe(watts / 12, 1e-9);        // five minutes on: Wh = W × 300 / 3600
    }

    [Fact]
    public void Choosing_a_range_reads_it()
    {
        var model = Model();
        model.Show();
        model.Range.Choice = RangeChoice.SevenDays;

        _history.Reads[^1].Title.ShouldBe("Last 7 days");
        model.Heading.ShouldBe("Last 7 days · hourly · stacked by component");
        model.Chart.Capacity.ShouldBe(168);
    }

    [Fact]
    public void The_table_gives_each_band_its_energy_and_share()
    {
        var model = Model();
        model.Show();

        model.Parts.Select(p => p.Name).ShouldBe(new[] { "CPU package", "GPU", "Display", "Rest of system", "Total" });
        model.Parts[0].Energy.ShouldBe("1.18");                       // 2.74 kWh × 43%
        model.Parts[0].Share.ShouldBe("43%");
        model.Parts[^1].Energy.ShouldBe("2.74");
        model.Parts[^1].Share.ShouldBe("100%");
        model.HasNegativeRest.ShouldBeFalse();
    }

    [Fact]
    public void A_negative_rest_counts_as_zero_and_is_called_out()
    {
        _history.Answer = range =>
        {
            var report = Reports.Typical(range);
            return report with { Totals = report.Totals with { RestKwh = -0.1 } };
        };
        var model = Model();
        model.Show();

        model.HasNegativeRest.ShouldBeTrue();
        model.Parts[3].Energy.ShouldBe("0.000");
        model.Parts[3].Share.ShouldBe("0%");
        model.Parts[0].Share.ShouldBe("67%");                         // 43 of the 64 left
    }

    [Fact]
    public void Nothing_to_show_says_why()
    {
        _history.Answer = _ => null;
        var model = Model();
        model.Show();
        model.Message.ShouldBe("History can't be read right now. It comes back when the service is running.");
        model.HasMessage.ShouldBeTrue();
        model.Heading.ShouldBe("Today · 5-min · stacked by component");

        _history.Answer = Reports.Empty;
        model.Refresh();
        model.Message.ShouldBe("No readings in this range.");
    }

    [Fact]
    public void A_read_that_a_newer_one_overtook_is_dropped()
    {
        var held = new Queue<Action>();
        var model = Model(new UiThreads(action => action(), held.Enqueue));
        model.Show();                                                 // today, held back
        model.Range.Choice = RangeChoice.SevenDays;                   // seven days, held back

        var today = held.Dequeue();
        held.Dequeue()();                                             // seven days lands first
        today();                                                      // then today, too late

        model.Heading.ShouldStartWith("Last 7 days");
    }

    [Fact]
    public void It_reads_every_minute_while_shown_and_stops_when_hidden()
    {
        var model = Model();
        model.Show();
        _clock.Advance(BreakdownViewModel.RefreshEvery);
        _history.Reads.Count.ShouldBe(2);

        model.Hide();
        _clock.Advance(BreakdownViewModel.RefreshEvery * 3);
        _history.Reads.Count.ShouldBe(2);
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.App.Tests --filter "RangePickerTests|BreakdownViewModelTests"`
Expected: build error, `RangePicker` and `BreakdownViewModel` not found.

- [x] **Step 3: Write the picker and the view model**

`src/PowerLedger.App/History/RangePicker.cs`
```csharp
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PowerLedger.App;

/// <summary>The ranges the history screens offer (spec §9: today, 7 days, 30 days and custom, and the months a report wants).</summary>
internal enum RangeChoice
{
    Today,
    SevenDays,
    ThirtyDays,
    ThisMonth,
    LastMonth,
    Custom,
}

/// <summary>
/// Which range a history screen shows. The custom days start as the last week and change the range only while Custom is
/// chosen; a date the picker clears keeps the last day.
/// </summary>
internal sealed class RangePicker : ObservableObject
{
    private RangeChoice _choice;
    private DateTime? _from;
    private DateTime? _to;

    public RangePicker(RangeChoice choice, DateOnly today)
    {
        _choice = choice;
        _from = today.AddDays(-6).ToDateTime(TimeOnly.MinValue);
        _to = today.ToDateTime(TimeOnly.MinValue);
    }

    /// <summary>Raised when the range to show has changed.</summary>
    public event Action? Changed;

    public RangeChoice Choice
    {
        get => _choice;
        set
        {
            if (!SetProperty(ref _choice, value)) return;
            OnPropertyChanged(nameof(IsCustom));
            Changed?.Invoke();
        }
    }

    public bool IsCustom => Choice == RangeChoice.Custom;

    /// <summary>The custom range's first day, as the date picker gives it.</summary>
    public DateTime? From { get => _from; set => SetDay(ref _from, value, nameof(From)); }

    /// <summary>The custom range's last day.</summary>
    public DateTime? To { get => _to; set => SetDay(ref _to, value, nameof(To)); }

    public DateRange Resolve(DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture) => Choice switch
    {
        RangeChoice.Today => Ranges.Today(now, zone, culture),
        RangeChoice.SevenDays => Ranges.LastDays(7, now, zone, culture),
        RangeChoice.ThirtyDays => Ranges.LastDays(30, now, zone, culture),
        RangeChoice.ThisMonth => Ranges.ThisMonth(now, zone, culture),
        RangeChoice.LastMonth => Ranges.LastMonth(now, zone, culture),
        _ => Ranges.Days(Day(_from, now, zone), Day(_to, now, zone), now, zone, culture),
    };

    private void SetDay(ref DateTime? field, DateTime? value, string name)
    {
        if (value is null)
        {
            OnPropertyChanged(name);   // the picker shows the last day again
            return;
        }
        if (SetProperty(ref field, value.Value.Date, name) && IsCustom) Changed?.Invoke();
    }

    private static DateOnly Day(DateTime? day, DateTimeOffset now, TimeZoneInfo zone) => day is { } d ? DateOnly.FromDateTime(d) : Ranges.LocalDay(now, zone);
}
```

`src/PowerLedger.App/Breakdown/BreakdownViewModel.cs`
```csharp
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>One line of the breakdown table: a band, or the total, with its energy in kWh and its share of the four bands.</summary>
internal sealed record PartRow(Part? Part, string Name, string Energy, string Share);

/// <summary>
/// The Breakdown screen (spec §9): power by component over a range, as a stacked chart in watts or watt-hours and as each
/// band's energy and share. It reads when shown, when the range changes and every minute while shown, off the UI thread;
/// switching the unit redraws from the last read.
/// </summary>
internal sealed class BreakdownViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(1);

    private readonly IRangeHistory _history;
    private readonly UiThreads _threads;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly CultureInfo _culture;
    private ITimer? _timer;
    private int _reads;
    private DateRange? _range;
    private RangeReport? _report;
    private ChartUnit _unit = ChartUnit.Watts;
    private ChartModel _chart = ChartModel.Empty;
    private string _heading = "";
    private string _unitLabel = "Watts";
    private IReadOnlyList<PartRow> _parts = [];
    private bool _hasNegativeRest;
    private string? _message;

    public BreakdownViewModel(IRangeHistory history, UiThreads threads, TimeProvider clock, TimeZoneInfo zone, CultureInfo culture)
    {
        _history = history;
        _threads = threads;
        _clock = clock;
        _zone = zone;
        _culture = culture;
        Range = new RangePicker(RangeChoice.Today, Ranges.LocalDay(clock.GetUtcNow(), zone));
        Range.Changed += Refresh;
    }

    public RangePicker Range { get; }

    public ChartUnit Unit
    {
        get => _unit;
        set
        {
            if (SetProperty(ref _unit, value)) Rebuild();
        }
    }

    public ChartModel Chart { get => _chart; private set => SetProperty(ref _chart, value); }

    /// <summary>"Last 7 days · hourly · stacked by component".</summary>
    public string Heading { get => _heading; private set => SetProperty(ref _heading, value); }

    /// <summary>"Watts", or "Wh per hour".</summary>
    public string UnitLabel { get => _unitLabel; private set => SetProperty(ref _unitLabel, value); }

    public IReadOnlyList<PartRow> Parts { get => _parts; private set => SetProperty(ref _parts, value); }

    /// <summary>Measured mode put the rest band below zero somewhere in the range, which the footnote explains (spec §9).</summary>
    public bool HasNegativeRest { get => _hasNegativeRest; private set => SetProperty(ref _hasNegativeRest, value); }

    /// <summary>Why there is nothing to show, or null.</summary>
    public string? Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value)) OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => Message is not null;

    /// <summary>The page is shown: read now, and every minute until it is hidden. Call on the UI thread.</summary>
    public void Show()
    {
        Refresh();
        _timer ??= _clock.CreateTimer(_ => _threads.Post(Refresh), null, RefreshEvery, RefreshEvery);
    }

    public void Hide()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Reads the chosen range off the UI thread; a read a newer one overtook is dropped. Call on the UI thread.</summary>
    internal void Refresh()
    {
        var read = ++_reads;
        var range = Range.Resolve(_clock.GetUtcNow(), _zone, _culture);
        _threads.Background(() =>
        {
            var report = _history.Read(range, _zone);
            _threads.Post(() =>
            {
                if (read != _reads) return;
                _range = range;
                _report = report;
                Rebuild();
            });
        });
    }

    public void Dispose()
    {
        Range.Changed -= Refresh;
        Hide();
    }

    private void Rebuild()
    {
        if (_range is not { } range) return;
        Heading = $"{range.Title} · {Ranges.BucketName(range.Bucket)} · stacked by component";
        UnitLabel = Unit == ChartUnit.Watts ? "Watts" : "Wh per " + Ranges.BucketLength(range.Bucket);
        if (_report is not { } report)
        {
            Chart = Charts.Build(range, [], Unit, _zone, _culture);
            Parts = [];
            HasNegativeRest = false;
            Message = "History can't be read right now. It comes back when the service is running.";
            return;
        }
        Chart = Charts.Build(range, report.Series, Unit, _zone, _culture);
        Parts = Rows(report.Totals);
        HasNegativeRest = report.Totals.RestKwh < 0 || report.Series.Any(b => b.RestWh < 0);
        Message = report.Totals.OnHours > 0 || report.Totals.AsleepHours > 0 ? null : "No readings in this range.";
    }

    private IReadOnlyList<PartRow> Rows(RangeTotals t)
    {
        (Part Part, string Name, double Kwh)[] bands =
        [
            (Part.Cpu, "CPU package", t.CpuKwh), (Part.Gpu, "GPU", t.GpuKwh),
            (Part.Display, "Display", t.DisplayKwh), (Part.Rest, "Rest of system", t.RestKwh),
        ];
        var clean = bands.Select(b => (b.Part, b.Name, Kwh: double.IsFinite(b.Kwh) ? Math.Max(0, b.Kwh) : 0)).ToList();
        var total = clean.Sum(b => b.Kwh);
        var rows = clean
            .Select(b => new PartRow(b.Part, b.Name, Format.Kwh(b.Kwh, _culture), Format.Percent(total > 0 ? b.Kwh / total : 0, _culture)))
            .ToList();
        rows.Add(new PartRow(null, "Total", Format.Kwh(t.EnergyKwh, _culture), total > 0 ? Format.Percent(1, _culture) : Format.Missing));
        return rows;
    }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter "RangePickerTests|BreakdownViewModelTests"`
Expected: `Passed! - Failed: 0, Passed: 16`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.App/History/RangePicker.cs src/PowerLedger.App/Breakdown tests/PowerLedger.App.Tests
git commit -m "Add the range picker and the Breakdown view model"
```

---

### Task 6: The Breakdown screen

**Files:**
- Create: `src/PowerLedger.App/Controls/RangeBar.xaml`, `src/PowerLedger.App/Controls/RangeBar.xaml.cs`
- Create: `src/PowerLedger.App/Breakdown/BreakdownView.xaml`, `src/PowerLedger.App/Breakdown/BreakdownView.xaml.cs`
- Modify: `src/PowerLedger.App/Controls/Converters.cs`
- Modify: `src/PowerLedger.App/Theme/Styles.xaml`
- Modify: `src/PowerLedger.App/Shell/ShellViewModel.cs`, `src/PowerLedger.App/Shell/MainWindow.xaml`
- Modify: `src/PowerLedger.App/App.xaml.cs`
- Modify: `tests/PowerLedger.App.Tests/BreakdownViewModelTests.cs`, `tests/PowerLedger.App.Tests/RenderingTests.cs`

The Breakdown screen follows the Now screen's grammar: a row of choices, an eyebrow heading over the chart, a legend, and a ledger-style table. The range buttons and the custom dates are a `RangeBar` the Report screen will share. Choices are segmented radio buttons, styled once as `Segment`, and the unit toggle uses the same style.

D1's `PageIs` converter compares any two values, so it becomes `ValueIs` under the key `Is`. The shell shows the Breakdown page, and it tells the view model when the page appears and disappears, so history is only read while someone is looking.

- [x] **Step 1: Write the failing test**

Append to `tests/PowerLedger.App.Tests/BreakdownViewModelTests.cs`, inside the class:

```csharp
    [Fact]
    public void The_shell_reads_the_breakdown_only_while_it_shows()
    {
        var breakdown = Model();
        var now = new NowViewModel(new FakeLink(), new FakeHistory(), UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, 0.4, () => { });
        var shell = new ShellViewModel(now, breakdown, "0.1.0");

        shell.Page = Page.Breakdown;
        shell.Current.ShouldBe(breakdown);
        _history.Reads.Count.ShouldBe(1);

        shell.Page = Page.Now;
        _clock.Advance(BreakdownViewModel.RefreshEvery * 2);
        _history.Reads.Count.ShouldBe(1);
    }
```

- [x] **Step 2: Run it to verify it fails**

Run: `dotnet build tests/PowerLedger.App.Tests`
Expected: build error, `ShellViewModel` takes two arguments.

- [x] **Step 3: Write the screen**

In `src/PowerLedger.App/Controls/Converters.cs`, replace `PageIs` with:

```csharp
/// <summary>Checks a radio button when its value is the one chosen, and chooses it when it is checked.</summary>
internal sealed class ValueIs : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Equals(value, parameter);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? parameter : Binding.DoNothing;
}
```

In `src/PowerLedger.App/Theme/Styles.xaml`, replace `<local:PageIs x:Key="PageIs" />` with `<local:ValueIs x:Key="Is" />`, and add before the closing `</ResourceDictionary>`:

```xml
    <!-- Segmented choices: flat buttons in a row; the chosen one is raised with an amber edge. -->
    <Style x:Key="Segment" TargetType="RadioButton">
        <Setter Property="FontFamily" Value="{StaticResource Font.Ui}" />
        <Setter Property="FontSize" Value="12.5" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.Ink2}" />
        <Setter Property="Cursor" Value="Hand" />
        <Setter Property="Margin" Value="0,0,6,6" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="RadioButton">
                    <Border x:Name="Face" Background="Transparent" BorderBrush="{DynamicResource Brush.Line}" BorderThickness="1" CornerRadius="3" Padding="11,5">
                        <ContentPresenter />
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter Property="Foreground" Value="{DynamicResource Brush.Ink}" />
                        </Trigger>
                        <Trigger Property="IsKeyboardFocused" Value="True">
                            <Setter TargetName="Face" Property="BorderBrush" Value="{DynamicResource Brush.LineStrong}" />
                        </Trigger>
                        <Trigger Property="IsChecked" Value="True">
                            <Setter Property="Foreground" Value="{DynamicResource Brush.Ink}" />
                            <Setter TargetName="Face" Property="Background" Value="{DynamicResource Brush.Raised}" />
                            <Setter TargetName="Face" Property="BorderBrush" Value="{DynamicResource Brush.Amber}" />
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- Dates for a custom range: WPF's picker with a text box and button in the palette. Its calendar keeps Windows' look. -->
    <Style TargetType="DatePickerTextBox">
        <Setter Property="Foreground" Value="{DynamicResource Brush.Ink}" />
        <Setter Property="CaretBrush" Value="{DynamicResource Brush.Ink}" />
        <Setter Property="FontFamily" Value="{StaticResource Font.Numbers}" />
        <Setter Property="FontSize" Value="12" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="DatePickerTextBox">
                    <ScrollViewer x:Name="PART_ContentHost" Margin="8,4,4,4" VerticalAlignment="Center" Focusable="False" />
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
    <Style TargetType="DatePicker">
        <Setter Property="Foreground" Value="{DynamicResource Brush.Ink}" />
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="DatePicker">
                    <Border BorderBrush="{DynamicResource Brush.LineStrong}" BorderThickness="1" CornerRadius="3" Background="{DynamicResource Brush.Panel}">
                        <Grid x:Name="PART_Root">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="Auto" />
                            </Grid.ColumnDefinitions>
                            <DatePickerTextBox x:Name="PART_TextBox" Grid.Column="0" />
                            <Button x:Name="PART_Button" Grid.Column="1" Style="{StaticResource Caption}" Width="28" Height="26" Content="&#xE787;" ToolTip="Pick a date" />
                            <Popup x:Name="PART_Popup" AllowsTransparency="True" Placement="Bottom" PlacementTarget="{Binding ElementName=PART_TextBox}" StaysOpen="False" />
                        </Grid>
                    </Border>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>
```

In `src/PowerLedger.App/Shell/MainWindow.xaml`, change the four `Converter={StaticResource PageIs}` to `Converter={StaticResource Is}`, and add after the `NowViewModel` data template:

```xml
        <DataTemplate DataType="{x:Type local:BreakdownViewModel}">
            <local:BreakdownView />
        </DataTemplate>
```

`src/PowerLedger.App/Controls/RangeBar.xaml`
```xml
<UserControl x:Class="PowerLedger.App.RangeBar"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:local="clr-namespace:PowerLedger.App">
    <WrapPanel>
        <RadioButton Style="{StaticResource Segment}" Content="Today"
                     IsChecked="{Binding Choice, Converter={StaticResource Is}, ConverterParameter={x:Static local:RangeChoice.Today}}" />
        <RadioButton Style="{StaticResource Segment}" Content="7 days"
                     IsChecked="{Binding Choice, Converter={StaticResource Is}, ConverterParameter={x:Static local:RangeChoice.SevenDays}}" />
        <RadioButton Style="{StaticResource Segment}" Content="30 days"
                     IsChecked="{Binding Choice, Converter={StaticResource Is}, ConverterParameter={x:Static local:RangeChoice.ThirtyDays}}" />
        <RadioButton Style="{StaticResource Segment}" Content="This month"
                     IsChecked="{Binding Choice, Converter={StaticResource Is}, ConverterParameter={x:Static local:RangeChoice.ThisMonth}}" />
        <RadioButton Style="{StaticResource Segment}" Content="Last month"
                     IsChecked="{Binding Choice, Converter={StaticResource Is}, ConverterParameter={x:Static local:RangeChoice.LastMonth}}" />
        <RadioButton Style="{StaticResource Segment}" Content="Custom"
                     IsChecked="{Binding Choice, Converter={StaticResource Is}, ConverterParameter={x:Static local:RangeChoice.Custom}}" />
        <StackPanel Orientation="Horizontal" Margin="6,0,0,6" Visibility="{Binding IsCustom, Converter={StaticResource VisibleWhen}}">
            <DatePicker Width="132" SelectedDate="{Binding From}" AutomationProperties.Name="First day" />
            <TextBlock Style="{StaticResource Text.Muted}" Text="to" VerticalAlignment="Center" Margin="8,0" />
            <DatePicker Width="132" SelectedDate="{Binding To}" AutomationProperties.Name="Last day" />
        </StackPanel>
    </WrapPanel>
</UserControl>
```

`src/PowerLedger.App/Controls/RangeBar.xaml.cs`
```csharp
using System.Windows.Controls;

namespace PowerLedger.App;

/// <summary>The range buttons and custom dates both history screens share; its data context is a <see cref="RangePicker"/>.</summary>
public partial class RangeBar : UserControl
{
    public RangeBar() => InitializeComponent();
}
```

`src/PowerLedger.App/Breakdown/BreakdownView.xaml`
```xml
<UserControl x:Class="PowerLedger.App.BreakdownView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:local="clr-namespace:PowerLedger.App">
    <ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled" Focusable="False">
        <StackPanel Margin="26,22,26,18">
            <DockPanel>
                <StackPanel DockPanel.Dock="Right" Orientation="Horizontal" VerticalAlignment="Top" Margin="16,0,0,0">
                    <RadioButton Style="{StaticResource Segment}" Content="W" ToolTip="Average watts while the machine was on"
                                 IsChecked="{Binding Unit, Converter={StaticResource Is}, ConverterParameter={x:Static local:ChartUnit.Watts}}" />
                    <RadioButton Style="{StaticResource Segment}" Content="Wh" ToolTip="Energy in each bucket" Margin="0,0,0,6"
                                 IsChecked="{Binding Unit, Converter={StaticResource Is}, ConverterParameter={x:Static local:ChartUnit.WattHours}}" />
                </StackPanel>
                <local:RangeBar DataContext="{Binding Range}" />
            </DockPanel>

            <DockPanel Margin="0,20,0,8">
                <TextBlock DockPanel.Dock="Right" Style="{StaticResource Text.Eyebrow}" Text="{Binding UnitLabel, Converter={StaticResource Upper}}" />
                <TextBlock Style="{StaticResource Text.Eyebrow}" Text="{Binding Heading, Converter={StaticResource Upper}}" />
            </DockPanel>
            <TextBlock Style="{StaticResource Text.Muted}" Margin="0,0,0,8" Text="{Binding Message}"
                       Visibility="{Binding HasMessage, Converter={StaticResource VisibleWhen}}" />
            <local:StackedChart Height="300" Model="{Binding Chart}" />
            <WrapPanel Margin="0,8,0,0">
                <WrapPanel.Resources>
                    <Style TargetType="TextBlock" BasedOn="{StaticResource Text.Secondary}">
                        <Setter Property="FontSize" Value="12" />
                        <Setter Property="Margin" Value="0,0,22,0" />
                        <Setter Property="VerticalAlignment" Value="Center" />
                    </Style>
                    <Style TargetType="Rectangle">
                        <Setter Property="Width" Value="10" />
                        <Setter Property="Height" Value="10" />
                        <Setter Property="Margin" Value="0,0,7,0" />
                    </Style>
                </WrapPanel.Resources>
                <Rectangle Fill="{DynamicResource Brush.PartCpu}" /><TextBlock Text="CPU" />
                <Rectangle Fill="{DynamicResource Brush.PartGpu}" /><TextBlock Text="GPU" />
                <Rectangle Fill="{DynamicResource Brush.PartDisplay}" /><TextBlock Text="Display" />
                <Rectangle Fill="{DynamicResource Brush.PartRest}" /><TextBlock Text="Rest" />
            </WrapPanel>

            <Border Style="{StaticResource LedgerHead}" Margin="0,28,0,0">
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*" />
                        <ColumnDefinition Width="110" />
                        <ColumnDefinition Width="80" />
                    </Grid.ColumnDefinitions>
                    <TextBlock Style="{StaticResource Text.Eyebrow}" Text="BY COMPONENT" />
                    <TextBlock Grid.Column="1" Style="{StaticResource Text.Eyebrow}" Text="KWH" HorizontalAlignment="Right" />
                    <TextBlock Grid.Column="2" Style="{StaticResource Text.Eyebrow}" Text="SHARE" HorizontalAlignment="Right" />
                </Grid>
            </Border>
            <ItemsControl ItemsSource="{Binding Parts}" Focusable="False">
                <ItemsControl.ItemTemplate>
                    <DataTemplate DataType="{x:Type local:PartRow}">
                        <Border x:Name="Line" BorderBrush="{DynamicResource Brush.Line}" BorderThickness="0,0,0,1" Padding="0,8">
                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="22" />
                                    <ColumnDefinition Width="*" />
                                    <ColumnDefinition Width="110" />
                                    <ColumnDefinition Width="80" />
                                </Grid.ColumnDefinitions>
                                <Rectangle x:Name="Swatch" Width="10" Height="10" RadiusX="1" RadiusY="1" HorizontalAlignment="Left" VerticalAlignment="Center"
                                           Fill="{DynamicResource Brush.PartRest}" />
                                <TextBlock x:Name="Label" Grid.Column="1" Style="{StaticResource Text.Body}" Text="{Binding Name}" />
                                <TextBlock x:Name="Energy" Grid.Column="2" Style="{StaticResource Text.Number}" Text="{Binding Energy}" HorizontalAlignment="Right" />
                                <TextBlock Grid.Column="3" Style="{StaticResource Text.Number}" Foreground="{DynamicResource Brush.Ink3}" Text="{Binding Share}"
                                           HorizontalAlignment="Right" />
                            </Grid>
                        </Border>
                        <DataTemplate.Triggers>
                            <DataTrigger Binding="{Binding Part}" Value="Cpu"><Setter TargetName="Swatch" Property="Fill" Value="{DynamicResource Brush.PartCpu}" /></DataTrigger>
                            <DataTrigger Binding="{Binding Part}" Value="Gpu"><Setter TargetName="Swatch" Property="Fill" Value="{DynamicResource Brush.PartGpu}" /></DataTrigger>
                            <DataTrigger Binding="{Binding Part}" Value="Display"><Setter TargetName="Swatch" Property="Fill" Value="{DynamicResource Brush.PartDisplay}" /></DataTrigger>
                            <DataTrigger Binding="{Binding Part}" Value="{x:Null}">
                                <Setter TargetName="Swatch" Property="Visibility" Value="Hidden" />
                                <Setter TargetName="Label" Property="FontWeight" Value="SemiBold" />
                                <Setter TargetName="Energy" Property="FontWeight" Value="SemiBold" />
                                <Setter TargetName="Line" Property="BorderThickness" Value="0" />
                            </DataTrigger>
                        </DataTemplate.Triggers>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
            <TextBlock Style="{StaticResource Text.Muted}" Margin="0,12,0,0" TextWrapping="Wrap"
                       Visibility="{Binding HasNegativeRest, Converter={StaticResource VisibleWhen}}"
                       Text="The rest of the system fell below zero for part of this range: in measured mode the parts sometimes report more than the battery drains. The chart and the table count it as zero." />
            <TextBlock Style="{StaticResource Text.Muted}" Margin="0,12,0,0" TextWrapping="Wrap"
                       Text="Display is the built-in panel plus the monitors in the machine profile. W is the average while the machine was on; Wh is the energy in each bucket." />
        </StackPanel>
    </ScrollViewer>
</UserControl>
```

`src/PowerLedger.App/Breakdown/BreakdownView.xaml.cs`
```csharp
using System.Windows.Controls;

namespace PowerLedger.App;

/// <summary>The Breakdown screen's layout (spec §9); everything it shows comes from <see cref="BreakdownViewModel"/>.</summary>
public partial class BreakdownView : UserControl
{
    public BreakdownView() => InitializeComponent();
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

/// <summary>A screen a later build brings; until then it says so.</summary>
internal sealed record PlaceholderViewModel(string Title, string Text);

/// <summary>The window: which page shows, the screens, and the version in the title bar.</summary>
internal sealed class ShellViewModel(NowViewModel now, BreakdownViewModel breakdown, string version) : ObservableObject
{
    private static readonly Dictionary<Page, PlaceholderViewModel> Placeholders = new()
    {
        [Page.Report] = new("Report", "The energy bill, comparisons and exports arrive in the next build."),
        [Page.Settings] = new("Settings", "Tariff, machine profile and preferences arrive in the next build."),
    };

    private Page _page = Page.Now;

    public NowViewModel Now { get; } = now;

    public BreakdownViewModel Breakdown { get; } = breakdown;

    public string Version { get; } = version;

    /// <summary>The page shown. A history screen reads while it shows and stops when it does not.</summary>
    public Page Page
    {
        get => _page;
        set
        {
            if (!SetProperty(ref _page, value)) return;
            if (value == Page.Breakdown) Breakdown.Show();
            else Breakdown.Hide();
            OnPropertyChanged(nameof(Current));
        }
    }

    public object Current => Page switch
    {
        Page.Now => Now,
        Page.Breakdown => Breakdown,
        _ => Placeholders[Page],
    };
}
```

In `src/PowerLedger.App/App.xaml.cs`, add the field `private BreakdownViewModel? _breakdown;`, and in `OnStartup` replace the lines from `_now = new NowViewModel(` to `_shell = new ShellViewModel(_now, Version());` with:

```csharp
        var history = new HistoryReader(_database);
        _now = new NowViewModel(
            _link, history, threads, TimeProvider.System, TimeZoneInfo.Local, CultureInfo.CurrentCulture,
            preferences.Co2KgPerKwh, ServiceStarter.Start);
        _breakdown = new BreakdownViewModel(history, threads, TimeProvider.System, TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        _shell = new ShellViewModel(_now, _breakdown, Version());
```

In `ExitUi`, add `_breakdown?.Dispose();` after `_now.Dispose();`'s block.

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
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Draws the window with each screen, in both themes, to PNGs for a person to look at.</summary>
[Trait("Category", "UI")]
public class RenderingTests
{
    public static readonly string Folder = Path.Combine(Path.GetTempPath(), "powerledger-renders");
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 7, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static readonly (Page Page, string Name, Func<ShellViewModel, FrameworkElement> View)[] Pages =
    [
        (Page.Now, "now", shell => new NowView { DataContext = shell.Now }),
        (Page.Breakdown, "breakdown", shell => new BreakdownView { DataContext = shell.Breakdown }),
    ];

    [Fact]
    public void The_window_draws_every_screen_in_both_themes()
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

        foreach (var (_, name, _) in Pages)
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                new FileInfo(Path.Combine(Folder, $"{name}-{theme}.png")).Length.ShouldBeGreaterThan(30_000);
            }
        }
    }

    private static void Render()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PowerLedger;component/Theme/Styles.xaml", UriKind.Absolute),
        });
        var shell = new ShellViewModel(NowScreen(), BreakdownScreen(), "0.1.0");
        ResourceDictionary? palette = null;
        foreach (var theme in new[] { Theme.Dark, Theme.Light })
        {
            if (palette is not null) application.Resources.MergedDictionaries.Remove(palette);
            palette = ThemeManager.Palette(theme);
            application.Resources.MergedDictionaries.Insert(0, palette);

            foreach (var (page, name, view) in Pages)
            {
                shell.Page = page;
                var window = new MainWindow
                {
                    DataContext = shell, WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
                };
                window.Show();
                Pump(TimeSpan.FromMilliseconds(1200));   // the live readout settles over 900 ms
                Save(window, (int)window.ActualWidth, (int)window.ActualHeight, $"{name}-{theme}.png");
                window.Close();

                // Windows keeps a window within the screen, so each screen's whole length is drawn from its view alone.
                var child = view(shell);
                child.Width = 1010;
                var host = new System.Windows.Controls.Border { Background = (Brush)application.FindResource("Brush.Panel"), Child = child };
                host.Measure(new Size(1010, double.PositiveInfinity));
                host.Arrange(new Rect(host.DesiredSize));
                Pump(TimeSpan.FromMilliseconds(1200));
                host.UpdateLayout();
                Save(host, (int)host.ActualWidth, (int)host.ActualHeight, $"{name}-{theme}-full.png");
            }
        }
        shell.Page = Page.Now;
    }

    private static void Save(Visual visual, int width, int height, string name)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(Folder, name));
        png.Save(file);
    }

    /// <summary>A Tuesday afternoon eight days into September: asleep until 07:30, a working morning, an idle patch, a peak at 14:00.</summary>
    private static NowViewModel NowScreen()
    {
        var link = new FakeLink();
        var history = new FakeHistory();
        var model = new NowViewModel(link, history, UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc, English,
            co2KgPerKwh: 0.38, startService: () => { });
        link.Connect(true);
        history.Snapshot = Snapshots.Typical(Now, Series());
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

    /// <summary>The same machine's last seven days: asleep overnight, working days, quiet evenings.</summary>
    private static BreakdownViewModel BreakdownScreen()
    {
        var history = new FakeRangeHistory { Answer = range => Reports.Typical(range) with { Series = Week(range) } };
        var model = new BreakdownViewModel(history, UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc, English);
        model.Range.Choice = RangeChoice.SevenDays;
        return model;
    }

    private static IReadOnlyList<Aggregate> Week(DateRange range)
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

    private static IReadOnlyList<Aggregate> Series()
    {
        var dayStart = new DateTimeOffset(Now.Date, TimeSpan.Zero);
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

- [x] **Step 4: Run tests, and look**

Run: `dotnet test tests/PowerLedger.App.Tests --filter "Category!=UI"`
Expected: all pass.

Run: `dotnet test tests/PowerLedger.App.Tests --filter "Category=UI"` and open `breakdown-Dark.png` and `breakdown-Light-full.png` in `%TEMP%\powerledger-renders`.
Expected: "7 days" chosen and raised with an amber edge. The chart spans seven days, labelled "Wed 2" to "Tue 8", with hatched nights, stacked working days and the dashed now line on Tuesday afternoon. Below it sit the legend and the component table with the total in semibold.

- [x] **Step 5: Commit**

```bash
git add -A src/PowerLedger.App tests/PowerLedger.App.Tests
git commit -m "Add the Breakdown screen: ranges, the stacked chart in W or Wh, and the component table"
```

---

### Task 7: Windows' timeouts, and the saving suggestion

**Files:**
- Create: `src/PowerLedger.App/Report/SleepSettings.cs`
- Create: `src/PowerLedger.App/Report/IdleAdvice.cs`
- Test: `tests/PowerLedger.App.Tests/SleepSettingsTests.cs`
- Test: `tests/PowerLedger.App.Tests/IdleAdviceTests.cs`

Spec §6 has the saving suggestion quote Windows' sleep timeout, "sleeps after 30 min" or "never". `SleepSettings` reads the active power plan through `powrprof`. That is the API behind `powercfg /query`, so no process is started. It reads the sleep and display timeouts, plugged in and on battery; zero means never, and null means Windows did not say.

`IdleAdvice` turns them and the range's idle energy into one sentence. It quotes the plugged-in timeouts, where idle time costs most and the only ones a desktop has. Idle time under 3% of the energy, or under 5 Wh, gets no advice.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.App.Tests/SleepSettingsTests.cs`
```csharp
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Reads this machine's power plan, so it runs where there is a real one.</summary>
[Trait("Category", "Hardware")]
public class SleepSettingsTests
{
    [Fact]
    public void The_active_plan_has_sleep_and_display_timeouts()
    {
        var timeouts = new SleepSettings().Read();
        timeouts.SleepAc.ShouldNotBeNull();
        timeouts.DisplayAc.ShouldNotBeNull();
    }
}
```

`tests/PowerLedger.App.Tests/IdleAdviceTests.cs`
```csharp
using Shouldly;

namespace PowerLedger.App.Tests;

public class IdleAdviceTests
{
    private static SleepTimeouts Plugged(double sleepMinutes, double displayMinutes = 10)
        => new(TimeSpan.FromMinutes(sleepMinutes), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(displayMinutes), TimeSpan.FromMinutes(5));

    [Fact]
    public void Little_idle_energy_needs_no_advice()
    {
        IdleAdvice.For(idleKwh: 0.004, idleDisplayOnKwh: 0.004, energyKwh: 0.05, Plugged(30)).ShouldBe("Idle time used little energy in this range.");
        IdleAdvice.For(idleKwh: 0.02, idleDisplayOnKwh: 0.02, energyKwh: 1, Plugged(30)).ShouldBe("Idle time used little energy in this range.");
    }

    [Fact]
    public void A_plan_that_never_sleeps_is_named()
        => IdleAdvice.For(0.4, 0.3, 2.7, Plugged(0))
            .ShouldBe("Windows never sleeps here when plugged in. Sleeping after 30 minutes idle would cut most of it.");

    [Fact]
    public void A_long_timeout_is_quoted()
        => IdleAdvice.For(0.4, 0.3, 2.7, Plugged(180))
            .ShouldBe("Windows sleeps after 3 h idle when plugged in. Sleeping after 30 minutes would cut much of it.");

    [Fact]
    public void A_short_timeout_with_the_display_left_on_suggests_turning_it_off()
    {
        IdleAdvice.For(0.4, 0.3, 2.7, Plugged(20, displayMinutes: 15))
            .ShouldBe("Windows already sleeps after 20 min, but the display stays on for 15 min. Turning it off after 5 minutes would save a little more.");
        IdleAdvice.For(0.4, 0.3, 2.7, Plugged(20, displayMinutes: 0))
            .ShouldBe("Windows already sleeps after 20 min, but the display stays on while idle. Turning it off after 5 minutes would save a little more.");
    }

    [Fact]
    public void A_short_timeout_leaves_little_to_save()
        => IdleAdvice.For(0.4, 0.1, 2.7, Plugged(15))
            .ShouldBe("Windows already sleeps after 15 min idle when plugged in, so little more can be saved.");

    [Fact]
    public void An_unknown_timeout_gives_the_general_advice()
        => IdleAdvice.For(0.4, 0.3, 2.7, SleepTimeouts.Unknown)
            .ShouldBe("Letting Windows sleep sooner when the machine is idle would cut most of it.");

    [Theory]
    [InlineData(45, "45 min")]
    [InlineData(180, "3 h")]
    [InlineData(90, "1 h 30 min")]
    public void Spans_read_naturally(int minutes, string text) => IdleAdvice.Span(TimeSpan.FromMinutes(minutes)).ShouldBe(text);
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.App.Tests --filter "SleepSettingsTests|IdleAdviceTests"`
Expected: build error, `SleepSettings`, `SleepTimeouts` and `IdleAdvice` not found.

- [x] **Step 3: Read the timeouts, and write the advice**

`src/PowerLedger.App/Report/SleepSettings.cs`
```csharp
using System.Runtime.InteropServices;

namespace PowerLedger.App;

/// <summary>Windows' idle timeouts in the active power plan, plugged in and on battery. Zero means never; null means Windows did not say.</summary>
internal sealed record SleepTimeouts(TimeSpan? SleepAc, TimeSpan? SleepDc, TimeSpan? DisplayAc, TimeSpan? DisplayDc)
{
    public static SleepTimeouts Unknown { get; } = new(null, null, null, null);
}

internal interface ISleepSettings
{
    SleepTimeouts Read();
}

/// <summary>Reads the active power plan's sleep and display timeouts with powrprof, the API behind <c>powercfg /query</c> (spec §6).</summary>
internal sealed class SleepSettings : ISleepSettings
{
    private static readonly Guid SleepGroup = new("238C9FA8-0AAD-41ED-83F4-97BE242C8F20");
    private static readonly Guid StandbyIdle = new("29F6C1DB-86DA-48C5-9FDB-F2B67B1F44DA");
    private static readonly Guid VideoGroup = new("7516B95F-F776-4464-8C53-06167F40CC99");
    private static readonly Guid VideoIdle = new("3C0BC021-C8A8-4E07-A973-6B14CBCB2B7E");

    public SleepTimeouts Read()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var scheme) != 0) return SleepTimeouts.Unknown;
        try
        {
            var plan = Marshal.PtrToStructure<Guid>(scheme);
            return new SleepTimeouts(
                Value(plan, SleepGroup, StandbyIdle, pluggedIn: true), Value(plan, SleepGroup, StandbyIdle, pluggedIn: false),
                Value(plan, VideoGroup, VideoIdle, pluggedIn: true), Value(plan, VideoGroup, VideoIdle, pluggedIn: false));
        }
        finally
        {
            LocalFree(scheme);
        }
    }

    private static TimeSpan? Value(Guid plan, Guid group, Guid setting, bool pluggedIn)
    {
        uint seconds;
        var result = pluggedIn
            ? PowerReadACValueIndex(IntPtr.Zero, ref plan, ref group, ref setting, out seconds)
            : PowerReadDCValueIndex(IntPtr.Zero, ref plan, ref group, ref setting, out seconds);
        return result == 0 ? TimeSpan.FromSeconds(seconds) : null;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint acValueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint dcValueIndex);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
```

`src/PowerLedger.App/Report/IdleAdvice.cs`
```csharp
using System.Globalization;

namespace PowerLedger.App;

/// <summary>
/// The idle-waste suggestion (spec §6): what Windows' timeouts say about the energy idle time used. It quotes the
/// plugged-in timeouts, where idle time costs most and the only ones a desktop has.
/// </summary>
internal static class IdleAdvice
{
    /// <summary>A sleep timeout at or under this already catches most idle time.</summary>
    public static readonly TimeSpan Prompt = TimeSpan.FromMinutes(30);

    /// <summary>A display timeout over this leaves the display lit longer than it needs to be.</summary>
    public static readonly TimeSpan PromptDisplay = TimeSpan.FromMinutes(5);

    public static string For(double idleKwh, double idleDisplayOnKwh, double energyKwh, SleepTimeouts timeouts)
    {
        if (!(energyKwh > 0) || idleKwh < 0.005 || idleKwh / energyKwh < 0.03) return "Idle time used little energy in this range.";
        if (timeouts.SleepAc is not { } sleep) return "Letting Windows sleep sooner when the machine is idle would cut most of it.";
        if (sleep == TimeSpan.Zero) return "Windows never sleeps here when plugged in. Sleeping after 30 minutes idle would cut most of it.";
        if (sleep > Prompt) return $"Windows sleeps after {Span(sleep)} idle when plugged in. Sleeping after 30 minutes would cut much of it.";
        if (idleDisplayOnKwh > idleKwh / 2 && timeouts.DisplayAc is { } display && (display == TimeSpan.Zero || display > PromptDisplay))
        {
            var lit = display == TimeSpan.Zero ? "while idle" : "for " + Span(display);
            return $"Windows already sleeps after {Span(sleep)}, but the display stays on {lit}. Turning it off after 5 minutes would save a little more.";
        }
        return $"Windows already sleeps after {Span(sleep)} idle when plugged in, so little more can be saved.";
    }

    /// <summary>"45 min", "3 h", "1 h 30 min".</summary>
    internal static string Span(TimeSpan span)
    {
        var minutes = (int)Math.Round(span.TotalMinutes);
        var invariant = CultureInfo.InvariantCulture;
        if (minutes < 60) return minutes.ToString(invariant) + " min";
        var hours = (minutes / 60).ToString(invariant) + " h";
        return minutes % 60 == 0 ? hours : hours + " " + (minutes % 60).ToString(invariant) + " min";
    }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter "SleepSettingsTests|IdleAdviceTests"`
Expected: `Passed! - Failed: 0, Passed: 10`. `SleepSettingsTests` is tagged Hardware, so CI skips it.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.App/Report tests/PowerLedger.App.Tests/SleepSettingsTests.cs tests/PowerLedger.App.Tests/IdleAdviceTests.cs
git commit -m "Read Windows' sleep and display timeouts, and turn them into the saving suggestion"
```

---

### Task 8: What a report says

**Files:**
- Create: `src/PowerLedger.App/Report/ReportData.cs`
- Create: `src/PowerLedger.App/History/Bands.cs`
- Modify: `src/PowerLedger.App/History/Ranges.cs`
- Modify: `src/PowerLedger.App/Breakdown/BreakdownViewModel.cs`
- Test: `tests/PowerLedger.App.Tests/ReportDataTests.cs`

`ReportData.From` writes a range's report out once, for the screen, the PDF and the monthly job. That covers spec §9's summary:
- kWh, cost, CO₂, average and peak.
- On, idle, asleep and unmonitored time.
- Idle waste with its cost, its share and the suggestion.
- Each band's energy and share, the everyday equivalents and the quality mix.
- A bar for every day of the range, with future days of this month left empty.

The cost note gives the average price, which follows any tariff change within the range. A range priced partly in another currency says so instead (spec §7). The Breakdown table and the report list the bands the same way, so their rows move into `Bands`. `Ranges.Covered` gives the days a range holds data for, and a month still under way stops at today.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.App.Tests/ReportDataTests.cs`
```csharp
using System.Globalization;
using Shouldly;

namespace PowerLedger.App.Tests;

public class ReportDataTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly SleepTimeouts Timeouts = new(TimeSpan.FromHours(3), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5));

    private static ReportData Month(Func<RangeReport, RangeReport>? change = null)
    {
        var report = Reports.Typical(Ranges.ThisMonth(Now, Utc, English));
        return ReportData.From(change is null ? report : change(report), Timeouts, 0.38, Utc, English);
    }

    [Fact]
    public void A_month_so_far_reads_as_a_bill()
    {
        var data = Month();
        data.Title.ShouldBe("September 2026");
        data.Period.ShouldBe("1 Sep – 8 Sep 2026");
        data.HasData.ShouldBeTrue();
        data.Energy.ShouldBe("2.74");
        data.Cost.ShouldBe("$0.47");
        data.CostNote.ShouldBe("$0.17 / kWh on average");
        data.Co2.ShouldBe("1.04 kg");                                   // 2.74 kWh at 0.38 kg / kWh
        data.Co2Note.ShouldBe("at 0.38 kg / kWh");
        data.Average.ShouldBe("41");
        data.Peak.ShouldBe("68");
        data.PeakAt.ShouldBe("at 14:00, Tue 1 Sep");
        data.On.ShouldBe("60h 00m");
        data.Asleep.ShouldBe("50h 00m");
        data.Unmonitored.ShouldBe("5h 00m");
    }

    [Fact]
    public void Idle_waste_is_priced_and_the_advice_quotes_windows()
    {
        var data = Month();
        data.IdleWaste.ShouldBe("0.384 kWh");                          // 14% of 2.74 kWh
        data.IdleWasteNote.ShouldBe("≈ $0.07 · 14% of the energy");
        data.Advice.ShouldBe("Windows sleeps after 3 h idle when plugged in. Sleeping after 30 minutes would cut much of it.");
        data.IdleOn.ShouldBe("6h 00m");
        data.IdleOff.ShouldBe("2h 00m");
    }

    [Fact]
    public void Parts_equivalents_and_quality_are_written_out()
    {
        var data = Month();
        data.Parts.Select(p => p.Name).ShouldBe(new[] { "CPU package", "GPU", "Display", "Rest of system" });
        data.Parts[0].Energy.ShouldBe("1.18");
        data.Parts[0].Share.ShouldBe("43%");
        data.Parts[0].Fraction.ShouldBe(0.43, 1e-9);
        data.Equivalents.Select(e => e.Value).ShouldBe(new[] { "274 hours", "183", "15 km" });
        data.QualityText.ShouldBe("62% measured · 20% calibrated · 18% estimated");
        data.Quality.ShouldBe(new QualityMix(0.62, 0.2, 0.18));
    }

    [Fact]
    public void Daily_bars_cover_every_day_of_the_month()
    {
        var data = Month();
        data.Days.Count.ShouldBe(30);
        data.Days[0].Day.ShouldBe(new DateOnly(2026, 9, 1));
        data.Days.Take(8).ShouldAllBe(d => d.Kwh > 0);
        data.Days.Skip(8).ShouldAllBe(d => d.Kwh == 0);
    }

    [Fact]
    public void Without_a_tariff_or_with_a_currency_change_the_cost_says_so()
    {
        var untariffed = Month(r => r with { Totals = r.Totals with { Currency = null, Cost = 0 } });
        untariffed.Cost.ShouldBe("–");
        untariffed.CostNote.ShouldBe("no tariff set");
        untariffed.IdleWasteNote.ShouldBe("14% of the energy");
        Month(r => r with { Totals = r.Totals with { CostIsPartial = true } }).CostNote
            .ShouldBe("partial: energy priced in an earlier currency is left out");
    }

    [Fact]
    public void A_day_reads_its_date_and_an_empty_range_has_no_data()
    {
        var today = ReportData.From(Reports.Typical(Ranges.Today(Now, Utc, English)), Timeouts, 0.38, Utc, English);
        today.Period.ShouldBe("Tue 8 Sep 2026");
        today.PeakAt.ShouldBe("at 14:00");
        today.Days.Count.ShouldBe(1);

        var empty = ReportData.From(Reports.Empty(Ranges.LastMonth(Now, Utc, English)), Timeouts, 0.38, Utc, English);
        empty.HasData.ShouldBeFalse();
        empty.Period.ShouldBe("1 Aug – 31 Aug 2026");
        empty.QualityText.ShouldBe("no readings");
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.App.Tests --filter ReportDataTests`
Expected: build error, `ReportData` not found.

- [x] **Step 3: Write the report**

`src/PowerLedger.App/History/Bands.cs`
```csharp
using System.Globalization;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>One line of a component table: a band, or the total, with its energy in kWh, its share, and the share as a fraction.</summary>
internal sealed record PartRow(Part? Part, string Name, string Energy, string Share, double Fraction);

/// <summary>The four bands of a range as the Breakdown table and the report list them.</summary>
internal static class Bands
{
    /// <summary>
    /// A row per band, and the total when asked. A negative rest, which measured mode shows when the parts over-report,
    /// counts as zero here as on every chart (spec §9), so the shares are of the four bands as drawn.
    /// </summary>
    public static IReadOnlyList<PartRow> Rows(RangeTotals t, CultureInfo culture, bool withTotal)
    {
        (Part Part, string Name, double Kwh)[] bands =
        [
            (Part.Cpu, "CPU package", t.CpuKwh), (Part.Gpu, "GPU", t.GpuKwh),
            (Part.Display, "Display", t.DisplayKwh), (Part.Rest, "Rest of system", t.RestKwh),
        ];
        var clean = bands.Select(b => (b.Part, b.Name, Kwh: double.IsFinite(b.Kwh) ? Math.Max(0, b.Kwh) : 0)).ToList();
        var total = clean.Sum(b => b.Kwh);
        var rows = clean.Select(b =>
        {
            var share = total > 0 ? b.Kwh / total : 0;
            return new PartRow(b.Part, b.Name, Format.Kwh(b.Kwh, culture), Format.Percent(share, culture), share);
        }).ToList();
        if (withTotal)
        {
            rows.Add(new PartRow(null, "Total", Format.Kwh(t.EnergyKwh, culture), total > 0 ? Format.Percent(1, culture) : Format.Missing, total > 0 ? 1 : 0));
        }
        return rows;
    }
}
```

In `src/PowerLedger.App/Breakdown/BreakdownViewModel.cs`, delete the `PartRow` record and its summary, and replace `Rows` with:

```csharp
    private IReadOnlyList<PartRow> Rows(RangeTotals t) => Bands.Rows(t, _culture, withTotal: true);
```

In `src/PowerLedger.App/History/Ranges.cs`, add after `Span`:

```csharp
    /// <summary>The first and last local days a range holds data for: a range under way stops at today, and an empty one keeps its days.</summary>
    public static (DateOnly First, DateOnly Last) Covered(DateRange range, TimeZoneInfo zone)
    {
        var end = range.To > range.From ? range.To : range.Through;
        return (LocalDay(range.From, zone), LocalDay(end.AddTicks(-1), zone));
    }
```

`src/PowerLedger.App/Report/ReportData.cs`
```csharp
using System.Globalization;
using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>A labelled figure in a report: "LED bulb", "274 hours", "a 10 W bulb".</summary>
internal sealed record ReportLine(string Name, string Value, string Note);

/// <summary>How much of the on-time each quality covered.</summary>
internal sealed record QualityMix(double Measured, double Calibrated, double Estimated);

/// <summary>One day's energy for the daily bars.</summary>
internal sealed record DayBar(DateOnly Day, double Kwh);

/// <summary>
/// Everything a report shows (spec §9 Report), written out. The Report screen, the PDF and the monthly job all read this,
/// so they always agree.
/// </summary>
internal sealed record ReportData(
    string Title, string Period, bool HasData,
    string Energy, string Cost, string CostNote, string Co2, string Co2Note,
    string Average, string Peak, string PeakAt,
    string On, string IdleOn, string IdleOff, string Asleep, string Unmonitored,
    string IdleWaste, string IdleWasteNote, string Advice,
    IReadOnlyList<PartRow> Parts, IReadOnlyList<ReportLine> Equivalents,
    QualityMix Quality, string QualityText, IReadOnlyList<DayBar> Days)
{
    public static ReportData Empty { get; } = new(
        "", "", false, Format.Missing, Format.Missing, "", Format.Missing, "", Format.Missing, Format.Missing, "",
        Format.Missing, Format.Missing, Format.Missing, Format.Missing, Format.Missing, Format.Missing, "", "",
        [], [], new QualityMix(0, 0, 0), "", []);

    /// <summary>The report for a range, priced as history priced it, with CO₂ at <paramref name="co2KgPerKwh"/> and the
    /// suggestion from Windows' <paramref name="sleep"/> timeouts.</summary>
    public static ReportData From(RangeReport report, SleepTimeouts sleep, double co2KgPerKwh, TimeZoneInfo zone, CultureInfo culture)
    {
        var t = report.Totals;
        var (first, last) = Ranges.Covered(report.Range, zone);
        decimal? price = t.Currency is not null && !t.CostIsPartial && t.EnergyKwh > 0 ? t.Cost / (decimal)t.EnergyKwh : null;
        var idle = t.IdleOnKwh + t.IdleOffKwh;
        var idleShare = Format.Percent(t.EnergyKwh > 0 ? idle / t.EnergyKwh : 0, culture) + " of the energy";
        return new ReportData(
            report.Range.Title,
            Ranges.Span(first, last, culture),
            t.OnHours > 0 || t.AsleepHours > 0,
            Format.Kwh(t.EnergyKwh, culture),
            t.Currency is { } currency ? Money.Format(t.Cost, currency, culture) : Format.Missing,
            CostNoteOf(t, price, culture),
            Format.Kg(Core.Co2.Kg(t.EnergyKwh, co2KgPerKwh), culture) + " kg",   // Co2 alone would be the property
            $"at {co2KgPerKwh.ToString("0.00", culture)} kg / kWh",
            Format.WholeWatts(t.AvgW, culture),
            Format.WholeWatts(t.PeakW, culture),
            t.PeakAt is { } at && t.PeakW > 0
                ? "at " + TimeZoneInfo.ConvertTime(at, zone).ToString(first == last ? "HH:mm" : "HH:mm, ddd d MMM", culture)
                : "",
            Format.Duration(t.OnHours), Format.Duration(t.IdleOnHours), Format.Duration(t.IdleOffHours),
            Format.Duration(t.AsleepHours), Format.Duration(t.UnmonitoredHours),
            Format.Kwh(idle, culture) + " kWh",
            price is { } rate && t.Currency is { } money
                ? $"≈ {Money.Format(decimal.Round((decimal)idle * rate, 2), money, culture)} · {idleShare}"
                : t.EnergyKwh > 0 ? idleShare : "",
            IdleAdvice.For(idle, t.IdleOnKwh, t.EnergyKwh, sleep),
            Bands.Rows(t, culture, withTotal: false),
            EquivalentsOf(t.EnergyKwh, culture),
            new QualityMix(t.MeasuredShare, t.CalibratedShare, t.EstimatedShare),
            t.MeasuredShare + t.CalibratedShare + t.EstimatedShare > 0
                ? $"{Format.Percent(t.MeasuredShare, culture)} measured · {Format.Percent(t.CalibratedShare, culture)} calibrated · {Format.Percent(t.EstimatedShare, culture)} estimated"
                : "no readings",
            Bars(first, Ranges.LocalDay(report.Range.Through.AddTicks(-1), zone), report.Days));
    }

    /// <summary>The average price over the range, which follows any tariff change; or why there is none.</summary>
    private static string CostNoteOf(RangeTotals t, decimal? price, CultureInfo culture)
    {
        if (t.Currency is not { } currency) return "no tariff set";
        if (t.CostIsPartial) return "partial: energy priced in an earlier currency is left out";
        return price is { } p ? Money.Rate(decimal.Round(p, 4), currency, culture) + " / kWh on average" : "";
    }

    /// <summary>Spec §9's everyday equivalents, from Core's round assumptions.</summary>
    private static IReadOnlyList<ReportLine> EquivalentsOf(double kwh, CultureInfo culture) =>
    [
        new("LED bulb", Count(Comparisons.LedBulbHours(kwh), culture) + " hours", "a 10 W bulb"),
        new("Phone charges", Count(Comparisons.PhoneCharges(kwh), culture), "15 Wh each"),
        new("Electric car", Count(Comparisons.EvKm(kwh), culture) + " km", "at 0.18 kWh / km"),
    ];

    private static string Count(double value, CultureInfo culture) => value < 10 ? value.ToString("0.0", culture) : value.ToString("N0", culture);

    /// <summary>A bar for every day of the range, those without readings at zero.</summary>
    private static IReadOnlyList<DayBar> Bars(DateOnly first, DateOnly last, IReadOnlyList<DayTotals> days)
    {
        var energy = days.ToDictionary(d => d.Day, d => d.EnergyKwh);
        var count = Math.Max(1, last.DayNumber - first.DayNumber + 1);
        return [.. Enumerable.Range(0, count).Select(i => first.AddDays(i)).Select(day => new DayBar(day, energy.GetValueOrDefault(day)))];
    }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter "ReportDataTests|BreakdownViewModelTests"`
Expected: `Passed! - Failed: 0, Passed: 15`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.App tests/PowerLedger.App.Tests/ReportDataTests.cs
git commit -m "Write a range's report out once, for the screen, the PDF and the monthly job"
```

---

### Task 9: The report as a PDF

**Files:**
- Modify: `src/PowerLedger.App/PowerLedger.App.csproj`
- Create: `src/PowerLedger.App/Report/ReportDocument.cs`
- Test: `tests/PowerLedger.App.Tests/ReportDocumentTests.cs`

The PDF is one A4 page in the light palette, so it prints well. It carries the title and period, the bill and time ledgers, the components with a stacked bar, the everyday equivalents, the daily bars, the idle waste with its suggestion, and the quality mix. The footer gives the version and the page number. Bars are QuestPDF rows with coloured backgrounds, so nothing depends on QuestPDF's SVG or canvas support.

Text is set in Segoe UI with Windows' script fonts behind it, so a month named in Hindi or Japanese draws. QuestPDF stopped using system fonts by default in 2026.9.0, so the document turns them back on; without them it falls back to its bundled Lato, which has no Devanagari or CJK. A glyph no font has comes out as an empty box rather than failing the export. QuestPDF's Community licence (spec §14) is set once, where the documents are made.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.App.Tests/ReportDocumentTests.cs`
```csharp
using System.Globalization;
using System.Text;
using Shouldly;

namespace PowerLedger.App.Tests;

public class ReportDocumentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static ReportData Data(DateRange range, CultureInfo culture)
        => ReportData.From(Reports.Typical(range), SleepTimeouts.Unknown, 0.38, TimeZoneInfo.Utc, culture);

    [Fact]
    public void A_report_becomes_a_pdf()
    {
        var pdf = ReportDocument.Generate(Data(Ranges.ThisMonth(Now, TimeZoneInfo.Utc, English), English), "0.1.0", Now, English);

        Encoding.ASCII.GetString(pdf, 0, 5).ShouldBe("%PDF-");
        Encoding.ASCII.GetString(pdf, pdf.Length - 8, 8).ShouldContain("%%EOF");
        pdf.Length.ShouldBeGreaterThan(5_000);
    }

    [Fact]
    public void A_report_in_hindi_and_rupees_with_no_readings_still_draws()
    {
        var hindi = CultureInfo.GetCultureInfo("hi-IN");
        var empty = Reports.Empty(Ranges.LastMonth(Now, TimeZoneInfo.Utc, hindi));
        var data = ReportData.From(empty with { Totals = empty.Totals with { Currency = "INR" } }, SleepTimeouts.Unknown, 0.71, TimeZoneInfo.Utc, hindi);

        ReportDocument.Generate(data, "0.1.0", Now, hindi).Length.ShouldBeGreaterThan(1_000);
    }

    [Fact]
    public void Ninety_days_of_bars_fit()
    {
        var range = Ranges.Days(new DateOnly(2026, 6, 1), new DateOnly(2026, 8, 29), Now, TimeZoneInfo.Utc, English);
        ReportDocument.Generate(Data(range, English), "0.1.0", Now, English).Length.ShouldBeGreaterThan(5_000);
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.App.Tests --filter ReportDocumentTests`
Expected: build error, `ReportDocument` not found.

- [x] **Step 3: Add QuestPDF and draw the report**

In `src/PowerLedger.App/PowerLedger.App.csproj`, add to the package `ItemGroup`:

```xml
    <PackageReference Include="QuestPDF" Version="2026.9.0" />
```

`src/PowerLedger.App/Report/ReportDocument.cs`
```csharp
using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PowerLedger.App;

/// <summary>
/// The report as an A4 PDF (spec §9 exports and the monthly report), drawn with QuestPDF from a <see cref="ReportData"/>
/// in the light palette so it prints well. Bars are rows with coloured backgrounds, not pictures.
/// </summary>
internal static class ReportDocument
{
    private const string Ink = "#1D1F1B";
    private const string Ink2 = "#575B53";
    private const string Ink3 = "#858980";
    private const string Rule = "#CDD0C8";
    private const string Amber = "#A2680C";
    private static readonly string[] PartColours = ["#C4761C", "#4A76A6", "#7C8A2E", "#8E928A"];
    private static readonly string[] QualityColours = ["#3E7E43", "#35678A", "#7C7355"];
    /// <summary>Segoe UI, then the fonts Windows ships for the scripts it lacks: Indic, Thai and Lao, Chinese, Japanese, Korean, Ethiopic, symbols.</summary>
    private static readonly string[] Fonts =
        ["Segoe UI", "Nirmala UI", "Leelawadee UI", "Microsoft YaHei UI", "Microsoft JhengHei UI", "Yu Gothic UI", "Malgun Gothic", "Ebrima", "Segoe UI Symbol"];

    static ReportDocument()
    {
        QuestPDF.Settings.License = LicenseType.Community;          // spec §14: QuestPDF's Community licence
        QuestPDF.Settings.UseSystemFonts = true;                    // off by default since 2026.9.0; Windows' own fonts cover the scripts
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;         // a glyph no font has is an empty box, not a failed export
        QuestPDF.Settings.ThrowOnMissingFontFamilies = false;       // a script font missing from this Windows is skipped
    }

    /// <summary>The PDF's bytes.</summary>
    public static byte[] Generate(ReportData data, string version, DateTimeOffset made, CultureInfo culture)
        => Document.Create(document => document.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(40);
            page.PageColor(Colors.White);
            page.DefaultTextStyle(style => style.FontFamily(Fonts).FontSize(9.5f).FontColor(Ink));
            page.Header().Column(header =>
            {
                header.Item().Text("POWERLEDGER · ENERGY REPORT").FontSize(8).SemiBold().FontColor(Amber).LetterSpacing(0.08f);
                header.Item().PaddingTop(4).Text(data.Title).FontSize(22).SemiBold();
                header.Item().Text(data.Period).FontColor(Ink2);
            });
            page.Content().PaddingTop(18).Column(column => Body(column, data, culture));
            page.Footer().Row(row =>
            {
                row.RelativeItem().Text($"PowerLedger {version} · made {made.ToString("d MMM yyyy HH:mm", culture)}").FontSize(7.5f).FontColor(Ink3);
                row.AutoItem().Text(text =>
                {
                    text.DefaultTextStyle(style => style.FontSize(7.5f).FontColor(Ink3));
                    text.Span("page ");
                    text.CurrentPageNumber();
                    text.Span(" of ");
                    text.TotalPages();
                });
            });
        })).GeneratePdf();

    private static void Body(ColumnDescriptor column, ReportData data, CultureInfo culture)
    {
        column.Spacing(18);
        if (!data.HasData) column.Item().Text("No readings were recorded in this range.").FontColor(Ink2);

        column.Item().Row(row =>
        {
            row.Spacing(28);
            row.RelativeItem().Column(bill =>
            {
                Heading(bill, "Bill");
                bill.Item().BorderBottom(0.5f).BorderColor(Rule).PaddingVertical(4).Row(hero =>
                {
                    hero.RelativeItem().AlignBottom().Text("Energy").FontColor(Ink2);
                    hero.AutoItem().Text(text =>
                    {
                        text.Span(data.Energy).FontSize(24).Light();
                        text.Span(" kWh").FontColor(Ink3);
                    });
                });
                Line(bill, "Cost", data.Cost, data.CostNote);
                Line(bill, "CO₂", data.Co2, data.Co2Note);
                Line(bill, "Average", data.Average + " W", "while on");
                Line(bill, "Peak", data.Peak + " W", data.PeakAt);
            });
            row.RelativeItem().Column(time =>
            {
                Heading(time, "Time");
                Line(time, "On", data.On, "");
                Line(time, "Idle, display on", data.IdleOn, "");
                Line(time, "Idle, display off", data.IdleOff, "");
                Line(time, "Asleep", data.Asleep, "while the service ran");
                Line(time, "Unmonitored", data.Unmonitored, "no readings at all");
            });
        });

        column.Item().Row(row =>
        {
            row.Spacing(28);
            row.RelativeItem().Column(parts =>
            {
                Heading(parts, "By component");
                if (data.Parts.Any(p => p.Fraction > 0))
                {
                    parts.Item().PaddingVertical(6).Height(8).Row(bar =>
                    {
                        for (var i = 0; i < data.Parts.Count; i++)
                        {
                            if (data.Parts[i].Fraction > 0) bar.RelativeItem((float)data.Parts[i].Fraction).Background(PartColours[i]);
                        }
                    });
                }
                for (var i = 0; i < data.Parts.Count; i++)
                {
                    var part = data.Parts[i];
                    var colour = PartColours[i];
                    parts.Item().BorderBottom(0.5f).BorderColor(Rule).PaddingVertical(4).Row(line =>
                    {
                        line.ConstantItem(14).AlignMiddle().AlignLeft().Width(7).Height(7).Background(colour);
                        line.RelativeItem().Text(part.Name).FontColor(Ink2);
                        line.AutoItem().Text(part.Energy + " kWh").SemiBold();
                        line.ConstantItem(40).AlignRight().Text(part.Share).FontColor(Ink3);
                    });
                }
            });
            row.RelativeItem().Column(equivalents =>
            {
                Heading(equivalents, "Everyday equivalents");
                foreach (var line in data.Equivalents) Line(equivalents, line.Name, line.Value, line.Note);
            });
        });

        column.Item().Column(daily =>
        {
            Heading(daily, "Daily energy · kWh");
            var max = data.Days.Count > 0 ? data.Days.Max(d => d.Kwh) : 0;
            daily.Item().PaddingTop(8).Height(90).Row(bars =>
            {
                foreach (var day in data.Days)
                {
                    var height = max > 0 ? (float)(86 * day.Kwh / max) : 0;
                    var slot = bars.RelativeItem().AlignBottom().PaddingHorizontal(data.Days.Count > 40 ? 0.5f : 2);
                    if (height >= 0.5f) slot.Height(height).Background(Amber);
                }
            });
            var every = Math.Max(1, (int)Math.Ceiling(data.Days.Count / 16.0));
            daily.Item().PaddingTop(2).Row(labels =>
            {
                for (var i = 0; i < data.Days.Count; i++)
                {
                    var cell = labels.RelativeItem().AlignCenter();
                    if (i % every == 0) cell.Text(data.Days[i].Day.Day.ToString(CultureInfo.InvariantCulture)).FontSize(7).FontColor(Ink3);
                }
            });
            if (max > 0) daily.Item().AlignRight().Text($"highest day {Format.Kwh(max, culture)} kWh").FontSize(7.5f).FontColor(Ink3);
        });

        column.Item().Row(row =>
        {
            row.Spacing(28);
            row.RelativeItem().Column(idle =>
            {
                Heading(idle, "Idle waste");
                Line(idle, "Energy while idle", data.IdleWaste, data.IdleWasteNote);
                idle.Item().PaddingTop(6).Text(data.Advice).FontColor(Ink2);
            });
            row.RelativeItem().Column(quality =>
            {
                Heading(quality, "Data quality");
                double[] shares = [data.Quality.Measured, data.Quality.Calibrated, data.Quality.Estimated];
                if (shares.Sum() > 0)
                {
                    quality.Item().PaddingVertical(6).Height(8).Row(bar =>
                    {
                        for (var i = 0; i < shares.Length; i++)
                        {
                            if (shares[i] > 0) bar.RelativeItem((float)shares[i]).Background(QualityColours[i]);
                        }
                    });
                }
                quality.Item().Text(data.QualityText).FontColor(Ink2);
                quality.Item().PaddingTop(4).Text("Measured: Windows' battery report. Calibrated: a model with a baseline learned on battery, ±10%. Estimated: the model alone, ±20%.")
                    .FontSize(7.5f).FontColor(Ink3);
            });
        });
    }

    private static void Heading(ColumnDescriptor column, string text)
        => column.Item().BorderBottom(1).BorderColor(Ink3).PaddingBottom(3)
            .Text(text.ToUpperInvariant()).FontSize(7.5f).SemiBold().FontColor(Ink2).LetterSpacing(0.06f);

    private static void Line(ColumnDescriptor column, string key, string value, string note)
        => column.Item().BorderBottom(0.5f).BorderColor(Rule).PaddingVertical(4).Row(row =>
        {
            row.RelativeItem().Text(key).FontColor(Ink2);
            row.AutoItem().Text(value).SemiBold();
            if (note.Length > 0) row.AutoItem().PaddingLeft(6).AlignBottom().Text(note).FontSize(7.5f).FontColor(Ink3);
        });
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter ReportDocumentTests`
Expected: `Passed! - Failed: 0, Passed: 3`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.App/PowerLedger.App.csproj src/PowerLedger.App/Report/ReportDocument.cs tests/PowerLedger.App.Tests/ReportDocumentTests.cs
git commit -m "Draw the report as a one-page PDF with QuestPDF"
```

---

### Task 10: The Report view model and its exports

**Files:**
- Create: `src/PowerLedger.App/Report/FileSaver.cs`
- Create: `src/PowerLedger.App/Report/ReportViewModel.cs`
- Test: `tests/PowerLedger.App.Tests/ReportViewModelTests.cs`
- Create: `tests/PowerLedger.App.Tests/FakeSaver.cs`, `tests/PowerLedger.App.Tests/FakeSleep.cs`

`ReportViewModel` reads like the Breakdown screen: when shown, when the range changes and every minute while shown, all off the UI thread. It opens on this month. Exports ask where to save on the UI thread and write off it, and the Save dialog suggests a name from the range:
- A finished month is `PowerLedger-2026-08`.
- A day is `PowerLedger-2026-09-08`.
- Anything else runs from first to last day, so a month still under way is `PowerLedger-2026-09-01-to-2026-09-08` and never takes the name the monthly job uses.
- CSV adds its grain: `-raw`, `-1min` or `-1h`.

A file is written beside its target and moved into place. A failed export then leaves any earlier file whole, and `Saved` says why it failed. The picture is drawn by the view into the stream the view model opens, so the view model never touches WPF.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.App.Tests/FakeSaver.cs`
```csharp
using System.IO;

namespace PowerLedger.App.Tests;

/// <summary>A Save dialog that answers with the suggested name in a folder of its own, or cancels.</summary>
internal sealed class FakeSaver : IFileSaver, IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"powerledger-saves-{Guid.NewGuid():N}");

    public bool Cancel { get; set; }

    public string? Suggested { get; private set; }

    /// <summary>Where the last answer pointed.</summary>
    public string Chosen => Path.Combine(_folder, Suggested ?? "nothing");

    public string? Ask(string name, string filter)
    {
        Suggested = name;
        Directory.CreateDirectory(_folder);
        return Cancel ? null : Chosen;
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
```

`tests/PowerLedger.App.Tests/FakeSleep.cs`
```csharp
namespace PowerLedger.App.Tests;

/// <summary>Windows' timeouts as the test sets them, counting the reads.</summary>
internal sealed class FakeSleep : ISleepSettings
{
    public int Reads { get; private set; }

    public SleepTimeouts Timeouts { get; set; } = new(TimeSpan.FromHours(3), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5));

    public SleepTimeouts Read()
    {
        Reads++;
        return Timeouts;
    }
}
```

`tests/PowerLedger.App.Tests/ReportViewModelTests.cs`
```csharp
using System.Globalization;
using System.IO;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class ReportViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeRangeHistory _history = new();
    private readonly FakeSaver _saver = new();
    private readonly FakeSleep _sleep = new();

    public void Dispose() => _saver.Dispose();

    private ReportViewModel Model() => new(_history, _sleep, _saver, _ => [1, 2, 3], UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, 0.38);

    [Fact]
    public void It_opens_on_this_month_and_reads_when_shown()
    {
        var model = Model();
        model.Data.ShouldBe(ReportData.Empty);
        model.Show();

        model.Data.Title.ShouldBe("September 2026");
        model.Data.Energy.ShouldBe("2.74");
        model.Data.Advice.ShouldStartWith("Windows sleeps after 3 h");
        model.Message.ShouldBeNull();
        _sleep.Reads.ShouldBe(1);
    }

    [Fact]
    public void Choosing_last_month_reads_it()
    {
        var model = Model();
        model.Show();
        model.Range.Choice = RangeChoice.LastMonth;
        model.Data.Title.ShouldBe("August 2026");
    }

    [Fact]
    public void The_pdf_is_saved_where_the_user_says()
    {
        var model = Model();
        model.Show();
        model.Range.Choice = RangeChoice.LastMonth;
        model.ExportPdf.Execute(null);

        _saver.Suggested.ShouldBe("PowerLedger-2026-08.pdf");
        File.ReadAllBytes(_saver.Chosen).ShouldBe(new byte[] { 1, 2, 3 });
        model.Saved.ShouldBe("Saved PowerLedger-2026-08.pdf");
    }

    [Fact]
    public void Csv_is_the_shown_range_at_the_chosen_grain()
    {
        var model = Model();
        model.Show();
        model.ExportCsv.Execute("Minute");

        _saver.Suggested.ShouldBe("PowerLedger-2026-09-01-to-2026-09-08-1min.csv");
        _history.Exports.Single().Grain.ShouldBe(ExportGrain.Minute);
        _history.Exports.Single().Range.Title.ShouldBe("September 2026");
        File.ReadAllLines(_saver.Chosen).ShouldBe(new[] { "header", "row" });
    }

    [Fact]
    public void A_cancelled_save_writes_nothing()
    {
        _saver.Cancel = true;
        var model = Model();
        model.Show();
        model.ExportPdf.Execute(null);

        File.Exists(_saver.Chosen).ShouldBeFalse();
        model.Saved.ShouldBeNull();
    }

    [Fact]
    public void A_save_that_fails_says_why_and_leaves_nothing_behind()
    {
        _history.Lines = null;                                     // history can't be read mid-export
        var model = Model();
        model.Show();
        model.ExportCsv.Execute("Hour");

        model.Saved.ShouldBe("Couldn't save: history can't be read right now.");
        File.Exists(_saver.Chosen).ShouldBeFalse();
        File.Exists(_saver.Chosen + ".partial").ShouldBeFalse();
    }

    [Fact]
    public void The_picture_is_drawn_by_the_view_into_the_chosen_file()
    {
        var model = Model();
        model.Show();
        model.SaveImage(stream => stream.WriteByte(7));

        _saver.Suggested.ShouldBe("PowerLedger-2026-09-01-to-2026-09-08.png");
        File.ReadAllBytes(_saver.Chosen).ShouldBe(new byte[] { 7 });
    }

    [Fact]
    public void Nothing_to_show_says_why()
    {
        _history.Answer = _ => null;
        var model = Model();
        model.Show();
        model.Message.ShouldBe("History can't be read right now. It comes back when the service is running.");
        model.Data.Title.ShouldBe("September 2026");

        _history.Answer = Reports.Empty;
        model.Refresh();
        model.Message.ShouldBe("No readings in this range.");
    }

    [Fact]
    public void A_day_is_named_by_its_date()
        => ReportViewModel.FileName(Ranges.Today(Now, TimeZoneInfo.Utc, English), TimeZoneInfo.Utc).ShouldBe("PowerLedger-2026-09-08");
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.App.Tests --filter ReportViewModelTests`
Expected: build error, `IFileSaver` and `ReportViewModel` not found.

- [x] **Step 3: Write the saver and the view model**

`src/PowerLedger.App/Report/FileSaver.cs`
```csharp
using Microsoft.Win32;

namespace PowerLedger.App;

/// <summary>Asks where to save a file.</summary>
internal interface IFileSaver
{
    /// <summary>The path chosen for a file suggested as <paramref name="name"/>, or null when the user cancels.</summary>
    string? Ask(string name, string filter);
}

/// <summary>Windows' Save dialog, opening in Documents.</summary>
internal sealed class FileSaver : IFileSaver
{
    public string? Ask(string name, string filter)
    {
        var dialog = new SaveFileDialog
        {
            FileName = name,
            Filter = filter,
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
```

`src/PowerLedger.App/Report/ReportViewModel.cs`
```csharp
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PowerLedger.App;

/// <summary>
/// The Report screen (spec §9): the energy bill for a range, idle waste and the saving suggestion, everyday equivalents,
/// the quality mix and daily bars, and the PDF, CSV and PNG exports. It reads when shown, when the range changes and every
/// minute while shown, off the UI thread. Exports ask where to save on the UI thread and write off it, except the picture,
/// which the view draws on the UI thread.
/// </summary>
internal sealed class ReportViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(1);

    private readonly IRangeHistory _history;
    private readonly ISleepSettings _sleep;
    private readonly IFileSaver _saver;
    private readonly Func<ReportData, byte[]> _pdf;
    private readonly UiThreads _threads;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly CultureInfo _culture;
    private readonly double _co2KgPerKwh;
    private ITimer? _timer;
    private int _reads;
    private DateRange? _range;
    private ReportData _data = ReportData.Empty;
    private string? _message;
    private string? _saved;

    public ReportViewModel(
        IRangeHistory history, ISleepSettings sleep, IFileSaver saver, Func<ReportData, byte[]> pdf,
        UiThreads threads, TimeProvider clock, TimeZoneInfo zone, CultureInfo culture, double co2KgPerKwh)
    {
        _history = history;
        _sleep = sleep;
        _saver = saver;
        _pdf = pdf;
        _threads = threads;
        _clock = clock;
        _zone = zone;
        _culture = culture;
        _co2KgPerKwh = co2KgPerKwh;
        Range = new RangePicker(RangeChoice.ThisMonth, Ranges.LocalDay(clock.GetUtcNow(), zone));
        Range.Changed += Refresh;
        ExportPdf = new RelayCommand(SavePdf);
        ExportCsv = new RelayCommand<string>(SaveCsv);
    }

    public RangePicker Range { get; }

    public ReportData Data { get => _data; private set => SetProperty(ref _data, value); }

    /// <summary>Why there is nothing to show, or null.</summary>
    public string? Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value)) OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => Message is not null;

    /// <summary>What the last export did: the file it saved, or why it could not.</summary>
    public string? Saved { get => _saved; private set => SetProperty(ref _saved, value); }

    public ICommand ExportPdf { get; }

    /// <summary>Takes the grain's name: Raw, Minute or Hour.</summary>
    public ICommand ExportCsv { get; }

    /// <summary>The page is shown: read now, and every minute until it is hidden. Call on the UI thread.</summary>
    public void Show()
    {
        Refresh();
        _timer ??= _clock.CreateTimer(_ => _threads.Post(Refresh), null, RefreshEvery, RefreshEvery);
    }

    public void Hide()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Reads the chosen range off the UI thread; a read a newer one overtook is dropped. Call on the UI thread.</summary>
    internal void Refresh()
    {
        var read = ++_reads;
        var range = Range.Resolve(_clock.GetUtcNow(), _zone, _culture);
        _threads.Background(() =>
        {
            var report = _history.Read(range, _zone);
            var data = report is null ? null : ReportData.From(report, _sleep.Read(), _co2KgPerKwh, _zone, _culture);
            _threads.Post(() =>
            {
                if (read != _reads) return;
                _range = range;
                Data = data ?? ReportData.Empty with { Title = range.Title };
                Message = data is null ? "History can't be read right now. It comes back when the service is running."
                    : data.HasData ? null : "No readings in this range.";
            });
        });
    }

    /// <summary>"PNG": asks where to save, then lets the view draw the report into the file. Call on the UI thread.</summary>
    public void SaveImage(Action<Stream> draw) => Save("", "png", "PNG image|*.png", draw, background: false);

    public void Dispose()
    {
        Range.Changed -= Refresh;
        Hide();
    }

    /// <summary>A finished month is "PowerLedger-2026-08", a day "PowerLedger-2026-09-08", anything else its first and last days.</summary>
    internal static string FileName(DateRange range, TimeZoneInfo zone)
    {
        var (first, last) = Ranges.Covered(range, zone);
        var invariant = CultureInfo.InvariantCulture;
        if (first.Day == 1 && last == first.AddMonths(1).AddDays(-1)) return "PowerLedger-" + first.ToString("yyyy-MM", invariant);
        if (first == last) return "PowerLedger-" + first.ToString("yyyy-MM-dd", invariant);
        return $"PowerLedger-{first.ToString("yyyy-MM-dd", invariant)}-to-{last.ToString("yyyy-MM-dd", invariant)}";
    }

    private void SavePdf()
    {
        var data = Data;
        Save("", "pdf", "PDF document|*.pdf", stream => stream.Write(_pdf(data)), background: true);
    }

    private void SaveCsv(string? grain)
    {
        if (_range is not { } range || !Enum.TryParse<ExportGrain>(grain, out var chosen)) return;
        var suffix = chosen switch
        {
            ExportGrain.Raw => "-raw",
            ExportGrain.Minute => "-1min",
            _ => "-1h",
        };
        Save(suffix, "csv", "CSV file|*.csv", stream =>
        {
            var lines = _history.Csv(range, chosen) ?? throw new IOException("history can't be read right now.");
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            foreach (var line in lines) writer.WriteLine(line);
        }, background: true);
    }

    /// <summary>Asks where to save, then writes beside the target and moves the file into place, so a failed write leaves any earlier file whole.</summary>
    private void Save(string suffix, string extension, string filter, Action<Stream> write, bool background)
    {
        if (_range is not { } range) return;
        if (_saver.Ask(FileName(range, _zone) + suffix + "." + extension, filter) is not { } path) return;
        void Write()
        {
            var partial = path + ".partial";
            string outcome;
            try
            {
                using (var file = File.Create(partial)) write(file);
                File.Move(partial, path, overwrite: true);
                outcome = "Saved " + Path.GetFileName(path);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Remove(partial);
                outcome = "Couldn't save: " + error.Message;
            }
            _threads.Post(() => Saved = outcome);
        }
        if (background) _threads.Background(Write);
        else Write();
    }

    private static void Remove(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Left behind; the next save to the same place replaces it.
        }
    }
}
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter ReportViewModelTests`
Expected: `Passed! - Failed: 0, Passed: 9`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.App/Report tests/PowerLedger.App.Tests
git commit -m "Add the Report view model with PDF, CSV and picture exports"
```

---

### Task 11: Daily bars, the quality bar, and drawn controls that describe themselves

**Files:**
- Create: `src/PowerLedger.App/Controls/DailyBars.cs`
- Create: `src/PowerLedger.App/Controls/QualityBar.cs`
- Modify: `src/PowerLedger.App/Controls/Geometry.cs`
- Modify: `src/PowerLedger.App/Controls/LiveReadout.cs`, `MeterScale.cs`, `Sparkline.cs`, `BudgetBar.cs`
- Modify: `src/PowerLedger.App/Theme/Styles.xaml`
- Test: `tests/PowerLedger.App.Tests/DescriptionTests.cs`
- Create: `tests/PowerLedger.App.Tests/Sta.cs`
- Modify: `tests/PowerLedger.App.Tests/GeometryTests.cs`

The Report screen needs two more drawings.
- `DailyBars` draws one amber bar per day over a round kWh scale, and labels the highest day. Day numbers sit along the bottom as often as they fit.
- `QualityBar` splits one bar into the measured, calibrated and estimated shares in the quality colours, 2 px apart like the budget bar.

Every drawn control now answers `Describe()`, the sentence its automation peer gives a screen reader (D1's rule). A name the view sets still wins.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.App.Tests/Sta.cs`
```csharp
using System.Runtime.ExceptionServices;

namespace PowerLedger.App.Tests;

/// <summary>Runs WPF code on a thread of its own in the single-threaded apartment WPF requires.</summary>
internal static class Sta
{
    public static T Run<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
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
        return result;
    }
}
```

`tests/PowerLedger.App.Tests/DescriptionTests.cs`
```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>The drawn controls tell a screen reader what they show.</summary>
public class DescriptionTests
{
    private static string NameOf(UIElement control) => UIElementAutomationPeer.CreatePeerForElement(control).GetName();

    [Fact]
    public void Drawn_controls_describe_what_they_show()
    {
        var names = Sta.Run(() =>
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            return new[]
            {
                NameOf(new LiveReadout { Value = 34.2 }),
                NameOf(new MeterScale { Value = 34.2, Average = 41, Peak = 68, Range = MeterRange.For(68) }),
                NameOf(new Sparkline { Values = [new SparkSample(10, 30.4), new SparkSample(0, 34.2)] }),
                NameOf(new BudgetBar { Rows = [new BudgetRow(Part.Cpu, "CPU package", "", "14.6 W", "43%", 0.43)] }),
                NameOf(new QualityBar { Mix = new QualityMix(0.62, 0.2, 0.18) }),
                NameOf(new DailyBars { Days = [new DayBar(new DateOnly(2026, 9, 1), 0.3), new DayBar(new DateOnly(2026, 9, 2), 0.5)] }),
                NameOf(new StackedChart { Model = ChartModel.Empty with { Description = "Today: power by component." } }),
            };
        });

        names.ShouldBe(new[]
        {
            "34.2 watts",
            "Meter: 34.2 W now, average 41 W, peak 68 W today.",
            "Last 60 seconds: between 30 and 34 W.",
            "Power budget: CPU package 14.6 W (43%).",
            "Quality: 62% measured, 20% calibrated, 18% estimated.",
            "Daily energy over 2 days; the highest was 0.500 kWh on 2 Sep.",
            "Today: power by component.",
        });
    }

    [Fact]
    public void A_name_the_view_gives_wins()
        => Sta.Run(() =>
        {
            var chart = new StackedChart();
            AutomationProperties.SetName(chart, "Today's chart");
            return NameOf(chart);
        }).ShouldBe("Today's chart");
}
```

Append to `tests/PowerLedger.App.Tests/GeometryTests.cs`, inside the class:

```csharp
    [Theory]
    [InlineData(30, 30.0, 1)]
    [InlineData(90, 10.0, 3)]
    [InlineData(0, 10.0, 1)]
    public void Labels_skip_slots_until_they_fit(int count, double slot, int every) => Geometry.LabelEvery(count, slot, 22).ShouldBe(every);
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.App.Tests --filter "DescriptionTests|GeometryTests"`
Expected: build error, `QualityBar`, `DailyBars` and `LabelEvery` not found.

- [x] **Step 3: Write the controls and the descriptions**

In `src/PowerLedger.App/Controls/Geometry.cs`, add after `StackTops`:

```csharp
    /// <summary>How many slots apart labels <paramref name="labelWidth"/> wide must be to fit: every slot when they fit in one.</summary>
    public static int LabelEvery(int count, double slotWidth, double labelWidth)
        => count <= 0 || slotWidth <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(labelWidth / slotWidth));
```

`src/PowerLedger.App/Controls/DailyBars.cs`
```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>The report's daily bars (spec §9): one amber bar per day of the range over a round kWh scale, the highest day
/// labelled, and day numbers along the bottom as often as they fit.</summary>
internal sealed class DailyBars : Instrument
{
    public static readonly DependencyProperty DaysProperty = Register<IReadOnlyList<DayBar>>(nameof(Days), [], typeof(DailyBars));

    private const double Left = 40;
    private const double RightInset = 14;
    private const double Top = 20;
    private const double AxisRoom = 22;

    public IReadOnlyList<DayBar> Days { get => (IReadOnlyList<DayBar>)GetValue(DaysProperty); set => SetValue(DaysProperty, value); }

    internal override string Describe()
    {
        var days = Days;
        if (!days.Any(d => d.Kwh > 0)) return "Daily energy: no readings.";
        var highest = days.MaxBy(d => d.Kwh)!;
        var culture = CultureInfo.CurrentCulture;
        return $"Daily energy over {days.Count.ToString(culture)} days; the highest was {Format.Kwh(highest.Kwh, culture)} kWh on {highest.Day.ToString("d MMM", culture)}.";
    }

    protected override Size MeasureOverride(Size availableSize) => Fixed(availableSize, 170);

    protected override void OnRender(DrawingContext dc)
    {
        var days = Days;
        var culture = CultureInfo.CurrentCulture;
        var right = ActualWidth - RightInset;
        var bottom = ActualHeight - AxisRoom;
        var (max, step) = Geometry.ChartScale(days.Count > 0 ? days.Max(d => d.Kwh) : 0, 0.1);
        double Y(double kwh) => bottom - (bottom - Top) * Math.Clamp(kwh / max, 0, 1);

        var grid = Line(LineBrush);
        for (var value = 0.0; value <= max + step / 1000; value += step)
        {
            dc.DrawLine(grid, new Point(Left, Y(value)), new Point(right, Y(value)));
            DrawText(dc, Format.Scale(value, step, culture), Left - 8, Y(value) - 7, LabelBrush, TextAlignment.Right);
        }
        if (days.Count == 0) return;

        var slot = (right - Left) / days.Count;
        var gap = Math.Min(slot * 0.3, 6);
        var every = Geometry.LabelEvery(days.Count, slot, 22);
        for (var i = 0; i < days.Count; i++)
        {
            var x = Left + i * slot;
            if (days[i].Kwh > 0) dc.DrawRectangle(AccentBrush, null, new Rect(x + gap / 2, Y(days[i].Kwh), Math.Max(1, slot - gap), bottom - Y(days[i].Kwh)));
            if (i % every == 0) DrawText(dc, days[i].Day.Day.ToString(culture), x + slot / 2, bottom + 5, LabelBrush, TextAlignment.Center);
        }
        var highest = Enumerable.Range(0, days.Count).MaxBy(i => days[i].Kwh);
        if (days[highest].Kwh > 0)
        {
            DrawText(dc, Format.Kwh(days[highest].Kwh, culture), Left + (highest + 0.5) * slot, Y(days[highest].Kwh) - 15, LabelBrush, TextAlignment.Center);
        }
    }
}
```

`src/PowerLedger.App/Controls/QualityBar.cs`
```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>The quality mix (spec §9): one bar split into the measured, calibrated and estimated shares of the on-time, in
/// the quality colours, 2 px apart. With no readings it is an empty outline.</summary>
internal sealed class QualityBar : Instrument
{
    public static readonly DependencyProperty MixProperty = Register(nameof(Mix), new QualityMix(0, 0, 0), typeof(QualityBar));
    public static readonly DependencyProperty MeasuredBrushProperty = Register<Brush>(nameof(MeasuredBrush), Brushes.Green, typeof(QualityBar));
    public static readonly DependencyProperty CalibratedBrushProperty = Register<Brush>(nameof(CalibratedBrush), Brushes.SteelBlue, typeof(QualityBar));
    public static readonly DependencyProperty EstimatedBrushProperty = Register<Brush>(nameof(EstimatedBrush), Brushes.Tan, typeof(QualityBar));

    private const double Gap = 2;

    public QualityMix Mix { get => (QualityMix)GetValue(MixProperty); set => SetValue(MixProperty, value); }

    public Brush MeasuredBrush { get => (Brush)GetValue(MeasuredBrushProperty); set => SetValue(MeasuredBrushProperty, value); }

    public Brush CalibratedBrush { get => (Brush)GetValue(CalibratedBrushProperty); set => SetValue(CalibratedBrushProperty, value); }

    public Brush EstimatedBrush { get => (Brush)GetValue(EstimatedBrushProperty); set => SetValue(EstimatedBrushProperty, value); }

    internal override string Describe()
    {
        var culture = CultureInfo.CurrentCulture;
        var mix = Mix;
        return $"Quality: {Format.Percent(mix.Measured, culture)} measured, {Format.Percent(mix.Calibrated, culture)} calibrated, {Format.Percent(mix.Estimated, culture)} estimated.";
    }

    protected override Size MeasureOverride(Size availableSize) => Fixed(availableSize, 12);

    protected override void OnRender(DrawingContext dc)
    {
        (double Share, Brush Brush)[] parts = [(Mix.Measured, MeasuredBrush), (Mix.Calibrated, CalibratedBrush), (Mix.Estimated, EstimatedBrush)];
        var shown = parts.Where(p => p.Share > 0).ToList();
        if (shown.Count == 0)
        {
            dc.DrawRectangle(null, Line(LineBrush), new Rect(0.5, 0.5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1)));
            return;
        }
        var total = shown.Sum(p => p.Share);
        var room = ActualWidth - Gap * (shown.Count - 1);
        var x = 0.0;
        foreach (var (share, brush) in shown)
        {
            var width = room * share / total;
            dc.DrawRectangle(brush, null, new Rect(x, 0, Math.Max(0, width), ActualHeight));
            x += width + Gap;
        }
    }
}
```

In `src/PowerLedger.App/Controls/LiveReadout.cs`, add before `MeasureOverride`:

```csharp
    internal override string Describe()
        => double.IsFinite(Value) ? Format.Watts(Value, CultureInfo.CurrentCulture) + " watts" : "Waiting for a reading";
```

In `src/PowerLedger.App/Controls/MeterScale.cs`, add before `MeasureOverride`:

```csharp
    internal override string Describe()
    {
        var culture = CultureInfo.CurrentCulture;
        return double.IsFinite(Value)
            ? $"Meter: {Format.Watts(Value, culture)} W now, average {Format.WholeWatts(Average, culture)} W, peak {Format.WholeWatts(Peak, culture)} W today."
            : "Meter: waiting for a reading.";
    }
```

In `src/PowerLedger.App/Controls/Sparkline.cs`, add before `MeasureOverride`:

```csharp
    internal override string Describe()
    {
        var values = Values;
        if (values.Count == 0) return "Last 60 seconds: no readings.";
        var culture = CultureInfo.CurrentCulture;
        return $"Last 60 seconds: between {Format.WholeWatts(values.Min(v => v.Watts), culture)} and {Format.WholeWatts(values.Max(v => v.Watts), culture)} W.";
    }
```

In `src/PowerLedger.App/Controls/BudgetBar.cs`, add before `MeasureOverride`:

```csharp
    internal override string Describe()
        => Rows.Count == 0 ? "Power budget: no reading." : "Power budget: " + string.Join(", ", Rows.Select(r => $"{r.Name} {r.Watts} ({r.Percent})")) + ".";
```

In `src/PowerLedger.App/Theme/Styles.xaml`, add after the `StackedChart` style:

```xml
    <Style TargetType="local:DailyBars" BasedOn="{StaticResource Instrument}" />
    <Style TargetType="local:QualityBar" BasedOn="{StaticResource Instrument}">
        <Setter Property="MeasuredBrush" Value="{DynamicResource Brush.Measured}" />
        <Setter Property="CalibratedBrush" Value="{DynamicResource Brush.Calibrated}" />
        <Setter Property="EstimatedBrush" Value="{DynamicResource Brush.Estimated}" />
    </Style>
```

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter "DescriptionTests|GeometryTests"`
Expected: `Passed! - Failed: 0, Passed: 15`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.App tests/PowerLedger.App.Tests
git commit -m "Add the daily bars and the quality bar, and let every drawn control describe itself"
```

---

### Task 12: The Report screen

**Files:**
- Create: `src/PowerLedger.App/Report/ReportView.xaml`, `src/PowerLedger.App/Report/ReportView.xaml.cs`
- Modify: `src/PowerLedger.App/Shell/ShellViewModel.cs`, `src/PowerLedger.App/Shell/MainWindow.xaml`
- Modify: `src/PowerLedger.App/App.xaml.cs`
- Test: `tests/PowerLedger.App.Tests/ShellViewModelTests.cs`
- Modify: `tests/PowerLedger.App.Tests/BreakdownViewModelTests.cs`, `tests/PowerLedger.App.Tests/RenderingTests.cs`

The Report screen reads like a statement. Under the shared range bar and the export buttons come:
- The bill and time ledgers side by side.
- The components beside the everyday equivalents.
- The idle waste with its suggestion, beside the quality mix.
- The daily bars.

The PNG button's handler draws that sheet at the screen's resolution and hands the stream to `SaveImage`. The shell now knows three screens, and it shows and hides the report the way it does the breakdown. Settings stays a placeholder until D3.

- [x] **Step 1: Write the failing test**

`tests/PowerLedger.App.Tests/ShellViewModelTests.cs`
```csharp
using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.App.Tests;

public class ShellViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeRangeHistory _history = new();

    private ShellViewModel Shell() => new(
        new NowViewModel(new FakeLink(), new FakeHistory(), UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, 0.4, () => { }),
        new BreakdownViewModel(_history, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English),
        new ReportViewModel(_history, new FakeSleep(), new FakeSaver(), _ => [], UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, 0.4),
        "0.1.0");

    [Fact]
    public void A_history_screen_reads_only_while_it_shows()
    {
        var shell = Shell();
        shell.Page = Page.Breakdown;
        shell.Current.ShouldBe(shell.Breakdown);
        _history.Reads.Single().Title.ShouldBe("Today");

        shell.Page = Page.Report;
        shell.Current.ShouldBe(shell.Report);
        _history.Reads[^1].Title.ShouldBe("September 2026");

        shell.Page = Page.Now;
        _clock.Advance(BreakdownViewModel.RefreshEvery * 3);
        _history.Reads.Count.ShouldBe(2);
    }

    [Fact]
    public void Settings_is_still_to_come()
    {
        var shell = Shell();
        shell.Page = Page.Settings;
        shell.Current.ShouldBeOfType<PlaceholderViewModel>();
    }
}
```

In `tests/PowerLedger.App.Tests/BreakdownViewModelTests.cs`, delete `The_shell_reads_the_breakdown_only_while_it_shows`; `ShellViewModelTests` covers it now.

- [x] **Step 2: Run it to verify it fails**

Run: `dotnet build tests/PowerLedger.App.Tests`
Expected: build error, `ShellViewModel` takes three arguments.

- [x] **Step 3: Write the screen**

`src/PowerLedger.App/Report/ReportView.xaml`
```xml
<UserControl x:Class="PowerLedger.App.ReportView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:local="clr-namespace:PowerLedger.App">
    <ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled" Focusable="False">
        <StackPanel Margin="26,22,26,18">
            <DockPanel>
                <StackPanel DockPanel.Dock="Right" Orientation="Horizontal" VerticalAlignment="Top" Margin="16,0,0,0">
                    <StackPanel.Resources>
                        <Style TargetType="Button" BasedOn="{StaticResource Quiet}">
                            <Setter Property="Margin" Value="0,0,6,6" />
                        </Style>
                    </StackPanel.Resources>
                    <Button Content="PDF" Command="{Binding ExportPdf}" ToolTip="Save this report as a PDF" />
                    <Button Content="PNG" Click="SavePicture" ToolTip="Save a picture of this report" />
                    <Button Content="CSV · 1 h" Command="{Binding ExportCsv}" CommandParameter="Hour" ToolTip="Hour rows for this range" />
                    <Button Content="CSV · 1 min" Command="{Binding ExportCsv}" CommandParameter="Minute" ToolTip="Minute rows for this range" />
                    <Button Content="CSV · raw" Command="{Binding ExportCsv}" CommandParameter="Raw" ToolTip="Every reading; raw readings are kept for the last two days" Margin="0,0,0,6" />
                </StackPanel>
                <local:RangeBar DataContext="{Binding Range}" />
            </DockPanel>
            <TextBlock Style="{StaticResource Text.Muted}" HorizontalAlignment="Right" Text="{Binding Saved}" />

            <StackPanel x:Name="Sheet" Background="{DynamicResource Brush.Panel}" Margin="0,12,0,0">
                <DockPanel>
                    <TextBlock DockPanel.Dock="Right" Style="{StaticResource Text.Eyebrow}" Text="{Binding Data.Period, Converter={StaticResource Upper}}" />
                    <TextBlock Style="{StaticResource Text.Eyebrow}" Text="{Binding Data.Title, Converter={StaticResource Upper}, StringFormat=REPORT · {0}}" />
                </DockPanel>
                <TextBlock Style="{StaticResource Text.Muted}" Margin="0,10,0,0" Text="{Binding Message}"
                           Visibility="{Binding HasMessage, Converter={StaticResource VisibleWhen}}" />

                <Grid Margin="0,12,0,0">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*" />
                        <ColumnDefinition Width="34" />
                        <ColumnDefinition Width="*" />
                    </Grid.ColumnDefinitions>
                    <StackPanel Grid.Column="0">
                        <Border Style="{StaticResource LedgerHead}"><TextBlock Style="{StaticResource Text.Eyebrow}" Text="BILL" /></Border>
                        <local:LedgerRow IsHero="True" Key="Energy" Value="{Binding Data.Energy}" Note="kWh" />
                        <local:LedgerRow Key="Cost" Value="{Binding Data.Cost}" Note="{Binding Data.CostNote}" />
                        <local:LedgerRow Key="CO₂" Value="{Binding Data.Co2}" Note="{Binding Data.Co2Note}" />
                        <local:LedgerRow Key="Average" Value="{Binding Data.Average}" Note="W while on" />
                        <local:LedgerRow Key="Peak" Value="{Binding Data.Peak}" Note="{Binding Data.PeakAt}" />
                    </StackPanel>
                    <StackPanel Grid.Column="2">
                        <Border Style="{StaticResource LedgerHead}"><TextBlock Style="{StaticResource Text.Eyebrow}" Text="TIME" /></Border>
                        <local:LedgerRow Key="On" Value="{Binding Data.On}" />
                        <local:LedgerRow Key="Idle, display on" Value="{Binding Data.IdleOn}" />
                        <local:LedgerRow Key="Idle, display off" Value="{Binding Data.IdleOff}" />
                        <local:LedgerRow Key="Asleep" Value="{Binding Data.Asleep}" Note="while the service ran" />
                        <local:LedgerRow Key="Unmonitored" Value="{Binding Data.Unmonitored}" Note="no readings at all" />
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
                                <TextBlock DockPanel.Dock="Right" Style="{StaticResource Text.Eyebrow}" Text="KWH · SHARE" />
                                <TextBlock Style="{StaticResource Text.Eyebrow}" Text="BY COMPONENT" />
                            </DockPanel>
                        </Border>
                        <ItemsControl ItemsSource="{Binding Data.Parts}" Focusable="False">
                            <ItemsControl.ItemTemplate>
                                <DataTemplate DataType="{x:Type local:PartRow}">
                                    <Border BorderBrush="{DynamicResource Brush.Line}" BorderThickness="0,0,0,1" Padding="0,7">
                                        <Grid>
                                            <Grid.ColumnDefinitions>
                                                <ColumnDefinition Width="20" />
                                                <ColumnDefinition Width="*" />
                                                <ColumnDefinition Width="Auto" />
                                                <ColumnDefinition Width="48" />
                                            </Grid.ColumnDefinitions>
                                            <Rectangle x:Name="Swatch" Width="10" Height="10" RadiusX="1" RadiusY="1" HorizontalAlignment="Left" VerticalAlignment="Center"
                                                       Fill="{DynamicResource Brush.PartRest}" />
                                            <TextBlock Grid.Column="1" Style="{StaticResource Text.Secondary}" Text="{Binding Name}" />
                                            <TextBlock Grid.Column="2" Style="{StaticResource Text.Number}" Text="{Binding Energy}" />
                                            <TextBlock Grid.Column="3" Style="{StaticResource Text.Number}" Foreground="{DynamicResource Brush.Ink3}" Text="{Binding Share}"
                                                       HorizontalAlignment="Right" />
                                        </Grid>
                                    </Border>
                                    <DataTemplate.Triggers>
                                        <DataTrigger Binding="{Binding Part}" Value="Cpu"><Setter TargetName="Swatch" Property="Fill" Value="{DynamicResource Brush.PartCpu}" /></DataTrigger>
                                        <DataTrigger Binding="{Binding Part}" Value="Gpu"><Setter TargetName="Swatch" Property="Fill" Value="{DynamicResource Brush.PartGpu}" /></DataTrigger>
                                        <DataTrigger Binding="{Binding Part}" Value="Display"><Setter TargetName="Swatch" Property="Fill" Value="{DynamicResource Brush.PartDisplay}" /></DataTrigger>
                                    </DataTemplate.Triggers>
                                </DataTemplate>
                            </ItemsControl.ItemTemplate>
                        </ItemsControl>
                    </StackPanel>
                    <StackPanel Grid.Column="2">
                        <Border Style="{StaticResource LedgerHead}"><TextBlock Style="{StaticResource Text.Eyebrow}" Text="EVERYDAY EQUIVALENTS" /></Border>
                        <ItemsControl ItemsSource="{Binding Data.Equivalents}" Focusable="False">
                            <ItemsControl.ItemTemplate>
                                <DataTemplate DataType="{x:Type local:ReportLine}">
                                    <local:LedgerRow Key="{Binding Name}" Value="{Binding Value}" Note="{Binding Note}" />
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
                        <Border Style="{StaticResource LedgerHead}"><TextBlock Style="{StaticResource Text.Eyebrow}" Text="IDLE WASTE" /></Border>
                        <local:LedgerRow Key="Energy while idle" Value="{Binding Data.IdleWaste}" Note="{Binding Data.IdleWasteNote}" />
                        <TextBlock Style="{StaticResource Text.Secondary}" FontSize="12.5" TextWrapping="Wrap" Margin="0,6,0,0" Text="{Binding Data.Advice}" />
                    </StackPanel>
                    <StackPanel Grid.Column="2">
                        <Border Style="{StaticResource LedgerHead}"><TextBlock Style="{StaticResource Text.Eyebrow}" Text="DATA QUALITY" /></Border>
                        <local:QualityBar Margin="0,14,0,8" Mix="{Binding Data.Quality}" />
                        <TextBlock Style="{StaticResource Text.Secondary}" FontSize="12.5" Text="{Binding Data.QualityText}" />
                        <TextBlock Style="{StaticResource Text.Muted}" TextWrapping="Wrap" Margin="0,6,0,0"
                                   Text="Measured: Windows' battery report. Calibrated: a model with a baseline learned on battery, ±10%. Estimated: the model alone, ±20%." />
                    </StackPanel>
                </Grid>

                <DockPanel Margin="0,26,0,8">
                    <TextBlock DockPanel.Dock="Right" Style="{StaticResource Text.Eyebrow}" Text="KWH" />
                    <TextBlock Style="{StaticResource Text.Eyebrow}" Text="DAILY ENERGY" />
                </DockPanel>
                <local:DailyBars Days="{Binding Data.Days}" />
            </StackPanel>
        </StackPanel>
    </ScrollViewer>
</UserControl>
```

`src/PowerLedger.App/Report/ReportView.xaml.cs`
```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PowerLedger.App;

/// <summary>The Report screen's layout (spec §9); everything it shows comes from <see cref="ReportViewModel"/>.</summary>
public partial class ReportView : UserControl
{
    public ReportView() => InitializeComponent();

    /// <summary>"PNG": the report's sheet as it stands, with a margin, at the screen's resolution. The view model asks where to save it.</summary>
    private void SavePicture(object sender, RoutedEventArgs e)
    {
        const double margin = 24;
        if (DataContext is not ReportViewModel model || Sheet.ActualWidth < 1 || Sheet.ActualHeight < 1) return;
        var sheet = new Rect(margin, margin, Sheet.ActualWidth, Sheet.ActualHeight);
        var page = new Rect(0, 0, sheet.Width + 2 * margin, sheet.Height + 2 * margin);
        var dpi = VisualTreeHelper.GetDpi(Sheet);
        var picture = new DrawingVisual();
        using (var dc = picture.RenderOpen())
        {
            dc.DrawRectangle((Brush)FindResource("Brush.Panel"), null, page);
            // The brush maps the sheet's own bounds, which its background fills, so its place on screen doesn't shift it.
            dc.DrawRectangle(new VisualBrush(Sheet), null, sheet);
        }
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(page.Width * dpi.DpiScaleX), (int)Math.Ceiling(page.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(picture);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        model.SaveImage(png.Save);
    }
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

/// <summary>A screen a later build brings; until then it says so.</summary>
internal sealed record PlaceholderViewModel(string Title, string Text);

/// <summary>The window: which page shows, the screens, and the version in the title bar.</summary>
internal sealed class ShellViewModel(NowViewModel now, BreakdownViewModel breakdown, ReportViewModel report, string version) : ObservableObject
{
    private static readonly PlaceholderViewModel Settings = new("Settings", "Tariff, machine profile and preferences arrive in the next build.");

    private Page _page = Page.Now;

    public NowViewModel Now { get; } = now;

    public BreakdownViewModel Breakdown { get; } = breakdown;

    public ReportViewModel Report { get; } = report;

    public string Version { get; } = version;

    /// <summary>The page shown. A history screen reads while it shows and stops when it does not.</summary>
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

In `src/PowerLedger.App/Shell/MainWindow.xaml`, add after the `BreakdownViewModel` data template:

```xml
        <DataTemplate DataType="{x:Type local:ReportViewModel}">
            <local:ReportView />
        </DataTemplate>
```

In `src/PowerLedger.App/App.xaml.cs`, add the field `private ReportViewModel? _report;`, replace `_shell = new ShellViewModel(_now, _breakdown, Version());` with:

```csharp
        var version = Version();
        byte[] Pdf(ReportData data) => ReportDocument.Generate(data, version, DateTimeOffset.Now, CultureInfo.CurrentCulture);
        _report = new ReportViewModel(
            history, new SleepSettings(), new FileSaver(), Pdf, threads, TimeProvider.System, TimeZoneInfo.Local, CultureInfo.CurrentCulture,
            preferences.Co2KgPerKwh);
        _shell = new ShellViewModel(_now, _breakdown, _report, version);
```

and add `_report?.Dispose();` after `_breakdown?.Dispose();` in `ExitUi`.

In `tests/PowerLedger.App.Tests/RenderingTests.cs`, replace `Pages` with:

```csharp
    private static readonly (Page Page, string Name, Action<ShellViewModel> Prepare, Func<ShellViewModel, FrameworkElement> View)[] Pages =
    [
        (Page.Now, "now", _ => { }, shell => new NowView { DataContext = shell.Now }),
        (Page.Breakdown, "breakdown", shell => shell.Breakdown.Range.Choice = RangeChoice.SevenDays, shell => new BreakdownView { DataContext = shell.Breakdown }),
        (Page.Breakdown, "custom", shell => shell.Breakdown.Range.Choice = RangeChoice.Custom, shell => new BreakdownView { DataContext = shell.Breakdown }),
        (Page.Report, "report", _ => { }, shell => new ReportView { DataContext = shell.Report }),
    ];
```

change the two loops over `Pages` to deconstruct four elements, `foreach (var (_, name, _, _) in Pages)` and `foreach (var (page, name, prepare, view) in Pages)`, and call `prepare(shell);` before `shell.Page = page;`. Change the shell to `new ShellViewModel(NowScreen(), BreakdownScreen(), ReportScreen(), "0.1.0")`. Delete the line `model.Range.Choice = RangeChoice.SevenDays;` from `BreakdownScreen`. After the theme loop, write the PDF of the report shown, for a look:

```csharp
        File.WriteAllBytes(Path.Combine(Folder, "report.pdf"), ReportDocument.Generate(shell.Report.Data, "0.1.0", Now, English));
```

and add:

```csharp
    /// <summary>September so far on the same machine, with a tariff and a plan that sleeps after three hours.</summary>
    private static ReportViewModel ReportScreen()
    {
        var history = new FakeRangeHistory { Answer = Month };
        return new ReportViewModel(history, new FakeSleep(), new FakeSaver(), _ => [], UiThreads.Inline, new FakeTimeProvider(Now),
            TimeZoneInfo.Utc, English, 0.38);
    }

    private static RangeReport Month(DateRange range)
    {
        var report = Reports.Typical(range);
        var weights = report.Days.Select((_, i) => 0.7 + 0.15 * (i * 3 % 5)).ToList();
        var days = report.Days.Select((d, i) => d with { EnergyKwh = report.Totals.EnergyKwh * weights[i] / weights.Sum() }).ToList();
        return report with { Days = days };
    }
```

- [x] **Step 4: Run tests, and look**

Run: `dotnet test tests/PowerLedger.App.Tests --filter "Category!=UI"`
Expected: all pass.

Run: `dotnet test tests/PowerLedger.App.Tests --filter "Category=UI"` and open `report-Dark.png`, `report-Light-full.png`, `custom-Dark.png` and `report.pdf` in `%TEMP%\powerledger-renders`.
Expected: the report screen shows September's bill and time side by side, the components beside the equivalents, the idle waste with the three-hour suggestion beside the quality bar, and eight amber daily bars in a thirty-day frame. With Custom chosen, two date pickers in the palette follow the range buttons. The PDF is one A4 page with the same figures.

- [x] **Step 5: Commit**

```bash
git add -A src/PowerLedger.App tests/PowerLedger.App.Tests
git commit -m "Add the Report screen: the bill, time, components, equivalents, idle waste, quality and daily bars, with exports"
```

---

### Task 13: The monthly report

**Files:**
- Create: `src/PowerLedger.App/Report/MonthlyReports.cs`
- Modify: `src/PowerLedger.App/Tray/TrayIcon.cs`
- Modify: `src/PowerLedger.App/App.xaml.cs`
- Test: `tests/PowerLedger.App.Tests/MonthlyReportsTests.cs`

Spec §9: "The App checks at startup and once per hour while running." The first check comes 30 seconds after startup, so it never slows the window's first paint.

A month is due when:
- it has ended,
- it falls in the last twelve months,
- it is no earlier than the month history began in, and
- `Documents\PowerLedger\PowerLedger-YYYY-MM.pdf` does not exist yet.

Each due month is read, written out with `ReportData.From` and drawn with the same PDF the Report screen exports. The file is written beside its target and moved into place.

A month with no readings at all is skipped. If history can't be read, the check stops and tries again next hour, and so does a failed write. The tray shows a notification with the newest month's headline numbers, and clicking it opens that PDF. Checks run on the timer's thread and never overlap, and nothing escapes one: a timer thread has no one to tell.

- [x] **Step 1: Write the failing tests**

`tests/PowerLedger.App.Tests/MonthlyReportsTests.cs`
```csharp
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class MonthlyReportsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"powerledger-monthly-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeRangeHistory _history = new() { First = new DateOnly(2026, 6, 15) };
    private readonly List<IReadOnlyList<MonthlyReport>> _toasts = [];

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private MonthlyReports Job() => new(
        _history, new FakeSleep(), data => Encoding.UTF8.GetBytes(data.Title), _folder, _clock, TimeZoneInfo.Utc, English, 0.38, _toasts.Add);

    [Fact]
    public void Finished_months_since_history_began_are_due_newest_first()
        => MonthlyReports.Due(new DateOnly(2026, 6, 15), new DateOnly(2026, 9, 8), _ => false)
            .ShouldBe(new[] { new DateOnly(2026, 8, 1), new DateOnly(2026, 7, 1), new DateOnly(2026, 6, 1) });

    [Fact]
    public void A_month_with_its_pdf_is_not_due_and_nothing_is_due_without_history()
    {
        MonthlyReports.Due(new DateOnly(2026, 6, 15), new DateOnly(2026, 9, 8), month => month.Month == 7)
            .ShouldBe(new[] { new DateOnly(2026, 8, 1), new DateOnly(2026, 6, 1) });
        MonthlyReports.Due(null, new DateOnly(2026, 9, 8), _ => false).ShouldBeEmpty();
        MonthlyReports.Due(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 8), _ => false).ShouldBeEmpty();
    }

    [Fact]
    public void Only_the_last_twelve_months_are_looked_at()
    {
        var due = MonthlyReports.Due(new DateOnly(2024, 1, 10), new DateOnly(2026, 9, 8), _ => false);
        due.Count.ShouldBe(12);
        due[^1].ShouldBe(new DateOnly(2025, 9, 1));
    }

    [Fact]
    public void A_check_writes_each_due_month_and_the_tray_hears_of_them_once()
    {
        var written = Job().Check();

        written.Select(w => Path.GetFileName(w.Path)).ShouldBe(new[] { "PowerLedger-2026-08.pdf", "PowerLedger-2026-07.pdf", "PowerLedger-2026-06.pdf" });
        File.ReadAllText(Path.Combine(_folder, "PowerLedger-2026-08.pdf")).ShouldBe("August 2026");
        _toasts.Single().Count.ShouldBe(3);
        MonthlyReports.Toast(_toasts.Single()).Title.ShouldBe("3 monthly reports saved");
        Job().Check().ShouldBeEmpty();                                        // a second check finds them written
        _toasts.Count.ShouldBe(1);
    }

    [Fact]
    public void A_month_with_no_readings_is_skipped_and_unreadable_history_waits()
    {
        _history.Answer = range => range.Title == "July 2026" ? Reports.Empty(range) : Reports.Typical(range);
        Job().Check().Select(w => w.Data.Title).ShouldBe(new[] { "August 2026", "June 2026" });

        Directory.Delete(_folder, recursive: true);
        _history.Answer = _ => null;
        Job().Check().ShouldBeEmpty();
        Directory.Exists(_folder).ShouldBeFalse();
    }

    [Fact]
    public void The_toast_gives_the_newest_months_headline()
    {
        var data = ReportData.Empty with { Title = "August 2026", Energy = "27.4", Cost = "$4.66" };
        MonthlyReports.Toast([new MonthlyReport("a.pdf", data)])
            .ShouldBe(("August 2026 report saved", "August 2026: 27.4 kWh · $4.66. Saved to Documents\\PowerLedger; click to open it."));
        MonthlyReports.Toast([new MonthlyReport("a.pdf", data with { Cost = "–" })]).Text
            .ShouldBe("August 2026: 27.4 kWh. Saved to Documents\\PowerLedger; click to open it.");
    }

    [Fact]
    public void The_first_check_comes_soon_after_start_then_hourly()
    {
        using var job = Job();
        job.Start();
        _clock.Advance(MonthlyReports.FirstCheck);
        _toasts.Count.ShouldBe(1);

        File.Delete(Path.Combine(_folder, "PowerLedger-2026-08.pdf"));
        _clock.Advance(MonthlyReports.CheckEvery);
        _toasts.Count.ShouldBe(2);
    }
}
```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PowerLedger.App.Tests --filter MonthlyReportsTests`
Expected: build error, `MonthlyReports` not found.

- [x] **Step 3: Write the job, the notification and the wiring**

`src/PowerLedger.App/Report/MonthlyReports.cs`
```csharp
using System.Globalization;
using System.IO;

namespace PowerLedger.App;

/// <summary>A monthly report the job wrote.</summary>
internal sealed record MonthlyReport(string Path, ReportData Data);

/// <summary>
/// The monthly report (spec §9). Soon after startup and every hour, each finished month of the last twelve, from the month
/// history began in, gets a PDF in Documents\PowerLedger if it has none yet, and the tray says so. A month with no readings
/// at all is skipped. The App does this because the service runs as SYSTEM and has no Documents folder.
/// </summary>
internal sealed class MonthlyReports : IDisposable
{
    public static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);
    public const int MonthsBack = 12;

    private readonly IRangeHistory _history;
    private readonly ISleepSettings _sleep;
    private readonly Func<ReportData, byte[]> _pdf;
    private readonly string _folder;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly CultureInfo _culture;
    private readonly double _co2KgPerKwh;
    private readonly Action<IReadOnlyList<MonthlyReport>> _written;
    private readonly Lock _gate = new();
    private ITimer? _timer;

    public MonthlyReports(
        IRangeHistory history, ISleepSettings sleep, Func<ReportData, byte[]> pdf, string folder,
        TimeProvider clock, TimeZoneInfo zone, CultureInfo culture, double co2KgPerKwh, Action<IReadOnlyList<MonthlyReport>> written)
    {
        _history = history;
        _sleep = sleep;
        _pdf = pdf;
        _folder = folder;
        _clock = clock;
        _zone = zone;
        _culture = culture;
        _co2KgPerKwh = co2KgPerKwh;
        _written = written;
    }

    public static string DefaultFolder { get; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PowerLedger");

    public static string FileName(DateOnly month) => $"PowerLedger-{month.ToString("yyyy-MM", CultureInfo.InvariantCulture)}.pdf";

    /// <summary>Checks soon after startup and then every hour, on the timer's thread.</summary>
    public void Start() => _timer ??= _clock.CreateTimer(_ => CheckQuietly(), null, FirstCheck, CheckEvery);

    public void Dispose() => _timer?.Dispose();

    /// <summary>The first days of the months due, newest first: finished, in the last twelve, no earlier than the month
    /// history began in, and without a PDF.</summary>
    internal static IReadOnlyList<DateOnly> Due(DateOnly? firstDay, DateOnly today, Func<DateOnly, bool> exists)
    {
        if (firstDay is not { } first) return [];
        var start = new DateOnly(first.Year, first.Month, 1);
        var month = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        var due = new List<DateOnly>();
        for (var i = 0; i < MonthsBack && month >= start; i++, month = month.AddMonths(-1))
        {
            if (!exists(month)) due.Add(month);
        }
        return due;
    }

    /// <summary>What the tray says about reports just written: the newest month's headline numbers, and how many there were.</summary>
    internal static (string Title, string Text) Toast(IReadOnlyList<MonthlyReport> written)
    {
        var newest = written[0].Data;
        var title = written.Count == 1 ? $"{newest.Title} report saved" : $"{written.Count.ToString(CultureInfo.InvariantCulture)} monthly reports saved";
        var cost = newest.Cost == Format.Missing ? "" : " · " + newest.Cost;
        return (title, $"{newest.Title}: {newest.Energy} kWh{cost}. Saved to Documents\\PowerLedger; click to open it.");
    }

    /// <summary>Writes the reports that are due and returns them, newest first. A check already running makes this one a no-op.</summary>
    internal IReadOnlyList<MonthlyReport> Check()
    {
        if (!_gate.TryEnter()) return [];
        try
        {
            var now = _clock.GetUtcNow();
            var written = new List<MonthlyReport>();
            foreach (var month in Due(_history.FirstDay(_zone), Ranges.LocalDay(now, _zone), m => File.Exists(PathOf(m))))
            {
                var range = Ranges.Month(month.Year, month.Month, now, _zone, _culture);
                if (_history.Read(range, _zone) is not { } report) break;                   // history can't be read: next hour
                var data = ReportData.From(report, _sleep.Read(), _co2KgPerKwh, _zone, _culture);
                if (!data.HasData) continue;                                                // nothing was recorded that month
                var path = PathOf(month);
                try
                {
                    Directory.CreateDirectory(_folder);
                    File.WriteAllBytes(path + ".partial", _pdf(data));
                    File.Move(path + ".partial", path, overwrite: true);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    break;                                                                  // Documents can't be written, or the PDF failed: next hour
                }
                written.Add(new MonthlyReport(path, data));
            }
            if (written.Count > 0) _written(written);
            return written;
        }
        finally
        {
            _gate.Exit();
        }
    }

    private void CheckQuietly()
    {
        try
        {
            Check();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A timer thread has no one to tell; the next hour tries again.
        }
    }

    private string PathOf(DateOnly month) => System.IO.Path.Combine(_folder, FileName(month));
}
```

In `src/PowerLedger.App/Tray/TrayIcon.cs`, add `using System.Diagnostics;` and `using System.IO;`, add the field `private string? _open;`, add to the constructor after `_icon.DoubleClick += (_, _) => open();`:

```csharp
        _icon.BalloonTipClicked += (_, _) => Launch(_open);
```

and add before `Dispose`:

```csharp
    /// <summary>A notification from the tray, the monthly report's (spec §9). Clicking it opens <paramref name="open"/>.</summary>
    public void Notify(string title, string text, string? open)
    {
        if (_disposed) return;
        _open = open;
        _icon.ShowBalloonTip(10_000, title, text, ToolTipIcon.None);
    }

    private static void Launch(string? path)
    {
        if (path is null || !File.Exists(path)) return;
        try
        {
            using var viewer = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No app opens PDFs here; the file is still in Documents.
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
    private BreakdownViewModel? _breakdown;
    private ReportViewModel? _report;
    private MonthlyReports? _monthly;
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
        var zone = TimeZoneInfo.Local;
        var culture = CultureInfo.CurrentCulture;
        var version = Version();
        _theme = new ThemeManager(this, preferences.Theme);
        _database = new SqliteDatabase(options.DatabasePath, readOnly: true);
        _link = new PipeServiceLink(options.PipeName, new LastInputIdleSource(), TimeProvider.System);
        var threads = new UiThreads(action => Dispatcher.InvokeAsync(action), action => Task.Run(action));
        var history = new HistoryReader(_database);
        var sleep = new SleepSettings();
        byte[] Pdf(ReportData data) => ReportDocument.Generate(data, version, DateTimeOffset.Now, culture);

        _now = new NowViewModel(_link, history, threads, TimeProvider.System, zone, culture, preferences.Co2KgPerKwh, ServiceStarter.Start);
        _breakdown = new BreakdownViewModel(history, threads, TimeProvider.System, zone, culture);
        _report = new ReportViewModel(history, sleep, new FileSaver(), Pdf, threads, TimeProvider.System, zone, culture, preferences.Co2KgPerKwh);
        _shell = new ShellViewModel(_now, _breakdown, _report, version);
        _tray = new TrayIcon(ShowWindow, ExitUi, new StartWithWindows(Environment.ProcessPath!));
        _monthly = new MonthlyReports(
            history, sleep, Pdf, MonthlyReports.DefaultFolder, TimeProvider.System, zone, culture, preferences.Co2KgPerKwh,
            written => Dispatcher.InvokeAsync(() =>
            {
                var (title, text) = MonthlyReports.Toast(written);
                _tray?.Notify(title, text, written[0].Path);
            }));
        _now.PropertyChanged += OnNowChanged;
        _instance.OnShowRequested(() => Dispatcher.InvokeAsync(ShowWindow));

        _link.Start();
        _now.Start();
        _monthly.Start();
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

    /// <summary>Windows is signing out or shutting down: let the window close instead of hiding it.</summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _exiting = true;
        base.OnSessionEnding(e);
    }

    /// <summary>However the App ends, the tray icon goes with it rather than lingering until the mouse passes over it.</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }

    /// <summary>"Exit UI" in the tray menu. It is an event handler, so nothing may escape it: the App ends either way.</summary>
    private async void ExitUi()
    {
        _exiting = true;
        try
        {
            _monthly?.Dispose();
            _window?.Close();
            _tray?.Dispose();
            if (_now is not null)
            {
                _now.PropertyChanged -= OnNowChanged;
                _now.Dispose();
            }
            _breakdown?.Dispose();
            _report?.Dispose();
            if (_link is not null) await _link.DisposeAsync();
            _theme?.Dispose();
            _database?.Dispose();
            _instance?.Dispose();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Ending anyway; nothing left to tell.
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

- [x] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PowerLedger.App.Tests --filter MonthlyReportsTests`
Expected: `Passed! - Failed: 0, Passed: 7`.

- [x] **Step 5: Commit**

```bash
git add src/PowerLedger.App tests/PowerLedger.App.Tests/MonthlyReportsTests.cs
git commit -m "Write each finished month's PDF to Documents and say so from the tray"
```

---

### Task 14: See it, and finish

**Files:**
- Modify: `tests/PowerLedger.App.Tests/RenderingTests.cs`
- Modify: `docs/superpowers/specs/2026-09-08-powerledger-design.md`
- Modify: this plan

The unit tests cover what each piece decides. What they cannot show is how the screens look, and whether the PDF and the PNG come out right. Two parts of this task get at that. The rendering test gains a press of the Report screen's PNG button, which runs the view's picture code through the real `ReportViewModel`, with a fake Save dialog answering. A real run then puts the App against the development service on this laptop.

- [x] **Step 1: Press PNG in the drawn report**

In `tests/PowerLedger.App.Tests/RenderingTests.cs`, add `using System.Windows.Controls;` and `using System.Windows.Controls.Primitives;`, and change `new System.Windows.Controls.Border` to `new Border`. Give `ReportScreen` the saver:

```csharp
    private static ReportViewModel ReportScreen(FakeSaver saver)
    {
        var history = new FakeRangeHistory { Answer = Month };
        return new ReportViewModel(history, new FakeSleep(), saver, _ => [], UiThreads.Inline, new FakeTimeProvider(Now),
            TimeZoneInfo.Utc, English, 0.38);
    }
```

In `Render`, make the shell with `using var saver = new FakeSaver();` and `ReportScreen(saver)`. After a page's window is saved and before it closes, press PNG on the report:

```csharp
                if (page == Page.Report && FindButton(window, "PNG") is { } picture)
                {
                    picture.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));   // the view draws the report into the file the saver names
                    File.Copy(saver.Chosen, Path.Combine(Folder, "report-picture.png"), overwrite: true);
                }
```

and add:

```csharp
    private static Button? FindButton(DependencyObject root, string content)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button && Equals(button.Content, content)) return button;
            if (FindButton(child, content) is { } found) return found;
        }
        return null;
    }
```

In the test method, before the loop over the pages' files, check the picture:

```csharp
        new FileInfo(Path.Combine(Folder, "report-picture.png")).Length.ShouldBeGreaterThan(30_000);
```

- [x] **Step 2: Draw everything, and look**

Run: `dotnet test tests/PowerLedger.App.Tests --filter "Category=UI"`
Expected: pass, with these in `%TEMP%\powerledger-renders`:
- `now`, `breakdown`, `custom` and `report` in both themes, each as the window and at full length.
- `report-picture.png`.
- `report.pdf`.

Look at each one. A PDF can be seen as a picture with Windows' own PDF renderer, `Windows.Data.Pdf`, from a scratch console project that targets `net10.0-windows10.0.19041.0`; the repository does not need it.

Two problems showed up here, and the code in Tasks 9 and 12 already carries the fixes:

| Seen | Fix |
|---|---|
| The PDF was set in QuestPDF's bundled Lato, not Segoe UI. QuestPDF 2026.9.0 stopped using system fonts by default, and Lato has no Devanagari or CJK, so a report in Hindi would have drawn empty boxes. | `ReportDocument` turns system fonts back on. A probe with QuestPDF's strict glyph checks on drew English, Hindi, Chinese, Japanese and Korean month names. |
| The PNG was pushed down by the sheet's place on screen and cut off at the bottom. A `VisualBrush` given an absolute viewbox counts the element's offset. | The brush maps the sheet's own bounds, which its background fills, and the picture gets a 24 px margin. |

- [x] **Step 3: A real run**

Start the development service and the App against it:

```bash
src/PowerLedger.Service/bin/Release/net10.0-windows/PowerLedger.Service.exe --data <scratch>/pl-run --pipe PowerLedger.dev
```

```bash
src/PowerLedger.App/bin/Release/net10.0-windows/PowerLedger.exe --pipe PowerLedger.dev --data <scratch>/pl-run
```

Switch pages with UI Automation, selecting the rail's radio buttons by name. Read the drawn controls' names from the same tree, and capture the window each time.

Expected:
- The meter, readout, sparkline, budget bar and chart announce "31.7 watts", "Meter: 31.7 W now, average 21 W, peak 36 W today." and so on.
- Breakdown shows today's readings from the database.
- Report shows September so far, with "no tariff set", 100% estimated and a daily bar on the 15th.
- The private working set is about 76 MB with the Report screen open, against D1's 71 MB with the Now screen.
- The monthly job writes nothing, because no finished month has readings yet.

The PDF button opened Windows' Save As dialog over the window. Windows' foreground lock kept the automation from typing into it, and the guarded script typed nothing. Saving to a file stays covered by the view model's tests and by the PNG press in Step 1.

- [x] **Step 4: Verify everything**

Run: `dotnet build -c Release`, then `dotnet test` for each test project, with and without `--filter "Category!=Hardware&Category!=UI"`.
Expected: 0 warnings, and:

| Project | All | CI filter |
|---|---|---|
| Core | 110 | 110 |
| Storage | 47 | 47 |
| Sensors | 99 | 94 |
| Service | 127 | 125 |
| App | 155 | 153 |

- [x] **Step 5: Update the spec**

In `docs/superpowers/specs/2026-09-08-powerledger-design.md`:
- §7: `GetSeries` is `ReportQueries.Series`, and short ranges read minutes throughout.
- §9: the Breakdown's ranges, buckets and chart.
- §9: the Report's exports, their names, the PDF's fonts, and where the suggestion's timeouts come from.
- §9: the monthly job's first check, its twelve-month look-back, its skips and waits, and its notification.
- §12: the App's tests.
- §13: publish for win-x64.

- [x] **Step 6: Commit**

```bash
git add docs tests/PowerLedger.App.Tests/RenderingTests.cs tests/PowerLedger.App.Tests/BreakdownViewModelTests.cs src/PowerLedger.App/Report/ReportView.xaml.cs
git commit -m "Complete Plan D2: Breakdown, Report, exports and the monthly PDF"
```

**Rules D3 must follow.** These are contracts the types cannot enforce:

- **The server check before writing.** Plan C's rule, carried over from D1: check the server before sending settings, tariffs or a calibration reset. Take `GetNamedPipeServerProcessId` on the pipe's handle; the process's image must be the installed service's. A development run skips the check only when started with `--pipe`.
- **The CO₂ factor at run time.** `NowViewModel`, `ReportViewModel` and `MonthlyReports` each take the factor at construction. D3 gives them one source that can change, and saves `ui.json` through `UiPreferencesStore`. The theme changes through `ThemeManager`.
- **A new tariff shows on the next refresh.** Cost is priced at query time (spec §7), so D3 need not touch history. After `SetTariff`, call `Refresh()` on the screen showing, or wait out the minute.
- **The Settings page.** It replaces `ShellViewModel`'s last placeholder. A screen that reads history uses `Show()` and `Hide()` like the others, so it reads only while someone is looking.
- **The existing patterns.** View models stay free of WPF types and reach the UI thread only through `UiThreads`. A file dialog goes behind an interface like `IFileSaver`. Views use `DynamicResource` for palette brushes; choices use the `Segment` style, and ranges use `RangeBar`. A new drawn control overrides `Describe()`.

**Known gaps, left for D3 or later:**

- The drop-down calendar of a custom range keeps Windows' light look in the dark theme.
- The bundled fonts are not in the repository yet; downloading them needs the owner's go-ahead. The PDF uses Windows' fonts either way.
- A change to the CO₂ factor reaches the monthly reports only after the App restarts, until D3 gives the factor a live source.
- Raw CSV holds only what raw retention keeps: 48 hours by default.
- A platform-neutral build carries 116 MB of native libraries for eight platforms. Plan E publishes for win-x64.
- The saving suggestion quotes the plugged-in timeouts only.

---

## Self-review against the spec

| Spec | Where |
|---|---|
| §9 Breakdown: stacked CPU, GPU, display and rest over today, 7 d, 30 d and custom; W↔Wh; the table; the negative-rest footnote | Tasks 4, 5, 6 |
| §9 Report: kWh, cost, CO₂, average and peak; on, idle, asleep and unmonitored; idle waste and suggestion; comparisons; quality mix; daily bars | Tasks 7, 8, 11, 12 |
| §9 Exports: PDF, CSV at raw, 1 minute and 1 hour, PNG | Tasks 3, 9, 10, 12, 14 |
| §9 Monthly report: startup and hourly, `Documents\PowerLedger\PowerLedger-YYYY-MM.pdf`, a notification with the headline numbers | Task 13 |
| §6 The suggestion reads Windows' sleep timeout, "30 min" or "never" | Task 7 |
| §7 `GetSeries` and `GetDailyBuckets` | Tasks 1, 3 |
| §12 App tests | Tasks 2 to 14 |
| D1's rules: WPF-free view models, `DynamicResource`, the CO₂ factor from `ui.json`, names for the drawn controls | Tasks 4, 5, 10, 11 |
