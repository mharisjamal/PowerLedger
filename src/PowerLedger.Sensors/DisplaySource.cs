using System.Management;

namespace PowerLedger.Sensors;

/// <param name="Brightness">0..1 for the internal panel; null where the machine has no brightness control.</param>
/// <param name="MonitorCount">How many displays Windows currently reports.</param>
/// <param name="DiagonalInches">The internal panel's diagonal; 0 when there is none or it cannot be read.</param>
public readonly record struct DisplayState(double? Brightness, int MonitorCount, double DiagonalInches);

/// <summary>
/// Panel brightness, monitor count and panel size. WMI is slow, so the query runs on a timer and every tick
/// reuses the answer (spec §4). Whether the screen is lit comes from the service's power notifications.
/// </summary>
public sealed class DisplaySource : ISensorSource
{
    private readonly Func<DisplayState> _query;
    private readonly Func<bool> _displayOn;
    private readonly TimeSpan _refreshEvery;
    private DisplayState _state;
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;
    private bool _everRead;

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

    /// <summary>The internal panel's diagonal, for the hardware inventory. 0 when unknown.</summary>
    public double DiagonalInches => _state.DiagonalInches;

    /// <summary>Forces the next tick to re-query, after a resume or a monitor change.</summary>
    public void Refresh() => _readAt = DateTimeOffset.MinValue;

    public void Contribute(SampleDraft draft)
    {
        var now = DateTimeOffset.UtcNow;
        if (!_everRead || now - _readAt >= _refreshEvery)
        {
            try
            {
                _state = _query();
                _everRead = true;
            }
            catch (ManagementException)
            {
                // Keep the last good answer; a WMI hiccup must not blank the brightness.
            }
            catch (InvalidOperationException)
            {
            }
            _readAt = now;
        }

        draft.Brightness = _state.Brightness;
        draft.MonitorCount = _state.MonitorCount;
        draft.DisplayOn = _displayOn();
    }

    private static DisplayState QueryWmi()
    {
        double? brightness = null;
        var monitors = 0;
        double diagonal = 0;

        using (var searcher = new ManagementObjectSearcher(@"\\.\root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
        using (var results = searcher.Get())
        {
            foreach (var row in results)
            {
                using var instance = (ManagementObject)row;
                brightness = Convert.ToDouble(instance["CurrentBrightness"]) / 100.0;
                break;
            }
        }

        using (var searcher = new ManagementObjectSearcher(@"\\.\root\wmi", "SELECT MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams"))
        using (var results = searcher.Get())
        {
            foreach (var row in results)
            {
                using var instance = (ManagementObject)row;
                monitors++;
                if (diagonal > 0) continue;
                // WMI reports the panel in centimetres; the model wants the diagonal in inches.
                var width = Convert.ToDouble(instance["MaxHorizontalImageSize"]);
                var height = Convert.ToDouble(instance["MaxVerticalImageSize"]);
                if (width > 0 && height > 0) diagonal = Math.Round(Math.Sqrt(width * width + height * height) / 2.54, 1);
            }
        }

        return new DisplayState(brightness, Math.Max(monitors, 1), diagonal);
    }

    public void Dispose() { }
}
