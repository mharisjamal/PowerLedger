using Microsoft.Data.Sqlite;
using PowerLedger.Contracts;

namespace PowerLedger.Storage;

/// <summary>One PC's complete hour in the household (households design §1), as every member keeps it.</summary>
/// <param name="DeviceId">The PC it is from.</param>
/// <param name="HourMs">The hour's UTC start, unix milliseconds.</param>
/// <param name="IdleS">Idle seconds, with the display on and with it off.</param>
/// <param name="CostMicro">The hour's cost in millionths of <paramref name="Currency"/>, at the tariff in force at its start; null
/// with no tariff.</param>
/// <param name="ChangedMs">When the PC it is from last changed its figures, unix milliseconds: a later change replaces it.</param>
public sealed record HouseholdRow(
    string DeviceId, long HourMs,
    double EnergyWh, double CpuWh, double GpuWh, double DisplayWh, double RestWh, double IdleOnWh, double IdleOffWh,
    double OnS, double BatteryS, double IdleS, double MeasuredS, double CalibratedS, double EstimatedS,
    long? CostMicro, string? Currency, long ChangedMs)
{
    /// <summary>True when the two hold the same figures, whenever each changed.</summary>
    public bool SameFigures(HouseholdRow other) => this with { ChangedMs = 0 } == other with { ChangedMs = 0 };
}

/// <summary>A PC in the household as this PC knows it (households design §1).</summary>
/// <param name="SignKey">Its signing key's public half, SubjectPublicKeyInfo.</param>
/// <param name="DhKey">Its key-agreement key's public half, SubjectPublicKeyInfo.</param>
/// <param name="AddedMs">When this PC first learned of it.</param>
/// <param name="LeftMs">When it left or was removed; null while it is a member.</param>
/// <param name="LastSyncedMs">When its rows last reached this PC; null before they ever have.</param>
public sealed record HouseholdMember(
    string DeviceId, string Name, ChassisKind Kind, byte[] SignKey, byte[] DhKey, long AddedMs, long? LeftMs, long? LastSyncedMs);

/// <summary>
/// The household's rows and members (households design §1, §5). The service writes them: this PC's own rows as it builds
/// them, the others' as they sync. A row only ever gives way to one that changed later, so syncing the same rows twice, or
/// out of order, leaves the newest.
/// </summary>
public sealed class HouseholdRepository(SqliteDatabase db)
{
    private const string RowColumns =
        "device_id, hour_ms, energy_wh, cpu_wh, gpu_wh, display_wh, rest_wh, idle_on_wh, idle_off_wh, on_s, battery_s, idle_s, " +
        "measured_s, calibrated_s, estimated_s, cost_micro, currency, changed_ms";

    private const string MemberColumns = "device_id, name, kind, sign_key, dh_key, added_ms, left_ms, last_synced_ms";

    /// <summary>Writes each row unless the one kept for its device and hour changed at the same time or later, in one
    /// transaction.</summary>
    /// <returns>How many rows were written.</returns>
    public int Upsert(IEnumerable<HouseholdRow> rows)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        var names = RowColumns.Split(", ").Select(column => "$" + column).ToArray();
        var updates = RowColumns.Split(", ").Skip(2).Select(column => $"{column} = excluded.{column}");
        cmd.CommandText = $"INSERT INTO household_rows ({RowColumns}) VALUES ({string.Join(", ", names)}) " +
            $"ON CONFLICT (device_id, hour_ms) DO UPDATE SET {string.Join(", ", updates)} WHERE excluded.changed_ms > household_rows.changed_ms";
        foreach (var name in names) cmd.Parameters.Add(new SqliteParameter(name, null));
        var written = 0;
        foreach (var row in rows)
        {
            object?[] values =
            [
                row.DeviceId, row.HourMs, row.EnergyWh, row.CpuWh, row.GpuWh, row.DisplayWh, row.RestWh, row.IdleOnWh, row.IdleOffWh,
                row.OnS, row.BatteryS, row.IdleS, row.MeasuredS, row.CalibratedS, row.EstimatedS, row.CostMicro, row.Currency, row.ChangedMs,
            ];
            for (var i = 0; i < values.Length; i++) cmd.Parameters[i].Value = values[i] ?? DBNull.Value;
            written += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return written;
    }

    public HouseholdRow? Row(string deviceId, long hourMs) =>
        ReadRows("WHERE device_id = $device AND hour_ms = $hour", ("$device", deviceId), ("$hour", hourMs)).FirstOrDefault();

    /// <summary>A device's rows with from ≤ hour &lt; to, oldest first.</summary>
    public List<HouseholdRow> RowsBetween(string deviceId, long fromMs, long toMs) =>
        ReadRows("WHERE device_id = $device AND hour_ms >= $from AND hour_ms < $to ORDER BY hour_ms",
            ("$device", deviceId), ("$from", fromMs), ("$to", toMs));

    /// <summary>A device's rows that changed after <paramref name="changedMs"/>, in the order they changed.</summary>
    public List<HouseholdRow> ChangedAfter(string deviceId, long changedMs) =>
        ReadRows("WHERE device_id = $device AND changed_ms > $changed ORDER BY changed_ms, hour_ms",
            ("$device", deviceId), ("$changed", changedMs));

