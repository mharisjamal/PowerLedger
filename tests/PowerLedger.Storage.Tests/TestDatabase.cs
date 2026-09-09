using PowerLedger.Storage;

namespace PowerLedger.Storage.Tests;

/// <summary>A fresh migrated database in a temp file, deleted on dispose (WAL needs a real file).</summary>
internal sealed class TestDatabase : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"powerledger-test-{Guid.NewGuid():N}.db");
    public SqliteDatabase Db { get; }

    public TestDatabase() => Db = SqliteDatabase.OpenAndMigrate(Path);

    public void Dispose()
    {
        Db.Dispose();
        foreach (var f in new[] { Path, Path + "-wal", Path + "-shm", Path + ".bak" })
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }
}
