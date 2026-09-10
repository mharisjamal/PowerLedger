using Microsoft.Data.Sqlite;
using PowerLedger.Core;

namespace PowerLedger.Storage;

public sealed class AggregateRepository(SqliteDatabase db)
{
    private const string MinuteTable = "samples_1m";
    private const string HourTable = "samples_1h";
    private const string Columns =
        "start_ms, avg_w, max_w, energy_wh, cpu_wh, gpu_wh, display_wh, rest_wh, idle_on_wh, idle_off_wh, " +
        "idle_on_s, idle_off_s, on_s, battery_s, gap_s, sample_count, measured_s, calibrated_s, estimated_s";

    /// <summary>Writes or replaces one minute row, keyed by its start. Because it replaces, build the row from every raw
    /// row in that minute (spec §7), never from one run's own ticks, or a restart inside the minute erases the earlier part.</summary>
    public void UpsertMinute(Aggregate a) => Upsert(MinuteTable, a);

    /// <summary>Writes or replaces one hour row, keyed by its start.</summary>
    public void UpsertHour(Aggregate a) => Upsert(HourTable, a);

    /// <summary>Minute rows with from ≤ start &lt; to, oldest first. Starts come back with a zero offset.</summary>
    public List<Aggregate> ReadMinutes(DateTimeOffset from, DateTimeOffset to) => Read(MinuteTable, from, to);

    /// <summary>Hour rows with from ≤ start &lt; to, oldest first. Starts come back with a zero offset.</summary>
    public List<Aggregate> ReadHours(DateTimeOffset from, DateTimeOffset to) => Read(HourTable, from, to);

    /// <summary>Start of the newest minute row, or null when there are none.</summary>
    public DateTimeOffset? LastMinuteStart() => LastStart(MinuteTable);

    /// <summary>Start of the newest hour row, or null when there are none.</summary>
    public DateTimeOffset? LastHourStart() => LastStart(HourTable);

    /// <summary>Deletes minute rows older than the cutoff and returns how many went. Hour rows are kept forever (spec §7).</summary>
    public int PurgeMinutesBefore(DateTimeOffset cutoff)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"DELETE FROM {MinuteTable} WHERE start_ms < $cutoff";
        Rows.Add(cmd, "$cutoff", Rows.Ms(cutoff));
        return cmd.ExecuteNonQuery();
    }

    private void Upsert(string table, Aggregate a)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"INSERT OR REPLACE INTO {table} ({Columns}) VALUES " +
            "($start, $avg, $max, $energy, $cpu, $gpu, $display, $rest, $idleOn, $idleOff, $idleOnS, $idleOffS, $on, $battery, $gap, $count, $measured, $calibrated, $estimated)";
        Rows.Add(cmd, "$start", Rows.Ms(a.Start));
        Rows.Add(cmd, "$avg", a.AvgW);
        Rows.Add(cmd, "$max", a.MaxW);
        Rows.Add(cmd, "$energy", a.EnergyWh);
        Rows.Add(cmd, "$cpu", a.CpuWh);
        Rows.Add(cmd, "$gpu", a.GpuWh);
        Rows.Add(cmd, "$display", a.DisplayWh);
        Rows.Add(cmd, "$rest", a.RestWh);
        Rows.Add(cmd, "$idleOn", a.IdleOnWh);
        Rows.Add(cmd, "$idleOff", a.IdleOffWh);
        Rows.Add(cmd, "$idleOnS", a.IdleOnSeconds);
        Rows.Add(cmd, "$idleOffS", a.IdleOffSeconds);
        Rows.Add(cmd, "$on", a.OnSeconds);
        Rows.Add(cmd, "$battery", a.BatterySeconds);
        Rows.Add(cmd, "$gap", a.GapSeconds);
        Rows.Add(cmd, "$count", a.SampleCount);
        Rows.Add(cmd, "$measured", a.MeasuredSeconds);
        Rows.Add(cmd, "$calibrated", a.CalibratedSeconds);
        Rows.Add(cmd, "$estimated", a.EstimatedSeconds);
        cmd.ExecuteNonQuery();
    }

    private List<Aggregate> Read(string table, DateTimeOffset from, DateTimeOffset to)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM {table} WHERE start_ms >= $from AND start_ms < $to ORDER BY start_ms";
        Rows.Add(cmd, "$from", Rows.Ms(from));
        Rows.Add(cmd, "$to", Rows.Ms(to));
        using var r = cmd.ExecuteReader();
        var list = new List<Aggregate>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    private DateTimeOffset? LastStart(string table)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT MAX(start_ms) FROM {table}";
        var v = cmd.ExecuteScalar();
        return v is long ms ? Rows.Time(ms) : null;
    }

    private static Aggregate Map(SqliteDataReader r) => new(
        Rows.Time(r.GetInt64(0)), r.GetDouble(1), r.GetDouble(2),
        r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), r.GetDouble(7),
        r.GetDouble(8), r.GetDouble(9),
        r.GetDouble(10), r.GetDouble(11),
        r.GetDouble(12), r.GetDouble(13), r.GetDouble(14),
        r.GetInt32(15),
        r.GetDouble(16), r.GetDouble(17), r.GetDouble(18));
}
