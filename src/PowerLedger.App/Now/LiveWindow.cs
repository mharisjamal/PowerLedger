namespace PowerLedger.App;

/// <summary>The readings of the last <paramref name="span"/>, oldest first, for the sparkline (spec §9: "Last 60 s").</summary>
internal sealed class LiveWindow(TimeSpan span)
{
    private readonly Queue<(DateTimeOffset At, double Watts)> _points = new();

    public IReadOnlyList<double> Values => [.. _points.Select(p => p.Watts)];

    public void Add(DateTimeOffset at, double watts)
    {
        if (!double.IsFinite(watts)) return;
        _points.Enqueue((at, Math.Max(0, watts)));
        while (at - _points.Peek().At > span) _points.Dequeue();
    }

    public void Clear() => _points.Clear();
}
