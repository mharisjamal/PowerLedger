namespace PowerLedger.App;

/// <param name="AgeSeconds">How long before the newest reading this one was taken.</param>
internal readonly record struct SparkSample(double AgeSeconds, double Watts);

/// <summary>The readings of the last <paramref name="span"/>, oldest first, for the sparkline (spec §9: "Last 60 s").</summary>
internal sealed class LiveWindow(TimeSpan span)
{
    private readonly Queue<(DateTimeOffset At, double Watts)> _points = new();

    public IReadOnlyList<double> Values => [.. _points.Select(p => p.Watts)];

    /// <summary>Each reading with its age, so a line with gaps in it is drawn where the readings fell.</summary>
    public IReadOnlyList<SparkSample> Samples
    {
        get
        {
            if (_points.Count == 0) return [];
            var newest = _points.Last().At;
            return [.. _points.Select(p => new SparkSample((newest - p.At).TotalSeconds, p.Watts))];
        }
    }

    public void Add(DateTimeOffset at, double watts)
    {
        if (!double.IsFinite(watts)) return;
        _points.Enqueue((at, Math.Max(0, watts)));
        while (at - _points.Peek().At > span) _points.Dequeue();
    }

    public void Clear() => _points.Clear();
}
