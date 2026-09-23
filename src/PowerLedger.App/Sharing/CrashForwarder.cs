using System.IO;
using System.Text.Json;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>
/// Sends the App's own caught crashes on to the service (data-sharing design §5): once the link first connects this
/// session, every file <see cref="CrashCatcher"/> left waits for the service to say Crash and sensor reports is on, then
/// is sent with <c>reportCrash</c> and deleted once the service takes it; a file the service refuses is kept for the next
/// try. Whatever the consent, a file older than <see cref="MaxAge"/> is deleted, since nothing that old is still wanted.
/// A connection with no Sharing status yet — the service still starting, or older than the feature — does not use up
/// this session's run: it tries again on the next connection.
/// </summary>
internal sealed class CrashForwarder(IServiceLink link, UiThreads threads, string folder)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private bool _done;

    /// <summary>Waits for the link's first connection this session, then runs; retries on every later connection until a
    /// status carrying Sharing comes back. Call on the UI thread, before <see cref="IServiceLink.Start"/>.</summary>
    public void Start() => link.ConnectionChanged += OnConnectionChanged;

    private void OnConnectionChanged(bool connected)
    {
        if (!connected || _done) return;
        threads.Background(() => _ = RunAsync());
    }

    internal async Task RunAsync()
    {
        DeleteOld(folder, DateTime.UtcNow - MaxAge);
        var status = await link.GetStatusAsync().ConfigureAwait(false);
        if (status?.Sharing is not { } sharing) return;   // link not connected yet, or the service still starting: try again next connection
        _done = true;
        link.ConnectionChanged -= OnConnectionChanged;
        if (!sharing.Consent.Diagnostics) return;
        foreach (var path in Files(folder))
        {
            if (Read(path) is not { } report) continue;   // not readable as a crash: leave it for age to clear
            var result = await link.ReportCrashAsync(report).ConfigureAwait(false);
            if (result.Succeeded) Delete(path);
        }
    }

    /// <summary>Every crash file waiting, oldest first (their names embed UTC ticks, so this is chronological).</summary>
    internal static IReadOnlyList<string> Files(string folder)
        => Directory.Exists(folder) ? [.. Directory.GetFiles(folder, "app-*.json").OrderBy(path => path, StringComparer.Ordinal)] : [];

    private static void DeleteOld(string folder, DateTime cutoffUtc)
    {
        foreach (var path in Files(folder))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoffUtc) Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static CrashReport? Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<CrashReport>(File.ReadAllText(path), Options);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Left behind; tried again next session.
        }
    }
}
