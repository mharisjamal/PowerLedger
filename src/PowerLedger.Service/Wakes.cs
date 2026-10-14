using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.Service;

/// <summary>
/// When the machine woke, from the sessions table, and so which ticks reach back across a sleep (spec §6, accuracy A17).
/// The first tick after a resume is measured from the last tick before the sleep by the wall clock, so its interval holds
/// the sleep, however short, and it is a gap. Any other tick counts as awake up to
/// <see cref="EnergyIntegrator.LongestAwakeTickSeconds"/>: a stall while the machine ran, a hung read or a slow disk, still
/// drew power.
/// </summary>
internal sealed class Wakes(IEnumerable<DateTimeOffset> at)
{
    private readonly DateTimeOffset[] _at = [.. at];

    /// <summary>The resumes of the sessions that overlap [<paramref name="from"/>, <paramref name="to"/>): every resume a
    /// tick in that range can reach back across, since the session it opened was still open at the tick.</summary>
    public static Wakes Between(SessionRepository sessions, DateTimeOffset from, DateTimeOffset to)
        => new(sessions.List(from, to).Where(session => session.Reason == SessionReason.Resume).Select(session => session.Start));

    /// <summary>True when the machine woke inside <paramref name="reading"/>'s interval: after its previous tick, and by its own.</summary>
    public bool Within(Reading reading)
    {
        if (!double.IsFinite(reading.DeltaSeconds) || reading.DeltaSeconds <= 0) return false;
        foreach (var woke in _at)
        {
            if (woke <= reading.Timestamp && (reading.Timestamp - woke).TotalSeconds < reading.DeltaSeconds) return true;
        }
        return false;
    }
}
