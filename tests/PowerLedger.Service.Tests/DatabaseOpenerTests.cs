using Microsoft.Data.Sqlite;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

public sealed class DatabaseOpenerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 10, 30, 15, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"powerledger-open-{Guid.NewGuid():N}");

    public DatabaseOpenerTests() => Directory.CreateDirectory(_folder);

    private string DbPath => Path.Combine(_folder, "power.db");

    [Fact]
    public void A_healthy_database_opens_with_its_data_and_no_notice()
    {
        using (var first = SqliteDatabase.OpenAndMigrate(DbPath)) new SettingsRepository(first).Set("k", "v");
        var (database, notice) = DatabaseOpener.Open(DbPath, Now);
        using (database)
        {
            notice.ShouldBeNull();
            new SettingsRepository(database).Get("k").ShouldBe("v");
        }
    }

    [Fact]
    public void A_file_that_is_not_a_database_is_set_aside_and_a_fresh_one_started()
    {
        File.WriteAllBytes(DbPath, Enumerable.Repeat((byte)0x5A, 8192).ToArray());
        var (database, notice) = DatabaseOpener.Open(DbPath, Now);
        using (database)
        {
            notice.ShouldNotBeNull().ShouldContain("power.corrupt-20260912-103015.db");
            File.Exists(Path.Combine(_folder, "power.corrupt-20260912-103015.db")).ShouldBeTrue();
            var settings = new SettingsRepository(database);
            settings.Set("fresh", "yes");
            settings.Get("fresh").ShouldBe("yes");
        }
    }

    [Fact]
    public void Setting_a_damaged_database_aside_leaves_alone_a_connection_to_another_database_that_is_being_opened()
    {
        using var other = new TestDatabase();
        using var connection = other.Db.Open();
        File.WriteAllBytes(DbPath, Enumerable.Repeat((byte)0x5A, 8192).ToArray());

        using (MidOpen.Hold(connection))
        {
            DatabaseOpener.Open(DbPath, Now).Database.Dispose();
        }

        new SqliteCommand("SELECT 1", connection).ExecuteScalar().ShouldBe(1L);
    }

    [Fact]
    public void Setting_aside_moves_the_write_ahead_log_and_shared_memory_with_the_database()
    {
        var suffixes = new[] { "", "-wal", "-shm" };
        foreach (var suffix in suffixes) File.WriteAllText(DbPath + suffix, "content" + suffix);
        var aside = DatabaseOpener.SetAside(DbPath, "corrupt", Now);
        aside.ShouldBe(Path.Combine(_folder, "power.corrupt-20260912-103015.db"));
        foreach (var suffix in suffixes)
        {
            File.Exists(DbPath + suffix).ShouldBeFalse();
            File.ReadAllText(aside + suffix).ShouldBe("content" + suffix);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
