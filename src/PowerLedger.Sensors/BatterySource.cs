namespace PowerLedger.Sensors;

/// <summary>
/// Whether the machine is on mains and, while it is not, how fast the battery is draining. This is the only
/// source that measures the whole machine rather than one of its parts, so it decides whether a reading is Measured.
/// A UPS on USB also looks like a battery to Windows. One Windows marks short-term is ignored here, because its drain
/// includes everything else plugged into it and would teach the calibration a baseline the machine does not have.
/// Windows doesn't mark every UPS that way, so a desktop enclosure outranks the battery when the chassis is detected,
/// and a desktop's readings never come from a battery.
/// </summary>
public sealed class BatterySource : ISensorSource
{
    private readonly Func<Win32.BatteryState?> _read;

    public BatterySource() : this(Win32.ReadBatteryState) { }

    /// <summary>Test seam: any source of battery states.</summary>
    internal BatterySource(Func<Win32.BatteryState?> read)
    {
        _read = read;
        var state = read();
        Supported = state?.OwnBattery ?? false;
        Unavailable = Supported ? null
            : state is null ? "Windows did not answer"
            : state.Value.Present ? "the only battery is a UPS, which powers more than this machine"
            : "no battery fitted";
    }

    public string Name => "battery";

    public bool Supported { get; }

    public string? Unavailable { get; }

    public void Contribute(SampleDraft draft)
    {
        if (_read() is not { } state) return;
        draft.OnBattery = state.OwnBattery && !state.AcOnLine;

        // Windows reports the rate as negative while discharging. A zero means "not moving", which is no reading.
        var discharging = draft.OnBattery && state.RateMilliwatts != 0 && state.RateMilliwatts != Win32.UnknownRate;
        draft.BatteryRateW = discharging ? Math.Abs(state.RateMilliwatts) / 1000.0 : null;
    }

    public void Dispose() { }
}
