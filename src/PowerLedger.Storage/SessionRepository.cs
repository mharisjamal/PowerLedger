using Microsoft.Data.Sqlite;
using PowerLedger.Contracts;

namespace PowerLedger.Storage;

/// <param name="Id">Row id.</param>
/// <param name="Start">When the session began.</param>
/// <param name="End">When it ended; null while the session is open.</param>
/// <param name="Reason">Why the session started.</param>
/// <param name="EndReason">Why it ended; null while the session is open.</param>
public sealed record Session(long Id, DateTimeOffset Start, DateTimeOffset? End, SessionReason Reason, SessionReason? EndReason);

/// <summary>Power-state timeline. A session is open from boot/resume until suspend/shutdown/stop.</summary>
public sealed class SessionRepository(SqliteDatabase db)
{
    /// <summary>Starts a session and returns its id.</summary>
    public long Open(SessionReason reason, DateTimeOffset start)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO sessions(start_ms, end_ms, reason) VALUES ($start, NULL, $reason) RETURNING id";
        Rows.Add(cmd, "$start", Rows.Ms(start));
        Rows.Add(cmd, "$reason", reason.ToString());
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Ends an open session. Returns false when the id is unknown or already closed, which the caller should treat as a lost session.</summary>
    public bool Close(long id, DateTimeOffset end, SessionReason reason)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET end_ms = $end, end_reason = $reason WHERE id = $id AND end_ms IS NULL";
        Rows.Add(cmd, "$end", Rows.Ms(end));
        Rows.Add(cmd, "$reason", reason.ToString());
        Rows.Add(cmd, "$id", id);
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>Closes every session still open (after a crash) with reason CrashRecovered and returns how many there were.</summary>
    public int CloseAllOpen(DateTimeOffset end)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET end_ms = $end, end_reason = $reason WHERE end_ms IS NULL";
        Rows.Add(cmd, "$end", Rows.Ms(end));
        Rows.Add(cmd, "$reason", SessionReason.CrashRecovered.ToString());
        return cmd.ExecuteNonQuery();
    }

    /// <summary>The newest still-open session, or null when none is open.</summary>
    public Session? OpenSession()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, start_ms, end_ms, reason, end_reason FROM sessions WHERE end_ms IS NULL ORDER BY start_ms DESC LIMIT 1";
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    /// <summary>Sessions overlapping [from, to).</summary>
    public List<Session> List(DateTimeOffset from, DateTimeOffset to)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, start_ms, end_ms, reason, end_reason FROM sessions WHERE start_ms < $to AND (end_ms IS NULL OR end_ms > $from) ORDER BY start_ms";
        Rows.Add(cmd, "$from", Rows.Ms(from));
        Rows.Add(cmd, "$to", Rows.Ms(to));
        using var r = cmd.ExecuteReader();
        var list = new List<Session>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    private static Session Map(SqliteDataReader r) => new(
        r.GetInt64(0), Rows.Time(r.GetInt64(1)),
        r.IsDBNull(2) ? null : Rows.Time(r.GetInt64(2)),
        Enum.Parse<SessionReason>(r.GetString(3)),
        r.IsDBNull(4) || !Enum.TryParse<SessionReason>(r.GetString(4), out var endReason) ? null : endReason);
}
