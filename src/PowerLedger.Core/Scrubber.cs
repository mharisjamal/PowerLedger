using System.Text.RegularExpressions;

namespace PowerLedger.Core;

/// <summary>The names a process knows itself by: its user, its machine and its domain.</summary>
public sealed record ScrubNames(string? User, string? Machine, string? Domain)
{
    /// <summary>This process's own names, as Windows gives them.</summary>
    public static ScrubNames Here() => new(Environment.UserName, Environment.MachineName, Environment.UserDomainName);
}

/// <summary>
/// Takes out of crash and error text whatever could name the user, the PC, its files or its devices before it is kept
/// for sending (data-sharing design §3):
/// <list type="bullet">
/// <item>a file path, on a drive or a share, becomes <c>&lt;path&gt;</c>, except that a stack frame keeps its source
/// file's name (<c>&lt;path&gt;\NowViewModel.cs:line 42</c>);</item>
/// <item>a device path or interface path becomes <c>&lt;device&gt;</c>, and a Windows device instance ID <c>&lt;id&gt;</c>;</item>
/// <item>an e-mail address becomes <c>&lt;email&gt;</c>, and an IP address <c>&lt;ip&gt;</c>;</item>
/// <item>the user, machine and domain names become <c>&lt;user&gt;</c>, <c>&lt;machine&gt;</c> and <c>&lt;domain&gt;</c>.</item>
/// </list>
/// A path's end can't be known in free text, so an unquoted path takes the rest of its line with it: text lost, never
/// text leaked. Scrubbing twice gives what scrubbing once did.
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
        var result = DevicePath().Replace(text, "<device>");
        result = InterfacePath().Replace(result, "<device>");
        result = KnownInstanceId().Replace(result, "<id>");
        result = InstanceId().Replace(result, "<id>");
        result = DrivePath().Replace(result, PathMark);
        result = SharePath().Replace(result, "<path>");
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

    /// <summary>A path becomes <c>&lt;path&gt;</c>; a stack frame's source file (<c>…\File.cs:line 42</c>) keeps its name and line.</summary>
    private static string PathMark(Match match)
    {
        var frame = StackFrameSource().Match(match.Value);
        return frame.Success ? $@"<path>\{frame.Groups["file"].Value}{frame.Groups["line"].Value}" : "<path>";
    }

    /// <summary>A name as a whole word, in any case. Names under three letters are left, since they would match inside
    /// ordinary words.</summary>
    private static string Name(string text, string? name, string mark) =>
        name is { Length: >= 3 } && !Common.Contains(name)
            ? Regex.Replace(text, $"(?<![A-Za-z0-9_]){Regex.Escape(name)}(?![A-Za-z0-9_])", mark,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            : text;

    /// <summary><c>\\?\hid#vid_1b1c&amp;pid_1c05…</c>, up to the first space or quote.</summary>
    [GeneratedRegex(@"\\\\\?\\[^\s""'<>|]+")]
    private static partial Regex DevicePath();

    /// <summary>An interface path written without its <c>\\?\</c>: <c>hid#vid_1b1c&amp;pid_1c05#8&amp;2d0f1a&amp;0&amp;0000#{guid}</c>.</summary>
    [GeneratedRegex(@"(?i)\b[A-Z0-9_]+#[^\s""'<>|]*#\{[0-9A-F]{8}(?:-[0-9A-F]{4}){3}-[0-9A-F]{12}\}")]
    private static partial Regex InterfacePath();

    /// <summary>A device instance ID under one of Windows' bus enumerators, whatever its instance holds, which for USB
    /// devices is often the serial number: <c>USB\VID_0764&amp;PID_0501\CR7GR2000123</c>.</summary>
    [GeneratedRegex(@"(?i)\b(?:USB|USBSTOR|USBPRINT|HID|PCI|PCIIDE|DISPLAY|MONITOR|SWD|ACPI|ROOT|BTH|BTHENUM|BTHLE|BTHLEDEVICE|HDAUDIO|INTELAUDIO|MMDEVAPI|SCSI|STORAGE|NVME|IDE|UMB|SW|WPDBUSENUM|VMBUS|TS_USB)\\[^\s\\""'<>|]+\\[^\s""'<>|]+")]
    private static partial Regex KnownInstanceId();

    /// <summary>A bus, a device and an instance with an ampersand in it, under an enumerator not listed above.</summary>
    [GeneratedRegex(@"(?i)\b[A-Z0-9_]+\\[A-Z0-9_&.#-]+\\[A-Z0-9_&.#{}-]*&[A-Z0-9_&.#{}-]*")]
    private static partial Regex InstanceId();

    /// <summary>A path on a drive, <c>C:\…</c> or <c>C:/…</c>, to the next quote, bracket, bar or line end. The drive letter
    /// mustn't follow a letter or digit, so a URL's <c>http://</c> isn't taken for one.</summary>
    [GeneratedRegex(@"(?i)(?<![A-Z0-9])[A-Z]:[\\/][^'""<>|\r\n]*")]
    private static partial Regex DrivePath();

    /// <summary>A share, <c>\\server\share\…</c>; not <c>\\.\pipe\…</c> or <c>\\?\…</c>, which start with a dot or a question mark.</summary>
    [GeneratedRegex(@"\\\\[A-Za-z0-9_-][^\\/\s'""<>|]*\\[^'""<>|\r\n]*")]
    private static partial Regex SharePath();

    /// <summary>The end of a stack frame's path: the source file's name and its line.</summary>
    [GeneratedRegex(@"[\\/](?<file>[^\\/]+\.cs)(?<line>:line \d+)\s*$")]
    private static partial Regex StackFrameSource();

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
