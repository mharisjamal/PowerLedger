namespace PowerLedger.Core;

/// <summary>Spec §6: a tick within the gap threshold counts in full at the reading's watts; anything longer is a gap
/// (sleep, hibernate, service stop) with zero energy. Non-finite readings contribute nothing.</summary>
public static class EnergyIntegrator
{
    /// <summary>Floor for the gap threshold; the Service raises it with <see cref="GapThresholdFor"/> when sampling slower than 1 Hz.</summary>
    public const double MaxDeltaSeconds = 5;

    /// <summary>Gap threshold for a sample interval: at least <see cref="MaxDeltaSeconds"/>, and two intervals so timer jitter never counts as sleep.</summary>
    public static double GapThresholdFor(double sampleIntervalSeconds) => Math.Max(MaxDeltaSeconds, 2 * sampleIntervalSeconds);

    public static EnergySlice Integrate(Reading r, double maxDeltaSeconds = MaxDeltaSeconds)
    {
        if (!double.IsFinite(r.DeltaSeconds) || !double.IsFinite(r.TotalW) || r.DeltaSeconds <= 0) return EnergySlice.Nothing;
        if (r.DeltaSeconds > maxDeltaSeconds) return EnergySlice.GapOf(r.DeltaSeconds);

        var hours = r.DeltaSeconds / 3600.0;
        var wh = r.TotalW * hours;
        var cpu = r.Components.Cpu * hours;
        var gpu = r.Components.Gpu * hours;
        var display = (r.Components.Display + r.Components.Monitors) * hours;
        var rest = wh - cpu - gpu - display;
        var idleOn = r.UserIdle && r.DisplayOn ? wh : 0;
        var idleOff = r.UserIdle && !r.DisplayOn ? wh : 0;
        return new EnergySlice(wh, cpu, gpu, display, rest, idleOn, idleOff,
            IdleOnSeconds: r.UserIdle && r.DisplayOn ? r.DeltaSeconds : 0,
            IdleOffSeconds: r.UserIdle && !r.DisplayOn ? r.DeltaSeconds : 0,
            OnSeconds: r.DeltaSeconds,
            BatterySeconds: r.OnBattery ? r.DeltaSeconds : 0,
            GapSeconds: 0);
    }
}
