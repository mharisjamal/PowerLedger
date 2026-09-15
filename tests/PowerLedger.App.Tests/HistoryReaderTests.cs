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
