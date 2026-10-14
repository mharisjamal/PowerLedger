using PowerLedger.Contracts;

namespace PowerLedger.Core;

/// <summary>
/// One minute or one hour of energy (spec §7). AvgW is energy over on-time, so gaps do not drag it down.
/// SampleCount counts every tick folded in, gaps included; GapSeconds is the length of gaps whose closing tick
/// fell in this row, so it can exceed the row length after a long stall; the three quality-seconds partition OnSeconds.
/// AsleepWh is what the machine drew asleep, estimated (accuracy A10), over the sleeps among those gaps; it is in no other
/// figure, EnergyWh and AvgW included, which are the machine's awake. BatteryWh is the part of EnergyWh the machine's battery
/// gave, at the pack as the live watts are, which the totals count at the wall it was charged from (accuracy A9).
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
    double AsleepWh = 0, double BatteryWh = 0)
{
    /// <summary>The quality with the most on-time; ties go to the higher quality, and an empty row is Estimated.</summary>
    public Quality DominantQuality =>
        MeasuredSeconds >= CalibratedSeconds && MeasuredSeconds >= EstimatedSeconds && MeasuredSeconds > 0 ? Quality.Measured
        : CalibratedSeconds >= EstimatedSeconds && CalibratedSeconds > 0 ? Quality.Calibrated
        : Quality.Estimated;

    /// <summary>What the machine drew awake as the totals count it: what came from the battery at the wall equivalent
    /// (<see cref="BatteryRoundTrip"/>), the rest as it was.</summary>
    public double AwakeCountedWh => EnergyWh + (BatteryWh * (BatteryRoundTrip.Factor - 1));

    /// <summary>The energy the totals count for this row: what the machine drew awake, from the battery at the wall
    /// equivalent, and, estimated, asleep.</summary>
    public double CountedWh => AwakeCountedWh + AsleepWh;

    /// <summary>What each watt-hour of this row's awake energy counts as in the totals: more than one on battery, whose energy
    /// counts at the wall equivalent. Idle energy, at the pack like the rest, is put on the totals' scale with it (Plan AC
    /// review), so it is priced as what it is part of.</summary>
    public double CountedPerWh => EnergyWh > 0 ? AwakeCountedWh / EnergyWh : 1;

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
        BatteryWh = BatteryWh + other.BatteryWh,
    };
}
