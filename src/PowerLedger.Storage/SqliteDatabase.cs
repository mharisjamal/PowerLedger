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

    /// <summary>Opens (creating if needed), backs up before a schema upgrade, sets pragmas and migrates.</summary>
    public static SqliteDatabase OpenAndMigrate(string path)
    {
        var existedBefore = File.Exists(path) && new FileInfo(path).Length > 0;
        var db = new SqliteDatabase(path);
        using var c = db.Open();
        Migrator.Exec(c, "PRAGMA auto_vacuum = INCREMENTAL");   // only takes effect on a brand-new file; harmless otherwise
        if (existedBefore && Migrator.CurrentVersion(c) < Migrator.LatestVersion)
        {
            File.Copy(path, path + ".bak", overwrite: true);   // spec §7: backup once per schema version bump
        }
        Migrator.Apply(c);
        return db;
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        Migrator.Exec(c, "PRAGMA busy_timeout = 5000");
        if (!_readOnly)
        {
            Migrator.Exec(c, "PRAGMA journal_mode = WAL");
            Migrator.Exec(c, "PRAGMA synchronous = NORMAL");
            Migrator.Exec(c, "PRAGMA foreign_keys = ON");
        }
        return c;
    }

    public void Dispose() => SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
}
