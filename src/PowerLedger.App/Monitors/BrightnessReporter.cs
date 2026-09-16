using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>
/// Tells the service how bright the external monitors are, and whether each is on (Plans J and K): the service runs in
/// session 0, where the monitors can't be reached, and the App runs in the user's. A minute after the App starts and every
/// minute after, while the user allows it, it asks the service which monitors it knows, reads them off the UI thread - each
/// one's power mode every time, and its brightness once its last is five minutes old (<see cref="DdcBrightness"/>) - and
/// reports each one the service listed, under the service's own name for it, with whatever it answered: a brightness, a
/// power state, or both. It asks the monitors nothing when the service lists none, or when the service's latest reading
/// says the displays are off, and it sends nothing to a service from before monitors, which lists none and drops a
/// connection that sends it a message it doesn't know. Nothing here throws: a report that fails is simply made again at the
/// next read.
/// </summary>
internal sealed class BrightnessReporter(IServiceLink link, IBrightnessReader reader, IUiSettings ui, TimeProvider clock) : IDisposable
{
    public static readonly TimeSpan FirstRead = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan ReadEvery = TimeSpan.FromMinutes(1);

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
            var (brightness, power) = Listed(reader.Read(), listed);
            if (brightness.Count > 0 || power.Count > 0) await link.ReportBrightnessAsync(brightness, power).ConfigureAwait(false);
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

    /// <summary>The brightnesses and the power states of the monitors the service listed, each under the instance the service
    /// gave it.</summary>
    private static (List<MonitorBrightness> Brightness, List<MonitorPowerReading> Power) Listed(IReadOnlyList<DdcReading> readings, IReadOnlyList<MonitorStatus> listed)
    {
        var instances = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var monitor in listed)
        {
            if (monitor?.Instance is { Length: > 0 } instance) instances.TryAdd(instance, instance);
        }
        var brightness = new List<MonitorBrightness>();
        var power = new List<MonitorPowerReading>();
        var reported = 0;
        foreach (var reading in readings)
        {
            if (reported == ServiceSettings.MaxMonitors) break;   // the most the service takes in either list of one report
            if (MonitorKeys.FromDevicePath(reading.DevicePath) is not { } key || !instances.Remove(key, out var instance)) continue;
            reported++;
            if (reading.Brightness is { } level) brightness.Add(new MonitorBrightness { Instance = instance, Brightness = level });
            if (reading.Power is { } state) power.Add(new MonitorPowerReading { Instance = instance, State = state });
        }
        return (brightness, power);
    }
}
