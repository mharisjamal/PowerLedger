using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Storage.Tests;

public class SqliteDatabaseTests
{
    [Fact]
    public void A_new_database_is_migrated_to_the_latest_version_in_wal_mode_with_incremental_vacuum()
    {
        using var t = new TestDatabase();
        using var c = t.Db.Open();
        Migrator.CurrentVersion(c).ShouldBe(Migrator.LatestVersion);
        Scalar<string>(c, "PRAGMA journal_mode").ShouldBe("wal");
        Scalar<long>(c, "PRAGMA auto_vacuum").ShouldBe(2);
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

    [Fact]
    public void A_backup_taken_while_a_writer_is_open_includes_uncheckpointed_rows()
    {
        using var t = new TestDatabase();
        using var writer = t.Db.Open();
        for (var i = 0; i < 200; i++) Exec(writer, $"INSERT INTO settings(key, value) VALUES ('k{i}', '{i}')");

        var bak = t.Path + ".bak";
        SqliteDatabase.Backup(writer, bak);

        using var bakDb = new SqliteDatabase(bak, readOnly: true);
        using var reader = bakDb.Open();
        Scalar<long>(reader, "SELECT COUNT(*) FROM settings").ShouldBe(200);
        Scalar<string>(reader, "PRAGMA journal_mode").ShouldBe("delete");
        File.Exists(bak + "-wal").ShouldBeFalse();
    }

    [Fact]
    public void A_read_only_handle_to_a_missing_file_throws_and_creates_nothing()
    {
        var missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"powerledger-missing-{Guid.NewGuid():N}.db");
        using var db = new SqliteDatabase(missing, readOnly: true);
        Should.Throw<SqliteException>(() => db.Open());
        File.Exists(missing).ShouldBeFalse();
    }

    [Fact]
    public void The_write_ahead_log_and_shared_memory_stay_when_the_last_writer_closes()
    {
        using var t = new TestDatabase();
        new SettingsRepository(t.Db).Set("a", "1");
        SqliteConnection.ClearAllPools();
        File.Exists(t.Path + "-wal").ShouldBeTrue();
        File.Exists(t.Path + "-shm").ShouldBeTrue();
    }

    [Fact]
    public void A_reader_that_cannot_write_the_folder_reads_after_the_writer_closes()
    {
        // The App reads the service's database with read-only access to its folder, so it cannot create the files itself.
        if (!OperatingSystem.IsWindows()) return;
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"powerledger-readonly-{Guid.NewGuid():N}");
        var path = System.IO.Path.Combine(folder, "power.db");
        Directory.CreateDirectory(folder);
        try
        {
            using (var writer = SqliteDatabase.OpenAndMigrate(path)) new SettingsRepository(writer).Set("tariff.currency", "EUR");
            SqliteConnection.ClearAllPools();
            Protect(folder, FileSystemRights.ReadAndExecute);
            Should.Throw<UnauthorizedAccessException>(() => File.WriteAllBytes(System.IO.Path.Combine(folder, "probe"), []));

            using var reader = new SqliteDatabase(path, readOnly: true);
            new SettingsRepository(reader).Get("tariff.currency").ShouldBe("EUR");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Protect(folder, FileSystemRights.FullControl);
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Prices_round_trip_through_micro_units_rounding_half_away_from_zero()
    {
        Rows.Micro(0.17m).ShouldBe(170_000);
        Rows.Micro(0.1234565m).ShouldBe(123_457);
        Rows.Price(123_457).ShouldBe(0.123457m);
    }

    /// <summary>
    /// The data folder's protected ACL with the current user in Users' place: SYSTEM full control and the user
    /// <paramref name="rights"/>, inherited by everything inside and nothing from above. Administrators are left out, since
    /// an elevated run, as on the hosted CI runners, would have full control through them and could create the files.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void Protect(string folder, FileSystemRights rights)
    {
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        using var user = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(user.User!, rights, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(folder).SetAccessControl(security);
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
