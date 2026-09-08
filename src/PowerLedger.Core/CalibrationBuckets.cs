namespace PowerLedger.Core;

/// <summary>Buckets baselines by panel brightness in 10 % steps (0..10); -1 means the display is off.</summary>
public static class CalibrationBuckets
{
    public const int DisplayOff = -1;
    public const int MaxBucket = 10;
    /// <summary>Number of buckets: DisplayOff plus 0..MaxBucket.</summary>
    public const int Count = MaxBucket + 2;

    public static int For(double? brightness, bool displayOn)
    {
        if (!displayOn) return DisplayOff;
        var b = brightness is { } value && !double.IsNaN(value) ? Math.Clamp(value, 0, 1) : 0.5;
        return (int)Math.Round(b * MaxBucket, MidpointRounding.AwayFromZero);
    }
}
