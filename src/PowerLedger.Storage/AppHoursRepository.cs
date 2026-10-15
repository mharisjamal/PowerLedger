using Microsoft.Data.Sqlite;
using PowerLedger.Core;

namespace PowerLedger.Storage;

/// <summary>
/// The service's writes to app_hours (storage version 6, Plan AE): the energy each app used, hour by hour. The service
/// keeps the hour it is in and writes it whole with each minute's readings, so a write replaces every row of the hour: the
/// hour's busiest apps can change as it goes on. Never shared, never synced to a household, never in an export.
/// </summary>
public sealed class AppHoursRepository(SqliteDatabase db)
{
    /// <summary>How long the rows are kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(400);

    /// <summary>Replaces every row of the hour starting at <paramref name="hourStart"/> with <paramref name="rows"/>, in one
    /// transaction. A row with a figure that is no number is refused before anything is written.</summary>
    public void ReplaceHour(DateTimeOffset hourStart, IReadOnlyList<AppHourRow> rows)
    {
        foreach (var row in rows)
        {
            if (!double.IsFinite(row.CpuWh) || !double.IsFinite(row.GpuWh) || !double.IsFinite(row.RunSeconds) || !double.IsFinite(row.FrontSeconds))
                throw new ArgumentException($"The row for {row.Key} holds a figure that is no number.", nameof(rows));
        }

        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using (var delete = c.CreateCommand())
        {
            delete.CommandText = "DELETE FROM app_hours WHERE start_ms = $start";
            Rows.Add(delete, "$start", Rows.Ms(hourStart));
            delete.ExecuteNonQuery();
        }

        using var insert = c.CreateCommand();
        insert.CommandText = "INSERT INTO app_hours (start_ms, app_key, name, path, cpu_wh, gpu_wh, run_s, front_s) " +
                             "VALUES ($start, $key, $name, $path, $cpu, $gpu, $run, $front)";
        var names = new[] { "$start", "$key", "$name", "$path", "$cpu", "$gpu", "$run", "$front" };
        foreach (var n in names) insert.Parameters.Add(new SqliteParameter(n, null));
        foreach (var row in rows)
        {
            object?[] values = [Rows.Ms(hourStart), row.Key, row.Name, row.Path, row.CpuWh, row.GpuWh, row.RunSeconds, row.FrontSeconds];
            for (var i = 0; i < values.Length; i++) insert.Parameters[i].Value = values[i] ?? DBNull.Value;
            insert.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Every row of the hour starting at <paramref name="hourStart"/>, in no particular order.</summary>
    public List<AppHourRow> ReadHour(DateTimeOffset hourStart)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT app_key, name, path, cpu_wh, gpu_wh, run_s, front_s FROM app_hours WHERE start_ms = $start";
        Rows.Add(cmd, "$start", Rows.Ms(hourStart));
        using var r = cmd.ExecuteReader();
        var list = new List<AppHourRow>();
        while (r.Read())
        {
            list.Add(new AppHourRow(r.GetString(0), r.GetString(1), Rows.NullableString(r, 2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6)));
        }
        return list;
    }

    /// <summary>Deletes the rows of hours that started before the cutoff and returns how many went.</summary>
    public int PurgeBefore(DateTimeOffset cutoff)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM app_hours WHERE start_ms < $cutoff";
        Rows.Add(cmd, "$cutoff", Rows.Ms(cutoff));
        return cmd.ExecuteNonQuery();
    }
}
