using Microsoft.Data.Sqlite;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Storage.Tests;

/// <summary>Storage version 2 (data-sharing design §4): what each reading measured, and the outbox of what waits to be sent.</summary>
public sealed class MigrationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"powerledger-v1-{Guid.NewGuid():N}.db");

    [Fact]
    public void A_fresh_database_is_at_version_2_with_the_new_columns_and_the_outbox()
    {
        using var db = SqliteDatabase.OpenAndMigrate(_path);
        using var c = db.Open();
        Migrator.LatestVersion.ShouldBe(2);
        Migrator.CurrentVersion(c).ShouldBe(2);
        Columns(c, "samples_raw").ShouldContain("total_source");
        Columns(c, "samples_raw").ShouldContain("gpu_scope");
        Columns(c, "samples_raw").ShouldContain("measured_mask");
        Columns(c, "outbox_minutes").Count.ShouldBe(29);
        Columns(c, "outbox_events").ShouldBe(new[] { "id", "day", "kind", "json" });
    }

    [Fact]
    public void A_version_1_database_moves_to_2_and_its_old_readings_read_0_in_the_new_columns()
    {
        MakeVersion1(_path);

        using var db = SqliteDatabase.OpenAndMigrate(_path);
        using var c = db.Open();
        Migrator.CurrentVersion(c).ShouldBe(2);
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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
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
