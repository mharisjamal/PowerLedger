using PowerLedger.Core;

namespace PowerLedger.Sensors;

/// <summary>
/// Runs every source once per tick behind its own try/catch (spec §4). A source that throws is skipped for a
/// doubling number of ticks and retried; one broken sensor costs its own fields and nothing else.
/// </summary>
public sealed class Sampler : IDisposable
{
    private readonly List<Entry> _entries;
    private readonly int _maxBackoffTicks;
    private long _tick;

    /// <param name="sources">The sources to run, in the order they should contribute.</param>
    /// <param name="maxBackoffTicks">Ceiling on the skip length, so a dead sensor is still retried about once a minute.</param>
    public Sampler(IEnumerable<ISensorSource> sources, int maxBackoffTicks = 60)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBackoffTicks);
        _entries = sources.Select(s => new Entry(s)).ToList();
        _maxBackoffTicks = maxBackoffTicks;
    }

    /// <summary>Per-source state for the status screen.</summary>
    public IReadOnlyList<SourceHealth> Health =>
        _entries.Select(e => new SourceHealth(e.Source.Name, e.Source.Supported, e.Source.Unavailable, e.Failures, e.SkipUntil, e.LastError)).ToList();

    /// <summary>One tick. Never throws: a source's failure is recorded, not propagated.</summary>
    public Sample Read(DateTimeOffset timestamp, double deltaSeconds)
    {
        _tick++;
        var draft = new SampleDraft();
        foreach (var entry in _entries)
        {
            if (!entry.Source.Supported || _tick < entry.SkipUntil) continue;
            try
            {
                entry.Source.Contribute(draft);
                entry.Backoff = 0;
                entry.SkipUntil = 0;
            }
            catch (Exception error)
            {
                entry.Failures++;
                entry.LastError = error.Message;
                entry.Backoff = entry.Backoff == 0 ? 1 : Math.Min(entry.Backoff * 2, _maxBackoffTicks);
                // SkipUntil is the tick it resumes at, so Backoff whole ticks are actually skipped.
                entry.SkipUntil = _tick + entry.Backoff + 1;
            }
        }
        return draft.ToSample(timestamp, deltaSeconds);
    }

    public void Dispose()
    {
        foreach (var entry in _entries) entry.Source.Dispose();
        _entries.Clear();
    }

    private sealed class Entry(ISensorSource source)
    {
        public ISensorSource Source { get; } = source;
        public int Failures { get; set; }
        public int Backoff { get; set; }
        public long SkipUntil { get; set; }
        public string? LastError { get; set; }
    }
}
