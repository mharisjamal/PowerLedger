using System.Management;

namespace PowerLedger.Sensors;

/// <param name="Brightness">0..1 for the internal panel; null where the machine has no brightness control.</param>
/// <param name="MonitorCount">How many displays Windows currently reports as active.</param>
public readonly record struct DisplayState(double? Brightness, int MonitorCount);

/// <summary>
/// Panel brightness and monitor count. WMI is slow, so the query runs on a timer and every tick reuses the answer
/// (spec §4). Whether the screen is lit comes from the service's power notifications. The panel's size is the
/// hardware inventory's business, not this source's.
/// </summary>
public sealed class DisplaySource : ISensorSource
{
    /// <summary>After a failed query, when to try again: soon, so a WMI restart heals quickly, but not every tick.</summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(10);

    private readonly Func<DisplayState> _query;
    private readonly Func<bool> _displayOn;
    private readonly TimeSpan _refreshEvery;
    private DisplayState _state = new(null, 1);
    private DateTimeOffset _nextReadAt = DateTimeOffset.MinValue;
    private volatile bool _refreshRequested;

    /// <param name="displayOn">The service supplies this from GUID_CONSOLE_DISPLAY_STATE; the preview passes true.</param>
    public DisplaySource(Func<bool> displayOn) : this(QueryWmi, displayOn) { }

    /// <summary>Test seam: any source of display state.</summary>
    internal DisplaySource(Func<DisplayState> query, Func<bool> displayOn, TimeSpan? refreshEvery = null)
    {
        _query = query;
        _displayOn = displayOn;
        _refreshEvery = refreshEvery ?? TimeSpan.FromMinutes(1);
    }

    public string Name => "display";

    public bool Supported => true;

    public string? Unavailable => null;

    /// <summary>Makes the next tick re-query, after a resume or a monitor change. Safe to call from any thread.</summary>
    public void Refresh() => _refreshRequested = true;

    public void Contribute(SampleDraft draft)
    {
        var now = DateTimeOffset.UtcNow;
        if (_refreshRequested || now >= _nextReadAt)
        {
            // Cleared before the query, so a Refresh that arrives while it runs is not lost.
            _refreshRequested = false;
            try
            {
                _state = _query();
                _nextReadAt = now + _refreshEvery;
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
