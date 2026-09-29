using System.Runtime.InteropServices;
using System.Windows;

namespace PowerLedger.App.Aero;

/// <summary>A glass surface the window's shape takes in: its bounds and corner radius in the window's units, and what
/// an ancestor (a scroll viewport) leaves of it in sight.</summary>
internal readonly record struct Surface(Rect Bounds, double Radius, Rect Clip);

/// <summary>One surface's part of the window's shape in device pixels: a rounded rectangle (<see cref="Corner"/> is the
/// corner's diameter, as CreateRoundRectRgn takes it), cut to the clip rectangle when <see cref="Clipped"/>.</summary>
internal readonly record struct RegionPiece(int Left, int Top, int Right, int Bottom, int Corner, int ClipLeft, int ClipTop, int ClipRight, int ClipBottom)
{
    public bool Clipped => ClipLeft > Left || ClipTop > Top || ClipRight < Right || ClipBottom < Bottom;
}

/// <summary>
/// The window's shape (0.10.1, free-form), pure: each glass surface as a rounded rectangle in device pixels, a pixel
/// wider all round (<see cref="Outset"/>) so the rim's soft outer edge is kept and the region's stepped corners fall
/// outside the glass; and where the window resizes: the outer edges of the shape.
/// </summary>
internal static class RegionMath
{
    /// <summary>How many device pixels the shape stands outside each surface.</summary>
    public const int Outset = 1;

    public const int HtLeft = 10, HtRight = 11, HtTop = 12, HtTopLeft = 13, HtTopRight = 14, HtBottom = 15, HtBottomLeft = 16, HtBottomRight = 17;

    /// <summary>The pieces for <paramref name="surfaces"/> at <paramref name="scale"/> device pixels to the unit, in a
    /// window of <paramref name="window"/> units; a surface with nothing of it in sight is left out.</summary>
    public static IReadOnlyList<RegionPiece> Pieces(IEnumerable<Surface> surfaces, double scale, Size window)
    {
        var inside = new Rect(0, 0, window.Width, window.Height);
        var pieces = new List<RegionPiece>();
        foreach (var surface in surfaces)
        {
            var b = surface.Bounds;
            if (b.IsEmpty || b.Width <= 0 || b.Height <= 0) continue;
            var seen = Rect.Intersect(b, surface.Clip);
            if (seen.IsEmpty || seen.Width <= 0 || seen.Height <= 0) continue;
            var onWindow = Rect.Intersect(seen, inside);
            if (onWindow.IsEmpty || onWindow.Width <= 0 || onWindow.Height <= 0) continue;
            int left = Floor(b.Left * scale) - Outset, top = Floor(b.Top * scale) - Outset;
            int right = Ceiling(b.Right * scale) + Outset, bottom = Ceiling(b.Bottom * scale) + Outset;
            var corner = surface.Radius > 0 ? (int)Math.Round(2 * (surface.Radius * scale + Outset)) : 0;
            corner = Math.Min(corner, Math.Min(right - left, bottom - top));
            var clipLeft = seen.Left > b.Left ? Floor(seen.Left * scale) : left;
            var clipTop = seen.Top > b.Top ? Floor(seen.Top * scale) : top;
            var clipRight = seen.Right < b.Right ? Ceiling(seen.Right * scale) : right;
            var clipBottom = seen.Bottom < b.Bottom ? Ceiling(seen.Bottom * scale) : bottom;
            pieces.Add(new RegionPiece(left, top, right, bottom, corner, clipLeft, clipTop, clipRight, clipBottom));
        }
        return pieces;
    }

    /// <summary>The hit-test code for a resize at <paramref name="p"/> on the outer edges of <paramref name="frame"/>
    /// (a band <paramref name="band"/> deep, the corners <paramref name="corner"/> along each edge), or 0 for none.</summary>
    public static int Edge(Point p, Rect frame, double band, double corner)
    {
        if (frame.IsEmpty || !frame.Contains(p)) return 0;
        bool left = p.X < frame.Left + band, right = p.X > frame.Right - band, top = p.Y < frame.Top + band, bottom = p.Y > frame.Bottom - band;
        bool nearLeft = p.X < frame.Left + corner, nearRight = p.X > frame.Right - corner;
        bool nearTop = p.Y < frame.Top + corner, nearBottom = p.Y > frame.Bottom - corner;
        if ((top && nearLeft) || (left && nearTop)) return HtTopLeft;
        if ((top && nearRight) || (right && nearTop)) return HtTopRight;
        if ((bottom && nearLeft) || (left && nearBottom)) return HtBottomLeft;
        if ((bottom && nearRight) || (right && nearBottom)) return HtBottomRight;
        if (left) return HtLeft;
        if (right) return HtRight;
        if (top) return HtTop;
        return bottom ? HtBottom : 0;
    }

