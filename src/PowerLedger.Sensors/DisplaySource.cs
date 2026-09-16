using System.Management;

namespace PowerLedger.Sensors;

/// <param name="Brightness">0..1 for the internal panel; null where the machine has no brightness control.</param>
/// <param name="MonitorCount">How many displays Windows currently reports as active.</param>
public readonly record struct DisplayState(double? Brightness, int MonitorCount);

/// <summary>
/// Panel brightness and monitor count, and the external monitors attached. WMI is slow, so the queries run on a timer and
/// every tick reuses the answer (spec §4). Whether the screen is lit comes from the service's power notifications. The
/// panel's size is the hardware inventory's business, not this source's.
/// </summary>
public sealed class DisplaySource : ISensorSource
{
    /// <summary>After a failed query, when to try again: soon, so a WMI restart heals quickly, but not every tick.</summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(10);

    private readonly Func<DisplayState> _query;
    private readonly Func<bool> _displayOn;
    private readonly TimeSpan _refreshEvery;
    private readonly Func<IReadOnlyList<MonitorFacts>> _monitors;
    private readonly Action<IReadOnlyList<MonitorFacts>>? _detected;
    private DisplayState _state = new(null, 1);
    private IReadOnlyList<MonitorFacts>? _handedOver;
    private DateTimeOffset _nextReadAt = DateTimeOffset.MinValue;
    private volatile bool _refreshRequested;

    /// <param name="displayOn">The service supplies this from GUID_CONSOLE_DISPLAY_STATE; the preview passes true.</param>
    /// <param name="monitorsDetected">Told which external monitors are attached, on the thread that reads this source: at the
    /// first refresh WMI answers, and then whenever the monitors found on a refresh differ from the ones it was last told of.
    /// Without it the monitors are not read at all.</param>
    public DisplaySource(Func<bool> displayOn, Action<IReadOnlyList<MonitorFacts>>? monitorsDetected = null)
        : this(QueryWmi, displayOn, monitors: MonitorInventory.Read, detected: monitorsDetected) { }

    /// <summary>Test seam: any source of display state and of monitors.</summary>
    internal DisplaySource(
        Func<DisplayState> query, Func<bool> displayOn, TimeSpan? refreshEvery = null,
        Func<IReadOnlyList<MonitorFacts>>? monitors = null, Action<IReadOnlyList<MonitorFacts>>? detected = null)
    {
        _query = query;
        _displayOn = displayOn;
        _refreshEvery = refreshEvery ?? TimeSpan.FromMinutes(1);
        _monitors = monitors ?? (() => []);
        _detected = detected;
    }

    public string Name => "display";

    public bool Supported => true;

    public string? Unavailable => null;

    /// <summary>Makes the next tick re-query, after a resume or a monitor change. Safe to call from any thread.</summary>
    public void Refresh() => _refreshRequested = true;

    public void Contribute(SampleDraft draft)
    {
        var now = DateTimeOffset.UtcNow;
        var refreshed = false;
        if (_refreshRequested || now >= _nextReadAt)
        {
            // Cleared before the query, so a Refresh that arrives while it runs is not lost.
            _refreshRequested = false;
            try
            {
                _state = _query();
                _nextReadAt = now + _refreshEvery;
                refreshed = true;
            }
            catch (Exception error) when (Wmi.IsFailure(error))
            {
                // Keep the last good answer; a WMI hiccup must not blank the brightness.
                _nextReadAt = now + (_refreshEvery < RetryAfterFailure ? _refreshEvery : RetryAfterFailure);
            }
        }

        draft.Brightness = _state.Brightness;
        draft.MonitorCount = _state.MonitorCount;
        draft.DisplayOn = _displayOn();

        // Only once WMI has just answered, so that a WMI hiccup, which the inventory reads as no monitors, doesn't unplug them.
        if (refreshed && _detected is not null) HandOverMonitors(_detected);
    }

    /// <summary>Reads the external monitors and passes them on if they changed. The draft is filled first, so a handover
    /// that throws costs the tick nothing it measured; what was handed over is noted only once it has been taken, so a
    /// handover that failed is tried again on the next refresh.</summary>
    private void HandOverMonitors(Action<IReadOnlyList<MonitorFacts>> detected)
    {
        var monitors = _monitors();
        if (_handedOver is not null && _handedOver.SequenceEqual(monitors)) return;
        detected(monitors);
        _handedOver = monitors;
    }

    private static DisplayState QueryWmi()
    {
        var monitors = Wmi.Read(@"\\.\root\wmi", "SELECT Active FROM WmiMonitorBasicDisplayParams",
            rows => rows.Count(row => row["Active"] is true));

        double? brightness;
        try
        {
            brightness = Wmi.Read(@"\\.\root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness",
                rows => rows.Count > 0 ? Convert.ToDouble(rows[0]["CurrentBrightness"]) / 100.0 : (double?)null);
        }
        catch (ManagementException error) when (error.ErrorCode == ManagementStatus.NotSupported)
        {
            // A desktop: WMI refuses outright when no panel has a brightness Windows controls.
            brightness = null;
        }

        return new DisplayState(brightness, Math.Max(monitors, 1));
    }

    public void Dispose() { }
}
