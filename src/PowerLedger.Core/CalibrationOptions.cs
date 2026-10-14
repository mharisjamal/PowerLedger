namespace PowerLedger.Core;

/// <param name="HalfLifeHours">Hours on battery after which an old observation carries half its weight. Time on mains ages
/// nothing: a laptop that rarely leaves the desk keeps what its last runs on battery taught it.</param>
/// <param name="MinBucketSeconds">Seconds on battery a bucket needs before it is trusted (300 = 5 min), whatever the tick
/// rate: the service reads every five seconds while nobody watches, and a count of ticks would then take five times as long.</param>
/// <param name="MinTotalSeconds">Seconds on battery the machine needs in total before any bucket is trusted (1800 = 30 min).</param>
public sealed record CalibrationOptions(
    double HalfLifeHours = 6,
    int MinBucketSeconds = 300,
    int MinTotalSeconds = 1800);