    private static int Floor(double value) => (int)Math.Floor(value + 1e-6);

    private static int Ceiling(double value) => (int)Math.Ceiling(value - 1e-6);
}

/// <summary>
/// Aero's two layouts (0.10.1), pure: spread over the work area, as the demo, which is how it first opens; or the
/// smaller centred layout, which the maximise button and a double click on the top toggle to. The choice and the smaller
/// layout's bounds are remembered; bounds no screen now shows well give way to the centred fit.
/// </summary>
internal static class AeroLayout
{
    /// <summary>How much of remembered bounds a screen must show for them to be used again.</summary>
    public const double ShownShare = 0.5;

    public static (bool Spread, Bounds Normal) Open(AeroPlacement? saved, IReadOnlyList<Bounds> workAreas, Bounds fitted, Extent minimum)
    {
        if (saved is null) return (true, fitted);
        var bounds = new Bounds(saved.Left, saved.Top, saved.Width, saved.Height);
        return (saved.Spread, Usable(bounds, workAreas, minimum) ? bounds : fitted);
    }

    private static bool Usable(Bounds b, IReadOnlyList<Bounds> workAreas, Extent minimum)
    {
        if (b.Width < minimum.Width || b.Height < minimum.Height) return false;
        var area = b.Width * b.Height;
        return workAreas.Any(work =>
        {
            var w = Math.Min(b.Left + b.Width, work.Left + work.Width) - Math.Max(b.Left, work.Left);
            var h = Math.Min(b.Top + b.Height, work.Top + work.Height) - Math.Max(b.Top, work.Top);
            return w > 0 && h > 0 && w * h >= ShownShare * area;
        });
    }
}

/// <summary>The Win32 calls that shape the window; thin, each failure harmless (the window stays whole).</summary>
internal static class RegionNative
{
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_WINDOWPOSCHANGED = 0x0047;
    public const int WM_DWMCOMPOSITIONCHANGED = 0x031E;
    private const int RGN_AND = 1;
    private const int RGN_OR = 2;

    /// <summary>Gives the window the union of <paramref name="pieces"/> as its shape; none gives it no shape at all.</summary>
    public static void Apply(IntPtr hwnd, IReadOnlyList<RegionPiece> pieces)
    {
        if (hwnd == IntPtr.Zero) return;
        var union = CreateRectRgn(0, 0, 0, 0);
        if (union == IntPtr.Zero) return;
        foreach (var piece in pieces)
        {
            // CreateRoundRectRgn leaves out the right and bottom edges, as CreateRectRgn does.
            var round = piece.Corner > 0
                ? CreateRoundRectRgn(piece.Left, piece.Top, piece.Right + 1, piece.Bottom + 1, piece.Corner, piece.Corner)
                : CreateRectRgn(piece.Left, piece.Top, piece.Right, piece.Bottom);
            if (round == IntPtr.Zero) continue;
            if (piece.Clipped)
            {
                var clip = CreateRectRgn(piece.ClipLeft, piece.ClipTop, piece.ClipRight, piece.ClipBottom);
                CombineRgn(round, round, clip, RGN_AND);
                DeleteObject(clip);
            }
            CombineRgn(union, union, round, RGN_OR);
            DeleteObject(round);
        }
        // The system owns the region from here, and frees it.
        if (SetWindowRgn(hwnd, union, true) == 0) DeleteObject(union);
    }

    /// <summary>Takes the shape off: the whole window again.</summary>
    public static void Clear(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero) SetWindowRgn(hwnd, IntPtr.Zero, true);
    }

    /// <summary>Whether the window has a shape at all.</summary>
    public static bool HasRegion(IntPtr hwnd) => hwnd != IntPtr.Zero && GetWindowRgnBox(hwnd, out _) != 0;

    /// <summary>Whether the window's shape holds the window-relative device point, for a test; true with no shape.</summary>
    public static bool Holds(IntPtr hwnd, int x, int y)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            const int error = 0;
            return GetWindowRgn(hwnd, region) == error || PtInRegion(region, x, y);
        }
        finally
        {
            DeleteObject(region);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr destination, IntPtr one, IntPtr other, int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PtInRegion(IntPtr region, int x, int y);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);

    [DllImport("user32.dll")]
    private static extern int GetWindowRgnBox(IntPtr hwnd, out Box box);

    [StructLayout(LayoutKind.Sequential)]
    private struct Box
    {
        public int Left, Top, Right, Bottom;
    }
}
