namespace PowerLedger.Core;

/// <param name="HalfLifeHours">Hours on battery after which an old observation carries half its weight. Time on mains ages
/// nothing: a laptop that rarely leaves the desk keeps what its last runs on battery taught it.</param>
/// <param name="MinBucketSamples">Battery ticks a bucket needs before it is trusted (300 = 5 min at 1 Hz).</param>
/// <param name="MinTotalSamples">Battery ticks the machine needs in total before any bucket is trusted (1800 = 30 min).</param>
public sealed record CalibrationOptions(
    double HalfLifeHours = 6,
    int MinBucketSamples = 300,
    int MinTotalSamples = 1800);
