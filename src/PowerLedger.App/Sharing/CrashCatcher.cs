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
/// the App ends the way it always did; this only writes what happened first. Each crash is trimmed to the sizes the pipe
/// and the server accept and scrubbed of names and paths before it ever reaches disk, since <see cref="CrashForwarder"/>
/// later sends the file on as it is.
/// </summary>
internal sealed class CrashCatcher(string folder, string version, ScrubNames names)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Hooks every source of an unhandled exception this process has. Call once, from OnStartup.</summary>
    public void Hook(Application app)
    {
        app.DispatcherUnhandledException += (_, e) => Write(e.Exception, folder, version, names);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown error."), folder, version, names);
        TaskScheduler.UnobservedTaskException += (_, e) => Write(e.Exception, folder, version, names);
    }

    /// <summary>Builds, trims and scrubs a crash report, then writes it to <paramref name="folder"/> as
    /// <c>app-&lt;utc ticks&gt;.json</c>. Never throws: a crash handler that itself threw would replace the crash being
    /// reported with a worse one.</summary>
    internal static void Write(Exception error, string folder, string version, ScrubNames names)
    {
        try
        {
            var report = Build(error, version);
            var scrubbed = report with { Message = Scrubber.Scrub(report.Message, names), Stack = Scrubber.Scrub(report.Stack, names) };
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"app-{DateTime.UtcNow.Ticks}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(scrubbed, Options));
        }
        catch (Exception writeError) when (writeError is not OutOfMemoryException)
        {
            // Writing a crash must never throw out of the handler that is already reporting one.
        }
    }

    /// <summary>The trimmed report for one exception: its type chain outermost first, an <see cref="AggregateException"/>'s
    /// branches included, the outermost message, and every exception's own text as .NET writes it (spec: "the stack
    /// traces, outermost first").</summary>
    internal static CrashReport Build(Exception error, string version) => new CrashReport(
        DateTimeOffset.UtcNow, "app", version, [.. TypeChain(error)], error.Message, error.ToString()).Trimmed();

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
