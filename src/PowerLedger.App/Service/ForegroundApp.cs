using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PowerLedger.App;

/// <summary>
/// Which process the window in front belongs to, for the time each app holds it (Plan AE). A Store app's window is framed
/// by Windows' ApplicationFrameHost.exe, which owns the window in front and counts as Windows; the app's own CoreWindow is
/// a child of that frame, so the time goes to the app inside. A frame with no app inside (one closing, or suspended) stays
/// the frame's.
/// </summary>
internal static class ForegroundApp
{
    /// <summary>The file of Windows' frame round a Store app's window.</summary>
    internal const string FrameHost = "ApplicationFrameHost.exe";

    /// <summary>The class of a Store app's own window inside its frame.</summary>
    internal const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

    /// <summary>The process to book <paramref name="window"/> to: the app inside it when it is a Store app's frame, else
    /// the window's own process; null with no window.</summary>
    public static uint? Owner(nint window, Func<nint, uint> processOf, Func<uint, string?> imageOf, Func<nint, IEnumerable<(nint Child, string Class)>> childrenOf)
    {
        if (window == 0) return null;
        var pid = processOf(window);
        if (pid == 0) return null;
        if (!string.Equals(Path.GetFileName(imageOf(pid)), FrameHost, StringComparison.OrdinalIgnoreCase)) return pid;
        foreach (var (child, kind) in childrenOf(window))
        {
            if (kind != CoreWindowClass) continue;
            var inside = processOf(child);
            if (inside != 0 && inside != pid) return inside;
        }
        return pid;
    }

    /// <summary>The window in front now, booked as <see cref="Owner"/> says.</summary>
    public static uint? Now() => Owner(GetForegroundWindow(), ProcessOf, ImageOf, ChildrenOf);

    private static uint ProcessOf(nint window) => GetWindowThreadProcessId(window, out var pid) != 0 ? pid : 0;

    /// <summary>The process's file, read with the least access a process can be opened with.</summary>
    private static string? ImageOf(uint pid)
    {
        const uint QueryLimitedInformation = 0x1000;
        var process = OpenProcess(QueryLimitedInformation, false, pid);
        if (process == 0) return null;
        try
        {
            var path = new StringBuilder(1024);
            var size = (uint)path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString(0, (int)size) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static List<(nint, string)> ChildrenOf(nint window)
    {
        var children = new List<(nint, string)>();
        var name = new StringBuilder(256);
        EnumChildWindows(window, (child, _) =>
        {
            name.Clear();
            if (GetClassName(child, name, name.Capacity) > 0) children.Add((child, name.ToString()));
            return true;
        }, 0);
        return children;
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int capacity);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref uint size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
