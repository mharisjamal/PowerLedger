using PowerLedger.Contracts;
using PowerLedger.Storage;

namespace PowerLedger.Service;

/// <summary>
/// The sessions timeline (spec §6): one open row while the machine is awake and the service runs. A row left open by
/// an earlier run means the service died; it is closed at its last recorded tick as crash-recovered, and the new
/// session starts with the same reason. Only the live session's id is ever closed (Plan A handoff).
/// </summary>
internal sealed class SessionTracker(SessionRepository sessions)
{
    /// <summary>The service counts as starting with Windows when the machine has been up less than this.</summary>
    public static readonly TimeSpan BootWindow = TimeSpan.FromMinutes(5);

    public long? Current { get; private set; }

    public SessionReason Start(DateTimeOffset now, TimeSpan systemUptime, DateTimeOffset? lastTick)
    {
        var reason = systemUptime < BootWindow ? SessionReason.Boot : SessionReason.ServiceStart;
        if (sessions.OpenSession() is { } abandoned)
        {
            sessions.CloseAllOpen(Clamp(lastTick, abandoned.Start, now));
            reason = SessionReason.CrashRecovered;
        }
        Current = sessions.Open(reason, now);
        return reason;
    }

    /// <summary>Closes the live session. A no-op when none is open, as after a suspend.</summary>
    public void End(DateTimeOffset now, SessionReason reason)
    {
        if (Current is not { } id) return;
        sessions.Close(id, now, reason);
        Current = null;
    }

    /// <summary>Opens the session a resume starts. A no-op when one is already open, as when a resume arrives without its suspend.</summary>
    public void Resume(DateTimeOffset now) => Current ??= sessions.Open(SessionReason.Resume, now);

    /// <summary>A crashed session ends at its last tick, which cannot fall before the session began or after now.</summary>
    private static DateTimeOffset Clamp(DateTimeOffset? lastTick, DateTimeOffset start, DateTimeOffset now)
        => lastTick is not { } tick ? start : tick < start ? start : tick > now ? now : tick;
}
