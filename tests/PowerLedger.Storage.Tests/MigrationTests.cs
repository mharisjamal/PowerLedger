using Microsoft.Data.Sqlite;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Storage.Tests;

/// <summary>Storage version 2 (data-sharing design §4): what each reading measured, and the outbox of what waits to be sent.</summary>
public sealed class MigrationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"powerledger-v1-{Guid.NewGuid():N}.db");

    [Fact]
    public void A_fresh_database_is_at_the_latest_version_with_the_new_columns_and_the_outbox()
    {
        using var db = SqliteDatabase.OpenAndMigrate(_path);
        using var c = db.Open();
        Migrator.LatestVersion.ShouldBe(6);
        Migrator.CurrentVersion(c).ShouldBe(6);
        // Review item 2: each app's key, name and path once, the hours naming it by number.
        Columns(c, "apps").ShouldBe(new[] { "id", "key", "name", "path" });
        Columns(c, "app_hours").ShouldBe(new[] { "start_ms", "app_id", "cpu_wh", "gpu_wh", "run_s", "front_s" });
        Columns(c, "samples_raw").ShouldContain("asleep_w");
        Columns(c, "samples_1m").ShouldContain("asleep_wh");
        Columns(c, "samples_1h").ShouldContain("asleep_wh");
        Columns(c, "samples_raw").ShouldContain("total_source");
        Columns(c, "samples_raw").ShouldContain("gpu_scope");
        Columns(c, "samples_raw").ShouldContain("measured_mask");
        Columns(c, "outbox_minutes").Count.ShouldBe(29);
        Columns(c, "outbox_events").ShouldBe(new[] { "id", "day", "kind", "json" });
    }

    [Fact]
    public void A_version_1_database_moves_to_the_latest_and_its_old_readings_read_0_in_the_new_columns()
    {
        MakeVersion1(_path);

        using var db = SqliteDatabase.OpenAndMigrate(_path);
        using var c = db.Open();
        Migrator.CurrentVersion(c).ShouldBe(Migrator.LatestVersion);
        Scalar(c, "SELECT COUNT(*) FROM samples_raw").ShouldBe(1);
        Scalar(c, "SELECT total_source FROM samples_raw").ShouldBe(0);
        Scalar(c, "SELECT gpu_scope FROM samples_raw").ShouldBe(0);
        Scalar(c, "SELECT measured_mask FROM samples_raw").ShouldBe(0);
        new RawSampleRepository(db).Read(DateTimeOffset.FromUnixTimeMilliseconds(0), DateTimeOffset.FromUnixTimeMilliseconds(2000))
            .ShouldHaveSingleItem().TotalW.ShouldBe(30);
    }

    [Fact]
    public void A_version_1_database_is_backed_up_as_it_was_before_it_is_upgraded()
    {
        MakeVersion1(_path);

        using (SqliteDatabase.OpenAndMigrate(_path))
        {
        }

        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path + ".bak", Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        backup.Open();
        Scalar(backup, "SELECT MAX(version) FROM schema_version").ShouldBe(1);
        Scalar(backup, "SELECT COUNT(*) FROM samples_raw").ShouldBe(1);
        Columns(backup, "samples_raw").ShouldNotContain("measured_mask");
        Scalar(backup, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'outbox_minutes'").ShouldBe(0);
    }

    [Fact]
    public void A_version_3_database_keeps_its_learned_buckets_as_buckets_from_before_the_cpu_share()
    {
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString()))
        {
            c.Open();
            Migrator.Exec(c, "CREATE TABLE schema_version (version INTEGER NOT NULL, applied_ms INTEGER NOT NULL)");
            Migrator.Exec(c, Schema.V1 + Schema.V2 + Schema.V3);
            Migrator.Exec(c, "INSERT INTO schema_version(version, applied_ms) VALUES (1, 0), (2, 0), (3, 0)");
            Migrator.Exec(c, "INSERT INTO calibration (inventory_hash, bucket, baseline_w, samples, updated_ms) VALUES ('abc', 6, 3.4, 6000, 0)");
        }

        using var db = SqliteDatabase.OpenAndMigrate(_path);
        using (var c = db.Open()) Migrator.CurrentVersion(c).ShouldBe(Migrator.LatestVersion);
        new CalibrationRepository(db).Load("abc").Buckets.ShouldBe([new Core.BucketState(6, 3.4, 6000)]);
    }

    /// <summary>Accuracy A9: a row from before version 5 takes as the battery's energy its energy's share of on-time spent on
    /// battery; it has no asleep energy, which was never booked then.</summary>
    [Fact]
    public void A_version_4_database_gives_its_rows_the_battery_s_share_of_their_energy()
    {
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString()))
        {
            c.Open();
            Migrator.Exec(c, "CREATE TABLE schema_version (version INTEGER NOT NULL, applied_ms INTEGER NOT NULL)");
            Migrator.Exec(c, Schema.V1 + Schema.V2 + Schema.V3 + Schema.V4);
            Migrator.Exec(c, "INSERT INTO schema_version(version, applied_ms) VALUES (1, 0), (2, 0), (3, 0), (4, 0)");
            Migrator.Exec(c, "INSERT INTO samples_1m VALUES (0, 30, 30, 0.5, 0.2, 0.05, 0.07, 0.18, 0, 0, 0, 0, 60, 30, 0, 60, 60, 0, 0)");
            Migrator.Exec(c, "INSERT INTO samples_1m VALUES (60000, 30, 30, 0.5, 0.2, 0.05, 0.07, 0.18, 0, 0, 0, 0, 0, 0, 3600, 1, 0, 0, 0)");
            Migrator.Exec(c, "INSERT INTO samples_1h VALUES (36000000, 30, 30, 30, 12, 3, 4, 11, 0, 0, 0, 0, 3600, 3600, 0, 3600, 3600, 0, 0)");
        }

        using var db = SqliteDatabase.OpenAndMigrate(_path);
        var repo = new AggregateRepository(db);
        var minutes = repo.ReadMinutes(DateTimeOffset.FromUnixTimeMilliseconds(0), DateTimeOffset.FromUnixTimeMilliseconds(120_000));
        minutes.Select(m => m.BatteryWh).ShouldBe([0.25, 0]);
        minutes.Select(m => m.AsleepWh).ShouldBe([0.0, 0]);
        repo.ReadHours(DateTimeOffset.FromUnixTimeMilliseconds(36000000), DateTimeOffset.FromUnixTimeMilliseconds(36000001)).ShouldHaveSingleItem().BatteryWh.ShouldBe(30);
    }

    /// <summary>Plan AC review: a laptop draws less on its battery than on AC, so an hour's energy shared by the time it spent
    /// on battery gave the battery too much of it (9.7% on the owner's laptop). An hour whose minutes are kept takes the sum
    /// of theirs, which are on battery or not throughout; the share stands in only for the time whose minutes are purged.</summary>
    [Fact]
    public void A_version_4_database_gives_an_hour_the_battery_energy_of_its_minutes()
    {
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString()))
        {
            c.Open();
            Migrator.Exec(c, "CREATE TABLE schema_version (version INTEGER NOT NULL, applied_ms INTEGER NOT NULL)");
            Migrator.Exec(c, Schema.V1 + Schema.V2 + Schema.V3 + Schema.V4);
            Migrator.Exec(c, "INSERT INTO schema_version(version, applied_ms) VALUES (1, 0), (2, 0), (3, 0), (4, 0)");
            // The first hour: a minute on battery at 12 W and one on AC at 30 W, both kept.
            Migrator.Exec(c, "INSERT INTO samples_1m VALUES (0, 12, 12, 0.2, 0, 0, 0, 0.2, 0, 0, 0, 0, 60, 60, 0, 60, 60, 0, 0)");
            Migrator.Exec(c, "INSERT INTO samples_1m VALUES (60000, 30, 30, 0.5, 0, 0, 0, 0.5, 0, 0, 0, 0, 60, 0, 0, 60, 60, 0, 0)");
            Migrator.Exec(c, "INSERT INTO samples_1h VALUES (0, 21, 30, 0.7, 0, 0, 0, 0.7, 0, 0, 0, 0, 120, 60, 0, 120, 120, 0, 0)");
            // The second: its first minutes purged (an hour on battery at 12 W for half of 40 minutes, then 20 at 30 W on
            // AC), and its last minute, on battery at 12 W, kept.
            Migrator.Exec(c, "INSERT INTO samples_1m VALUES (7140000, 12, 12, 0.2, 0, 0, 0, 0.2, 0, 0, 0, 0, 60, 60, 0, 60, 60, 0, 0)");
            Migrator.Exec(c, "INSERT INTO samples_1h VALUES (3600000, 20, 30, 14.2, 0, 0, 0, 14.2, 0, 0, 0, 0, 2460, 1260, 0, 2460, 2460, 0, 0)");
            // The third: on AC throughout.
            Migrator.Exec(c, "INSERT INTO samples_1h VALUES (7200000, 30, 30, 30, 0, 0, 0, 30, 0, 0, 0, 0, 3600, 0, 0, 3600, 3600, 0, 0)");
        }

        using var db = SqliteDatabase.OpenAndMigrate(_path);
        var hours = new AggregateRepository(db).ReadHours(DateTimeOffset.FromUnixTimeMilliseconds(0), DateTimeOffset.FromUnixTimeMilliseconds(3 * 3600_000));
        hours.Select(h => h.BatteryWh).ToArray().ShouldBe([0.2, 0.2 + (14.0 * 1200 / 2400), 0], tolerance: 1e-9);
    }

    /// <summary>Each test disposes its own database, which closes only that database's pooled connections: clearing every
    /// pool could close a connection a test running alongside is opening.</summary>
    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm", _path + ".bak" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>A database as version 1 left it, holding one reading.</summary>
    private static void MakeVersion1(string path)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        Migrator.Exec(c, "CREATE TABLE schema_version (version INTEGER NOT NULL, applied_ms INTEGER NOT NULL)");
        Migrator.Exec(c, Schema.V1);
        Migrator.Exec(c, "INSERT INTO schema_version(version, applied_ms) VALUES (1, 0)");
        Migrator.Exec(c, "INSERT INTO samples_raw (ts_ms, delta_s, total_w, quality, cpu_w, gpu_w, display_w, ram_w, storage_w, board_w, " +
            "extras_w, monitors_w, psu_loss_w, unattributed_w, on_battery, display_on, user_idle, locked, cpu_load, gpu_load, brightness, suspect) " +
            "VALUES (1000, 1, 30, 2, 12, 3, 4, 0, 0, 0, 0, 0, 0, 11, 1, 1, 0, 0, 0.3, NULL, 0.6, 0)");
    }

    private static List<string> Columns(SqliteConnection c, string table)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid";
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }

    private static long Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
