using PowerLedger.Storage;

namespace PowerLedger.App.Tests;

/// <summary>The apps a test hands out (Plan AE), and the ranges asked for; a day of a PC unless told otherwise.</summary>
internal sealed class FakeAppHistory : IAppHistory
{
    public Func<DateRange, AppsRange?> Answer { get; set; } = range => AppLedgerTests.Day() with { From = range.From, To = range.To };

    public List<DateRange> Reads { get; } = [];

    public AppsRange? Apps(DateRange range)
    {
        lock (Reads) Reads.Add(range);
        return Answer(range);
    }
}
