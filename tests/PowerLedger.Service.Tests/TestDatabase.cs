using PowerLedger.Storage;

namespace PowerLedger.Service.Tests;

/// <summary>A fresh migrated database in its own temp folder, deleted on dispose (WAL needs a real file).</summary>
internal sealed class TestDatabase : IDisposable
{
    public TestDatabase()
    {
        Directory.CreateDirectory(Folder);
        Db = SqliteDatabase.OpenAndMigrate(DatabasePath);
    }

    public string Folder { get; } = Path.Combine(Path.GetTempPath(), $"powerledger-service-{Guid.NewGuid():N}");

    public string DatabasePath => Path.Combine(Folder, "power.db");

    public SqliteDatabase Db { get; }

    /// <summary>Closes this database's pooled connections, and no other's, then deletes the folder. Clearing every pool would
    /// also close a connection that a test running alongside is opening (see <see cref="MidOpen"/>).</summary>
    public void Dispose()
    {
        Db.Dispose();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
