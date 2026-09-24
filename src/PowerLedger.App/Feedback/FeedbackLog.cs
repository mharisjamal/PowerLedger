using System.IO;

namespace PowerLedger.App;

/// <summary>The log text Send feedback offers to attach: the App's own and the service's, tailed and capped.</summary>
internal static class FeedbackLog
{
    public const int TailLines = 300;

    /// <summary>The Worker's feedback route's own "log" limit (review round: matched exactly), in characters, not bytes.</summary>
    public const int MaxChars = 204_800;

    /// <summary>The newest <paramref name="tailLines"/> lines of the newest file matching <paramref name="pattern"/> in
    /// <paramref name="folder"/>; null if there is none, or it can't be read — the service's log, say, kept in a folder
    /// this PC's user can't open.</summary>
    public static string? TailLatestFile(string folder, string pattern, int tailLines = TailLines)
    {
        try
        {
            if (!Directory.Exists(folder)) return null;
            var newest = new DirectoryInfo(folder).GetFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest is null) return null;
            return TailLines_(File.ReadAllLines(newest.FullName), tailLines);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string TailLines_(IReadOnlyList<string> lines, int tailLines)
        => string.Join('\n', lines.Count <= tailLines ? lines : lines.Skip(lines.Count - tailLines));

    /// <summary>The App's tail and the service's, headed and joined, cut to <see cref="MaxChars"/> by dropping the
    /// oldest content first, so what's kept is the most recent. Null when there is neither.</summary>
    public static string? Combined(string? appTail, string? serviceTail)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(appTail)) parts.Add("--- App log ---\n" + appTail);
        if (!string.IsNullOrEmpty(serviceTail)) parts.Add("--- Service log ---\n" + serviceTail);
        if (parts.Count == 0) return null;
        var combined = string.Join("\n\n", parts);
        return combined.Length <= MaxChars ? combined : combined[^MaxChars..];
    }
}
