using Microsoft.Data.Sqlite;
using PowerLedger.Core;

namespace PowerLedger.Storage;

/// <summary>
/// The service's writes to app_hours and apps (storage version 6, Plan AE): the energy each app used, hour by hour, each
/// app's key, name and path kept once in apps and its hours naming it by number (review item 2). The service keeps the
/// hour it is in and writes it whole with each minute's readings, so a write replaces every row of the hour: the hour's
/// busiest apps can change as it goes on. An app's name is the newest written. Never shared, never synced to a household,
/// never in an export.
/// </summary>
public sealed class AppHoursRepository(SqliteDatabase db)
{
    /// <summary>How long the rows are kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(400);

    /// <summary>Replaces every row of the hour starting at <paramref name="hourStart"/> with <paramref name="rows"/>, in one
    /// transaction, each app's name and path written as given. A row with a figure that is no number is refused before
    /// anything is written.</summary>
    public void ReplaceHour(DateTimeOffset hourStart, IReadOnlyList<AppHourRow> rows)
    {
        foreach (var row in rows)
        {
            // The message names no app: a key is a path, the user's folder in it, and the service's log goes with feedback.
            if (!double.IsFinite(row.CpuWh) || !double.IsFinite(row.GpuWh) || !double.IsFinite(row.RunSeconds) || !double.IsFinite(row.FrontSeconds))
                throw new ArgumentException("An app's row holds a figure that is no number.", nameof(rows));
        }

        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using (var delete = c.CreateCommand())
        {
            delete.CommandText = "DELETE FROM app_hours WHERE start_ms = $start";
            Rows.Add(delete, "$start", Rows.Ms(hourStart));
            delete.ExecuteNonQuery();
        }

        // Each app once: added, or its name and path brought up to date, and its number given back.
        using var app = c.CreateCommand();
        app.CommandText = "INSERT INTO apps (key, name, path) VALUES ($key, $name, $path) " +
                          "ON CONFLICT (key) DO UPDATE SET name = excluded.name, path = excluded.path RETURNING id";
        foreach (var n in new[] { "$key", "$name", "$path" }) app.Parameters.Add(new SqliteParameter(n, null));

        using var insert = c.CreateCommand();
        insert.CommandText = "INSERT INTO app_hours (start_ms, user_id, app_id, cpu_wh, gpu_wh, run_s, front_s) VALUES ($start, $user, $app, $cpu, $gpu, $run, $front)";
        foreach (var n in new[] { "$start", "$user", "$app", "$cpu", "$gpu", "$run", "$front" }) insert.Parameters.Add(new SqliteParameter(n, null));
        foreach (var row in rows)
        {
            app.Parameters[0].Value = row.Key;
            app.Parameters[1].Value = row.Name;
            app.Parameters[2].Value = (object?)row.Path ?? DBNull.Value;
            var id = (long)app.ExecuteScalar()!;
            object[] values = [Rows.Ms(hourStart), row.User, id, row.CpuWh, row.GpuWh, row.RunSeconds, row.FrontSeconds];
            for (var i = 0; i < values.Length; i++) insert.Parameters[i].Value = values[i];
            insert.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Every row of the hour starting at <paramref name="hourStart"/>, in no particular order.</summary>
    public List<AppHourRow> ReadHour(DateTimeOffset hourStart)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT a.key, a.name, a.path, h.cpu_wh, h.gpu_wh, h.run_s, h.front_s, h.user_id FROM app_hours h JOIN apps a ON a.id = h.app_id " +
                          "WHERE h.start_ms = $start";
        Rows.Add(cmd, "$start", Rows.Ms(hourStart));
        using var r = cmd.ExecuteReader();
        var list = new List<AppHourRow>();
        while (r.Read())
        {
            list.Add(new AppHourRow(r.GetString(0), r.GetString(1), Rows.NullableString(r, 2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), r.GetInt32(7)));
        }
        return list;
    }

    /// <summary>Deletes the rows of hours that started before the cutoff, and the apps no hour names any more, and returns
    /// how many hour rows went.</summary>
    public int PurgeBefore(DateTimeOffset cutoff)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM app_hours WHERE start_ms < $cutoff";
        Rows.Add(cmd, "$cutoff", Rows.Ms(cutoff));
        var deleted = cmd.ExecuteNonQuery();
        using var apps = c.CreateCommand();
        apps.CommandText = "DELETE FROM apps WHERE id NOT IN (SELECT DISTINCT app_id FROM app_hours)";
        apps.ExecuteNonQuery();
        tx.Commit();
        return deleted;
    }
}
