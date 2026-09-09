using PowerLedger.Contracts;

namespace PowerLedger.Core;

/// <summary>One minute or one hour of energy. AvgW is energy-weighted over on-time, so gaps do not drag it down.</summary>
public sealed record Aggregate(
    DateTimeOffset Start,
    double AvgW, double MaxW,
    double EnergyWh, double CpuWh, double GpuWh, double DisplayWh, double RestWh,
    double IdleOnWh, double IdleOffWh,
    double IdleOnSeconds, double IdleOffSeconds,
    double OnSeconds, double BatterySeconds, double GapSeconds,
    int SampleCount,
    double MeasuredSeconds, double CalibratedSeconds, double EstimatedSeconds)
{
    public Quality DominantQuality =>
        MeasuredSeconds >= CalibratedSeconds && MeasuredSeconds >= EstimatedSeconds && MeasuredSeconds > 0 ? Quality.Measured
        : CalibratedSeconds >= EstimatedSeconds && CalibratedSeconds > 0 ? Quality.Calibrated
        : Quality.Estimated;

    public static Aggregate Empty(DateTimeOffset start) => new(start, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}
