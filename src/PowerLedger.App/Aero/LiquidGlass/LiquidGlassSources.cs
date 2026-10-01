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
    private static bool _allowScreenshots;
    private static bool _excludeFromCapture = true;

    /// <summary>When set, every window's source comes from here instead of the screen.</summary>
    public static Func<HwndSource, ILiquidGlassSource>? Override { get; set; }

    /// <summary>
    /// The owner's switch (GlassSettings.ShowInScreenshots, which GlassMaterial copies here): true lets screenshots and
    /// screen sharing see our windows (WDA_NONE on each), stops the live capture and shows the wallpaper through the
    /// same recipe; false, the default, leaves the windows out of capture and shows the live screen. Applies at once.
    /// </summary>
    public static bool AllowScreenshots
    {
        get => _allowScreenshots;
        set
        {
            if (_allowScreenshots == value) return;
            _allowScreenshots = value;
            ApplyAll();
        }
    }

    /// <summary>The render harness's switch, apart from the owner's: false leaves our windows capturable (an on-screen
    /// BitBlt check) whatever <see cref="AllowScreenshots"/> says. Pair it with <see cref="Override"/>: live glass on a
    /// capturable window would show itself.</summary>
    public static bool ExcludeFromCapture
    {
        get => _excludeFromCapture;
        set
        {
            if (_excludeFromCapture == value) return;
            _excludeFromCapture = value;
            ApplyAll();
        }
    }

    /// <summary>False keeps every window on the wallpaper: Windows before 10 2004 has no WDA_EXCLUDEFROMCAPTURE, so a live
    /// capture there would show the glass to itself.</summary>
    public static bool CaptureAllowed { get; set; } = Environment.OSVersion.Version >= new Version(10, 0, 19041);

    /// <summary>False keeps every window off the GPU path (the tests, to try the CPU path); true, the default, lets a
    /// window take it where it can.</summary>
    public static bool GpuAllowed
    {
        get => _gpuAllowed;
        set
        {
            if (_gpuAllowed == value) return;
            _gpuAllowed = value;
            ApplyAll();
        }
    }

    private static bool _gpuAllowed = true;

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
            var source = Override?.Invoke(window) ?? new WindowGlassSource(window);
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

    private static void ApplyAll()
    {
        foreach (var source in Live.OfType<WindowGlassSource>()) source.Apply();
    }
}
