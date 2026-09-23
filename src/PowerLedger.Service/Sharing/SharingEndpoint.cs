using Microsoft.Win32;

namespace PowerLedger.Service.Sharing;

/// <summary>
/// Where the data server is (data-sharing design §4): built in, or a stand-in on this machine for testing, named by the
/// REG_SZ value <c>SharingEndpoint</c> under the service's <c>Parameters</c> key. Only administrators can write there, and a
/// stand-in anywhere but this machine is ignored, the rule the App applies to <c>--update-feed</c>, so nothing can point
/// the uploads at another server.
/// </summary>
internal static class SharingEndpoint
{
    /// <summary>The deployed Worker; the lead sets the real address once it is deployed.</summary>
    public static Uri BuiltIn { get; } = new("https://powerledger-data.example.invalid/");

    internal const string ParametersKey = @"SYSTEM\CurrentControlSet\Services\" + ServiceHost.ServiceName + @"\Parameters";
    internal const string ValueName = "SharingEndpoint";

    /// <param name="readOverride">Reads the registry value; null for the real registry.</param>
    public static Uri Resolve(Func<string?>? readOverride = null)
    {
        string? text;
        try
        {
            text = (readOverride ?? ReadRegistry)();
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            text = null;
        }
        return OnThisMachine(text) ?? BuiltIn;
    }

    /// <summary>An absolute http or https address on a loopback host, ending in a slash so the paths go under it; else null.</summary>
    internal static Uri? OnThisMachine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (!Uri.TryCreate(text.EndsWith('/') ? text : text + "/", UriKind.Absolute, out var address)) return null;
        return address.IsLoopback && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps) ? address : null;
    }

    private static string? ReadRegistry()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ParametersKey);
        return key?.GetValue(ValueName) as string;
    }
}
