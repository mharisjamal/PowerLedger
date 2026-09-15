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
internal sealed class HistoryReader(SqliteDatabase database) : IHistory, IRangeHistory
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
