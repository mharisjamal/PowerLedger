namespace PowerLedger.Sensors;

/// <summary>
/// Whether the machine is on mains and, while it is not, how fast the battery is draining. This is the only
/// source that measures the whole machine rather than one of its parts, so it decides whether a reading is Measured.
/// A UPS on USB also looks like a battery to Windows. One Windows marks short-term is ignored here, because its drain
/// includes everything else plugged into it and would teach the calibration a baseline the machine does not have.
/// Windows doesn't mark every UPS that way, so a desktop enclosure outranks the battery when the chassis is detected,
/// and a desktop's readings never come from a battery.
/// <para>
/// The battery rule. Energy the battery gives the machine is counted when it is given: on battery the discharge is the
/// measured total, and on AC a discharge (the adapter can't carry the load) is a floor under the total. Energy that goes
/// into the battery while it charges is never counted: the PC hasn't used it yet, and counting the charge and then the
/// discharge it later pays for would count the same energy twice. So the charge rate is kept apart in
/// <see cref="SampleDraft.BatteryChargeW"/>, where it is only ever taken off a figure that holds it, and never added.
/// A battery that reports relative units (BATTERY_CAPACITY_RELATIVE) gives a rate that is not milliwatts, so it gives
/// neither: the machine still reads its mains state from it, and its total comes from elsewhere.
/// </para>
/// </summary>
public sealed class BatterySource : ISensorSource
{
    /// <summary>How long a battery state is kept before Windows is asked again, unless it announces a change first. The
    /// question can reach the battery's own firmware through the embedded controller, and the rate it gives moves slowly;
    /// energy is still counted over each tick's own length.</summary>
    public const long RefreshMilliseconds = 5_000;

    private readonly Func<Win32.BatteryState?> _read;
    private readonly bool _relativeUnits;
    private readonly Func<long>? _clock;
    private readonly ISystemSignals? _signals;
    private Win32.BatteryState? _state;
    private long _readAt;
    private int _changesSeen;

    /// <summary>The machine's battery: asked every five seconds, and at once when Windows announces that the mains came or
    /// went or the charge moved. Whether the batteries are short-term is asked once, when the source is built.</summary>
    public BatterySource()
        : this(ShortTermAskedOnce(Win32.BatteriesAreShortTerm, Win32.ReadBatteryState), BatteryUnits.Relative(),
               static () => Environment.TickCount64, SystemSignals.Windows)
    {
    }

    /// <summary>Test seam: any source of battery states, any clock and any announcements.</summary>
    /// <param name="relativeUnits">True when the machine's battery reports relative units rather than milliwatts.</param>
    /// <param name="clockMilliseconds">A monotonic clock; left out, every tick asks Windows afresh.</param>
    /// <param name="signals">What Windows announces; left out, nothing is.</param>
    internal BatterySource(Func<Win32.BatteryState?> read, bool relativeUnits = false, Func<long>? clockMilliseconds = null, ISystemSignals? signals = null)
    {
        _read = read;
        _relativeUnits = relativeUnits;
        _clock = clockMilliseconds;
        _signals = signals;
        _changesSeen = signals?.BatteryChanges ?? 0;
        var state = read();
        _state = state;
        _readAt = clockMilliseconds?.Invoke() ?? 0;
        Supported = state?.OwnBattery ?? false;
        Unavailable = Supported ? null
            : state is null ? "Windows did not answer"
            : state.Value.Present ? "the only battery is a UPS, which powers more than this machine"
            : "no battery fitted";
    }

    public string Name => "battery";

    public bool Supported { get; }

    public string? Unavailable { get; }

    /// <summary>A reader that asks whether the batteries are short-term once, on its first read, and reuses the answer.</summary>
    internal static Func<Win32.BatteryState?> ShortTermAskedOnce(Func<bool> shortTerm, Func<bool, Win32.BatteryState?> read)
    {
        var asked = new Lazy<bool>(shortTerm, LazyThreadSafetyMode.None);
        return () => read(asked.Value);
    }

    public void Contribute(SampleDraft draft)
    {
        if (Current() is not { } state) return;
        draft.OnBattery = state.OwnBattery && !state.AcOnLine;

        // Windows reports the rate as negative while discharging and positive while charging. A zero means "not moving",
        // which is no reading. On battery any rate is a discharge, whatever its sign.
        var known = state.OwnBattery && !_relativeUnits && state.RateMilliwatts != 0 && state.RateMilliwatts != Win32.UnknownRate;
        var discharging = known && (draft.OnBattery || state.RateMilliwatts < 0);
        var charging = known && !draft.OnBattery && state.RateMilliwatts > 0;
        draft.BatteryRateW = discharging ? Math.Abs((long)state.RateMilliwatts) / 1000.0 : null;
        draft.BatteryChargeW = charging ? state.RateMilliwatts / 1000.0 : null;
    }

    /// <summary>The state last read, read afresh when it is old or Windows has announced a change. A read Windows declines
    /// keeps the last state and is tried again on the next tick.</summary>
    private Win32.BatteryState? Current()
    {
        var now = _clock?.Invoke() ?? 0;
        var changes = _signals?.BatteryChanges ?? 0;
        if (_clock is not null && _state is not null && changes == _changesSeen && now - _readAt < RefreshMilliseconds) return _state;

        _changesSeen = changes;
        if (_read() is { } state)
        {
            _state = state;
            _readAt = now;
        }
        return _state;
    }

    public void Dispose() { }
}
