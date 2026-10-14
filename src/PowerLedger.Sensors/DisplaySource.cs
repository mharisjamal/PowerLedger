namespace PowerLedger.Sensors;

/// <param name="Brightness">0..1 for the internal panel; null where the machine has no brightness control.</param>
/// <param name="MonitorCount">How many displays Windows currently reports as active.</param>
/// <param name="PanelActive">Whether the built-in panel is showing anything (see <see cref="MonitorInventory.InternalPanelActive"/>);
/// null where Windows lists none.</param>
public readonly record struct DisplayState(double? Brightness, int MonitorCount, bool? PanelActive = null);

/// <summary>
/// Panel brightness and monitor count, and the external monitors attached (spec §4). WMI is slow, so it is asked only when
/// Windows says something changed, and every tick reuses the answer: the panel's brightness comes from Windows' own
/// announcement of it (GUID_VIDEO_CURRENT_MONITOR_BRIGHTNESS) with no query at all, and the monitors are read again when one
/// arrives or leaves (GUID_DEVINTERFACE_MONITOR), when the lid opens or shuts, and when the console display comes back on
/// (GUID_CONSOLE_DISPLAY_STATE, as the service's power notifications report it). A monitor change is read at once and again
/// ten seconds on, for a monitor Windows was still setting up. A slow poll every quarter of an hour is the safety net.
/// Whether the screen is lit comes from the service's power notifications. The panel's size is the hardware inventory's
/// business, not this source's.
/// </summary>
public sealed class DisplaySource : ISensorSource
{
    /// <summary>How often WMI is asked when Windows has announced nothing.</summary>
    public static readonly TimeSpan SafetyPoll = TimeSpan.FromMinutes(15);

    /// <summary>After a failed query, when to try again: soon, so a WMI restart heals quickly, but not every tick.</summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(10);

    /// <summary>How long after a monitor change it is read again.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    private readonly Func<DisplayState> _query;
    private readonly Func<bool> _displayOn;
    private readonly TimeSpan _refreshEvery;
    private readonly Func<IReadOnlyList<MonitorFacts>?> _monitors;
    private readonly Action<IReadOnlyList<MonitorFacts>>? _detected;
    private readonly ISystemSignals? _signals;
    private readonly Func<DateTimeOffset> _clock;
    private DisplayState _state = new(null, 1);
    private IReadOnlyList<MonitorFacts>? _handedOver;
    private DateTimeOffset _nextReadAt = DateTimeOffset.MinValue;
    private DateTimeOffset? _settledAt;
    private int _monitorChangesSeen;
    private int _brightnessChangesSeen;
    private bool _displayWasOn = true;
    private volatile bool _refreshRequested;

    /// <param name="displayOn">The service supplies this from GUID_CONSOLE_DISPLAY_STATE; the preview passes true.</param>
    /// <param name="monitorsDetected">Told which external monitors are attached, on the thread that reads this source: at the
    /// first refresh WMI answers, and then whenever the monitors found on a refresh differ from the ones it was last told of.
    /// Without it the monitors are not read at all.</param>
    public DisplaySource(Func<bool> displayOn, Action<IReadOnlyList<MonitorFacts>>? monitorsDetected = null)
        : this(QueryWmi, displayOn, monitors: MonitorInventory.Read, detected: monitorsDetected, signals: SystemSignals.Windows) { }

    /// <summary>Test seam: any source of display state, of monitors, of announcements and of time.</summary>
    /// <param name="signals">What Windows announces; left out, nothing is, and only the poll and <see cref="Refresh"/> ask.</param>
    internal DisplaySource(
        Func<DisplayState> query, Func<bool> displayOn, TimeSpan? refreshEvery = null,
        Func<IReadOnlyList<MonitorFacts>?>? monitors = null, Action<IReadOnlyList<MonitorFacts>>? detected = null,
        ISystemSignals? signals = null, Func<DateTimeOffset>? clock = null)
    {
        _query = query;
        _displayOn = displayOn;
        _refreshEvery = refreshEvery ?? SafetyPoll;
        _monitors = monitors ?? (() => []);
        _detected = detected;
        _signals = signals;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
        _monitorChangesSeen = signals?.MonitorChanges ?? 0;
        _brightnessChangesSeen = signals?.BrightnessChanges ?? 0;
    }

    public string Name => "display";

