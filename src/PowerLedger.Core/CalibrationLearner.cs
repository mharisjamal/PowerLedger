namespace PowerLedger.Core;

/// <summary>
/// Learns a laptop's "rest of system" from its battery (spec §5, Plan AC): what the battery gives less the CPU package,
/// the GPU and the display, as a level per brightness bucket plus a share of each CPU watt. The platform's own draw rises
/// with the processor's (voltage regulators, memory, fabric): on the owner's i7-1165G7 every CPU watt showed up as
/// about 1.6 W at the battery, minute by minute, and a flat rest read AC totals 17% low under load.
/// <list type="bullet">
/// <item>Ticks are averaged to minutes, closed early by a pause or a change of bucket, since Windows updates the battery
/// rate only every 20 s or so and it lags the CPU by seconds: tick by tick, the lag would hide the slope.</item>
/// <item>Each minute goes into its bucket's weighted means and spreads; the data ages with a half-life counted in hours on
/// battery (<see cref="CalibrationOptions.HalfLifeHours"/>), so time on mains ages nothing.</item>
/// <item>The slope (draw per CPU watt, which is 1 + the rest's share) is one for the machine, fitted within each bucket
/// and pooled, so brightness can't pass for CPU; it starts at the uncalibrated model's
/// (<see cref="PowerModel.LaptopRestPerCpuWatt"/>), which data outweighs once the CPU has moved a few watts for some
/// minutes, and stays within 1.0 to 2.2.</item>
/// <item>Each bucket's level is shrunk toward a line through all the buckets' levels by brightness, so a bucket seen for
/// two minutes leans on its neighbours; the display-off bucket leans on their mean.</item>
/// </list>
/// Thread-safe: the Service samples on one thread and exports or resets from the pipe thread.
/// </summary>
public sealed class CalibrationLearner : IBaselineProvider
{
    /// <summary>A bucket's seconds on battery saturate here; anything past the trust thresholds carries no information.</summary>
    public const int SecondsCap = int.MaxValue / CalibrationBuckets.Count;

    /// <summary>Baselines outside ±this are treated as corrupt on import.</summary>
    public const double SanityBoundW = 10_000;

    /// <summary>The most of each CPU watt the rest may add: a total slope of 2.2, the top of what the owner's runs showed
    /// (1.2 to 2.2).</summary>
    public const double MostRestPerCpuWatt = 1.2;

    /// <summary>Where a bucket saved before 0.11, which kept only its mean rest, is taken to have been learned: a typical
    /// CPU package draw on battery (the owner's minutes averaged 5 to 7 W). Its level is kept there and the slope starts at
    /// the prior.</summary>
    public const double MigratedCpuW = 5;

    /// <summary>The weight such a bucket gets at most: what the old average held, a ten-minute half-life at 1 Hz being
    /// 600 / ln 2 ticks, about 14 minutes.</summary>
    public const double MigratedMinutes = 600 / 0.6931471805599453 / 60;

    /// <summary>Ticks are averaged over this long before they count.</summary>
    public const double WindowSeconds = 60;

    /// <summary>A tick further than this from the last ends the minute being averaged.</summary>
    public const double LongestPauseSeconds = 10;

    /// <summary>How strongly the prior slope holds, in W²·min of CPU spread: an hour of minutes 1 W either side of their
    /// mean weighs as much as the prior.</summary>
    public const double SlopePriorWeight = 60;

    /// <summary>How strongly a bucket's level leans on the brightness line, in minutes of its own data.</summary>
    public const double LineWeightMinutes = 10;

    private const double LeastSlope = 1.0;

    private readonly object _gate = new();
    private readonly CalibrationOptions _options;
    private readonly double _halfLifeSeconds;
    private readonly Stats[] _stats = new Stats[CalibrationBuckets.Count];
    private readonly double[] _seconds = new double[CalibrationBuckets.Count];   // on battery, each tick's capped length

