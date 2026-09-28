namespace PowerLedger.App.Tests;

/// <summary>Insights that answer with a fixed report and record each read's time and zone.</summary>
internal sealed class FakeInsights : IInsights
{
    public static readonly InsightsReport Empty = new(
        BillForecast.NotReady(0, "USD"), [], null, new CarbonEstimate(0, 0, 481, "World average"));

    public InsightsReport Answer { get; set; } = Empty;

    public List<(DateTimeOffset Now, TimeZoneInfo Zone)> Reads { get; } = [];

    public InsightsReport Read(DateTimeOffset now, TimeZoneInfo zone)
    {
        Reads.Add((now, zone));
        return Answer;
    }
}
