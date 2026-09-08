namespace PowerLedger.Core;

/// <summary>Buckets baselines by panel brightness in 10 % steps (0..10); -1 means the display is off.</summary>
public static class CalibrationBuckets
{
    public const int DisplayOff = -1;
    public const int Count = 12;

    public static int For(double? brightness, bool displayOn)
    {
        if (!displayOn) return DisplayOff;
        var b = Math.Clamp(brightness ?? 0.5, 0, 1);
        return (int)Math.Round(b * 10, MidpointRounding.AwayFromZero);
    }
}
