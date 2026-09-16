using System.Globalization;
using Microsoft.Data.Sqlite;
using PowerLedger.Storage;

namespace PowerLedger.Service;

/// <summary>Opens the service's one database (spec §7, §10).</summary>
internal static class DatabaseOpener
{
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    /// <summary>
    /// Opens, checks and migrates the database. A file SQLite cannot read as a database is set aside as
    /// power.corrupt-&lt;time&gt;.db and a fresh one started, and the notice says so for the status screen.
    /// </summary>
    public static (SqliteDatabase Database, string? Notice) Open(string path, DateTimeOffset now)
    {
        try
        {
            var database = SqliteDatabase.OpenAndMigrate(path);
            if (QuickCheck(database) is null) return (database, null);
            database.Dispose();
        }
        catch (SqliteException error) when (error.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
        }

        // Closes what the failed open left pooled on this file, so it can be moved, and nothing else: clearing every pool would
        // also close a connection that another thread is opening to another database.
        new SqliteDatabase(path).Dispose();
        var aside = SetAside(path, "corrupt", now);
        return (SqliteDatabase.OpenAndMigrate(path), $"The database was damaged. It was set aside as {Path.GetFileName(aside)} and a new one started.");
    }

    /// <summary>Null when SQLite's quick integrity check passes; otherwise its first complaint.</summary>
    internal static string? QuickCheck(SqliteDatabase database)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check(1)";
        var result = command.ExecuteScalar() as string;
        return result == "ok" ? null : result ?? "no answer";
    }

    /// <summary>Renames the database and its write-ahead log and shared-memory files out of the way, keeping them together.</summary>
    /// <returns>The database file's new path.</returns>
    internal static string SetAside(string path, string label, DateTimeOffset now)
    {
        var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var aside = Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}.{label}-{stamp}.db");
        foreach (var suffix in new[] { "-wal", "-shm", "" })
        {
            if (File.Exists(path + suffix)) File.Move(path + suffix, aside + suffix, overwrite: true);
        }
        return aside;
    }
}
