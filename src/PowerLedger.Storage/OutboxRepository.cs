using Microsoft.Data.Sqlite;

namespace PowerLedger.Storage;

/// <summary>
/// What waits to be sent (data-sharing design §4): minute rows, and events by local day. A crash is one row; the App's
/// usage and the sources' failures are one row a day, merged. A day leaves once it is sent, rejected or too old.
/// </summary>
public sealed class OutboxRepository(SqliteDatabase db)
{
    private const string MinuteColumns =
        "start_ms, day, minute, avg_w, max_w, cpu_w, gpu_w, display_w, ram_w, storage_w, board_w, extras_w, monitors_w, psu_loss_w, " +
        "unattributed_w, cpu_load, gpu_load, brightness, display_on_s, idle_s, locked_s, battery_s, measured_s, calibrated_s, estimated_s, " +
        "samples, total_source, gpu_scope, measured_mask";

    /// <summary>Writes the minutes in one transaction; a minute already there is replaced.</summary>
    public void InsertMinutes(IEnumerable<MinuteRow> minutes)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        var names = MinuteColumns.Split(", ").Select(column => "$" + column).ToArray();
        cmd.CommandText = $"INSERT OR REPLACE INTO outbox_minutes ({MinuteColumns}) VALUES ({string.Join(", ", names)})";
        foreach (var name in names) cmd.Parameters.Add(new SqliteParameter(name, null));
        foreach (var m in minutes)
        {
            object?[] values =
            [
                m.StartMs, m.Day, m.Minute, m.AvgW, m.MaxW, m.CpuW, m.GpuW, m.DisplayW, m.RamW, m.StorageW, m.BoardW, m.ExtrasW, m.MonitorsW,
                m.PsuLossW, m.UnattributedW, m.CpuLoad, m.GpuLoad, m.Brightness, m.DisplayOnS, m.IdleS, m.LockedS, m.BatteryS,
                m.MeasuredS, m.CalibratedS, m.EstimatedS, m.Samples, m.TotalSource, m.GpuScope, m.MeasuredMask,
            ];
            for (var i = 0; i < values.Length; i++) cmd.Parameters[i].Value = values[i] ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>The days with minutes waiting, oldest first.</summary>
    public List<string> MinuteDays() => Strings("SELECT DISTINCT day FROM outbox_minutes ORDER BY day");

    /// <summary>A day's minutes, oldest first.</summary>
    public List<MinuteRow> Minutes(string day)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {MinuteColumns} FROM outbox_minutes WHERE day = $day ORDER BY start_ms";
        Rows.Add(cmd, "$day", day);
        using var r = cmd.ExecuteReader();
        var list = new List<MinuteRow>();
        while (r.Read())
        {
            list.Add(new MinuteRow(
                r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetDouble(3), r.GetDouble(4),
                r.GetDouble(5), r.GetDouble(6), r.GetDouble(7), r.GetDouble(8), r.GetDouble(9),
                r.GetDouble(10), r.GetDouble(11), r.GetDouble(12), r.GetDouble(13), r.GetDouble(14),
                r.GetDouble(15), Rows.NullableDouble(r, 16), Rows.NullableDouble(r, 17),
                r.GetDouble(18), r.GetDouble(19), r.GetDouble(20), r.GetDouble(21),
                r.GetDouble(22), r.GetDouble(23), r.GetDouble(24),
                r.GetInt32(25), r.GetInt32(26), r.GetInt32(27), r.GetInt32(28)));
        }
        return list;
    }

    /// <summary>The days with events waiting, oldest first.</summary>
    public List<string> EventDays() => Strings("SELECT DISTINCT day FROM outbox_events ORDER BY day");

    /// <summary>Every day with anything waiting, oldest first.</summary>
    public List<string> Days() => Strings("SELECT day FROM outbox_minutes UNION SELECT day FROM outbox_events ORDER BY day");

    /// <summary>A day's events of one kind, in the order they came.</summary>
    public List<string> Events(string day, string kind)
    {
        using var c = db.Open();
        return Events(c, day, kind);
    }

    public int CountEvents(string day, string kind)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM outbox_events WHERE day = $day AND kind = $kind";
        Rows.Add(cmd, "$day", day);
        Rows.Add(cmd, "$kind", kind);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void AddEvent(string day, string kind, string json)
    {
        using var c = db.Open();
        Insert(c, day, kind, json);
    }

    /// <summary>Replaces the day's one event of <paramref name="kind"/> with what <paramref name="merge"/> makes of it, given
    /// null when there is none yet, in one transaction.</summary>
    public void MergeEvent(string day, string kind, Func<string?, string> merge)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var existing = Events(c, day, kind);
        var merged = merge(existing.Count > 0 ? existing[^1] : null);
        Execute(c, "DELETE FROM outbox_events WHERE day = $day AND kind = $kind", ("$day", day), ("$kind", kind));
        Insert(c, day, kind, merged);
        tx.Commit();
    }

    /// <summary>Removes a day's minutes and events together.</summary>
    public void DeleteDay(string day)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        Execute(c, "DELETE FROM outbox_minutes WHERE day = $day", ("$day", day));
        Execute(c, "DELETE FROM outbox_events WHERE day = $day", ("$day", day));
        tx.Commit();
    }

    public void DeleteMinutes()
    {
        using var c = db.Open();
        Execute(c, "DELETE FROM outbox_minutes");
    }

    public void DeleteEvents(string kind)
    {
        using var c = db.Open();
        Execute(c, "DELETE FROM outbox_events WHERE kind = $kind", ("$kind", kind));
    }

    public void Clear()
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        Execute(c, "DELETE FROM outbox_minutes");
        Execute(c, "DELETE FROM outbox_events");
        tx.Commit();
    }

    private static List<string> Events(SqliteConnection c, string day, string kind)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM outbox_events WHERE day = $day AND kind = $kind ORDER BY id";
        Rows.Add(cmd, "$day", day);
        Rows.Add(cmd, "$kind", kind);
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    private static void Insert(SqliteConnection c, string day, string kind, string json) =>
        Execute(c, "INSERT INTO outbox_events(day, kind, json) VALUES ($day, $kind, $json)", ("$day", day), ("$kind", kind), ("$json", json));

    private static void Execute(SqliteConnection c, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) Rows.Add(cmd, name, value);
        cmd.ExecuteNonQuery();
    }

    private List<string> Strings(string sql)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }
}
