namespace PowerLedger.App;

/// <summary>Where feedback is posted (spec's feedback feature). A mutable field, not a const, so a test can point it at
/// a fake server instead.</summary>
internal static class FeedbackEndpoint
{
    public static string Url = "https://powerledger-data.powerledger-data.workers.dev/v1/feedback";
}
