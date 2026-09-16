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

    /// <summary>Whether WMI refusing a query with <paramref name="status"/> says only that its class has no instances, which
    /// is an answer: none. The provider behind the classes in root\wmi, the monitor classes among them, refuses with "Not
    /// supported" when no device provides an instance rather than list none, and a class that isn't registered at all,
    /// "Invalid class", has none either. Any other status, a timeout, access denied or a provider failure among them, is
    /// WMI failing to answer.</summary>
    public static bool MeansNoInstances(ManagementStatus status) => status is ManagementStatus.NotSupported or ManagementStatus.InvalidClass;

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

    /// <summary>As <see cref="Read{T}"/>, but a query WMI refuses because its class has no instances (see
    /// <see cref="MeansNoInstances"/>) folds no rows, for a class whose provider refuses rather than list none.</summary>
    public static T ReadInstances<T>(string scope, string query, Func<IReadOnlyList<ManagementBaseObject>, T> fold)
    {
        try
        {
            return Read(scope, query, fold);
        }
        catch (ManagementException error) when (MeansNoInstances(error.ErrorCode))
        {
            return fold([]);
        }
    }

    /// <summary>As <see cref="Read{T}"/>, but a query WMI cannot answer comes back as <paramref name="fallback"/>.</summary>
    public static T ReadOr<T>(string scope, string query, Func<IReadOnlyList<ManagementBaseObject>, T> fold, T fallback)
        => Or(() => Read(scope, query, fold), fallback);

    /// <summary>As <see cref="ReadInstances{T}"/>, but a query WMI cannot answer comes back as null, for a caller that must
    /// tell no answer apart from an empty one.</summary>
    public static T? ReadOrNull<T>(string scope, string query, Func<IReadOnlyList<ManagementBaseObject>, T> fold) where T : class
        => Or<T?>(() => ReadInstances(scope, query, fold), null);

    /// <summary>What <paramref name="read"/> gives, or <paramref name="fallback"/> when WMI cannot answer.</summary>
    private static T Or<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception error) when (IsFailure(error))
        {
            return fallback;
        }
    }
}
