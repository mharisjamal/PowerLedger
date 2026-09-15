using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace PowerLedger.Storage;

/// <summary>
/// Keeps a database's write-ahead log and shared-memory files when its last connection closes (SQLITE_FCNTL_PERSIST_WAL).
/// SQLite deletes them on a clean close, and a reader that cannot create files in the folder then cannot open the database,
/// because "the WAL and shared memory files must exist in order for the database to be readable". The App reads with the
/// Users group's read-only access to the data folder, so without them it could not read history while the service is
/// stopped. Neither Microsoft.Data.Sqlite nor SQLitePCLRaw's public API offers sqlite3_file_control, so this imports it
/// from e_sqlite3, the library their bundled provider loads and therefore the SQLite that owns each connection's handle.
/// </summary>
internal static class PersistentWal
{
    private const int SqliteOk = 0;
    private const int SqliteFcntlPersistWal = 10;
    private static readonly byte[] MainDatabase = "main\0"u8.ToArray();

    /// <summary>
    /// Asks SQLite to keep the files if this open read-write connection is the last to close. Best effort: false when SQLite
    /// declines, and the database then works as before, losing the files at the last close.
    /// </summary>
    public static bool TryEnable(SqliteConnection connection)
    {
        var persist = 1;
        return sqlite3_file_control(connection.Handle!, MainDatabase, SqliteFcntlPersistWal, ref persist) == SqliteOk;
    }

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int sqlite3_file_control(sqlite3 db, byte[] zDbName, int op, ref int arg);
}
