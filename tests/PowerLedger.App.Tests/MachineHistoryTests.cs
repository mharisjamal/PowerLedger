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
