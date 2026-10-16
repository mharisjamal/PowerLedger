using System.Text.RegularExpressions;

namespace PowerLedger.Core;

/// <summary>
/// How the Apps page's rows are keyed (Plan AE, storage version 6): an app's image path in lower case without its version
/// folders (<see cref="ForPath"/>), "pkg:" and a Store package family, or one of the special keys here, which start with
/// "::" and are never a path.
/// </summary>
public static partial class AppKeys
{
    /// <summary>Windows' own processes: every image under the Windows folder and Windows' own Defender, the kernel's System,
    /// Registry, Secure System and Memory Compression processes, and the time the processor spends on drivers' interrupts and
    /// deferred calls.</summary>
    public const string Windows = "::windows";

    /// <summary>The processor and graphics at rest, which no app is given: what they draw with nothing to do.</summary>
    public const string Idle = "::idle";

    /// <summary>Programs that ended before the service could see them: the busy time no running process explains.</summary>
    public const string Closed = "::closed";

    /// <summary>Another signed in user's apps: processes in a Windows session other than the console's.</summary>
    public const string OtherUsers = "::otherusers";

    /// <summary>Apps past the hour's busiest, together.</summary>
    public const string Other = "::other";

    /// <summary>The prefix of a Store app's key, followed by its package family.</summary>
    public const string PackagePrefix = "pkg:";

    /// <summary>True for the special keys, which are kept whatever their size.</summary>
    public static bool IsSpecial(string key) => key.StartsWith("::", StringComparison.Ordinal);

    /// <summary>A Store app's key.</summary>
    public static string Package(string family) => PackagePrefix + family;

    /// <summary>
    /// An image's key (review item 5): its path in lower case, the folders a new version renames left out, so an app keeps
    /// one line across its updates: a version folder ("app-1.0.9001", "130.0.2849.80", "4.18.24090.11-0"), a build's hash
    /// folder ("d2e29494ec83") and a version after a folder's name ("IntelliJ IDEA 2024.2.1"). The drive and the file
    /// name stay as they are.
    /// </summary>
    public static string ForPath(string path)
    {
        var parts = path.ToLowerInvariant().Split('\\');
        List<string> kept = new(parts.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (i > 0 && i < parts.Length - 1)
            {
                if (VersionFolder().IsMatch(part) || (HashFolder().IsMatch(part) && part.Any(char.IsDigit) && part.Any(char.IsLetter))) continue;
                if (NamedVersion().Match(part) is { Success: true } named) part = named.Groups[1].Value;
            }
            kept.Add(part);
        }
        return string.Join('\\', kept);
    }

    /// <summary>True for an image, its path in lower case, that is Windows' own: under the Windows folder
    /// (<paramref name="windowsFolder"/>, lower case, ending in a backslash), or Windows' own Defender's, whose antivirus and
    /// its updates live outside it.</summary>
    public static bool IsWindows(string lowerPath, string windowsFolder)
        => lowerPath.StartsWith(windowsFolder, StringComparison.Ordinal) || lowerPath.Contains(@"\windows defender", StringComparison.Ordinal);

    [GeneratedRegex(@"^(app-|v)?\d+(\.\d+)+([-_+][0-9a-z.]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionFolder();

    [GeneratedRegex(@"^[0-9a-f]{8,}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashFolder();

    [GeneratedRegex(@"^(.+?)[ _-]v?\d+(\.\d+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex NamedVersion();

    /// <summary>The plain name a special key's row is written with.</summary>
    public static string? SpecialName(string key) => key switch
    {
        Windows => "Windows",
        Idle => "Idle",
        Closed => "Programs that already closed",
        OtherUsers => "Other users",
        Other => "Other apps",
        _ => null,
    };
}

/// <summary>One app's energy in one hour, as storage version 6 keeps it: its share of the hour's processor and graphics
/// energy (the hour row's cpu_wh and gpu_wh), how long it had a process running and how long it held the window in front.</summary>
/// <param name="Key">See <see cref="AppKeys"/>.</param>
/// <param name="Name">What the App shows.</param>
/// <param name="Path">The image the App takes the icon from; null for the special keys and when there is none.</param>
/// <param name="User">Who ran it (<see cref="AppUsers"/>): the machine for Windows, services and the special keys.</param>
public sealed record AppHourRow(string Key, string Name, string? Path, double CpuWh, double GpuWh, double RunSeconds, double FrontSeconds, int User = AppUsers.Machine);

/// <summary>
/// Who ran an app, as the Apps page tags it (review item 10): a number from the person's Windows account (a hash of its
/// SID, never the SID or the name), so the App shows each person their own apps by name and pools everyone else's under
/// Other users, whenever they ran them; nought for the machine's own accounts (SYSTEM, LOCAL SERVICE, NETWORK SERVICE,
/// service and window manager accounts), whose apps everyone sees.
/// </summary>
public static class AppUsers
{
    /// <summary>The machine's own: Windows, services and the special keys.</summary>
    public const int Machine = 0;

    /// <summary>The number for an account's SID; <see cref="Machine"/> for anything but a person's account, local, domain
    /// (S-1-5-21-) or Entra ID (S-1-12-1-).</summary>
    public static int Of(string? sid)
    {
        if (sid is null || !(sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) || sid.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase)))
            return Machine;
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sid.ToUpperInvariant()), hash);
        var number = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(hash) & int.MaxValue;
        return number == Machine ? 1 : number;
    }
}
