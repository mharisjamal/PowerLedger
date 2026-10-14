using PowerLedger.Contracts;

namespace PowerLedger.Core;

/// <summary>
/// One minute or one hour of energy (spec §7). AvgW is energy over on-time, so gaps do not drag it down.
/// SampleCount counts every tick folded in, gaps included; GapSeconds is the length of gaps whose closing tick
/// fell in this row, so it can exceed the row length after a long stall; the three quality-seconds partition OnSeconds.
/// AsleepWh is what the machine drew asleep, estimated (accuracy A10), over the sleeps among those gaps; it is in no other
/// figure, EnergyWh and AvgW included, which are the machine's awake.
/// </summary>
public sealed record Aggregate(
    DateTimeOffset Start,
    double AvgW, double MaxW,
    double EnergyWh, double CpuWh, double GpuWh, double DisplayWh, double RestWh,
    double IdleOnWh, double IdleOffWh,
    double IdleOnSeconds, double IdleOffSeconds,
    double OnSeconds, double BatterySeconds, double GapSeconds,
    int SampleCount,
    double MeasuredSeconds, double CalibratedSeconds, double EstimatedSeconds,
    double AsleepWh = 0)
{
    /// <summary>The quality with the most on-time; ties go to the higher quality, and an empty row is Estimated.</summary>
    public Quality DominantQuality =>
        MeasuredSeconds >= CalibratedSeconds && MeasuredSeconds >= EstimatedSeconds && MeasuredSeconds > 0 ? Quality.Measured
        : CalibratedSeconds >= EstimatedSeconds && CalibratedSeconds > 0 ? Quality.Calibrated
        : Quality.Estimated;

    /// <summary>The energy the totals count for this row: what the machine drew awake and, estimated, asleep.</summary>
    public double CountedWh => EnergyWh + AsleepWh;

    public static Aggregate Empty(DateTimeOffset start) => new(start, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>This row plus another: sums every additive field, keeps the larger peak and this Start. AvgW is left for the caller to recompute.</summary>
    public Aggregate Plus(Aggregate other) => this with
    {
        MaxW = Math.Max(MaxW, other.MaxW),
        EnergyWh = EnergyWh + other.EnergyWh,
        CpuWh = CpuWh + other.CpuWh,
        GpuWh = GpuWh + other.GpuWh,
        DisplayWh = DisplayWh + other.DisplayWh,
        RestWh = RestWh + other.RestWh,
        IdleOnWh = IdleOnWh + other.IdleOnWh,
        IdleOffWh = IdleOffWh + other.IdleOffWh,
        IdleOnSeconds = IdleOnSeconds + other.IdleOnSeconds,
        IdleOffSeconds = IdleOffSeconds + other.IdleOffSeconds,
        OnSeconds = OnSeconds + other.OnSeconds,
        BatterySeconds = BatterySeconds + other.BatterySeconds,
        GapSeconds = GapSeconds + other.GapSeconds,
        SampleCount = SampleCount + other.SampleCount,
        MeasuredSeconds = MeasuredSeconds + other.MeasuredSeconds,
        CalibratedSeconds = CalibratedSeconds + other.CalibratedSeconds,
        EstimatedSeconds = EstimatedSeconds + other.EstimatedSeconds,
        AsleepWh = AsleepWh + other.AsleepWh,
    };
}
