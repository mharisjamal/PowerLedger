namespace PowerLedger.Core;

/// <summary>
/// Learns the laptop's "rest of system" watts per brightness bucket from battery ticks:
/// observed = measured − cpu − gpu − display, smoothed with an exponential moving average.
/// </summary>
public sealed class CalibrationLearner : IBaselineProvider
{
    private readonly CalibrationOptions _options;
    private readonly double _alpha;
    private readonly double[] _baseline = new double[CalibrationBuckets.Count];
    private readonly int[] _samples = new int[CalibrationBuckets.Count];

    public CalibrationLearner(CalibrationOptions? options = null)
    {
        _options = options ?? new CalibrationOptions();
        _alpha = 1 - Math.Pow(2, -1.0 / _options.HalfLifeSamples);
    }

    public int TotalSamples => _samples.Sum();

    /// <summary>Feed one tick. Ignored unless the sample has a usable discharge rate (see <see cref="Sample.HasDischargeRate"/>) and is not suspect.
    /// The caller must also skip the 3 s after an AC transition.</summary>
    public void Observe(Sample s, double cpuW, double gpuW, double displayW)
    {
        if (s.Suspect || !s.HasDischargeRate || s.BatteryRateW is not { } measured) return;
        var observed = Math.Max(0, measured - cpuW - gpuW - displayW);
        var i = Index(CalibrationBuckets.For(s.Brightness, s.DisplayOn));
        _baseline[i] = _samples[i] == 0 ? observed : _baseline[i] + _alpha * (observed - _baseline[i]);
        _samples[i]++;
    }

    public double? GetBaseline(int bucket)
    {
        var i = Index(bucket);
        if (TotalSamples < _options.MinTotalSamples || _samples[i] < _options.MinBucketSamples) return null;
        return _baseline[i];
    }

    public CalibrationState Export() => new(
        Enumerable.Range(0, CalibrationBuckets.Count)
            .Where(i => _samples[i] > 0)
            .Select(i => new BucketState(BucketOf(i), _baseline[i], _samples[i]))
            .ToList());

    public void Import(CalibrationState state)
    {
        Reset();
        foreach (var b in state.Buckets)
        {
            if (!double.IsFinite(b.BaselineW) || b.BaselineW < 0 || b.Samples <= 0) continue;   // corrupt row: ignore rather than poison the model
            var i = Index(b.Bucket);
            _baseline[i] = b.BaselineW;
            _samples[i] = b.Samples;
        }
    }

    public void Reset()
    {
        Array.Clear(_baseline);
        Array.Clear(_samples);
    }

    private static int Index(int bucket) => Math.Clamp(bucket, CalibrationBuckets.DisplayOff, CalibrationBuckets.MaxBucket) + 1;

    private static int BucketOf(int index) => index - 1;
}
