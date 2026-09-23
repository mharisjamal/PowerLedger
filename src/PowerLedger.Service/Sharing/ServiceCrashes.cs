using System.Text;
using PowerLedger.Contracts;

namespace PowerLedger.Service.Sharing;

/// <summary>
/// The service's own crashes (data-sharing design §5): each is written to <c>Crashes\service-&lt;utc ticks&gt;.json</c> as a
/// <see cref="CrashReport"/>, whatever the consent. Only the machine's own users can read that folder, so the file isn't
/// scrubbed; the sharing worker scrubs it when it records it while Crash and sensor reports is on, and deletes it
/// otherwise.
/// </summary>
internal static class ServiceCrashes
{
    private static int _caught;

    /// <summary>Writes every crash from here on to <paramref name="folder"/>: an exception nothing caught, and a failed task
    /// nobody looked at. What each already did is left as it was. Once per process.</summary>
    public static void Catch(string folder)
    {
        if (Interlocked.Exchange(ref _caught, 1) == 1) return;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error) TryWrite(folder, error, DateTimeOffset.UtcNow, ServiceVersion.Short);
        };
        TaskScheduler.UnobservedTaskException += (_, args) => TryWrite(folder, args.Exception, DateTimeOffset.UtcNow, ServiceVersion.Short);
    }

    /// <summary>Writes the crash, first to a temporary name so the worker never reads half of it.</summary>
    /// <returns>The file, or null when it couldn't be written: recording a crash never throws one of its own.</returns>
    public static string? TryWrite(string folder, Exception error, DateTimeOffset at, string version)
    {
        try
        {
            var report = Report(error, at, version);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"service-{at.UtcTicks}.json");
            var partial = Path.ChangeExtension(path, ".tmp");
            File.WriteAllText(partial, OutboxEvents.Write(report));
            File.Move(partial, path, overwrite: true);
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The crash as a report, trimmed to what the server takes: the exception's type and each inner one's,
    /// outermost first and an <see cref="AggregateException"/>'s all in turn; the outermost message; and each one's stack
    /// trace in the same order.</summary>
    public static CrashReport Report(Exception error, DateTimeOffset at, string version)
    {
        ArgumentNullException.ThrowIfNull(error);
        var chain = Chain(error).Take(CrashReport.MaxTypes).ToList();
        var stack = new StringBuilder();
        foreach (var (exception, index) in chain.Select((exception, index) => (exception, index)))
        {
            if (stack.Length > CrashReport.MaxStackLength) break;
            stack.Append(index == 0 ? "" : "---> ").Append(TypeName(exception)).Append(": ").AppendLine(exception.Message);
            if (exception.StackTrace is { Length: > 0 } trace) stack.AppendLine(trace);
        }
        return new CrashReport(at, "service", version, [.. chain.Select(TypeName)], error.Message, stack.ToString().TrimEnd()).Trimmed();
    }

    /// <summary>The exception, then its inner ones, depth first.</summary>
    private static IEnumerable<Exception> Chain(Exception error)
    {
        yield return error;
        var inner = error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : (IEnumerable<Exception>)[error.InnerException!];
        foreach (var next in inner.Where(next => next is not null))
        {
            foreach (var deeper in Chain(next)) yield return deeper;
        }
    }

    private static string TypeName(Exception error) => error.GetType().FullName ?? error.GetType().Name;
}
