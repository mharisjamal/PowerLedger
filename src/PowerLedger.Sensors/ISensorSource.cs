namespace PowerLedger.Sensors;

/// <summary>
/// One place Windows is asked a question. A source fills its own fields of the draft and never computes a total,
/// never reads another source's fields, and never throws on the caller's behalf: the sampler isolates it.
/// </summary>
public interface ISensorSource : IDisposable
{
    /// <summary>Short name used in status and logs, e.g. "battery".</summary>
    string Name { get; }

    /// <summary>False when this machine cannot answer at all, so the sampler can skip it for good.</summary>
    bool Supported { get; }

    /// <summary>Why the source is unsupported, for the status screen; null when it is supported.</summary>
    string? Unavailable { get; }

    /// <summary>Fill this source's fields. Called once per tick.</summary>
    void Contribute(SampleDraft draft);
}

/// <param name="Name">The source's name.</param>
/// <param name="Supported">Whether the machine can answer at all.</param>
/// <param name="Unavailable">Why not, when it cannot.</param>
/// <param name="Failures">How many ticks have thrown since the service started.</param>
/// <param name="SkippedUntilTick">While the source is backing off, the tick it resumes at.</param>
/// <param name="LastError">The most recent exception message, for the status screen.</param>
public sealed record SourceHealth(
    string Name, bool Supported, string? Unavailable,
    int Failures, long SkippedUntilTick, string? LastError);
