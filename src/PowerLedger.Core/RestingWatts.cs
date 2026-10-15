namespace PowerLedger.Core;

/// <summary>
/// What a part draws at rest (Plan AE): the low end of what it drew in the recent minutes the machine did nothing. Each
/// minute with at least half its time at rest gives its average watts, weighed by time, and the estimate is the 10th
/// percentile of the last 60 such minutes, so it moves slowly, a busy minute never lifts it and a stray low tick never sinks
/// it. Until a quiet minute has ended, the lowest watts of any tick at rest stand in; with none at all, nought. The Apps
/// page gives this much of the part's draw to no app (the greenhouse gas protocol's idle split, research §9): usage
/// shares alone hand an app the power the part draws anyway. Single-threaded.
/// </summary>
public sealed class RestingWatts
{
    /// <summary>The load under which the machine counts as at rest: processor and graphics both under it.</summary>
    public const double QuietLoad = 0.05;

    private const double QuietShareOfMinute = 0.5;
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private readonly double[] _minutes;
    private readonly double[] _sorted;
    private readonly double _percentile;
    private int _count;
    private int _next;
    private long _minuteTicks = long.MinValue;
    private double _restWattSeconds;
    private double _restSeconds;
    private double? _lowestTick;

    /// <param name="minutes">How many quiet minutes the estimate looks back over.</param>
    /// <param name="percentile">Which of them, 0 the lowest and 1 the highest.</param>
    public RestingWatts(int minutes = 60, double percentile = 0.1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minutes);
        ArgumentOutOfRangeException.ThrowIfNegative(percentile);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentile, 1);
        _minutes = new double[minutes];
        _sorted = new double[minutes];
        _percentile = percentile;
    }

    /// <summary>The estimate: watts, nought or more.</summary>
    public double Watts { get; private set; }

    /// <summary>One tick: the part drew <paramref name="watts"/> for <paramref name="seconds"/> up to <paramref name="at"/>.
    /// A tick counts in the minute of its timestamp, as a reading does in the minute rows.</summary>
    public void Observe(DateTimeOffset at, double watts, double seconds, bool atRest)
    {
        var minute = at.UtcTicks - (at.UtcTicks % Minute.Ticks);
        if (minute != _minuteTicks)
        {
            EndMinute();
            _minuteTicks = minute;
        }
        if (!atRest || !double.IsFinite(watts) || watts < 0 || !double.IsFinite(seconds) || seconds <= 0) return;
        _restWattSeconds += watts * seconds;
        _restSeconds += seconds;
        if (_count == 0 && (_lowestTick is not { } lowest || watts < lowest))
        {
            _lowestTick = watts;
            Watts = watts;
        }
    }

    private void EndMinute()
    {
        if (_restSeconds >= QuietShareOfMinute * Minute.TotalSeconds)
        {
            _minutes[_next] = _restWattSeconds / _restSeconds;
            _next = (_next + 1) % _minutes.Length;
            _count = Math.Min(_count + 1, _minutes.Length);
            Array.Copy(_minutes, _sorted, _count);
            Array.Sort(_sorted, 0, _count);
            Watts = _sorted[(int)Math.Floor(_percentile * (_count - 1))];
        }
        _restWattSeconds = 0;
        _restSeconds = 0;
    }
}
