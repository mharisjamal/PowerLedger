using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PowerLedger.App.Tests;

/// <summary>
/// The real screen for the liquid glass tests: a window shown on it the way the app shows its own (per-monitor DPI aware,
/// as the app's manifest makes it, so one device pixel is one screen pixel), and GDI's BitBlt of a screen rectangle,
/// which sees what DWM composed there, hardware effects included, and leaves out windows excluded from capture.
/// </summary>
internal static class ScreenCapture
{
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sx, int sy, int rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfo info, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    /// <summary>Runs <paramref name="work"/> with this thread per-monitor DPI aware, so the windows it makes are.</summary>
    public static T Aware<T>(Func<T> work)
    {
        var before = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            return work();
        }
        finally
        {
            SetThreadDpiAwarenessContext(before);
        }
    }

    /// <summary>A borderless, topmost, unactivated window of <paramref name="content"/> at <paramref name="left"/>,
    /// <paramref name="top"/> in its own units, shown.</summary>
    public static Window Show(UIElement content, double left, double top, double width, double height)
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = left, Top = top, Width = width, Height = height, Content = content,
            Background = Brushes.Black, UseLayoutRounding = true,
        };
        window.Show();
        return window;
    }

    /// <summary>The screen's pixels in a rectangle of physical pixels, as BGRA rows.</summary>
    public static byte[] Grab(int x, int y, int width, int height)
    {
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var old = SelectObject(memory, bitmap);
        try
        {
            BitBlt(memory, 0, 0, width, height, screen, x, y, 0x00CC0020);
            SelectObject(memory, old);
            var info = new BitmapInfo { Size = Marshal.SizeOf<BitmapInfo>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
            var pixels = new byte[width * height * 4];
            GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, 0);
            return pixels;
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
