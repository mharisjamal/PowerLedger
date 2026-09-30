using System.Windows.Interop;

namespace PowerLedger.App.Aero;

/// <summary>
/// The backdrop source for each top-level window (an HwndSource: a Window, the overlay, a popup), made on the first
/// liquid glass piece that loads in it and let go with the last one. Tests and the render harness set
/// <see cref="Override"/> to hand every window a fake source, so nothing captures the screen.
/// </summary>
internal static class LiquidGlassSources
{
    private static readonly Dictionary<HwndSource, (ILiquidGlassSource Source, int Pieces)> Windows = [];

    /// <summary>When set, every window's source comes from here instead of the screen.</summary>
    public static Func<HwndSource, ILiquidGlassSource>? Override { get; set; }

    /// <summary>The windows with glass now, for the tests.</summary>
    internal static IReadOnlyList<ILiquidGlassSource> Live
    {
        get
        {
            lock (Windows) return Windows.Values.Select(v => v.Source).ToList();
        }
    }

    /// <summary>The source for <paramref name="window"/>, shared by its pieces; each <see cref="Acquire"/> wants a
    /// <see cref="Release"/>.</summary>
    public static ILiquidGlassSource Acquire(HwndSource window)
    {
        lock (Windows)
        {
            if (Windows.TryGetValue(window, out var entry))
            {
                Windows[window] = (entry.Source, entry.Pieces + 1);
                return entry.Source;
            }
            var source = Override?.Invoke(window) ?? new NoGlassSource();   // the live capture comes next
            Windows[window] = (source, 1);
            window.Disposed += OnWindowDisposed;
            return source;
        }
    }

    public static void Release(HwndSource window)
    {
        ILiquidGlassSource? gone = null;
        lock (Windows)
        {
            if (!Windows.TryGetValue(window, out var entry)) return;
            if (entry.Pieces > 1)
            {
                Windows[window] = (entry.Source, entry.Pieces - 1);
                return;
            }
            Windows.Remove(window);
            window.Disposed -= OnWindowDisposed;
            gone = entry.Source;
        }
        (gone as IDisposable)?.Dispose();
    }

    private static void OnWindowDisposed(object? sender, EventArgs e)
    {
        if (sender is not HwndSource window) return;
        ILiquidGlassSource? gone;
        lock (Windows)
        {
            if (!Windows.Remove(window, out var entry)) return;
            gone = entry.Source;
        }
        (gone as IDisposable)?.Dispose();
    }

    private sealed class NoGlassSource : ILiquidGlassSource
    {
        public LiquidGlassSourceKind Kind => LiquidGlassSourceKind.None;

        public System.Windows.Media.ImageSource? Image => null;

        public System.Windows.Rect ScreenBounds => System.Windows.Rect.Empty;

        public event Action? Changed
        {
            add { }
            remove { }
        }
    }
}
