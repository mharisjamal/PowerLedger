namespace PowerLedger.Core;

/// <summary>
/// What each app does at rest (Plan AF: power above rest goes to work above rest). The resting watts
/// (<see cref="RestingWatts"/>) are what the processor draws over the quiet minutes, the quiet work of every app running then
/// included, and go to no app. So the working part above them is split by each app's work above its own quiet rate, not by
/// all its work: an app doing what it always does at rest is given none of a busy loop's power, nor of the clock speed the
/// loop drives every core to. A rate is learned from the same quiet windows the resting watts are, in its own unit
/// (cycles a second for an app, busy time for drivers and closed programs): the mean of the first windows, then an average
/// over <see cref="TimeConstant"/> of quiet time, an app absent from a window doing none of its work then. Single-threaded.
/// </summary>
public sealed class QuietRates(TimeSpan? timeConstant = null)
{
    /// <summary>How slowly a rate follows: an average over about this much quiet time.</summary>
    public static readonly TimeSpan TimeConstant = TimeSpan.FromMinutes(30);

    /// <summary>The most keys kept; past it the least busy are forgotten.</summary>
    public const int MostKeys = 512;

    private readonly double _tau = (timeConstant ?? TimeConstant).TotalSeconds;
    private readonly Dictionary<(int User, string Key), Rate> _rates = [];

    /// <summary>Keys with a rate.</summary>
    public int Count => _rates.Count;

    /// <summary>A key's work a second at rest; nought for one never seen at rest.</summary>
    public double RateOf((int User, string Key) key) => _rates.TryGetValue(key, out var rate) ? rate.PerSecond : 0;

    /// <summary>The work over a window above <paramref name="rate"/>, never below nought.</summary>
    public static double Excess(double amount, double rate, double seconds)
        => Math.Max(0, (double.IsFinite(amount) ? amount : 0) - (Math.Max(0, rate) * seconds));

    /// <summary>One quiet window: each key's work over <paramref name="seconds"/>. A key learned before and absent from it did
    /// none.</summary>
    public void Learn(IReadOnlyDictionary<(int User, string Key), double> work, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return;
        var decay = 1 - Math.Exp(-seconds / _tau);
        foreach (var (key, rate) in _rates) rate.Add(work.TryGetValue(key, out var amount) ? amount / seconds : 0, seconds, decay);
        foreach (var (key, amount) in work)
        {
            if (_rates.ContainsKey(key) || !double.IsFinite(amount) || amount < 0) continue;
            var rate = new Rate();
            rate.Add(amount / seconds, seconds, decay);
            _rates[key] = rate;
        }
        if (_rates.Count <= MostKeys) return;
        foreach (var key in _rates.OrderBy(pair => pair.Value.PerSecond).Take(_rates.Count - MostKeys).Select(pair => pair.Key).ToList()) _rates.Remove(key);
    }

    private sealed class Rate
    {
        public double PerSecond;
        private double _seconds;

        /// <summary>A running mean of the windows until the time constant's average weighs a window more.</summary>
        public void Add(double observed, double seconds, double decay)
        {
            _seconds += seconds;
            PerSecond += Math.Max(decay, seconds / _seconds) * (observed - PerSecond);
        }
    }
}
