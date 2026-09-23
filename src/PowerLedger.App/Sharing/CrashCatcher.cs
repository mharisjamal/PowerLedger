using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PowerLedger.Contracts;
using PowerLedger.Core;

namespace PowerLedger.App;

/// <summary>
/// Catches the App's own crashes (data-sharing design §5): the dispatcher's unhandled exceptions, the AppDomain's, and
/// unobserved task exceptions. Existing behaviour is untouched — nothing here marks an exception handled that wasn't, so
/// the App ends the way it always did; this only writes what happened first. Each crash is scrubbed of names and paths,
/// then trimmed to the sizes the pipe and the server accept, before it ever reaches disk, since
/// <see cref="CrashForwarder"/> later sends the file on as it is. Scrubbing runs before trimming, not after: it can
/// lengthen the text (a short name replaced by <c>&lt;user&gt;</c>, a short path by <c>&lt;path&gt;\File.cs:line N</c>),
/// so trimming the raw text first could leave the scrubbed result over the pipe's own limit, failing every session's send
/// until the file is old enough to be deleted.
/// </summary>
internal sealed class CrashCatcher(string folder, string version, ScrubNames names, Action<Exception, string, string, ScrubNames>? write = null)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Action<Exception, string, string, ScrubNames> _write = write ?? Write;
    private Exception? _lastReported;

    /// <summary>Hooks every source of an unhandled exception this process has. Call once, from OnStartup.</summary>
    public void Hook(Application app)
    {
        app.DispatcherUnhandledException += (_, e) => Report(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown error."));
        TaskScheduler.UnobservedTaskException += (_, e) => Report(e.Exception);
    }

    /// <summary>What each hooked event calls: skips an exception object already reported this process, since WPF
    /// re-raises an unhandled <see cref="Application.DispatcherUnhandledException"/> through
    /// <see cref="AppDomain.UnhandledException"/> as it ends the process, which would otherwise write and forward the
    /// same crash twice; then writes, catching everything, including <see cref="OutOfMemoryException"/> — unlike
    /// <see cref="Write"/> alone, since this can run on the finalizer thread for
    /// <see cref="TaskScheduler.UnobservedTaskException"/>, where letting anything escape crashes the process outright.
    /// </summary>
    internal void Report(Exception error)
    {
        if (ReferenceEquals(error, _lastReported)) return;
        _lastReported = error;
        try
        {
            _write(error, folder, version, names);
        }
        catch
        {
            // As above: must never throw out of a crash handler, not even to report a worse one.
        }
    }

    /// <summary>Builds, scrubs and trims a crash report, then writes it to <paramref name="folder"/> as
    /// <c>app-&lt;utc ticks&gt;.json</c>. Trimming runs after scrubbing, not before: scrubbing can lengthen the text, so
    /// trimming the raw text first could leave the scrubbed result over the pipe's own limit. Never throws: a crash
    /// handler that itself threw would replace the crash being reported with a worse one.</summary>
    internal static void Write(Exception error, string folder, string version, ScrubNames names)
    {
        try
        {
            var report = Build(error, version);
            var scrubbed = (report with { Message = Scrubber.Scrub(report.Message, names), Stack = Scrubber.Scrub(report.Stack, names) }).Trimmed();
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"app-{DateTime.UtcNow.Ticks}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(scrubbed, Options));
        }
        catch (Exception writeError) when (writeError is not OutOfMemoryException)
        {
            // Writing a crash must never throw out of the handler that is already reporting one.
        }
    }

    /// <summary>The report for one exception, not yet trimmed: its type chain outermost first, an
    /// <see cref="AggregateException"/>'s branches included, the outermost message, and every exception's own text as
    /// .NET writes it (spec: "the stack traces, outermost first"). <see cref="Write"/> trims it after scrubbing.</summary>
    internal static CrashReport Build(Exception error, string version) => new(
        DateTimeOffset.UtcNow, "app", version, [.. TypeChain(error)], error.Message, error.ToString());

    private static IEnumerable<string> TypeChain(Exception error)
    {
        yield return error.GetType().FullName ?? error.GetType().Name;
        if (error is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
                foreach (var name in TypeChain(inner)) yield return name;
        }
        else if (error.InnerException is { } innerException)
        {
            foreach (var name in TypeChain(innerException)) yield return name;
        }
    }
}
