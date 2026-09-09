using PowerLedger.Contracts;

namespace PowerLedger.Core;

/// <summary>Folds ticks into minute rows and minute rows into hour rows. Callers pass exactly the rows of that period;
/// a tick straddling a boundary counts wholly in the period of its timestamp.</summary>
public static class Downsampler
{
    /// <summary>One minute from its readings. Energy is integrated per tick, never avg × 60; only ticks that contribute on-time can set the peak.</summary>
    public static Aggregate ToMinute(DateTimeOffset minuteStart, IReadOnlyList<Reading> readings, double maxDeltaSeconds = EnergyIntegrator.MaxDeltaSeconds)
    {
        var a = Aggregate.Empty(minuteStart);
        foreach (var r in readings)
        {
            a = a.Plus(FromTick(minuteStart, r, EnergyIntegrator.Integrate(r, maxDeltaSeconds)));
        }
        return WithAverage(a);
    }

    /// <summary>One hour from its minute rows.</summary>
    public static Aggregate ToHour(DateTimeOffset hourStart, IReadOnlyList<Aggregate> minutes)
    {
        var a = Aggregate.Empty(hourStart);
        foreach (var m in minutes)
        {
            a = a.Plus(m);
        }
        return WithAverage(a);
    }

    private static Aggregate FromTick(DateTimeOffset start, Reading r, EnergySlice e) => new(
        start,
        AvgW: 0,
        MaxW: e.OnSeconds > 0 ? r.TotalW : 0,
        e.Wh, e.CpuWh, e.GpuWh, e.DisplayWh, e.RestWh,
        e.IdleOnWh, e.IdleOffWh,
        e.IdleOnSeconds, e.IdleOffSeconds,
        e.OnSeconds, e.BatterySeconds, e.GapSeconds,
        SampleCount: 1,
        MeasuredSeconds: r.Quality == Quality.Measured ? e.OnSeconds : 0,
        CalibratedSeconds: r.Quality == Quality.Calibrated ? e.OnSeconds : 0,
        EstimatedSeconds: r.Quality == Quality.Estimated ? e.OnSeconds : 0);

    private static Aggregate WithAverage(Aggregate a) => a with { AvgW = a.OnSeconds > 0 ? a.EnergyWh / (a.OnSeconds / 3600.0) : 0 };
}
