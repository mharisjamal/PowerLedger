using Microsoft.Data.Sqlite;
using PowerLedger.Storage;

namespace PowerLedger.Storage.Tests;

/// <summary>
/// Stands in for Storage V3 (Plan N task C1, built by the service agent in a worktree this one can't see): the exact
/// column list the households plan gives for household_rows and household_members. Tests exec this by hand since the
/// Migrator here has no V3 yet; once C1 lands and the two branches merge, this file's columns must still match it.
/// </summary>
internal static class HouseholdSchema
{
    public const string Sql = """
        CREATE TABLE household_rows (
            device_id    TEXT    NOT NULL,
            hour_ms      INTEGER NOT NULL,
            energy_wh    REAL    NOT NULL,
            cpu_wh       REAL    NOT NULL,
            gpu_wh       REAL    NOT NULL,
            display_wh   REAL    NOT NULL,
            rest_wh      REAL    NOT NULL,
            idle_on_wh   REAL    NOT NULL,
            idle_off_wh  REAL    NOT NULL,
            on_s         REAL    NOT NULL,
            battery_s    REAL    NOT NULL,
            idle_s       REAL    NOT NULL,
            measured_s   REAL    NOT NULL,
            calibrated_s REAL    NOT NULL,
            estimated_s  REAL    NOT NULL,
            cost_micro   INTEGER,
            currency     TEXT,
            changed_ms   INTEGER NOT NULL,
            PRIMARY KEY (device_id, hour_ms)
        );

        CREATE TABLE household_members (
            device_id      TEXT PRIMARY KEY,
            name           TEXT    NOT NULL,
            kind           INTEGER NOT NULL,
            sign_key       BLOB    NOT NULL,
            dh_key         BLOB    NOT NULL,
            added_ms       INTEGER NOT NULL,
            left_ms        INTEGER,
            last_synced_ms INTEGER
        );
        """;

    /// <summary>A fresh <see cref="TestDatabase"/> with the households tables created.</summary>
    public static TestDatabase Create()
    {
        var t = new TestDatabase();
        using var c = t.Db.Open();
        Migrator.Exec(c, Sql);
        return t;
    }

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
