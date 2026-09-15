namespace PowerLedger.App;

/// <summary>What a chart's height shows: average watts over the time on, or energy per bucket.</summary>
internal enum ChartUnit
{
    Watts,
    WattHours,
}

/// <summary>One bucket of a stacked chart in the chart's unit, with how long the machine was on and asleep in it.</summary>
internal sealed record ChartBucket(double Cpu, double Gpu, double Display, double Rest, double OnSeconds, double AsleepSeconds)
{
    /// <summary>The height to draw. A negative rest, which measured mode shows when the parts over-report, counts as zero
    /// on every chart (spec §9).</summary>
    public double Total => Cpu + Gpu + Display + Math.Max(0, Rest);
}

/// <param name="At">Where the tick sits, in buckets from the chart's left edge.</param>
/// <param name="Label">What it says, just right of the tick.</param>
internal readonly record struct AxisTick(double At, string Label);

/// <summary>
/// A stacked chart (spec §9): its buckets from the left edge, how many buckets the width holds and how long each is, the
/// time axis, where now falls when the range includes it, the unit, and a sentence that says what it shows.
/// </summary>
internal sealed record ChartModel(
    IReadOnlyList<ChartBucket> Buckets, int Capacity, TimeSpan Bucket, IReadOnlyList<AxisTick> Ticks, double? NowAt, ChartUnit Unit,
    string Description)
{
    public static ChartModel Empty { get; } = new([], 1, TimeSpan.FromMinutes(5), [], null, ChartUnit.Watts, "No readings yet.");
}
