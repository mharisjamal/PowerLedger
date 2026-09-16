using System.Management;
using System.Runtime.InteropServices;
using EnumerationOptions = System.Management.EnumerationOptions;

namespace PowerLedger.Sensors;

/// <summary>
/// The one way this assembly asks WMI anything. Queries are forward-only with a timeout, because a hung WMI provider
/// would otherwise stall whichever thread asked, and every row is disposed, because each one holds a COM object.
/// </summary>
internal static class Wmi
{
    /// <summary>How long one row may take to arrive. Healthy queries answer in milliseconds.</summary>
    private static readonly TimeSpan RowTimeout = TimeSpan.FromSeconds(5);

    /// <summary>What WMI throws when it cannot answer: a refused or timed-out query, the WMI service stopping or
    /// restarting, or a namespace the caller may not read.</summary>
    public static bool IsFailure(Exception error) => error is ManagementException or COMException or UnauthorizedAccessException;

    /// <summary>Runs one query and folds its rows. Throws whatever WMI throws; see <see cref="IsFailure"/>.</summary>
    public static T Read<T>(string scope, string query, Func<IReadOnlyList<ManagementBaseObject>, T> fold)
    {
        var options = new EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = RowTimeout };
        var rows = new List<ManagementBaseObject>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, query, options);
            using var results = searcher.Get();
            foreach (var row in results) rows.Add(row);
            return fold(rows);
        }
        finally
        {
            foreach (var row in rows) row.Dispose();
        }
    }

    /// <summary>As <see cref="Read{T}"/>, but a query WMI cannot answer comes back as <paramref name="fallback"/>.</summary>
    public static T ReadOr<T>(string scope, string query, Func<IReadOnlyList<ManagementBaseObject>, T> fold, T fallback)
    {
        try
        {
            return Read(scope, query, fold);
        }
        catch (Exception error) when (IsFailure(error))
        {
            return fallback;
        }
    }

    /// <summary>As <see cref="Read{T}"/>, but a query WMI cannot answer comes back as null, for a caller that must tell no
    /// answer apart from an empty one.</summary>
    public static T? ReadOrNull<T>(string scope, string query, Func<IReadOnlyList<ManagementBaseObject>, T> fold) where T : class
        => ReadOr<T?>(scope, query, fold, null);
}
