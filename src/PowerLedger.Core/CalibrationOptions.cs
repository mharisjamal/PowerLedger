namespace PowerLedger.Core;

/// <param name="HalfLifeSamples">Samples after which an old observation carries half its weight (600 = 10 min at 1 Hz).</param>
/// <param name="MinBucketSamples">Samples a bucket needs before it is trusted (300 = 5 min).</param>
/// <param name="MinTotalSamples">Battery samples the machine needs in total before any bucket is trusted (1800 = 30 min).</param>
public sealed record CalibrationOptions(
    int HalfLifeSamples = 600,
    int MinBucketSamples = 300,
    int MinTotalSamples = 1800);
