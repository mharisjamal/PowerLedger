using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PowerLedger.App;

/// <summary>Whether the process at the other end of the pipe may be sent changes (Plan C's rule, spec §11).</summary>
internal interface IServerCheck
{
    /// <summary>Null when the server may be sent changes; otherwise why not, in words the App can show.</summary>
    string? Refusal(SafePipeHandle pipe);
}

/// <summary>A development run, started with --pipe, names its own pipe and trusts whatever serves it.</summary>
internal sealed class TrustAnyServer : IServerCheck
{
    public string? Refusal(SafePipeHandle pipe) => null;
}

/// <summary>
/// The process serving the pipe must be the installed service: the executable Windows starts for the PowerLedger
/// service. Anyone may create a pipe with PowerLedger's name before the service does, so the name proves nothing.
/// </summary>
/// <param name="installedImage">The installed service's executable, or null when it is not installed.</param>
internal sealed class InstalledServiceCheck(Func<string?> installedImage) : IServerCheck
{
    public const string ServiceName = "PowerLedger";
    private const uint QueryLimitedInformation = 0x1000;

    public static InstalledServiceCheck FromRegistry() => new(RegisteredImage);

    public string? Refusal(SafePipeHandle pipe)
    {
        if (installedImage() is not { } expected) return "The PowerLedger service isn't installed, so nothing can be changed.";
        if (ServerImage(pipe) is not { } actual) return "The program serving PowerLedger's pipe couldn't be identified, so nothing was sent.";
        return string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase)
            ? null
            : "The program serving PowerLedger's pipe isn't the installed service, so nothing was sent.";
    }

    /// <summary>The executable of the process serving a pipe, or null when Windows won't say.</summary>
    internal static string? ServerImage(SafePipeHandle pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe, out var id)) return null;
        var process = OpenProcess(QueryLimitedInformation, false, id);
        if (process == IntPtr.Zero) return null;
        try
        {
            var name = new StringBuilder(1024);
            var length = name.Capacity;
            return QueryFullProcessImageName(process, 0, name, ref length) ? name.ToString(0, length) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>The executable a registered command line starts: the quoted part, or everything up to ".exe".</summary>
    internal static string? ExecutableOf(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var line = commandLine.Trim();
        if (line.StartsWith('"'))
        {
            var close = line.IndexOf('"', 1);
            return close > 1 ? line[1..close] : null;
        }
        var exe = line.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? line[..(exe + 4)] : line;
    }

    /// <summary>The installed service's executable, or null when it is not installed or the registry won't say. The link
    /// asks on every connection, so nothing may escape from here.</summary>
    private static string? RegisteredImage()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
            return ExecutableOf(key?.GetValue("ImagePath") as string);
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
