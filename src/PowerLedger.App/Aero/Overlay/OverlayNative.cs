using System.Runtime.InteropServices;
using System.Windows;

namespace PowerLedger.App.Aero;

/// <summary>The Win32 calls the watts overlay needs: out of the taskbar and Alt Tab, placed in pixels, and the displays
/// with their scales. What is behind the pill is the liquid glass engine's (0.10.9), not Windows' blur.</summary>
internal static class OverlayNative
{
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_DPICHANGED = 0x02E0;

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const long WS_EX_APPWINDOW = 0x40000;
    private const uint SWP_NOSIZE = 0x1;
    private const uint SWP_NOZORDER = 0x4;
    private const uint SWP_NOACTIVATE = 0x10;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint MONITORINFOF_PRIMARY = 1;

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr clip, IntPtr data);

    /// <summary>A tool window: never in the taskbar or Alt Tab, as the overlay floats over whatever the user does.</summary>
    public static void MakeToolWindow(IntPtr window)
    {
        var style = (long)GetWindowLongPtr(window, GWL_EXSTYLE);
        SetWindowLongPtr(window, GWL_EXSTYLE, new IntPtr((style | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW));
    }

    /// <summary>Moves the window's top-left to <paramref name="pixels"/>, keeping its size and its place on top.</summary>
    public static void MoveTo(IntPtr window, Point pixels)
        => SetWindowPos(window, IntPtr.Zero, (int)Math.Round(pixels.X), (int)Math.Round(pixels.Y), 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    /// <summary>The window's top-left in pixels on the virtual screen.</summary>
    public static Point TopLeft(IntPtr window) => GetWindowRect(window, out var rect) ? new Point(rect.Left, rect.Top) : default;

    /// <summary>Every display, its bounds and work area in pixels and its scale; none when Windows won't say.</summary>
    public static IReadOnlyList<OverlayDisplay> Displays()
    {
        var displays = new List<OverlayDisplay>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return true;
            var scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
            displays.Add(new OverlayDisplay(info.Monitor.ToRect(), info.Work.ToRect(), scale, (info.Flags & MONITORINFOF_PRIMARY) != 0));
            return true;
        }, IntPtr.Zero);
        return displays;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
