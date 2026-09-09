using Microsoft.Data.Sqlite;

namespace PowerLedger.Storage;

public static class Migrator
{
    private static readonly IReadOnlyList<(int Version, string Sql)> Migrations = [(1, Schema.V1)];

    public static int LatestVersion => Migrations[^1].Version;

    /// <summary>Stored schema version, 0 when never migrated. Creates the schema_version table if missing, so a never-migrated file needs a writable connection.</summary>
    public static int CurrentVersion(SqliteConnection c)
    {
        Exec(c, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL, applied_ms INTEGER NOT NULL)");
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Applies every migration newer than the stored version, each in its own transaction.</summary>
    public static void Apply(SqliteConnection c)
    {
        var current = CurrentVersion(c);
        foreach (var (version, sql) in Migrations)
        {
            if (version <= current) continue;
            using var tx = c.BeginTransaction();
            Exec(c, sql);
            Exec(c, $"INSERT INTO schema_version(version, applied_ms) VALUES ({version}, {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()})");
            tx.Commit();
        }
    }

    internal static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
