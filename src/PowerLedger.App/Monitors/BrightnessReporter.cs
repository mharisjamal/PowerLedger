using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>
/// Tells the service how bright the external monitors are, whether each is on, and how Windows drives each (Plans J and K):
/// the service runs in session 0, where neither the monitors nor the user's display settings can be reached, and the App
/// runs in the user's. A minute after the App starts and every minute after, it asks the service which monitors it knows,
/// reads the refresh rate and HDR state Windows drives each at (<see cref="DisplayConfigReader"/>) and, while the user
/// allows it, reads the monitors themselves off the UI thread: each one's power mode every time, and its brightness once
/// its last is five minutes old (<see cref="DdcBrightness"/>). The user's tick stops only those requests, the ones sent to
/// monitors, as Windows' display settings ask no monitor anything. It reports each monitor the service listed, under the
/// service's own name for it, with whatever was read: a brightness, a power state, how it is driven, or any of them. It
/// reads nothing when the service lists no monitors, or when the service's latest reading says the displays are off, and
/// it sends nothing to a service from before monitors, which lists none and drops a connection that sends it a message it
/// doesn't know. Nothing here throws: a report that fails is simply made again at the next read.
/// </summary>
internal sealed class BrightnessReporter(IServiceLink link, IBrightnessReader reader, IDisplayReader displays, IUiSettings ui, TimeProvider clock) : IDisposable
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
            if (!link.IsConnected) return;
            var status = await link.GetStatusAsync().ConfigureAwait(false);
            if (status?.Monitors is not { Count: > 0 } listed || status.Last is { DisplayOn: false }) return;
            var instances = Instances(listed);
            var (brightness, power) = ui.Current.ReadMonitorBrightness ? Answered(reader.Read(), instances) : ([], []);
            var driven = Driven(displays.Read(), instances);
            if (brightness.Count > 0 || power.Count > 0 || driven.Count > 0)
                await link.ReportBrightnessAsync(brightness, power, driven).ConfigureAwait(false);
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

    /// <summary>Each monitor the service listed, by its instance, which a key read from a device path finds whatever its
    /// case, giving the service's own spelling.</summary>
    private static Dictionary<string, string> Instances(IReadOnlyList<MonitorStatus> listed)
    {
        var instances = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var monitor in listed)
        {
            if (monitor?.Instance is { Length: > 0 } instance) instances.TryAdd(instance, instance);
        }
        return instances;
    }

    /// <summary>The instance the service gave the monitor at this device path, or null when the path isn't a display's or
    /// the service didn't list its monitor.</summary>
    private static string? Instance(string devicePath, Dictionary<string, string> instances)
        => MonitorKeys.FromDevicePath(devicePath) is { } key && instances.TryGetValue(key, out var instance) ? instance : null;

    /// <summary>The brightnesses and the power states of the monitors the service listed, each monitor once, under the instance
    /// the service gave it.</summary>
    private static (List<MonitorBrightness> Brightness, List<MonitorPowerReading> Power) Answered(IReadOnlyList<DdcReading> readings, Dictionary<string, string> instances)
    {
        var brightness = new List<MonitorBrightness>();
        var power = new List<MonitorPowerReading>();
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reading in readings)
        {
            if (reported.Count == ServiceSettings.MaxMonitors) break;   // the most the service takes in any list of one report
            if (Instance(reading.DevicePath, instances) is not { } instance || !reported.Add(instance)) continue;
            if (reading.Brightness is { } level) brightness.Add(new MonitorBrightness { Instance = instance, Brightness = level });
            if (reading.Power is { } state) power.Add(new MonitorPowerReading { Instance = instance, State = state });
        }
        return (brightness, power);
    }

    /// <summary>How Windows drives each monitor the service listed, each monitor once, under the instance the service gave
    /// it.</summary>
    private static List<MonitorDisplayReading> Driven(IReadOnlyList<DisplayReading> readings, Dictionary<string, string> instances)
    {
        var driven = new List<MonitorDisplayReading>();
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reading in readings)
        {
            if (reported.Count == ServiceSettings.MaxMonitors) break;   // the most the service takes in one report
            if (Instance(reading.DevicePath, instances) is not { } instance || !reported.Add(instance)) continue;
            driven.Add(new MonitorDisplayReading { Instance = instance, RefreshHz = reading.RefreshHz, Hdr = reading.Hdr });
        }
        return driven;
    }
}
