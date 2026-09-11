namespace PowerLedger.Service;

/// <summary>
/// Δt between ticks (spec §6), from the monotonic clock. The first tick after a resume measures from the last tick by
/// the wall clock instead, because a monotonic counter may not have counted the sleep, and the sleep must reach the
/// database as a gap. A tick that is measured but never committed, because its read hung, is folded into the next.
/// </summary>
internal sealed class TickClock(TimeProvider clock)
{
    private long? _lastStamp;
    private DateTimeOffset _lastWall;
    private bool _resumed;

    /// <summary>
    /// Seconds since the last committed tick. <paramref name="interval"/> before any tick has been committed, and at least
    /// <paramref name="interval"/> after a resume, so a wall clock set back during sleep cannot produce a negative Δt.
    /// </summary>
    public double Measure(DateTimeOffset now, double interval)
    {
        if (_lastStamp is not { } stamp) return interval;
        if (_resumed) return Math.Max((now - _lastWall).TotalSeconds, interval);
        return clock.GetElapsedTime(stamp).TotalSeconds;
    }

    /// <summary>Records that the tick measured at <paramref name="now"/> was taken.</summary>
    public void Commit(DateTimeOffset now)
    {
        _lastStamp = clock.GetTimestamp();
        _lastWall = now;
        _resumed = false;
    }

    /// <summary>The machine woke: the next measurement uses the wall clock.</summary>
    public void MarkResumed() => _resumed = true;
}
