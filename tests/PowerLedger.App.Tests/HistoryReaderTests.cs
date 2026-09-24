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
        snapshot.TodayRange.From.ShouldBe(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        snapshot.TodaySeries.Count.ShouldBe(174);                             // 14.5 hours of five-minute buckets
        snapshot.TodaySeries[168].CpuWh.ShouldBe(0.4, 1e-9);                  // 14:00: two minutes of 0.2 Wh of CPU
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
        reader.FirstRow().ShouldBeNull();
    }

    /// <summary>The Dashboard's 1H pill (Midnight look design §4): a minute-bucket range reads the minute rows, one to a bucket.</summary>
    [Fact]
    public void A_minute_bucket_read_returns_the_minute_rows_one_to_a_bucket()
    {
        var minutes = new AggregateRepository(_writer);
        minutes.UpsertMinute(Minute(Now.AddMinutes(-30), wh: 0.5));
        minutes.UpsertMinute(Minute(Now.AddMinutes(-29), wh: 0.7));
        minutes.UpsertMinute(Minute(Now.AddMinutes(-2), wh: 0.9));

        using var readOnly = new SqliteDatabase(_path, readOnly: true);
        var range = Ranges.LastHour(Now, TimeZoneInfo.Utc, System.Globalization.CultureInfo.InvariantCulture);
        var report = new HistoryReader(readOnly).Read(range, TimeZoneInfo.Utc).ShouldNotBeNull();

        report.Series.Count.ShouldBe(59);                 // 13:31 up to now, on the minute; the sixtieth bucket is the one about to start
        report.Series[29].EnergyWh.ShouldBe(0.5, 1e-9);   // 14:00, 30 minutes before now, is the 30th bucket from 13:31
        report.Series[30].EnergyWh.ShouldBe(0.7, 1e-9);
        report.Series[57].EnergyWh.ShouldBe(0.9, 1e-9);
        report.Series[57].AvgW.ShouldBe(54, 1e-9);        // 0.9 Wh over a minute on
        report.Series.Count(b => b.OnSeconds > 0).ShouldBe(3);
        report.Totals.EnergyKwh.ShouldBe(0.0021, 1e-12);
    }

    /// <summary>The 1Y and All pills: a day-bucket range sums each day's hour rows into its bucket, and All starts at the first row.</summary>
    [Fact]
    public void A_day_bucket_read_sums_each_day_and_all_starts_at_the_first_row()
    {
        var aggregates = new AggregateRepository(_writer);
        var first = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);
        foreach (var (start, wh) in new[] { (first, 30.0), (first.AddHours(1), 20.0), (first.AddDays(2), 45.0), (Now.AddMinutes(-90), 12.0) })
        {
            var minute = Minute(start, wh);
            aggregates.UpsertMinute(minute);
            aggregates.UpsertHour(Downsampler.ToHour(start, [minute]));
        }

        using var readOnly = new SqliteDatabase(_path, readOnly: true);
        var reader = new HistoryReader(readOnly);
        reader.FirstRow().ShouldBe(first);
        var range = Ranges.All(reader.FirstRow(), Now, TimeZoneInfo.Utc, System.Globalization.CultureInfo.InvariantCulture);
        var report = reader.Read(range, TimeZoneInfo.Utc).ShouldNotBeNull();

        range.From.ShouldBe(new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero));
        report.Series.Count.ShouldBe(13);                                    // 3 to 15 September, a bucket a day
        report.Series[0].EnergyWh.ShouldBe(50, 1e-9);                        // the 3rd: two hours summed
        report.Series[2].EnergyWh.ShouldBe(45, 1e-9);                        // the 5th
        report.Series[12].EnergyWh.ShouldBe(12, 1e-9);                       // today
        report.Series.Sum(b => b.EnergyWh).ShouldBe(107, 1e-9);
        report.Days.Count.ShouldBe(3);
        report.Totals.EnergyKwh.ShouldBe(0.107, 1e-12);
    }

    /// <summary>Review round: across London's clock change, a day-bucket read starts each bucket at its day's midnight,
    /// so a bucket is named for its own day and an evening's row stays in its day, as the daily totals have it.</summary>
    [Fact]
    public void A_day_bucket_read_across_a_clock_change_cuts_at_local_midnights()
    {
        var london = TimeZoneInfo.TryFindSystemTimeZoneById("GMT Standard Time", out var windows) ? windows : TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var now = new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
        var aggregates = new AggregateRepository(_writer);
        var first = new DateTimeOffset(2026, 10, 20, 11, 0, 0, TimeSpan.Zero);
        var saturdayEvening = new DateTimeOffset(2026, 11, 7, 23, 0, 0, TimeSpan.Zero);   // 23:00 on Saturday 7 November, winter time
        foreach (var (start, wh) in new[] { (first, 30.0), (saturdayEvening, 20.0) })
        {
            var minute = Minute(start, wh);
            aggregates.UpsertMinute(minute);
            aggregates.UpsertHour(Downsampler.ToHour(start, [minute]));
        }

        using var readOnly = new SqliteDatabase(_path, readOnly: true);
        var reader = new HistoryReader(readOnly);
        var range = Ranges.All(reader.FirstRow(), now, london, System.Globalization.CultureInfo.InvariantCulture);
        var report = reader.Read(range, london).ShouldNotBeNull();

        report.Series.Count.ShouldBe(20);   // 20 October to 8 November
        report.Series.ShouldAllBe(b => TimeZoneInfo.ConvertTime(b.Start, london).TimeOfDay == TimeSpan.Zero);
        var saturday = report.Series[18];   // the 19th day from 20 October
        saturday.EnergyWh.ShouldBe(20, 1e-9);
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(saturday.Start, london).DateTime).ShouldBe(new DateOnly(2026, 11, 7));
        report.Days.Last().Day.ShouldBe(new DateOnly(2026, 11, 7));
    }
}
