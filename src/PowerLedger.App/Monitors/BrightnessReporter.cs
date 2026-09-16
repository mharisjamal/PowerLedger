using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>
/// Tells the service how bright the external monitors are (Plan J): the service runs in session 0, where the monitors
/// can't be reached, and the App runs in the user's. A minute after the App starts and every five minutes after, while the
/// user allows it, it asks the service which monitors it knows, reads them off the UI thread, and reports each one the
/// service listed, under the service's own name for it. It asks the monitors nothing when the service lists none, or when
/// the service's latest reading says the displays are off, and it sends nothing to a service from before monitors, which
/// lists none and drops a connection that sends it a message it doesn't know. Nothing here throws: a report that fails is
/// simply made again at the next read.
/// </summary>
internal sealed class BrightnessReporter(IServiceLink link, IBrightnessReader reader, IUiSettings ui, TimeProvider clock) : IDisposable
{
    public static readonly TimeSpan FirstRead = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan ReadEvery = TimeSpan.FromMinutes(5);

    private ITimer? _timer;
    private int _reporting;

    /// <summary>Starts the schedule.</summary>
    public void Start() => _timer ??= clock.CreateTimer(_ => _ = ReportAsync(), null, FirstRead, ReadEvery);

    public void Dispose() => _timer?.Dispose();

    /// <summary>One scheduled read and its report. A read already running makes this one a no-op.</summary>
    internal async Task ReportAsync()
    {
        if (Interlocked.Exchange(ref _reporting, 1) == 1) return;
        try
        {
            if (!ui.Current.ReadMonitorBrightness || !link.IsConnected) return;
            var status = await link.GetStatusAsync().ConfigureAwait(false);
            if (status?.Monitors is not { Count: > 0 } listed || status.Last is { DisplayOn: false }) return;
            var readings = Listed(reader.Read(), listed);
            if (readings.Count > 0) await link.ReportBrightnessAsync(readings, []).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The service or the monitors failed this time; the next read tries again.
        }
        finally
        {
            Volatile.Write(ref _reporting, 0);
        }
    }

    /// <summary>The readings of the monitors the service listed, each under the instance the service gave it.</summary>
    private static List<MonitorBrightness> Listed(IReadOnlyList<DdcReading> readings, IReadOnlyList<MonitorStatus> listed)
    {
        var instances = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var monitor in listed)
        {
            if (monitor?.Instance is { Length: > 0 } instance) instances.TryAdd(instance, instance);
        }
        var reported = new List<MonitorBrightness>();
        foreach (var reading in readings)
        {
            if (reported.Count == ServiceSettings.MaxMonitors) break;   // the most the service takes in one report
            if (MonitorKeys.FromDevicePath(reading.DevicePath) is not { } key || !instances.Remove(key, out var instance)) continue;
            reported.Add(new MonitorBrightness { Instance = instance, Brightness = reading.Brightness });
        }
        return reported;
    }
}
