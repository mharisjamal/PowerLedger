namespace PowerLedger.App;

/// <summary>One attached image, base64, as the feedback endpoint takes it.</summary>
internal sealed record FeedbackImagePayload(string Name, string ContentType, string Data);

/// <summary>What Send feedback posts (spec's feedback feature): the report, the email if one was given, and what the
/// static line in the window told the user would also go — the app version (with its +sha), Windows' own name, and this
/// PC's architecture — plus the log, when the tick is on, and up to five images.</summary>
internal sealed record FeedbackReport(
    string Text, string? Email, string App, string? Os, string Arch, string? Log, IReadOnlyList<FeedbackImagePayload> Images);

/// <summary>A report saved under Feedback\pending because it couldn't be sent yet, with when it was saved so a 30-day-old
/// one can be given up on.</summary>
internal sealed record PendingFeedback(DateTimeOffset SavedAt, FeedbackReport Report);
