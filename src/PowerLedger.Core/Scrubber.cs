using System.Text.RegularExpressions;

namespace PowerLedger.Core;

/// <summary>The names a process knows itself by: its user, its machine and its domain.</summary>
public sealed record ScrubNames(string? User, string? Machine, string? Domain)
{
    /// <summary>This process's own names, as Windows gives them.</summary>
    public static ScrubNames Here() => new(Environment.UserName, Environment.MachineName, Environment.UserDomainName);
}

/// <summary>
/// Takes out of crash and error text whatever could name the user, the PC or its devices before it is kept for sending
/// (data-sharing design §3): a profile path becomes <c>%USERPROFILE%</c>, a device path <c>&lt;device&gt;</c>, a Windows
/// device instance ID <c>&lt;id&gt;</c>, an e-mail address <c>&lt;email&gt;</c>, an IP address <c>&lt;ip&gt;</c>, and the
/// user, machine and domain names <c>&lt;user&gt;</c>, <c>&lt;machine&gt;</c> and <c>&lt;domain&gt;</c>.
/// Scrubbing twice gives what scrubbing once did.
/// </summary>
public static partial class Scrubber
{
    /// <summary>Names Windows gives service accounts and workgroups: replacing them would take "System" out of every .NET
    /// type name, and they name no one.</summary>
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "SYSTEM", "NT AUTHORITY", "WORKGROUP", "LOCAL SERVICE", "NETWORK SERVICE", "LocalSystem",
    };

    public static string Scrub(string? text, ScrubNames names)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var result = ProfilePath().Replace(text, "%USERPROFILE%");
        result = DevicePath().Replace(result, "<device>");
        result = InstanceId().Replace(result, "<id>");
        result = Email().Replace(result, "<email>");
        result = IpV6Full().Replace(result, "<ip>");
        result = IpV6Short().Replace(result, "<ip>");
        result = IpV6LinkLocal().Replace(result, "<ip>");
        result = IpV4().Replace(result, "<ip>");
        result = Name(result, names.User, "<user>");
        result = Name(result, names.Machine, "<machine>");
        result = Name(result, names.Domain, "<domain>");
        return result;
    }

    /// <summary>A name as a whole word, in any case. Names under three letters are left, since they would match inside
    /// ordinary words.</summary>
    private static string Name(string text, string? name, string mark) =>
        name is { Length: >= 3 } && !Common.Contains(name)
            ? Regex.Replace(text, $"(?<![A-Za-z0-9_]){Regex.Escape(name)}(?![A-Za-z0-9_])", mark,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            : text;

    /// <summary><c>C:\Users\alice</c> or <c>C:/Users/alice</c>, the user's folder name included.</summary>
    [GeneratedRegex(@"(?i)[A-Z]:[\\/]Users[\\/][^\\/:*?""<>|\r\n]+")]
    private static partial Regex ProfilePath();

    /// <summary><c>\\?\hid#vid_1b1c&amp;pid_1c05…</c>, up to the first space or quote.</summary>
    [GeneratedRegex(@"\\\\\?\\[^\s""'<>|]+")]
    private static partial Regex DevicePath();

    /// <summary>A bus, a device and an instance with an ampersand in it: <c>USB\VID_1B1C&amp;PID_1C05\7&amp;2D0F1A&amp;0&amp;1</c>,
    /// <c>DISPLAY\GSM5B7F\5&amp;1A2B3C&amp;0&amp;UID4352</c>.</summary>
    [GeneratedRegex(@"(?i)\b[A-Z0-9_]+\\[A-Z0-9_&.#-]+\\[A-Z0-9_&.#{}-]*&[A-Z0-9_&.#{}-]*")]
    private static partial Regex InstanceId();

    [GeneratedRegex(@"(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b")]
    private static partial Regex Email();

    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b")]
    private static partial Regex IpV4();

    /// <summary>All eight groups written out.</summary>
    [GeneratedRegex(@"(?i)\b(?:[0-9A-F]{1,4}:){7}[0-9A-F]{1,4}\b")]
    private static partial Regex IpV6Full();

    /// <summary>Shortened with <c>::</c> after at least two groups (<c>2001:db8::1</c>), so a C++ name such as
    /// <c>abc::def</c> or a time such as <c>10:11:12</c> is left alone.</summary>
    [GeneratedRegex(@"(?i)\b[0-9A-F]{1,4}(?::[0-9A-F]{1,4})+::(?:[0-9A-F]{1,4}(?::[0-9A-F]{1,4})*)?(?:%\w+)?")]
    private static partial Regex IpV6Short();

    [GeneratedRegex(@"(?i)\bfe80::[0-9A-F:]*(?:%\w+)?")]
    private static partial Regex IpV6LinkLocal();
}
