using Microsoft.Data.Sqlite;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Storage.Tests;

public class SqliteDatabaseTests
{
    [Fact]
    public void A_new_database_is_migrated_to_the_latest_version_in_wal_mode()
    {
        using var t = new TestDatabase();
        using var c = t.Db.Open();
        Migrator.CurrentVersion(c).ShouldBe(Migrator.LatestVersion);
        Scalar<string>(c, "PRAGMA journal_mode").ShouldBe("wal");
        Scalar<long>(c, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('samples_raw','samples_1m','samples_1h','sessions','tariffs','calibration','hardware_inventory','settings')").ShouldBe(8);
    }

    [Fact]
    public void Opening_twice_does_not_re_run_migrations()
    {
        using var t = new TestDatabase();
        using var again = SqliteDatabase.OpenAndMigrate(t.Path);
        using var c = again.Open();
        Scalar<long>(c, "SELECT COUNT(*) FROM schema_version").ShouldBe(Migrator.LatestVersion);
    }

    [Fact]
    public void A_read_only_handle_can_read_while_a_writer_holds_a_transaction()
    {
        using var t = new TestDatabase();
        using var writer = t.Db.Open();
        using var tx = writer.BeginTransaction();
        Exec(writer, "INSERT INTO settings(key, value) VALUES ('a', '1')");

        using var readerDb = new SqliteDatabase(t.Path, readOnly: true);
        using var reader = readerDb.Open();
        Scalar<long>(reader, "SELECT COUNT(*) FROM settings").ShouldBe(0);
        tx.Commit();
        Scalar<long>(reader, "SELECT COUNT(*) FROM settings").ShouldBe(1);
    }

    private static T Scalar<T>(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T));
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