    /// <summary>A device's rows after the one that changed at <paramref name="changedMs"/> for <paramref name="hourMs"/>, in the
    /// order they changed, then by hour: where a run that stopped part way through rows changed at the same moment goes on.</summary>
    public List<HouseholdRow> ChangedAfter(string deviceId, long changedMs, long hourMs) =>
        ReadRows("WHERE device_id = $device AND (changed_ms > $changed OR (changed_ms = $changed AND hour_ms > $hour)) ORDER BY changed_ms, hour_ms",
            ("$device", deviceId), ("$changed", changedMs), ("$hour", hourMs));

    /// <summary>When one device's newest change was; 0 when it has no rows.</summary>
    public long LatestChange(string deviceId)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(changed_ms), 0) FROM household_rows WHERE device_id = $device";
        Rows.Add(cmd, "$device", deviceId);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>For each device with rows, how far its rows reach, in the order they are sent: the newest change, and the
    /// latest hour among the rows changed then.</summary>
    public Dictionary<string, (long Changed, long Hour)> Reach()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT r.device_id, r.changed_ms, MAX(r.hour_ms) FROM household_rows r " +
            "JOIN (SELECT device_id, MAX(changed_ms) AS newest FROM household_rows GROUP BY device_id) m " +
            "ON r.device_id = m.device_id AND r.changed_ms = m.newest GROUP BY r.device_id, r.changed_ms";
        using var r = cmd.ExecuteReader();
        var reach = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
        while (r.Read()) reach[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2));
        return reach;
    }

    /// <summary>For each device with rows, when its newest change was.</summary>
    public Dictionary<string, long> Latest()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT device_id, MAX(changed_ms) FROM household_rows GROUP BY device_id";
        using var r = cmd.ExecuteReader();
        var latest = new Dictionary<string, long>(StringComparer.Ordinal);
        while (r.Read()) latest[r.GetString(0)] = r.GetInt64(1);
        return latest;
    }

    /// <summary>Removes every row of one device.</summary>
    /// <returns>How many went.</returns>
    public int DeleteRows(string deviceId) => Execute("DELETE FROM household_rows WHERE device_id = $device", ("$device", deviceId));

    /// <summary>Every member this PC knows, left ones too, in the order they were added.</summary>
    public List<HouseholdMember> Members() => ReadMembers("ORDER BY added_ms, device_id");

    public HouseholdMember? Member(string deviceId) => ReadMembers("WHERE device_id = $device", ("$device", deviceId)).FirstOrDefault();

    /// <summary>Adds a member, or updates one already known: its name, kind and keys, and whether it has left. When it was
    /// first added and when it last synced stay as they were.</summary>
    public void SaveMember(HouseholdMember member) => Execute(
        $"INSERT INTO household_members ({MemberColumns}) VALUES ($device, $name, $kind, $sign, $dh, $added, $left, $synced) " +
        "ON CONFLICT (device_id) DO UPDATE SET name = excluded.name, kind = excluded.kind, sign_key = excluded.sign_key, " +
        "dh_key = excluded.dh_key, left_ms = excluded.left_ms",
        ("$device", member.DeviceId), ("$name", member.Name), ("$kind", (int)member.Kind), ("$sign", member.SignKey), ("$dh", member.DhKey),
        ("$added", member.AddedMs), ("$left", member.LeftMs), ("$synced", member.LastSyncedMs));

    /// <summary>Marks a member as gone; its rows stay until the user removes them.</summary>
    public void MarkLeft(string deviceId, long atMs) =>
        Execute("UPDATE household_members SET left_ms = $left WHERE device_id = $device AND left_ms IS NULL", ("$device", deviceId), ("$left", atMs));

    /// <summary>Notes that a member's rows reached this PC; the time only moves forward.</summary>
    public void Synced(string deviceId, long atMs) => Execute(
        "UPDATE household_members SET last_synced_ms = MAX(COALESCE(last_synced_ms, 0), $at) WHERE device_id = $device",
        ("$device", deviceId), ("$at", atMs));

    public void DeleteMember(string deviceId) => Execute("DELETE FROM household_members WHERE device_id = $device", ("$device", deviceId));

    private List<HouseholdRow> ReadRows(string where, params (string Name, object? Value)[] parameters)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {RowColumns} FROM household_rows {where}";
        foreach (var (name, value) in parameters) Rows.Add(cmd, name, value);
        using var r = cmd.ExecuteReader();
        var list = new List<HouseholdRow>();
        while (r.Read())
        {
            list.Add(new HouseholdRow(
                r.GetString(0), r.GetInt64(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6),
                r.GetDouble(7), r.GetDouble(8), r.GetDouble(9), r.GetDouble(10), r.GetDouble(11), r.GetDouble(12), r.GetDouble(13),
                r.GetDouble(14), r.IsDBNull(15) ? null : r.GetInt64(15), r.IsDBNull(16) ? null : r.GetString(16), r.GetInt64(17)));
        }
        return list;
    }

    private List<HouseholdMember> ReadMembers(string where, params (string Name, object? Value)[] parameters)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {MemberColumns} FROM household_members {where}";
        foreach (var (name, value) in parameters) Rows.Add(cmd, name, value);
        using var r = cmd.ExecuteReader();
        var list = new List<HouseholdMember>();
        while (r.Read())
        {
            list.Add(new HouseholdMember(
                r.GetString(0), r.GetString(1), (ChassisKind)r.GetInt32(2), (byte[])r.GetValue(3), (byte[])r.GetValue(4), r.GetInt64(5),
                r.IsDBNull(6) ? null : r.GetInt64(6), r.IsDBNull(7) ? null : r.GetInt64(7)));
        }
        return list;
    }

    private int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) Rows.Add(cmd, name, value);
        return cmd.ExecuteNonQuery();
    }
}
