namespace PowerLedger.Core;

public sealed record BucketState(int Bucket, double BaselineW, int Samples);

/// <summary>Serialisable snapshot of a <see cref="CalibrationLearner"/>, keyed by hardware inventory hash in storage.</summary>
public sealed record CalibrationState(IReadOnlyList<BucketState> Buckets)
{
    public static CalibrationState Empty { get; } = new([]);
}
