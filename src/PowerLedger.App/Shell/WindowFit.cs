namespace PowerLedger.App;

/// <summary>A width and a height in the window's device-independent units.</summary>
internal readonly record struct Extent(double Width, double Height);

/// <summary>A rectangle on the desktop in the window's device-independent units: a screen's work area, or the window's place.</summary>
internal readonly record struct Bounds(double Left, double Top, double Width, double Height);

/// <summary>
/// Where the window opens on its screen, and the smallest size it may then be given. The window keeps the size it asks
/// for when that fits the screen's work area with <see cref="Margin"/> clear on every side, and shrinks to that space when
/// it does not; either way it is centred in the work area. Its minimum shrinks with it, so on a small screen the whole
/// window fits, title bar to status bar, and each page scrolls what no longer fits.
/// </summary>
internal readonly record struct WindowFit(Bounds Bounds, Extent Minimum)
{
    /// <summary>The space kept clear between the window and each edge of the work area.</summary>
    public const double Margin = 8;

    /// <summary>The fit to <paramref name="workArea"/> of a window that asks for <paramref name="size"/> and may be made no
    /// smaller than <paramref name="minimum"/>. It is null when the work area has no room for a window inside the margins.</summary>
    public static WindowFit? Within(Bounds workArea, Extent size, Extent minimum)
    {
        if (!(workArea.Width > 2 * Margin && workArea.Height > 2 * Margin)) return null;   // false for NaN too
        var width = Math.Min(size.Width, workArea.Width - 2 * Margin);
        var height = Math.Min(size.Height, workArea.Height - 2 * Margin);
        var bounds = new Bounds(workArea.Left + (workArea.Width - width) / 2, workArea.Top + (workArea.Height - height) / 2, width, height);
        return new WindowFit(bounds, new Extent(Math.Min(minimum.Width, width), Math.Min(minimum.Height, height)));
    }
}
