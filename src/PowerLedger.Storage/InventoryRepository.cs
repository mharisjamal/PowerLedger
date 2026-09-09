namespace PowerLedger.Storage;

/// <param name="Hash">Stable hash of the detected hardware set (computed by the Sensors project in Plan B).</param>
/// <param name="Json">The detected inventory, serialised for display and diagnostics.</param>
public sealed record InventoryRecord(string Hash, DateTimeOffset DetectedAt, string Json);

public sealed class InventoryRepository(SqliteDatabase db)
{
    /// <summary>Inserts a new hardware set; an existing hash keeps its first detection time.</summary>
    public void Upsert(InventoryRecord record)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO hardware_inventory(hash, detected_ms, json) VALUES ($hash, $detected, $json)";
        Rows.Add(cmd, "$hash", record.Hash);
        Rows.Add(cmd, "$detected", Rows.Ms(record.DetectedAt));
        Rows.Add(cmd, "$json", record.Json);
        cmd.ExecuteNonQuery();
    }

    public InventoryRecord? Latest() => All().OrderByDescending(r => r.DetectedAt).FirstOrDefault();

    public List<InventoryRecord> All()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT hash, detected_ms, json FROM hardware_inventory ORDER BY detected_ms";
        using var r = cmd.ExecuteReader();
        var list = new List<InventoryRecord>();
        while (r.Read()) list.Add(new InventoryRecord(r.GetString(0), Rows.Time(r.GetInt64(1)), r.GetString(2)));
        return list;
    }
}
