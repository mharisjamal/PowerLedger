using System.Reflection;
using Microsoft.Data.Sqlite;

namespace PowerLedger.Service.Tests;

/// <summary>
/// Holds an open connection in the state Microsoft.Data.Sqlite 10 gives a pooled connection for an instant while another
/// thread opens it: marked active, but not yet tied to the SqliteConnection opening it (SqliteConnectionInternal.Activate
/// sets the one and then the other, outside the pool's lock). A pool cleared in that instant, by ClearPool or ClearAllPools
/// on any thread, takes the connection for one whose owner leaked it and closes it, and the opener's first command throws
/// ObjectDisposedException.
/// </summary>
internal sealed class MidOpen : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly WeakReference<SqliteConnection?> _owner;

    private MidOpen(SqliteConnection connection)
    {
        var pooled = Field(typeof(SqliteConnection), "_innerConnection").GetValue(connection)
            ?? throw new InvalidOperationException("Only an open connection can be held mid-open.");
        _connection = connection;
        _owner = (WeakReference<SqliteConnection?>)Field(pooled.GetType(), "_outerConnection").GetValue(pooled)!;
        _owner.SetTarget(null);
        if (pooled.GetType().GetProperty("Leaked")?.GetValue(pooled) is not true)
        {
            Dispose();
            throw new InvalidOperationException(
                "Microsoft.Data.Sqlite no longer takes a connection in this state for a leaked one; see whether clearing a pool can still close a connection that is being opened.");
        }
    }

    public static MidOpen Hold(SqliteConnection connection) => new(connection);

    /// <summary>Ties the connection to its SqliteConnection, which is what the open does next.</summary>
    public void Dispose() => _owner.SetTarget(_connection);

    private static FieldInfo Field(Type type, string name)
        => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"Microsoft.Data.Sqlite has no {type.Name}.{name} any more; see whether clearing a pool can still close a connection that is being opened.");
}
