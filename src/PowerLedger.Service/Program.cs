using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PowerLedger.Service.Sharing;
using Serilog;

namespace PowerLedger.Service;

internal static class Program
{
    private static int Main(string[] args)
    {
        IHost host;
        try
        {
            host = ServiceHost.Build(args);
        }
        catch (Exception error) when (error is UntrustedDataDirectoryException or IOException or UnauthorizedAccessException)
        {
            Fatal($"PowerLedger cannot use its data folder: {error.Message}");
            return 1;
        }

        // Crashes go to a file whatever the user chose; the sharing worker records or deletes each (data-sharing design §5).
        var crashes = host.Services.GetRequiredService<ServicePaths>().Crashes;
        ServiceCrashes.Catch(crashes);
        try
        {
            var loop = host.Services.GetRequiredService<SamplingLoop>();
            host.Run();
            return loop.ExecuteTask is { IsFaulted: true } ? 1 : 0;
        }
        catch (Exception error)
        {
            ServiceCrashes.TryWrite(crashes, error, DateTimeOffset.UtcNow, ServiceVersion.Short);
            Log.Fatal(error, "PowerLedger stopped on an error");
            Fatal($"PowerLedger stopped on an error: {error.Message}");
            return 1;
        }
        finally
        {
            host.Dispose();
            Log.CloseAndFlush();
        }
    }

    /// <summary>The Application event log is where an administrator looks when a service will not start; the log file may not exist yet.</summary>
    private static void Fatal(string message)
    {
        Console.Error.WriteLine(message);
        try
        {
            EventLog.WriteEntry(ServiceHost.ServiceName, message, EventLogEntryType.Error);
        }
        catch (Exception)
        {
            // No event source and no right to create one: standard error is all there is.
        }
    }
}
