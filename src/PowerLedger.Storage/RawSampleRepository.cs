using Microsoft.Data.Sqlite;
using PowerLedger.Contracts;
using PowerLedger.Core;

namespace PowerLedger.Storage;

public sealed class RawSampleRepository(SqliteDatabase db)
{
    private const string Columns =
        "ts_ms, delta_s, total_w, quality, cpu_w, gpu_w, display_w, ram_w, storage_w, board_w, extras_w, monitors_w, psu_loss_w, unattributed_w, " +
        "on_battery, display_on, user_idle, locked, cpu_load, gpu_load, brightness, suspect";

    /// <summary>What storage version 2 added: where the total came from, what the card's figure covered, what was measured.</summary>
    private const string Provenance = "total_source, gpu_scope, measured_mask";

    /// <summary>Writes all readings in one transaction. Same timestamp replaces the earlier row.</summary>
    public void InsertBatch(IReadOnlyList<Reading> readings)
    {
        if (readings.Count == 0) return;
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"INSERT OR REPLACE INTO samples_raw ({Columns}, {Provenance}) VALUES " +
            "($ts, $delta, $total, $quality, $cpu, $gpu, $display, $ram, $storage, $board, $extras, $monitors, $psu, $rest, " +
            "$onBattery, $displayOn, $userIdle, $locked, $cpuLoad, $gpuLoad, $brightness, $suspect, $totalSource, $gpuScope, $measured)";
        var names = new[] { "$ts", "$delta", "$total", "$quality", "$cpu", "$gpu", "$display", "$ram", "$storage", "$board", "$extras", "$monitors", "$psu", "$rest", "$onBattery", "$displayOn", "$userIdle", "$locked", "$cpuLoad", "$gpuLoad", "$brightness", "$suspect", "$totalSource", "$gpuScope", "$measured" };
        foreach (var n in names) cmd.Parameters.Add(new SqliteParameter(n, null));

        foreach (var r in readings)
        {
            var p = r.Components;
            object?[] values = [Rows.Ms(r.Timestamp), r.DeltaSeconds, r.TotalW, (int)r.Quality, p.Cpu, p.Gpu, p.Display, p.Ram, p.Storage, p.Board, p.Extras, p.Monitors, p.PsuLoss, p.Unattributed,
                r.OnBattery ? 1 : 0, r.DisplayOn ? 1 : 0, r.UserIdle ? 1 : 0, r.SessionLocked ? 1 : 0, r.CpuLoad, r.GpuLoad, r.Brightness, r.Suspect ? 1 : 0,
                (int)r.TotalSource, (int)r.GpuScope, (int)r.Measured];
            for (var i = 0; i < values.Length; i++) cmd.Parameters[i].Value = values[i] ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// Readings with from ≤ timestamp &lt; to, oldest first. Timestamps are stored to the millisecond and come back with a
    /// zero offset. Where each total came from, what the card's figure covered and what was measured are left at the
    /// model's defaults, so the App can read a database the service hasn't upgraded yet; <see cref="ReadRange"/> reads them.
    /// </summary>
    public List<Reading> Read(DateTimeOffset from, DateTimeOffset to) => Select(Rows.Ms(from), Rows.Ms(to), provenance: false);

    /// <summary>Readings with <paramref name="fromMs"/> ≤ timestamp &lt; <paramref name="toMs"/>, oldest first, with every
    /// column, for the data-sharing collector.</summary>
    public List<Reading> ReadRange(long fromMs, long toMs) => Select(fromMs, toMs, provenance: true);

    private List<Reading> Select(long fromMs, long toMs, bool provenance)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns}{(provenance ? ", " + Provenance : "")} FROM samples_raw WHERE ts_ms >= $from AND ts_ms < $to ORDER BY ts_ms";
        Rows.Add(cmd, "$from", fromMs);
        Rows.Add(cmd, "$to", toMs);
        using var r = cmd.ExecuteReader();
        var list = new List<Reading>();
        while (r.Read())
        {
            var parts = new Components(r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), r.GetDouble(7), r.GetDouble(8), r.GetDouble(9), r.GetDouble(10), r.GetDouble(11), r.GetDouble(12), r.GetDouble(13));
            var reading = new Reading(Rows.Time(r.GetInt64(0)), r.GetDouble(1), r.GetDouble(2), (Quality)r.GetInt32(3), parts,
                r.GetInt32(14) == 1, r.GetInt32(15) == 1, r.GetInt32(16) == 1, r.GetInt32(17) == 1,
                r.GetDouble(18), Rows.NullableDouble(r, 19), Rows.NullableDouble(r, 20), r.GetInt32(21) == 1);
            list.Add(provenance
                ? reading with { TotalSource = (TotalSource)r.GetInt32(22), GpuScope = (GpuPowerScope)r.GetInt32(23), Measured = (MeasuredParts)r.GetInt32(24) }
                : reading);
        }
        return list;
    }

    /// <summary>The newest tick's timestamp, or null when the table is empty.</summary>
    public DateTimeOffset? Latest()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT MAX(ts_ms) FROM samples_raw";
        return cmd.ExecuteScalar() is long ms ? Rows.Time(ms) : null;
    }

    /// <summary>Deletes rows older than the cutoff and returns how many went.</summary>
    public int PurgeBefore(DateTimeOffset cutoff)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM samples_raw WHERE ts_ms < $cutoff";
        Rows.Add(cmd, "$cutoff", Rows.Ms(cutoff));
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Total raw rows (full scan; for status and tests).</summary>
    public long Count()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM samples_raw";
        return (long)cmd.ExecuteScalar()!;
    }
}
