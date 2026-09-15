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
