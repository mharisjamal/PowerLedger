using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PowerLedger.App;

/// <summary>What Send feedback attaches about this PC and this build, besides the report itself.</summary>
internal static class HostInfo
{
    /// <summary>"x64", "arm64" or "x86": the same names and the same three architectures PowerLedger is built for as
    /// the service's own sharing report uses (PowerLedger.Service.Sharing.ReportInputs.ArchName).</summary>
    public static string ArchName(Architecture architecture) => architecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "x64",
    };

    /// <summary>The informational version including its +sha, unlike the trimmed one the rail shows, matched to the
    /// Worker's own "app" pattern (review round).</summary>
    public static string FullVersion() => SanitizeAppVersion(
        typeof(HostInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");

    /// <summary>The Worker's feedback route matches "app" against <c>[0-9A-Za-z][0-9A-Za-z.+_-]{0,63}</c> (review round):
    /// a leading letter or digit, then up to 63 more of those, a dot, a plus, an underscore or a hyphen — filtered
    /// defensively, though the build's own version string should already qualify.</summary>
    internal static string SanitizeAppVersion(string version)
    {
        var filtered = new string([.. version.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '_' or '-')]);
        var start = 0;
        while (start < filtered.Length && !char.IsAsciiLetterOrDigit(filtered[start])) start++;
        filtered = filtered[start..];
        if (filtered.Length == 0) return "0";
        return filtered.Length > 64 ? filtered[..64] : filtered;
    }

    /// <summary>"Windows 11 Home 26200": the registry's own product name and build number, the same key
    /// installer\PowerLedger.iss reads, since a compatibility mode leaves it alone unlike Environment.OSVersion. Null if
    /// the registry can't be read.</summary>
    public static string? OsName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return Combine(key?.GetValue("ProductName") as string, key?.GetValue("CurrentBuild") as string);
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>The product name and the build on their own, apart from the registry, so a test can check the wording
    /// without one.</summary>
    internal static string? Combine(string? productName, string? build) => productName switch
    {
        null => null,
        _ when build is null => productName,
        _ => $"{productName} {build}",
    };
}
