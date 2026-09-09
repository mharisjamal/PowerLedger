namespace PowerLedger.Core;

/// <summary>
/// Learns the laptop's "rest of system" watts per brightness bucket from battery ticks:
/// residual = measured − cpu − gpu − display, averaged per bucket (a plain running mean while the bucket is young,
/// then an exponential moving average with the configured half-life). Negative residuals are averaged as they are
/// (a clamp would bias the baseline upward) and clamped to zero only when reported. Thread-safe: the Service samples
/// on one thread and exports or resets from the pipe thread.
/// </summary>
public sealed class CalibrationLearner : IBaselineProvider
{
    /// <summary>Counts saturate here; anything past the trust thresholds carries no information.</summary>
    public const int SampleCap = int.MaxValue / CalibrationBuckets.Count;

    /// <summary>Baselines outside ±this are treated as corrupt on import.</summary>
    public const double SanityBoundW = 10_000;

    private readonly object _gate = new();
    private readonly CalibrationOptions _options;
    private readonly double _alpha;
    private readonly double[] _baseline = new double[CalibrationBuckets.Count];
    private readonly int[] _samples = new int[CalibrationBuckets.Count];

    public CalibrationLearner(CalibrationOptions? options = null)
    {
        _options = options ?? new CalibrationOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.HalfLifeSamples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MinBucketSamples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MinTotalSamples);
        _alpha = 1 - Math.Pow(2, -1.0 / _options.HalfLifeSamples);
    }

    /// <summary>Battery samples observed so far across all buckets (saturating).</summary>
    public int TotalSamples
    {
        get { lock (_gate) { return _samples.Sum(); } }
    }

    /// <summary>Feed one tick. Ignored unless the sample has a usable discharge rate (see <see cref="Sample.HasDischargeRate"/>),
    /// is not suspect, and the parts are finite. The caller must also skip the 3 s after an AC transition.</summary>
    public void Observe(Sample s, double cpuW, double gpuW, double displayW)
    {
        if (s.Suspect || !s.HasDischargeRate || s.BatteryRateW is not { } measured) return;
        var residual = measured - cpuW - gpuW - displayW;
        if (!double.IsFinite(residual)) return;
        var i = Index(CalibrationBuckets.For(s.Brightness, s.DisplayOn));
        lock (_gate)
        {
            // Running mean while young (step 1/(n+1)), exponential average with the configured half-life once older.
            var step = Math.Max(_alpha, 1.0 / (_samples[i] + 1));
            _baseline[i] += step * (residual - _baseline[i]);
            if (_samples[i] < SampleCap) _samples[i]++;
        }
    }

    /// <summary>Learned baseline for a bucket, never negative; null for unknown buckets and until the bucket and the machine have enough samples.</summary>
    public double? GetBaseline(int bucket)
    {
        if (!IsValid(bucket)) return null;
        var i = Index(bucket);
        lock (_gate)
        {
            if (_samples.Sum() < _options.MinTotalSamples || _samples[i] < _options.MinBucketSamples) return null;
            return Math.Max(0, _baseline[i]);
        }
    }

    /// <summary>Snapshot of every bucket with at least one sample, in bucket order, with raw (possibly negative) averages.</summary>
    public CalibrationState Export()
    {
        lock (_gate)
        {
            return new CalibrationState(
                Enumerable.Range(0, CalibrationBuckets.Count)
                    .Where(i => _samples[i] > 0)
                    .Select(i => new BucketState(BucketOf(i), _baseline[i], _samples[i]))
                    .ToList());
        }
    }

    /// <summary>Replaces all state with the snapshot. Rows with an unknown bucket, a non-finite or absurd baseline, or no samples are ignored.</summary>
    public void Import(CalibrationState state)
    {
        lock (_gate)
        {
            Clear();
            foreach (var b in state.Buckets)
            {
                if (!IsValid(b.Bucket) || !double.IsFinite(b.BaselineW) || Math.Abs(b.BaselineW) > SanityBoundW || b.Samples <= 0) continue;
                var i = Index(b.Bucket);
                _baseline[i] = b.BaselineW;
                _samples[i] = Math.Min(b.Samples, SampleCap);
            }
        }
    }

    /// <summary>Forgets everything learned.</summary>
    public void Reset()
    {
        lock (_gate) { Clear(); }
    }

    private void Clear()
    {
        Array.Clear(_baseline);
        Array.Clear(_samples);
    }

    private static bool IsValid(int bucket) => bucket is >= CalibrationBuckets.DisplayOff and <= CalibrationBuckets.MaxBucket;

    private static int Index(int bucket) => bucket + 1;

    private static int BucketOf(int index) => index - 1;
}
