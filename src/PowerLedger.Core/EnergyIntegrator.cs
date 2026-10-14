namespace PowerLedger.Core;

/// <summary>Spec §6: a tick within the gap threshold counts in full at the reading's watts; anything longer is a gap
/// (sleep, hibernate, service stop) with zero energy. Non-finite readings contribute nothing.</summary>
public static class EnergyIntegrator
{
    /// <summary>The gap threshold for readings taken with nothing known of when the machine slept; the Service, which
    /// knows, uses <see cref="GapThresholdFor"/> and marks each tick after a resume a gap itself.</summary>
    public const double MaxDeltaSeconds = 5;

    /// <summary>The longest tick counted as awake when the machine did not wake within it (accuracy A17): a stall while it
    /// ran, a hung read or a slow disk, still drew power, which counting it as sleep lost. Past a minute, the service
    /// missed the time, as in a sleep it was never told of.</summary>
    public const double LongestAwakeTickSeconds = 60;

    /// <summary>Gap threshold for a sample interval: <see cref="LongestAwakeTickSeconds"/>, or two intervals when they are
    /// longer, so neither a stall nor timer jitter while awake counts as sleep.</summary>
    public static double GapThresholdFor(double sampleIntervalSeconds) => Math.Max(LongestAwakeTickSeconds, 2 * sampleIntervalSeconds);

    public static EnergySlice Integrate(Reading r, double maxDeltaSeconds = MaxDeltaSeconds)
    {
        if (!double.IsFinite(r.DeltaSeconds) || !double.IsFinite(r.TotalW) || r.DeltaSeconds <= 0) return EnergySlice.Nothing;
        if (r.DeltaSeconds > maxDeltaSeconds)
        {
            // A sleep the service was told of carries what the machine drew asleep (accuracy A10); any other gap, none.
            var asleepWh = r.AsleepW is { } asleepW && double.IsFinite(asleepW) && asleepW > 0 ? asleepW * r.DeltaSeconds / 3600.0 : 0;
            return EnergySlice.GapOf(r.DeltaSeconds) with { AsleepWh = asleepWh };
        }

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
            GapSeconds: 0,
            BatteryWh: r.OnBattery ? Math.Min(wh, PackWatts(r) * hours) : 0);
    }

    /// <summary>What the battery gave on a tick on battery: the reading's pack watts, a monitor with its own plug left out
    /// (Plan AC review), or its whole total when it says nothing of them, as a reading stored before them does.</summary>
    private static double PackWatts(Reading r) => r.PackW is { } pack && double.IsFinite(pack) && pack >= 0 ? pack : r.TotalW;
}
