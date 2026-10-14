namespace PowerLedger.Core;

/// <summary>One brightness bucket of a <see cref="CalibrationLearner"/>, as stored. The learner keeps, per bucket, weighted
/// minute averages of the CPU package's watts and of what the laptop drew besides its GPU and display; these are their
/// means and spreads.</summary>
/// <param name="BaselineW">The bucket's mean rest of system (what the battery gave less the CPU, GPU and display), raw, so
/// it may be below zero.</param>
/// <param name="Samples">Seconds on battery observed in the bucket, whole (saturating), for the trust thresholds and the
/// status. Before 0.11 a count of one-second ticks, which is the same.</param>
/// <param name="CpuW">The mean CPU package watts over the same data; null for a bucket saved before 0.11, which kept only
/// <paramref name="BaselineW"/>.</param>
/// <param name="Minutes">The weight of the data, in minutes on battery, after ageing.</param>
/// <param name="CpuSpread">The weighted sum of squared deviations of the CPU's minute averages from their mean (W²·min).</param>
/// <param name="CpuDrawSpread">The weighted sum of the CPU's deviations times the draw's (W²·min).</param>
public sealed record BucketState(
    int Bucket, double BaselineW, int Samples,
    double? CpuW = null, double Minutes = 0, double CpuSpread = 0, double CpuDrawSpread = 0);

/// <summary>Serialisable snapshot of a <see cref="CalibrationLearner"/>, keyed by hardware inventory hash in storage.</summary>
public sealed record CalibrationState(IReadOnlyList<BucketState> Buckets)
{
    public static CalibrationState Empty { get; } = new([]);
}
