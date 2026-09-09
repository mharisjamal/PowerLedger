using Microsoft.Data.Sqlite;

namespace PowerLedger.Storage;

/// <summary>Connection factory for one database file. WAL mode lets the App read while the Service writes.</summary>
public sealed class SqliteDatabase : IDisposable
{
    private readonly string _connectionString;
    private readonly bool _readOnly;

    public string Path { get; }

    public SqliteDatabase(string path, bool readOnly = false)
    {
        Path = path;
        _readOnly = readOnly;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    /// <summary>Opens (creating if needed), takes a complete backup before a schema upgrade, and migrates.</summary>
    public static SqliteDatabase OpenAndMigrate(string path)
    {
        var existedBefore = File.Exists(path) && new FileInfo(path).Length > 0;
        var db = new SqliteDatabase(path);
        using var c = db.Open();
        if (existedBefore && Migrator.CurrentVersion(c) < Migrator.LatestVersion)
        {
            Backup(c, path + ".bak");   // spec §7: backup once per schema version bump
        }
        Migrator.Apply(c);
        return db;
    }

    /// <summary>A connection with busy_timeout set; writers also get WAL, synchronous=NORMAL and foreign keys.</summary>
    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        try
        {
            c.Open();
            Migrator.Exec(c, "PRAGMA busy_timeout = 5000");
            if (!_readOnly)
            {
                // auto_vacuum is baked into page 1 by the first write, so it must precede journal_mode on a brand-new file.
                if (IsEmptyFile()) Migrator.Exec(c, "PRAGMA auto_vacuum = INCREMENTAL");
                Migrator.Exec(c, "PRAGMA journal_mode = WAL");
                Migrator.Exec(c, "PRAGMA synchronous = NORMAL");
                Migrator.Exec(c, "PRAGMA foreign_keys = ON");
            }
            return c;
        }
        catch
        {
            c.Dispose();
            throw;
        }
    }

    /// <summary>Complete copy through the online backup API (WAL content included), written as a single non-WAL file.</summary>
    internal static void Backup(SqliteConnection source, string backupPath)
    {
        foreach (var f in new[] { backupPath, backupPath + "-wal", backupPath + "-shm" }) File.Delete(f);
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        destination.Open();
        source.BackupDatabase(destination);
        Migrator.Exec(destination, "PRAGMA journal_mode = DELETE");
    }

    public void Dispose()
    {
        using var probe = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(probe);
    }

    private bool IsEmptyFile() => !File.Exists(Path) || new FileInfo(Path).Length == 0;
}