    public bool Supported => true;

    public string? Unavailable => null;

    /// <summary>Makes the next tick re-query, after a resume or a monitor change. Safe to call from any thread.</summary>
    public void Refresh() => _refreshRequested = true;

    public void Contribute(SampleDraft draft)
    {
        var now = _clock();
        var displayOn = _displayOn();
        NoteAnnouncements(now, displayOn);
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

        TakeAnnouncedBrightness();
        draft.Brightness = _state.Brightness;
        draft.MonitorCount = _state.MonitorCount;
        draft.PanelInactive = _state.PanelActive == false;
        draft.DisplayOn = displayOn;

        // Only on a refresh WMI answered: the monitors keep the display query's schedule, and are left alone while WMI fails.
        if (refreshed && _detected is not null) HandOverMonitors(_detected);
    }

    /// <summary>Asks for a refresh when a monitor arrived or left, the lid moved, or the display came back on, and again once
    /// such a change has settled.</summary>
    private void NoteAnnouncements(DateTimeOffset now, bool displayOn)
    {
        if (_signals?.MonitorChanges is { } changes && changes != _monitorChangesSeen)
        {
            _monitorChangesSeen = changes;
            _refreshRequested = true;
            _settledAt = now + Settle;
        }
        if (displayOn && !_displayWasOn) _refreshRequested = true;
        _displayWasOn = displayOn;
        if (_settledAt is { } settled && now >= settled)
        {
            _settledAt = null;
            _refreshRequested = true;
        }
    }

    /// <summary>Takes the brightness Windows last announced, on a machine whose panel WMI says has one: a desktop's
    /// announcement, if it makes one, is no panel's.</summary>
    private void TakeAnnouncedBrightness()
    {
        if (_signals is not { } signals) return;
        var changes = signals.BrightnessChanges;
        if (changes == _brightnessChangesSeen) return;
        _brightnessChangesSeen = changes;
        if (_state.Brightness is not null && signals.Brightness is { } percent) _state = _state with { Brightness = percent / 100.0 };
    }

    /// <summary>Reads the external monitors and passes them on if they changed. The draft is filled first, so a handover
    /// that throws costs the tick nothing it measured; what was handed over is noted only once it has been taken, so a
    /// handover that failed is tried again on the next refresh. When WMI didn't say which monitors are attached, nothing is
    /// passed on, so whoever was told keeps what it was told rather than hearing that they were unplugged.</summary>
    private void HandOverMonitors(Action<IReadOnlyList<MonitorFacts>> detected)
    {
        if (_monitors() is not { } monitors) return;
        if (_handedOver is not null && _handedOver.SequenceEqual(monitors)) return;
        detected(monitors);
        _handedOver = monitors;
    }

    private static DisplayState QueryWmi()
    {
        // Once no display is left, as when a desktop's only monitor is unplugged, WMI refuses the class rather than list none.
        // That is an answer, none active, and must not fail the query, since only a query that answers hands the monitors over.
        var displays = Wmi.ReadInstances(@"\\.\root\wmi", "SELECT InstanceName, Active FROM WmiMonitorBasicDisplayParams",
            rows => rows.Select(row => (Instance: row["InstanceName"] as string ?? "", Active: row["Active"] is true)).ToList());
        var monitors = displays.Count(display => display.Active);

        // Which display is the built-in panel, so a docked laptop's shut lid is seen; without an answer nothing is said of it.
        var connections = Wmi.ReadOrNull<IReadOnlyDictionary<string, uint>>(@"\\.\root\wmi", "SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams", rows =>
        {
            var byInstance = new Dictionary<string, uint>();
            foreach (var row in rows)
            {
                if (row["InstanceName"] is string name && row["VideoOutputTechnology"] is uint connection) byInstance[name] = connection;
            }
            return byInstance;
        });
        var panel = connections is null ? null : MonitorInventory.InternalPanelActive(displays, connections);

        // A desktop: WMI refuses outright when no panel has a brightness Windows controls, which is no brightness.
        var brightness = Wmi.ReadInstances(@"\\.\root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness",
            rows => rows.Count > 0 ? Convert.ToDouble(rows[0]["CurrentBrightness"]) / 100.0 : (double?)null);

        return new DisplayState(brightness, Math.Max(monitors, 1), panel);
    }

    public void Dispose() { }
}
