using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace PowerLedger.App.Aero;

/// <summary>The Win32 calls the watts overlay needs: out of the taskbar and Alt Tab, placed in pixels, the displays with
/// their scales, and the blur of what is behind the pill.</summary>
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

    /// <summary>Settings, Personalization, Colours, Transparency effects: off, Windows paints every backdrop solid.</summary>
    public static bool TransparencyOn
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("EnableTransparency") is not int value || value != 0;
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
            {
                return true;
            }
        }
    }

    /// <summary>Acrylic blur of what is behind the window, with <paramref name="tintAbgr"/> over it, or none; whether
    /// Windows took it.</summary>
    public static bool Blur(IntPtr window, bool on, uint tintAbgr)
    {
        var policy = new AccentPolicy { State = on ? 4 : 0, Flags = 2, Gradient = tintAbgr };
        var size = Marshal.SizeOf(policy);
        var data = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, data, false);
            var composition = new CompositionData { Attribute = 19, Data = data, Size = size };
            return SetWindowCompositionAttribute(window, ref composition) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    /// <summary>Trims the window's blur to a rounded rectangle, the pill; <see cref="IntPtr.Zero"/> as the region lifts it.
    /// Windows owns the region once set.</summary>
    public static void Shape(IntPtr window, Rect? pixels, double radius)
    {
        var region = pixels is { } r
            ? CreateRoundRectRgn((int)Math.Round(r.Left), (int)Math.Round(r.Top), (int)Math.Round(r.Right) + 1, (int)Math.Round(r.Bottom) + 1,
                (int)Math.Round(radius * 2), (int)Math.Round(radius * 2))
            : IntPtr.Zero;
        _ = SetWindowRgn(window, region, true);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int State;
        public int Flags;
        public uint Gradient;
        public int Animation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionData
    {
        public int Attribute;
        public IntPtr Data;
        public int Size;
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

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr window, ref CompositionData data);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
}
