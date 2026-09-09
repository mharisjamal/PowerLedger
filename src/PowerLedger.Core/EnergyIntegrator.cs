namespace PowerLedger.Core;

/// <summary>Spec §6: a tick of at most <see cref="MaxDeltaSeconds"/> counts in full; anything longer is a gap.</summary>
public static class EnergyIntegrator
{
    public const double MaxDeltaSeconds = 5;

    public static EnergySlice Integrate(Reading r, double maxDeltaSeconds = MaxDeltaSeconds)
    {
        if (r.DeltaSeconds <= 0) return EnergySlice.Nothing;
        if (r.DeltaSeconds > maxDeltaSeconds) return EnergySlice.GapOf(r.DeltaSeconds);

        var hours = r.DeltaSeconds / 3600.0;
        var wh = r.TotalW * hours;
        var cpu = r.Components.Cpu * hours;
        var gpu = r.Components.Gpu * hours;
        var display = r.Components.Display * hours;
        var rest = wh - cpu - gpu - display;
        var idleOn = r.UserIdle && r.DisplayOn ? wh : 0;
        var idleOff = r.UserIdle && !r.DisplayOn ? wh : 0;
        return new EnergySlice(wh, cpu, gpu, display, rest, idleOn, idleOff,
            IdleOnSeconds: r.UserIdle && r.DisplayOn ? r.DeltaSeconds : 0,
            IdleOffSeconds: r.UserIdle && !r.DisplayOn ? r.DeltaSeconds : 0,
            OnSeconds: r.DeltaSeconds,
            BatterySeconds: r.OnBattery ? r.DeltaSeconds : 0,
            GapSeconds: 0, Gap: false);
    }
}
