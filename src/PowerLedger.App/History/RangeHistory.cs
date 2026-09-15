using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>A range's totals, days and chart buckets, from one set of reads so they agree, and the tariff in force at its end.</summary>
internal sealed record RangeReport(
    DateRange Range, RangeTotals Totals, IReadOnlyList<DayTotals> Days, IReadOnlyList<Aggregate> Series, Tariff? Tariff = null);

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
