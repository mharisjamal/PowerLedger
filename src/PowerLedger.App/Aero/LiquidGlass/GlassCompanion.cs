using System.Runtime.InteropServices;
using System.Windows;

namespace PowerLedger.App.Aero;

/// <summary>
/// The window the composed glass of one of our windows is shown in: a borderless, unactivated, unredirected popup
/// (WS_EX_NOREDIRECTIONBITMAP: DirectComposition is all it shows) laid exactly over the owner's client area and kept
/// directly beneath it in the z-order, so DWM draws the glass under every pixel WPF draws in the owner. (A DirectComposition
/// target on the owner itself would be drawn over WPF's content, whatever its topmost flag says: that flag places it only
/// against the owner's child windows.) It follows the owner as it moves, resizes, shows, hides, minimises and changes
/// z-order; it is left out of capture as the owner is; its region is its pieces' shapes, so it takes no click meant for
/// what lies between them. Made and driven on the owner's UI thread.
/// </summary>
internal sealed class GlassCompanion : IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int HTTRANSPARENT = -1;
    private const int MA_NOACTIVATE = 3;
    private const uint SWP_NOACTIVATE = 0x10;
    private const uint SWP_SHOWWINDOW = 0x40;
    private const uint SWP_HIDEWINDOW = 0x80;
    private const uint SWP_NOOWNERZORDER = 0x200;
    private const int SW_HIDE = 0;

    private static readonly string ClassName = "PowerLedger.LiquidGlass." + Environment.ProcessId;
    private static readonly WndProc Procedure = Handle;
    private static bool _registered;

    private readonly IntPtr _owner;
    private (int X, int Y, int W, int H, bool Shown)? _placed;
    private string? _region;
    private bool _disposed;

    private delegate IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int Size, Style;
        public IntPtr Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    private GlassCompanion(IntPtr owner, IntPtr hwnd)
    {
        _owner = owner;
        Hwnd = hwnd;
    }

    /// <summary>The companion's own window, which the composition targets.</summary>
    public IntPtr Hwnd { get; }

    /// <summary>A companion for <paramref name="owner"/>, or null where Windows refused one.</summary>
    public static GlassCompanion? For(IntPtr owner)
    {
        if (!_registered)
        {
            var wc = new WNDCLASSEX
            {
                Size = Marshal.SizeOf<WNDCLASSEX>(), Procedure = Marshal.GetFunctionPointerForDelegate(Procedure), Instance = GetModuleHandle(null),
                ClassName = ClassName,
            };
            if (RegisterClassEx(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410) return null;   // 1410: already registered
            _registered = true;
        }
        var hwnd = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP, ClassName, "PowerLedger glass", WS_POPUP,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero) return null;
        var companion = new GlassCompanion(owner, hwnd);
        companion.Exclude(LiquidGlassSources.ExcludeFromCapture && !LiquidGlassSources.AllowScreenshots);
        companion.Track();
        return companion;
    }

    /// <summary>Left out of capture, as its owner is (or let in, with it).</summary>
    public void Exclude(bool exclude) => CaptureNative.SetWindowDisplayAffinity(Hwnd, exclude ? CaptureNative.WDA_EXCLUDEFROMCAPTURE : CaptureNative.WDA_NONE);

    /// <summary>Lays the companion over the owner's client area, directly beneath it, shown while the owner shows.</summary>
    public void Track()
    {
        if (_disposed) return;
        var shown = CaptureNative.IsWindowVisible(_owner) && !CaptureNative.IsIconic(_owner);
        var at = new System.Drawing.Point();
        GpuNative.ClientToScreen(_owner, ref at);
        GpuNative.GetClientRect(_owner, out var client);
        var placed = (at.X, at.Y, Math.Max(1, client.Right - client.Left), Math.Max(1, client.Bottom - client.Top), shown);
        if (!shown)
        {
            if (_placed is { Shown: true }) ShowWindow(Hwnd, SW_HIDE);
            _placed = placed;
            return;
        }
        // Already there, and still right beneath the owner: nothing to do (a SetWindowPos would still cost DWM a frame).
        if (_placed == placed && GetWindow(Hwnd, GW_HWNDPREV) == _owner) return;
        // Beneath the owner, wherever it is in the z-order (a topmost owner takes it topmost too).
        SetWindowPos(Hwnd, _owner, placed.X, placed.Y, placed.Item3, placed.Item4, SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_SHOWWINDOW);
        _placed = placed;
    }

    /// <summary>Lets clicks through everywhere but the pieces: the union of their rounded shapes, in client pixels.</summary>
    public void Shape(IEnumerable<(Rect Shape, CornerRadius Radii)> pieces)
    {
        if (_disposed) return;
        var list = pieces.Where(p => !p.Shape.IsEmpty).ToList();
        var key = string.Join(";", list.Select(p => $"{p.Shape}|{p.Radii}"));
        if (key == _region) return;
        _region = key;
        var region = CreateRectRgn(0, 0, 0, 0);
        foreach (var (shape, radii) in list)
        {
            var r = Math.Max(0, Math.Min(radii.TopLeft, Math.Min(shape.Width, shape.Height) / 2));
            var piece = CreateRoundRectRgn((int)Math.Floor(shape.Left), (int)Math.Floor(shape.Top), (int)Math.Ceiling(shape.Right) + 1, (int)Math.Ceiling(shape.Bottom) + 1, (int)(2 * r), (int)(2 * r));
            CombineRgn(region, region, piece, 2);   // RGN_OR
            DeleteObject(piece);
        }
        if (SetWindowRgn(Hwnd, region, true) == 0) DeleteObject(region);   // on success the window owns the region
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DestroyWindow(Hwnd);
    }

    private static IntPtr Handle(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam) => msg switch
    {
        WM_NCHITTEST => HTTRANSPARENT,
        WM_MOUSEACTIVATE => MA_NOACTIVATE,
        _ => DefWindowProc(hwnd, msg, wParam, lParam),
    };

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string title, int style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    private const uint GW_HWNDPREV = 3;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr destination, IntPtr one, IntPtr two, int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
