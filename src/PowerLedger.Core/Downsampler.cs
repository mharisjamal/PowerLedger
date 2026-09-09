using PowerLedger.Contracts;

namespace PowerLedger.Core;

public static class Downsampler
{
    public static Aggregate ToMinute(DateTimeOffset minuteStart, IReadOnlyList<Reading> readings, double maxDeltaSeconds = EnergyIntegrator.MaxDeltaSeconds)
    {
        var a = Aggregate.Empty(minuteStart);
        foreach (var r in readings)
        {
            var e = EnergyIntegrator.Integrate(r, maxDeltaSeconds);
            a = a with
            {
                EnergyWh = a.EnergyWh + e.Wh,
                CpuWh = a.CpuWh + e.CpuWh,
                GpuWh = a.GpuWh + e.GpuWh,
                DisplayWh = a.DisplayWh + e.DisplayWh,
                RestWh = a.RestWh + e.RestWh,
                IdleOnWh = a.IdleOnWh + e.IdleOnWh,
                IdleOffWh = a.IdleOffWh + e.IdleOffWh,
                IdleOnSeconds = a.IdleOnSeconds + e.IdleOnSeconds,
                IdleOffSeconds = a.IdleOffSeconds + e.IdleOffSeconds,
                OnSeconds = a.OnSeconds + e.OnSeconds,
                BatterySeconds = a.BatterySeconds + e.BatterySeconds,
                GapSeconds = a.GapSeconds + e.GapSeconds,
                SampleCount = a.SampleCount + 1,
                MaxW = e.Gap ? a.MaxW : Math.Max(a.MaxW, r.TotalW),
                MeasuredSeconds = a.MeasuredSeconds + (r.Quality == Quality.Measured ? e.OnSeconds : 0),
                CalibratedSeconds = a.CalibratedSeconds + (r.Quality == Quality.Calibrated ? e.OnSeconds : 0),
                EstimatedSeconds = a.EstimatedSeconds + (r.Quality == Quality.Estimated ? e.OnSeconds : 0),
            };
        }
        return a with { AvgW = Average(a.EnergyWh, a.OnSeconds) };
    }

    public static Aggregate ToHour(DateTimeOffset hourStart, IReadOnlyList<Aggregate> minutes)
    {
        var a = Aggregate.Empty(hourStart);
        foreach (var m in minutes)
        {
            a = a with
            {
                EnergyWh = a.EnergyWh + m.EnergyWh,
                CpuWh = a.CpuWh + m.CpuWh,
                GpuWh = a.GpuWh + m.GpuWh,
                DisplayWh = a.DisplayWh + m.DisplayWh,
                RestWh = a.RestWh + m.RestWh,
                IdleOnWh = a.IdleOnWh + m.IdleOnWh,
                IdleOffWh = a.IdleOffWh + m.IdleOffWh,
                IdleOnSeconds = a.IdleOnSeconds + m.IdleOnSeconds,
                IdleOffSeconds = a.IdleOffSeconds + m.IdleOffSeconds,
                OnSeconds = a.OnSeconds + m.OnSeconds,
                BatterySeconds = a.BatterySeconds + m.BatterySeconds,
                GapSeconds = a.GapSeconds + m.GapSeconds,
                SampleCount = a.SampleCount + m.SampleCount,
                MaxW = Math.Max(a.MaxW, m.MaxW),
                MeasuredSeconds = a.MeasuredSeconds + m.MeasuredSeconds,
                CalibratedSeconds = a.CalibratedSeconds + m.CalibratedSeconds,
                EstimatedSeconds = a.EstimatedSeconds + m.EstimatedSeconds,
            };
        }
        return a with { AvgW = Average(a.EnergyWh, a.OnSeconds) };
    }

    private static double Average(double wh, double onSeconds) => onSeconds > 0 ? wh / (onSeconds / 3600.0) : 0;
}
