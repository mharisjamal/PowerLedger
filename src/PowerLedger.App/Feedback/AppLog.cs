using System.IO;

namespace PowerLedger.App;

/// <summary>A minimal rolling log of the App's own, so Send feedback has something of the App's to attach alongside the
/// service's (data-sharing design's crash catching already covers full crash reports on their own; this is the day's
/// plain-text trail a bug report benefits from). One file a day, kept seven days.</summary>
internal static class AppLog
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerLedger", "Logs");

    private static readonly TimeSpan Retain = TimeSpan.FromDays(7);

    /// <summary>Appends one line, timestamped; never throws, since nothing should break over a log write failing.</summary>
    public static void Write(string line, DateTimeOffset? now = null)
    {
        try
        {
            var at = now ?? DateTimeOffset.UtcNow;
            Directory.CreateDirectory(Folder);
            File.AppendAllText(Path.Combine(Folder, $"app-{at:yyyyMMdd}.log"), $"{at:O} {line}{Environment.NewLine}");
            Sweep(at);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Sweep(DateTimeOffset now)
    {
        foreach (var file in Directory.GetFiles(Folder, "app-*.log"))
        {
            if (now - new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero) > Retain) File.Delete(file);
        }
    }
}