    /// <summary>The minute being averaged: its bucket's index, or -1 for none, and its time-weighted sums.</summary>
    private int _window = -1;
    private double _windowSeconds, _windowCpu, _windowDraw;
    private DateTimeOffset _windowLast;

    public CalibrationLearner(CalibrationOptions? options = null)
    {
        _options = options ?? new CalibrationOptions();
        if (!(double.IsFinite(_options.HalfLifeHours) && _options.HalfLifeHours > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.HalfLifeHours, "The half-life must be a positive number of hours.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MinBucketSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MinTotalSeconds);
        _halfLifeSeconds = _options.HalfLifeHours * 3600;
    }

    /// <summary>Seconds on battery learned from so far across all buckets, whole (each bucket's saturating at
    /// <see cref="SecondsCap"/>): each tick counts its own length, at most <see cref="LongestPauseSeconds"/>.</summary>
    public int TotalSeconds
    {
        get { lock (_gate) { return (int)_seconds.Sum(); } }
    }

    /// <summary>The rest's share of each CPU watt as learned, for the status; null until the CPU has been seen at more than
    /// one level on battery, while the share is still the prior's.</summary>
    public double? LearnedRestPerCpuWatt
    {
        get
        {
            lock (_gate)
            {
                var stats = Snapshot();
                return SpreadOf(stats) > 1e-9 ? SlopeOf(stats) - 1 : null;
            }
        }
    }

    /// <summary>Feed one tick. Ignored unless the sample has a usable discharge rate (see <see cref="Sample.HasDischargeRate"/>),
    /// is not suspect, and the parts are finite; such a tick also ends the minute being averaged. The caller must also skip
    /// the 3 s after an AC transition.</summary>
    /// <param name="displayW">What the display draws from the battery: the built-in panel, and any monitor running off the
    /// laptop, which is no part of the rest of the laptop.</param>
    public void Observe(Sample s, double cpuW, double gpuW, double displayW)
    {
        var usable = !s.Suspect && s.HasDischargeRate && s.BatteryRateW is { } measured
                     && double.IsFinite(measured - cpuW - gpuW - displayW);
        lock (_gate)
        {
            if (!usable)
            {
                CloseWindow();
                return;
            }
            var draw = s.BatteryRateW!.Value - gpuW - displayW;
            var i = Index(CalibrationBuckets.For(s.Brightness, s.DisplayOn));
            var pause = (s.Timestamp - _windowLast).TotalSeconds;
            if (_window != i || pause is < 0 or > LongestPauseSeconds) CloseWindow();
            var seconds = double.IsFinite(s.DeltaSeconds) && s.DeltaSeconds > 0 ? Math.Min(s.DeltaSeconds, LongestPauseSeconds) : 1;
            _window = i;
            _windowSeconds += seconds;
            _windowCpu += seconds * cpuW;
            _windowDraw += seconds * draw;
            _windowLast = s.Timestamp;
            _seconds[i] = Math.Min(_seconds[i] + seconds, SecondsCap);
            if (_windowSeconds >= WindowSeconds) CloseWindow();
        }
    }

    /// <summary>The bucket's level, the rest at a CPU drawing nothing; null for unknown buckets and until the bucket and the
    /// machine have enough seconds on battery.</summary>
    public double? GetBaseline(int bucket) => GetRest(bucket)?.InterceptW;

    /// <summary>The bucket's rest as a level and a share of each CPU watt; null for unknown buckets and until the bucket and
    /// the machine have enough seconds on battery.</summary>
    public RestOfSystem? GetRest(int bucket)
    {
        if (!IsValid(bucket)) return null;
        var i = Index(bucket);
        lock (_gate)
        {
            if (_seconds.Sum() < _options.MinTotalSeconds || _seconds[i] < _options.MinBucketSeconds) return null;
            var stats = Snapshot();
            if (stats[i].Minutes <= 0) return null;
            var slope = SlopeOf(stats);
            return new RestOfSystem(Level(stats, i, slope), slope - 1);
        }
    }

    /// <summary>Snapshot of every bucket with at least one sample, in bucket order, the minute being averaged included.</summary>
    public CalibrationState Export()
    {
        lock (_gate)
        {
            var stats = Snapshot();
            return new CalibrationState(
                Enumerable.Range(0, CalibrationBuckets.Count)
                    .Where(i => _seconds[i] > 0 && stats[i].Minutes > 0)
                    .Select(i => new BucketState(
                        BucketOf(i), stats[i].Draw - stats[i].Cpu, (int)Math.Ceiling(_seconds[i]), stats[i].Cpu, stats[i].Minutes, stats[i].CpuSpread,
                        stats[i].CpuDrawSpread))
                    .ToList());
        }
    }

    /// <summary>Replaces all state with the snapshot. Rows with an unknown bucket, a non-finite or absurd figure, or no
    /// seconds are ignored. A row from before 0.11, with no CPU figure, keeps its mean rest at <see cref="MigratedCpuW"/>
    /// with the weight the old average held; its count of one-second ticks is its seconds.</summary>
    public void Import(CalibrationState state)
    {
        lock (_gate)
        {
            Clear();
            foreach (var b in state.Buckets)
            {
                if (!IsValid(b.Bucket) || b.Samples <= 0 || !Sane(b.BaselineW)) continue;
                Stats stats;
                if (b.CpuW is not { } cpu)
                {
                    stats = new Stats(Math.Min(b.Samples / 60.0, MigratedMinutes), MigratedCpuW, b.BaselineW + MigratedCpuW, 0, 0);
                }
                else
                {
                    if (!Sane(cpu) || !(double.IsFinite(b.Minutes) && b.Minutes > 0)
                        || !(double.IsFinite(b.CpuSpread) && b.CpuSpread >= 0) || !double.IsFinite(b.CpuDrawSpread)) continue;
                    stats = new Stats(b.Minutes, cpu, b.BaselineW + cpu, b.CpuSpread, b.CpuDrawSpread);
                }
                var i = Index(b.Bucket);
                _stats[i] = stats;
                _seconds[i] = Math.Min(b.Samples, SecondsCap);
            }
        }
    }

    /// <summary>Forgets everything learned.</summary>
    public void Reset()
    {
        lock (_gate) { Clear(); }
    }

    /// <summary>Folds the minute being averaged into its bucket, ageing everything learned before by its length. Called with
    /// the lock held.</summary>
    private void CloseWindow()
    {
        if (_window >= 0 && _windowSeconds > 0)
        {
            var ageing = Math.Pow(2, -_windowSeconds / _halfLifeSeconds);
            for (var i = 0; i < _stats.Length; i++) _stats[i] = _stats[i].Scaled(ageing);
            _stats[_window] = _stats[_window].Add(_windowSeconds / 60, _windowCpu / _windowSeconds, _windowDraw / _windowSeconds);
        }
        _window = -1;
        _windowSeconds = _windowCpu = _windowDraw = 0;
    }

    /// <summary>Every bucket's statistics with the minute being averaged added as it stands. Called with the lock held.</summary>
    private Stats[] Snapshot()
    {
        var stats = (Stats[])_stats.Clone();
        if (_window >= 0 && _windowSeconds > 0)
        {
            stats[_window] = stats[_window].Add(_windowSeconds / 60, _windowCpu / _windowSeconds, _windowDraw / _windowSeconds);
        }
        return stats;
    }

    /// <summary>The CPU's spread within the buckets, pooled.</summary>
    private static double SpreadOf(Stats[] stats)
    {
        var spread = 0.0;
        foreach (var s in stats) spread += s.CpuSpread;
        return spread;
    }

    /// <summary>The draw's slope on the CPU, within the buckets and pooled, held toward the prior and within its bounds.</summary>
    private static double SlopeOf(Stats[] stats)
    {
        var spread = 0.0;
        var together = 0.0;
        foreach (var s in stats)
        {
            spread += s.CpuSpread;
            together += s.CpuDrawSpread;
        }
        var prior = 1 + PowerModel.LaptopRestPerCpuWatt;
        var slope = (together + SlopePriorWeight * prior) / (spread + SlopePriorWeight);
        return Math.Clamp(slope, LeastSlope, 1 + MostRestPerCpuWatt);
    }

    /// <summary>The level of bucket <paramref name="i"/> at <paramref name="slope"/>, shrunk toward the brightness line
    /// through every bucket's level (toward their mean for the display-off bucket, or while fewer than two brightnesses are
    /// known).</summary>
    private static double Level(Stats[] stats, int i, double slope)
    {
        double weight = 0, level = 0;
        double onWeight = 0, onAt = 0, onLevel = 0;
        var brightnesses = 0;
        for (var j = 0; j < stats.Length; j++)
        {
            var s = stats[j];
            if (s.Minutes <= 0) continue;
            var own = s.Draw - (slope * s.Cpu);
            weight += s.Minutes;
            level += s.Minutes * own;
            if (BucketOf(j) < 0) continue;
            brightnesses++;
            onWeight += s.Minutes;
            onAt += s.Minutes * Brightness(j);
            onLevel += s.Minutes * own;
        }
        var target = level / weight;
        if (BucketOf(i) >= 0 && brightnesses >= 2)
        {
            var meanAt = onAt / onWeight;
            var meanLevel = onLevel / onWeight;
            double spread = 0, together = 0;
            for (var j = 0; j < stats.Length; j++)
            {
                if (stats[j].Minutes <= 0 || BucketOf(j) < 0) continue;
                var dx = Brightness(j) - meanAt;
                spread += stats[j].Minutes * dx * dx;
                together += stats[j].Minutes * dx * (stats[j].Draw - (slope * stats[j].Cpu) - meanLevel);
            }
            target = meanLevel + ((spread > 0 ? together / spread : 0) * (Brightness(i) - meanAt));
        }
        var mine = stats[i];
        var ownLevel = mine.Draw - (slope * mine.Cpu);
        return ((mine.Minutes * ownLevel) + (LineWeightMinutes * target)) / (mine.Minutes + LineWeightMinutes);
    }

    private static double Brightness(int index) => (double)BucketOf(index) / CalibrationBuckets.MaxBucket;

    private static bool Sane(double watts) => double.IsFinite(watts) && Math.Abs(watts) <= SanityBoundW;

    private void Clear()
    {
        Array.Clear(_stats);
        Array.Clear(_seconds);
        _window = -1;
        _windowSeconds = _windowCpu = _windowDraw = 0;
    }

    private static bool IsValid(int bucket) => bucket is >= CalibrationBuckets.DisplayOff and <= CalibrationBuckets.MaxBucket;

    private static int Index(int bucket) => bucket + 1;

    private static int BucketOf(int index) => index - 1;

    /// <summary>A bucket's weighted minute averages: the weight in minutes, the means of the CPU's watts and of the draw
    /// (what the battery gave less the GPU and display), and their spreads about those means.</summary>
    private readonly record struct Stats(double Minutes, double Cpu, double Draw, double CpuSpread, double CpuDrawSpread)
    {
        /// <summary>The same data aged by <paramref name="factor"/>: the weights shrink, the means stay.</summary>
        public Stats Scaled(double factor)
            => this with { Minutes = Minutes * factor, CpuSpread = CpuSpread * factor, CpuDrawSpread = CpuDrawSpread * factor };

        /// <summary>The data with one more average of <paramref name="minutes"/> weight added (West's weighted update).</summary>
        public Stats Add(double minutes, double cpu, double draw)
        {
            var total = Minutes + minutes;
            var dCpu = cpu - Cpu;
            var dDraw = draw - Draw;
            var share = minutes / total;
            return new Stats(
                total,
                Cpu + (share * dCpu),
                Draw + (share * dDraw),
                CpuSpread + (Minutes * share * dCpu * dCpu),
                CpuDrawSpread + (Minutes * share * dCpu * dDraw));
        }
    }
}
