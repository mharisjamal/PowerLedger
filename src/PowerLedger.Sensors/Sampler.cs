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
            if (_tick < entry.SkipUntil) continue;
            try
            {
                // Asking a source whether it is supported reaches a library or a driver too, so it is asked in here
                // with the reading: one that throws is a source that failed, not a tick that failed.
                if (!entry.Source.Supported) continue;
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

    /// <summary>Lets go of every source. Each is closed behind a guard of its own and the set is emptied whatever
    /// happens, because one source that will not close would otherwise leave every source after it open for the life
    /// of the process: a UPS's HID handles and preparsed blocks, and a power supply's read and write handle.</summary>
    public void Dispose()
    {
        try
        {
            foreach (var entry in _entries)
            {
                try
                {
                    entry.Source.Dispose();
                }
                catch (Exception)
                {
                    // A source that will not close is the operating system's business, not the other sources'.
                }
            }
        }
        finally
        {
            _entries.Clear();
        }
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
