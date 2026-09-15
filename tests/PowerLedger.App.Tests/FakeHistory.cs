namespace PowerLedger.App.Tests;

/// <summary>History that answers with whatever snapshot the test sets.</summary>
internal sealed class FakeHistory : IHistory
{
    public HistorySnapshot? Snapshot { get; set; }

    public HistorySnapshot? Read(DateTimeOffset now, TimeZoneInfo zone) => Snapshot;
}
