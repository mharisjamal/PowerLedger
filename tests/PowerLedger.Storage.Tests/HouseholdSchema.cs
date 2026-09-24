using PowerLedger.Storage;

namespace PowerLedger.Storage.Tests;

/// <summary>
/// Test data for the households tables of Storage V3 (Plan N task C1): a migrated test database, and rows put straight
/// into household_members and household_rows.
/// </summary>
internal static class HouseholdSchema
{
    /// <summary>A fresh, migrated <see cref="TestDatabase"/>; V3 made the households tables.</summary>
    public static TestDatabase Create() => new();

    public static void AddMember(
        TestDatabase t, string deviceId, string name, int kind, byte[] signKey, byte[] dhKey, long addedMs, long? leftMs = null,
        long? lastSyncedMs = null)
    {
        using var c = t.Db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO household_members (device_id, name, kind, sign_key, dh_key, added_ms, left_ms, last_synced_ms)
            VALUES ($device, $name, $kind, $sign, $dh, $added, $left, $synced)
            """;
        Rows.Add(cmd, "$device", deviceId);
        Rows.Add(cmd, "$name", name);
        Rows.Add(cmd, "$kind", kind);
        Rows.Add(cmd, "$sign", signKey);
        Rows.Add(cmd, "$dh", dhKey);
        Rows.Add(cmd, "$added", addedMs);
        Rows.Add(cmd, "$left", leftMs);
        Rows.Add(cmd, "$synced", lastSyncedMs);
        cmd.ExecuteNonQuery();
    }

    public static void AddRow(
        TestDatabase t, string deviceId, long hourMs, double energyWh, long? costMicro, string? currency, long changedMs,
        double cpuWh = 0, double gpuWh = 0, double displayWh = 0, double restWh = 0)
    {
        using var c = t.Db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO household_rows (
                device_id, hour_ms, energy_wh, cpu_wh, gpu_wh, display_wh, rest_wh, idle_on_wh, idle_off_wh,
                on_s, battery_s, idle_s, measured_s, calibrated_s, estimated_s, cost_micro, currency, changed_ms)
            VALUES (
                $device, $hour, $energy, $cpu, $gpu, $display, $rest, 0, 0,
                3600, 0, 0, 3600, 0, 0, $cost, $currency, $changed)
            """;
        Rows.Add(cmd, "$device", deviceId);
        Rows.Add(cmd, "$hour", hourMs);
        Rows.Add(cmd, "$energy", energyWh);
        Rows.Add(cmd, "$cpu", cpuWh);
        Rows.Add(cmd, "$gpu", gpuWh);
        Rows.Add(cmd, "$display", displayWh);
        Rows.Add(cmd, "$rest", restWh);
        Rows.Add(cmd, "$cost", costMicro);
        Rows.Add(cmd, "$currency", currency);
        Rows.Add(cmd, "$changed", changedMs);
        cmd.ExecuteNonQuery();
    }
}
