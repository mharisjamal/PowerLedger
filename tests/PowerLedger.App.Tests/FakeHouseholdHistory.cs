using PowerLedger.Storage;

namespace PowerLedger.App.Tests;

/// <summary>History for the Household page: answers with <see cref="Answer"/> and records when it was asked.</summary>
internal sealed class FakeHouseholdHistory : IHouseholdHistory
{
    public static readonly HouseholdSnapshot Empty = new(
        new HouseholdRangeTotals(0, [], []), new HouseholdRangeTotals(0, [], []), new HouseholdRangeTotals(0, [], []), []);

    public List<DateTimeOffset> Reads { get; } = [];

    public Func<DateTimeOffset, HouseholdSnapshot?> Answer { get; set; } = _ => Empty;

    public HouseholdSnapshot? Read(DateTimeOffset now, TimeZoneInfo zone)
    {
        Reads.Add(now);
        return Answer(now);
    }

    /// <summary>Every range the report asked for.</summary>
    public List<DateRange> ReportReads { get; } = [];

    public Func<DateRange, HouseholdReportSnapshot?> ReportAnswer { get; set; } = _ => new HouseholdReportSnapshot(new HouseholdRangeTotals(0, [], []), [], []);

    public HouseholdReportSnapshot? ReadReport(DateRange range)
    {
        ReportReads.Add(range);
        return ReportAnswer(range);
    }
}
