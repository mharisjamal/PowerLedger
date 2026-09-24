namespace PowerLedger.App.Tests;

/// <summary>History that answers with whatever snapshot the test sets.</summary>
internal sealed class FakeHistory : IHistory
{
    public HistorySnapshot? Snapshot { get; set; }

    /// <summary>Where the history begins, for the All range; null until a test sets it.</summary>
    public DateTimeOffset? First { get; set; }

    public HistorySnapshot? Read(DateTimeOffset now, TimeZoneInfo zone) => Snapshot;

    public DateTimeOffset? FirstRow() => First;
}
