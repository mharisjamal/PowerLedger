using System.IO;
using System.Text.Json;

namespace PowerLedger.App;

/// <summary>Feedback that couldn't be sent, kept under %LOCALAPPDATA%\PowerLedger\Feedback\pending so it can be retried
/// once this PC is online, and given up on — deleted — after 30 days.</summary>
internal static class FeedbackQueue
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    public static string DefaultFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerLedger", "Feedback", "pending");

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Best-effort: a report that can't even be saved for later is just lost, since there's nothing else to do
    /// with it and the App must not throw out of Send for this.</summary>
    public static void Save(string folder, FeedbackReport report, DateTimeOffset now)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"feedback-{now.UtcTicks}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new PendingFeedback(now, report), Options));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Every pending report on file that could be read, oldest first, with the path <see cref="Delete"/> takes.
    /// A file that fails to parse — half-written, or from an older version — is skipped rather than throwing, and is
    /// cleared out later by <see cref="MaxAge"/> if nothing else touches it.</summary>
    public static IReadOnlyList<(string Path, PendingFeedback Pending)> ReadAll(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        var found = new List<(string Path, PendingFeedback Pending)>();
        foreach (var file in Directory.GetFiles(folder, "feedback-*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<PendingFeedback>(File.ReadAllText(file), Options) is { } pending)
                    found.Add((file, pending));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }
        return [.. found.OrderBy(f => f.Pending.SavedAt)];
    }

    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
