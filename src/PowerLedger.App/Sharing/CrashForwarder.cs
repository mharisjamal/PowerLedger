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
/// this session's run: it retries on the next connection and every <see cref="RetryEvery"/> (as <see cref="ConsentGate"/>
/// does), since a connection that stays up the whole time the service finishes starting never raises another connection
/// event to retry on.
/// </summary>
internal sealed class CrashForwarder(IServiceLink link, UiThreads threads, string folder, TimeProvider clock) : IDisposable
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private int _done;
    private ITimer? _timer;

    /// <summary>Waits for the link's first connection this session, then runs; retries on every later connection and
    /// every <see cref="RetryEvery"/> until a status carrying Sharing comes back. Call on the UI thread, before
    /// <see cref="IServiceLink.Start"/>.</summary>
    public void Start()
    {
        link.ConnectionChanged += OnConnectionChanged;
        _timer = clock.CreateTimer(_ => threads.Background(() => _ = RunAsync()), null, RetryEvery, RetryEvery);
    }

    private void OnConnectionChanged(bool connected)
    {
        if (connected) threads.Background(() => _ = RunAsync());
    }

    /// <summary>A timer tick and a connection event can start overlapping runs; only the one that first claims
    /// <see cref="_done"/> once a status has come back may send, so the same file is not handed to the pipe twice.</summary>
    internal async Task RunAsync()
    {
        if (Volatile.Read(ref _done) != 0) return;   // another run, or Dispose, already claimed this session's run
        DeleteOld(folder, DateTime.UtcNow - MaxAge);
        var status = await link.GetStatusAsync().ConfigureAwait(false);
        if (status?.Sharing is not { } sharing) return;   // link not connected yet, or the service still starting: try again
        if (Interlocked.Exchange(ref _done, 1) != 0) return;   // another overlapping run, or Dispose, already claimed this
        link.ConnectionChanged -= OnConnectionChanged;
        _timer?.Dispose();
        _timer = null;
        if (!sharing.Consent.Diagnostics) return;
        foreach (var path in Files(folder))
        {
            if (Read(path) is not { } report) continue;   // not readable as a crash: leave it for age to clear
            var result = await link.ReportCrashAsync(report).ConfigureAwait(false);
            if (result.Succeeded) Delete(path);
        }
    }

    /// <summary>Stops retrying and claims the session's run, so a run already awaiting the service's answer finds it
    /// taken and does not send after the App has started to exit.</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _done, 1);
        link.ConnectionChanged -= OnConnectionChanged;
        _timer?.Dispose();
        _timer = null;
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
