namespace PowerLedger.Sensors;

/// <summary>
/// Median of the last N values. The validator compares each reading against this rather than against the
/// previous one, so a single spike cannot drag the reference with it.
/// </summary>
public sealed class RollingMedian
{
    private readonly double[] _values;
    private readonly double[] _scratch;
    private int _next;

    public RollingMedian(int window)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(window);
        _values = new double[window];
        _scratch = new double[window];
    }

    /// <summary>How many values the window holds so far, up to its size.</summary>
    public int Count { get; private set; }

    /// <summary>The median, or null while the window is empty.</summary>
    public double? Median
    {
        get
        {
            if (Count == 0) return null;
            var span = _scratch.AsSpan(0, Count);
            _values.AsSpan(0, Count).CopyTo(span);
            span.Sort();
            var middle = Count / 2;
            return Count % 2 == 1 ? span[middle] : (span[middle - 1] + span[middle]) / 2;
        }
    }

    public void Add(double value)
    {
        _values[_next] = value;
        _next = (_next + 1) % _values.Length;
        if (Count < _values.Length) Count++;
    }

    public void Reset()
    {
        Array.Clear(_values);
        _next = 0;
        Count = 0;
    }
}
