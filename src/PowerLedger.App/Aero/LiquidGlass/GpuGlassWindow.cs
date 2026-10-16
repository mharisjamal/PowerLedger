using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using static PowerLedger.App.Aero.GpuNative;

namespace PowerLedger.App.Aero;

/// <summary>
/// Direct3D 9Ex, one device per graphics adapter, only as the bridge WPF's D3DImage takes: it opens a Direct3D 11
/// texture shared by the GPU path as a surface, which WPF then copies on the GPU. Nothing is drawn with it.
/// </summary>
internal static class D3D9Bridge
{
    private static readonly Dictionary<long, IntPtr> Devices = [];
    private static IntPtr _d3d9;

    /// <summary>The Direct3D 9Ex device on the adapter with <paramref name="luid"/>, made on first use; zero when there is
    /// none (no such adapter, or Direct3D 9Ex refused).</summary>
    public static IntPtr For(long luid)
    {
        lock (Devices)
        {
            if (Devices.TryGetValue(luid, out var device)) return device;
            if (_d3d9 == IntPtr.Zero && Direct3DCreate9Ex(D3D_SDK_VERSION, out _d3d9) < 0) return IntPtr.Zero;
            device = IntPtr.Zero;
            for (uint a = 0; a < GetAdapterCount(_d3d9); a++)
            {
                if (GetAdapterLuid(_d3d9, a, out var id) < 0 || id != luid) continue;
                var parameters = new D3DPRESENT_PARAMETERS
                {
                    BackBufferWidth = 1, BackBufferHeight = 1, Windowed = 1, SwapEffect = D3DSWAPEFFECT_DISCARD, DeviceWindow = GetDesktopWindow(),
                };
                if (CreateDeviceEx(_d3d9, a, GetDesktopWindow(), ref parameters, out device) < 0) device = IntPtr.Zero;
                break;
            }
            if (device != IntPtr.Zero) Devices[luid] = device;
            return device;
        }
    }
}

/// <summary>
/// The GPU path for one top-level window (<see cref="WindowGlassSource"/> makes it when the GPU can): every piece of glass
/// in the window is drawn by <see cref="GpuGlassRenderer"/> on its monitor's capture thread straight from the duplicated
/// desktop. No pixel crosses to the CPU. Two ways to show a piece:
/// <list type="bullet">
/// <item><b>Composed</b> (DirectComposition), the usual way: a companion window laid beneath the window
/// (GlassCompanion) shows a swap chain the size of the window's client area. Each piece is drawn at its own place in
/// it, as WPF shows it (scaled, clipped, faded), its corners rounded in its alpha, and DWM puts it under every pixel
/// WPF draws in the window. A frame of glass never makes WPF draw anything. The piece itself (LiquidGlassBackdrop)
/// draws nothing there.</item>
/// <item><b>Imaged</b> (D3DImage), where DirectComposition can't serve (Windows refused the companion window or a
/// composition device): the pieces are drawn into one shared picture, each in a rectangle of its own, which the window
/// shows through one D3DImage, each piece its own rectangle.</item>
/// </list>
/// <para>The D3DImage is kept locked while the capture thread may draw, so WPF never copies a half drawn picture: a frame
/// drawn, the UI thread marks the pieces' rectangles dirty and unlocks. WPF sends the copy as it commits its next frame
/// and signals when its render thread has made it; from the frame after, the UI thread tries the lock again (TryLock,
/// never waiting; a failed TryLock still counts a lock, which is undone at once), and once it has it the capture thread
/// may draw the next. A lock taken before the commit would cancel the copy, so the first frame after unlocking is never
/// tried.</para>
/// </summary>
internal sealed class GpuGlassWindow : IGpuGlassConsumer, IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly Dispatcher _dispatcher;
    private readonly MonitorCapture _session;
    private readonly D3DImage _image = new();
    private readonly Dictionary<LiquidGlassBackdrop, Piece> _pieces = [];
    private readonly object _gpu = new();
    private readonly List<Int32Rect> _pending = [];
    private readonly Dictionary<int, int> _drawn = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<(int Id, TaskCompletionSource<byte[]> Done)> _readRequests = new();
    private GpuGlassJob[] _jobs = [];
    private Int32Rect[] _areas = [];

    // Imaged: the shared picture and its D3DImage.
    private IntPtr _picture, _pictureTarget, _texture9, _surface9;
    private int _pictureWidth, _pictureHeight;
    private bool _locked, _waiting, _skip;
    private long _attempts;
    private volatile bool _canDraw;

    // Composed: DirectComposition and the layer its swap chain shows.
    private GlassCompanion? _companion;
    private IntPtr _dcomp, _target, _visual, _swapChain, _layer, _layerTarget;
    private int _layerWidth, _layerHeight;
    private (int Width, int Height)? _resizeTo;
    private bool _composedFailed;
    private volatile bool _clearLayer;

    private volatile bool _active;
    private volatile bool _wants = true;
    private bool _disposed;
    private int _nextId;
    private int _losses;
    private DateTime _firstLoss;
    private long _frames;

    private sealed class Piece
    {
        public LiquidGlassBackdrop Element = null!;
        public int Id;
        public int Order;
        public Int32Rect Box;
        public double Dpi;
        public float Brightness;
        public double Sigma;
        public double Scale;
        public GpuGlassShape Shape;
        public (double Dpi, double Scale)? MapFor;

        public IntPtr MapView;   // the window's shared move map for the piece's display and displacement scales
        public bool Composed;
        public Int32Rect Target;
        public int Version;
    }

    public GpuGlassWindow(IntPtr hwnd, Dispatcher dispatcher, MonitorCapture session)
    {
        _hwnd = hwnd;
        _dispatcher = dispatcher;
        _session = session;
        _image.IsFrontBufferAvailableChanged += OnFrontBuffer;
        _session.AddConsumer(this);
    }

    /// <summary>The window's shared picture for its imaged pieces, every such piece's rectangle in it.</summary>
    public D3DImage Image => _image;

    /// <summary>True once the GPU path gave out on this window (three lost devices in a minute, or it couldn't start):
    /// the window takes the CPU path.</summary>
    public bool Failed { get; private set; }

    /// <summary>Raised on the UI thread when a piece's rectangle, its way of showing or the picture changed, and when
    /// <see cref="Failed"/>.</summary>
    public event Action? Changed;

    /// <summary>How many frames were shown, for the tests and the measurements.</summary>
    public long Frames => Interlocked.Read(ref _frames);

    /// <summary>How many of <see cref="Frames"/> another window's change asked for (one reaching past every window of ours,
    /// drawn with no compare), for the tests: the rest are a change behind found by the compare, or a piece new or moved.</summary>
    public long ForeignFrames => Interlocked.Read(ref _foreignFrames);

    private long _foreignFrames;

    public MonitorCapture Session => _session;

    /// <summary>How many source maps have been made, for the measurements.</summary>
    internal static long MapsMade;

    private readonly List<LiquidGlassBackdrop> _following = [];
    private DateTime _followUntil;
    private bool _followHooked;

    /// <summary>Places <paramref name="piece"/> again on every frame while something animates (LiquidGlassSources.Animate:
    /// a spring, a slide, a fade, which no layout pass reports), and only then.</summary>
    public void Follow(LiquidGlassBackdrop piece)
    {
        if (_following.Count == 0) LiquidGlassSources.Animating += OnAnimating;
        _following.Add(piece);
    }

    public void Unfollow(LiquidGlassBackdrop piece)
    {
        _following.Remove(piece);
        if (_following.Count > 0) return;
        LiquidGlassSources.Animating -= OnAnimating;
        Hook(false);
    }

    private void OnAnimating(TimeSpan length)
    {
        var until = DateTime.UtcNow + length + TimeSpan.FromMilliseconds(50);
        if (until > _followUntil) _followUntil = until;
        Hook(true);
    }

    private void Hook(bool on)
    {
        if (on == _followHooked) return;
        _followHooked = on;
        if (on) CompositionTarget.Rendering += OnFollowFrame;
        else CompositionTarget.Rendering -= OnFollowFrame;
    }

    private void OnFollowFrame(object? sender, EventArgs e)
    {
        foreach (var piece in _following.ToArray()) piece.FollowFrame();
        if (DateTime.UtcNow > _followUntil) Hook(false);
    }

    /// <summary>The path's state in a line, for the tests and the measurements.</summary>
    internal string State => $"pieces {_pieces.Count} ({_pieces.Values.Count(p => p.Composed)} composed), with maps {_pieces.Values.Count(p => p.MapView != IntPtr.Zero)}, " +
        $"jobs {Volatile.Read(ref _jobs).Length}, layer {_layerWidth}x{_layerHeight}, picture {_pictureWidth}x{_pictureHeight}, locked {_locked}, relock attempts {_attempts}, " +
        $"can draw {_canDraw}, wants {_wants}, active {_active}, frames {Frames}; imaged " + string.Join(" ", _pieces.Values.Where(p => !p.Composed).Select(p => $"{p.Box.X},{p.Box.Y},{p.Box.Width}x{p.Box.Height}[{Who(p)}]")) +
        "; composed " + string.Join(" ", _pieces.Values.Where(p => p.Composed).Select(p => $"{p.Box.X},{p.Box.Y},{p.Box.Width}x{p.Box.Height}[{Who(p)}]"));

    private static string Who(Piece p)
    {
        var owner = p.Element.TemplatedParent as FrameworkElement;
        var chain = new List<string>();
        for (DependencyObject? n = owner; n != null && chain.Count < 6; n = VisualTreeHelper.GetParent(n))
        {
            if (n is FrameworkElement { Name.Length: > 0 } f) chain.Add(f.Name);
            else if (n is FrameworkElement { TemplatedParent: FrameworkElement { Name.Length: > 0 } tp }) chain.Add("t:" + tp.Name);
        }
        return $"{owner?.GetType().Name}:{string.Join("/", chain.Distinct())}";
    }

    /// <summary>Shown (neither hidden nor minimised): the capture thread draws for it.</summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            if (value) Want();
        }
    }

    public IReadOnlyList<Int32Rect> Areas => Volatile.Read(ref _areas);

    public bool WantsFrame => _wants;

    /// <summary>True while every piece is composed: a want is then drawn on the UI thread at once (<see cref="Want"/>), and
    /// the capture thread needn't look for it. An imaged piece's wants come as WPF hands the picture back, for the capture
    /// thread to draw.</summary>
    public bool ServesItself => _servesItself;

    private volatile bool _servesItself = true;

    public IntPtr Window => _hwnd;

    /// <summary>Whether <paramref name="piece"/> is composed (DirectComposition beneath the window: the piece draws
    /// nothing) rather than imaged.</summary>
    public bool Composes(LiquidGlassBackdrop piece) => _pieces.TryGetValue(piece, out var p) && p.Composed;

    /// <summary>Where <paramref name="piece"/> is drawn among the window's pieces (later over earlier), or null.</summary>
    internal int? OrderOf(LiquidGlassBackdrop piece) => _pieces.TryGetValue(piece, out var p) ? p.Order : null;

    /// <summary>The window the composed glass is shown in, or zero while there is none.</summary>
    internal IntPtr CompanionHwnd => _companion?.Hwnd ?? IntPtr.Zero;

    /// <summary>The rectangle of the window's shared picture an imaged piece shows, or null while it has none.</summary>
    public Int32Rect? TargetOf(LiquidGlassBackdrop piece)
        => _pieces.TryGetValue(piece, out var p) && !p.Composed && p.Target.Width > 0 && p.MapView != IntPtr.Zero && _picture != IntPtr.Zero ? p.Target : null;

    /// <summary>Adds or updates <paramref name="piece"/>: its box on the screen (physical pixels: what it reads and where
    /// it is drawn), how it shows there (<paramref name="shape"/>, in the box's pixels), its display scale and the recipe's
    /// numbers. On the UI thread.</summary>
    public void Update(LiquidGlassBackdrop piece, Int32Rect box, GpuGlassShape shape, double dpi, double brightness, double sigma, double scale)
    {
        if (_disposed) return;
        if (!_pieces.TryGetValue(piece, out var p))
        {
            p = new Piece { Id = ++_nextId, Element = piece };
            _pieces[piece] = p;
        }
        if (p.Box == box && p.Shape == shape && p.Dpi == dpi && p.Brightness == (float)brightness && p.Sigma == sigma && p.Scale == scale) return;
        var moved = p.Box != box || p.Dpi != dpi || p.Brightness != (float)brightness || p.Sigma != sigma || p.Scale != scale;
        (p.Box, p.Shape, p.Dpi, p.Brightness, p.Sigma, p.Scale) = (box, shape, dpi, (float)brightness, sigma, scale);
        p.Version++;
        if (moved) MakeMap(p);
        Arrange();
        Publish();
        // Composed: drawn once the layout pass that moved it is done, not when the capture next looks.
        if (p.Composed) DrawSoon();
    }

    /// <summary>
    /// Draws the pieces that moved, resized or changed now, on the UI thread, from the capture's desktop copy (what lies
    /// behind the window is in it already): the window's own move (WindowGlassSource draws before it moves the glass's
    /// window), so the glass shows at the window's new place in the frame Windows first shows it there.
    /// </summary>
    public void DrawNow()
    {
        _drawQueued = false;
        if (_disposed || !_active) return;
        _session.DrawNow(this);
    }

    private bool _drawQueued;

    /// <summary>One <see cref="DrawNow"/> after the pieces placed in this pass of the dispatcher (a layout pass places
    /// each piece in turn).</summary>
    private void DrawSoon()
    {
        if (_drawQueued) return;
        _drawQueued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Loaded, DrawNow);
    }

    public void Remove(LiquidGlassBackdrop piece)
    {
        if (!_pieces.Remove(piece, out var p)) return;
        _clearLayer = true;
        Arrange();
        // The capture thread reads the published pieces under the GPU lock: once the piece is off them, its map can go.
        Publish();
        ReleaseMaps(p);
    }

    /// <summary>Lets go of every map a piece holds. Under the GPU lock, once no published job names them.</summary>
    private static void ReleaseMaps(Piece p) => (p.MapView, p.MapFor) = (IntPtr.Zero, null);

    /// <summary>Lets go of the window's move maps. Under the GPU lock, once no published job names them.</summary>
    private void ReleaseAllMaps()
    {
        foreach (var map in _maps.Values)
        {
            CaptureNative.Release(map.View);
            CaptureNative.Release(map.Texture);
        }
        _maps.Clear();
        _mapsMaking.Clear();
        _mapGeneration++;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _toneTimer?.Stop();
        Wait(false);
        Hook(false);
        if (_following.Count > 0) LiquidGlassSources.Animating -= OnAnimating;
        _following.Clear();
        _session.RemoveConsumer(this);
        _image.IsFrontBufferAvailableChanged -= OnFrontBuffer;
        Volatile.Write(ref _jobs, []);
        Volatile.Write(ref _areas, []);
        lock (_gpu)
        {
            foreach (var p in _pieces.Values) ReleaseMaps(p);
            ReleaseAllMaps();
            _pieces.Clear();
            DropPicture();
            DropLayer();
        }
        _companion?.Dispose();
        _companion = null;
        if (_locked)
        {
            _image.Unlock();
            _locked = false;
        }
    }

    /// <summary>Keeps the composed glass's window over the owner's client area, beneath it, shown with it.</summary>
    public void Track() => _companion?.Track();

    /// <summary>Leaves the composed glass's window out of capture, as its owner, or lets it in.</summary>
    public void Exclude(bool exclude) => _companion?.Exclude(exclude);

    /// <summary>On the capture thread: draws the pieces a change touched (and any new, moved or resized), then shows the
    /// frame: composed pieces at once (the swap chain), imaged ones through the UI thread. Imaged pieces wait while the
    /// UI hasn't the picture locked.</summary>
    public void Frame(GpuGlassRenderer renderer, IReadOnlyList<Int32Rect>? changed, bool foreign)
    {
        lock (_gpu)
        {
            Answer(renderer);
            if (changed != null) _pending.AddRange(changed);
            if (_disposed) return;
            ResizeLayer();
            var clear = _clearLayer && _layer != IntPtr.Zero;
            if (clear)
            {
                renderer.Clear(_layerTarget);
                _clearLayer = false;
            }
            var (composed, imaged, waiting) = (new List<Int32Rect>(), new List<Int32Rect>(), false);
            foreach (var job in Volatile.Read(ref _jobs))
            {
                // A composed piece drawn over (a pane redrawn under its card) is drawn again after it.
                var due = clear && job.Composed || !_drawn.TryGetValue(job.Id, out var version) || version != job.Version || _pending.Any(c => Touches(c, job.Box))
                    || (job.Composed && composed.Any(t => Touches(t, job.Target)));
                if (!due || job.Target.Width <= 0) continue;
                if (job.Composed)
                {
                    if (_layer == IntPtr.Zero || !renderer.Render(job, _layerTarget)) continue;
                    composed.Add(job.Target);
                }
                else
                {
                    if (!_canDraw || _picture == IntPtr.Zero)
                    {
                        waiting = true;
                        continue;
                    }
                    if (!renderer.Render(job, _pictureTarget)) continue;
                    imaged.Add(job.Target);
                }
                _drawn[job.Id] = job.Version;
            }
            if (!waiting) _pending.Clear();
            _wants = waiting;
            if (composed.Count > 0 || clear) PresentLayer(renderer, composed, clear);
            if (imaged.Count > 0)
            {
                renderer.Finish(_picture);
                _canDraw = false;
                _dispatcher.BeginInvoke(DispatcherPriority.Render, () => Present(imaged));
            }
            // The ink follows what the glass shows (Plan AD): read again once glass was drawn, a few times a second at most.
            if (composed.Count > 0 || imaged.Count > 0) _toneOwed = true;
            if (_toneOwed) ReadTones(renderer);
            if (composed.Count == 0 && imaged.Count == 0) return;
            Interlocked.Increment(ref _frames);
            if (foreign) Interlocked.Increment(ref _foreignFrames);
            LiquidGlassGovernor.Delivered();
        }
    }

    // ---- Tones (Plan AD) ----

    private static readonly long ToneTicks = (long)(System.Diagnostics.Stopwatch.Frequency * LiquidGlassBackdrop.ToneEvery.TotalSeconds);
    // Each read's cells, the read's own (capture thread), the handed over (under the gate) and the shown (UI thread); they
    // grow to the window's cells and stay.
    private uint[] _toneRead = [], _toneCells = [], _toneCellsShown = [];
    private readonly ToneLayout[] _toneReadLayouts = new ToneLayout[GpuGlassRenderer.MaxTones], _toneLayouts = new ToneLayout[GpuGlassRenderer.MaxTones],
        _toneLayoutsShown = new ToneLayout[GpuGlassRenderer.MaxTones];
    private readonly int[] _toneIds = new int[GpuGlassRenderer.MaxTones], _toneIdsShown = new int[GpuGlassRenderer.MaxTones];
    private int _toneCellCount;
    private readonly object _toneGate = new();
    private int _toneCount;
    private long _toneAt;
    private volatile bool _toneOwed;
    private volatile bool _tonePublishQueued, _toneLaterQueued;
    private DispatcherTimer? _toneTimer;
    private Action? _toneLater, _showTones;
    private long _toneReads;

    /// <summary>How many times this window's tones were read, for the tests.</summary>
    internal long ToneReads => Interlocked.Read(ref _toneReads);

    /// <summary>Whether no read is owed, waiting for its moment, or on its way to the pieces, for the tests.</summary>
    internal bool ToneIdle => !_toneOwed && !_toneLaterQueued && !_tonePublishQueued;

    /// <summary>Under the GPU lock (the capture thread, or the UI thread's DrawNow): each piece's tone read on the GPU and
    /// handed to the UI thread; or, read too lately, a read asked for once the moment has passed.</summary>
    private void ReadTones(GpuGlassRenderer renderer)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - _toneAt < ToneTicks)
        {
            if (_toneLaterQueued) return;
            _toneLaterQueued = true;
            _dispatcher.BeginInvoke(DispatcherPriority.Background, _toneLater ??= ToneLater);
            return;
        }
        _toneOwed = false;
        _toneAt = now;
        var jobs = Volatile.Read(ref _jobs);
        // Room for every piece's cells, grown once.
        var need = 0;
        foreach (var job in jobs)
        {
            var (cols, rows) = BackdropTone.GridFor(job.Box.Width, job.Box.Height);
            need += cols * rows;
        }
        need = Math.Min(need, GpuGlassRenderer.MaxToneCells);
        if (_toneRead.Length < need) _toneRead = new uint[need];
        var count = renderer.Tones(jobs, _toneRead, _toneReadLayouts);
        if (count == 0) return;
        Interlocked.Increment(ref _toneReads);
        var used = 0;
        for (var i = 0; i < count; i++)
        {
            if (_toneReadLayouts[i].Read) used = Math.Max(used, _toneReadLayouts[i].First + _toneReadLayouts[i].Cols * _toneReadLayouts[i].Rows);
        }
        lock (_toneGate)
        {
            if (_toneCells.Length < used) _toneCells = new uint[_toneRead.Length];
            for (var i = 0; i < count; i++) _toneIds[i] = jobs[i].Id;
            Array.Copy(_toneRead, _toneCells, used);
            Array.Copy(_toneReadLayouts, _toneLayouts, count);
            (_toneCount, _toneCellCount) = (count, used);
        }
        if (_tonePublishQueued) return;
        _tonePublishQueued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, _showTones ??= ShowTones);
    }

    /// <summary>On the UI thread: each piece takes its tone.</summary>
    private void ShowTones()
    {
        _tonePublishQueued = false;
        if (_disposed) return;
        int count;
        lock (_toneGate)
        {
            count = _toneCount;
            if (_toneCellsShown.Length < _toneCellCount) _toneCellsShown = new uint[_toneCells.Length];
            Array.Copy(_toneIds, _toneIdsShown, count);
            Array.Copy(_toneCells, _toneCellsShown, _toneCellCount);
            Array.Copy(_toneLayouts, _toneLayoutsShown, count);
        }
        foreach (var p in _pieces.Values)
        {
            for (var i = 0; i < count; i++)
            {
                if (_toneIdsShown[i] != p.Id) continue;
                var layout = _toneLayoutsShown[i];
                if (layout.Read && p.Element.OnGpu)
                    p.Element.SetTones(new ReadOnlySpan<uint>(_toneCellsShown, layout.First, layout.Cols * layout.Rows), layout.Cols, layout.Rows, layout.Region);
                break;
            }
        }
    }

    /// <summary>On the UI thread: a read once the moment has passed, through a frame of its own.</summary>
    private void ToneLater()
    {
        if (_disposed) return;
        _toneTimer ??= new DispatcherTimer(LiquidGlassBackdrop.ToneEvery, DispatcherPriority.Background, (_, _) =>
        {
            _toneTimer!.Stop();
            _toneLaterQueued = false;
            if (!_disposed && _toneOwed && _active) Want();
        }, _dispatcher);
        _toneTimer.Stop();
        _toneTimer.Start();
    }

    /// <summary>On the capture thread: the device went. The UI thread drops what was made on it and makes it again on
    /// the next device; three losses in a minute and the window takes the CPU path.</summary>
    public void DeviceLost()
    {
        lock (_gpu)
        {
            _canDraw = false;
            _drawn.Clear();
        }
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            var now = DateTime.UtcNow;
            if (now - _firstLoss > TimeSpan.FromMinutes(1)) (_firstLoss, _losses) = (now, 0);
            if (++_losses >= 3)
            {
                Failed = true;
                Changed?.Invoke();
                return;
            }
            Volatile.Write(ref _jobs, []);
            Volatile.Write(ref _areas, []);
            lock (_gpu)
            {
                DropPicture();
                DropLayer();
                foreach (var p in _pieces.Values) ReleaseMaps(p);
                ReleaseAllMaps();
            }
            foreach (var p in _pieces.Values)
            {
                p.Version++;
                MakeMap(p);
            }
            Arrange();
            Publish();
        });
    }

    /// <summary>Reads <paramref name="piece"/>'s drawn pixels back (premultiplied BGRA rows, its box's size), on the
    /// capture thread; for the tests.</summary>
    internal Task<byte[]> ReadAsync(LiquidGlassBackdrop piece)
    {
        var done = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pieces.TryGetValue(piece, out var p))
        {
            done.SetResult([]);
            return done.Task;
        }
        _readRequests.Enqueue((p.Id, done));
        Want();
        return done.Task;
    }

    /// <summary>On the capture thread, under the GPU lock: answers the tests' reads.</summary>
    private void Answer(GpuGlassRenderer renderer)
    {
        while (_readRequests.TryDequeue(out var request))
        {
            var job = Volatile.Read(ref _jobs).FirstOrDefault(j => j.Id == request.Id);
            var source = job == null ? IntPtr.Zero : job.Composed ? _layer : _picture;
            request.Done.TrySetResult(source == IntPtr.Zero ? [] : renderer.Read(source, job!.Target));
        }
    }

    private static bool Touches(Int32Rect a, Int32Rect b)
        => a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    // ---- Composed ----

    /// <summary>On the UI thread: DirectComposition for the window, in its companion window beneath it (GlassCompanion),
    /// showing a swap chain the size of its client area; false where it can't (Windows refused a window or
    /// DirectComposition).</summary>
    private bool MakeComposition()
    {
        if (_composedFailed) return false;
        if (_dcomp != IntPtr.Zero) return true;
        var (device, _) = _session.Device();
        if (device == IntPtr.Zero) return false;
        _companion ??= GlassCompanion.For(_hwnd);
        if (_companion == null)
        {
            _composedFailed = true;
            return false;
        }
        IntPtr dxgi = IntPtr.Zero, adapter = IntPtr.Zero, factory = IntPtr.Zero;
        try
        {
            if (CaptureNative.QueryInterface(device, IID_IDXGIDevice, out dxgi) < 0 || DCompositionCreateDevice(dxgi, IID_IDCompositionDevice, out _dcomp) < 0
                || CreateTargetForHwnd(_dcomp, _companion.Hwnd, topmost: true, out _target) < 0 || CreateVisual(_dcomp, out _visual) < 0
                || GetAdapter(dxgi, out adapter) < 0 || GetParent(adapter, IID_IDXGIFactory2, out factory) < 0)
            {
                lock (_gpu) DropLayer();
                _composedFailed = true;
                return false;
            }
            var (w, h) = ClientSize();
            var desc = new DXGI_SWAP_CHAIN_DESC1
            {
                Width = (uint)w, Height = (uint)h, Format = CaptureNative.DXGI_FORMAT_B8G8R8A8_UNORM, SampleCount = 1, BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = 2, SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL, AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED,
            };
            if (CreateSwapChainForComposition(factory, device, desc, out _swapChain) < 0 || SetContent(_visual, _swapChain) < 0 || SetRoot(_target, _visual) < 0 || Commit(_dcomp) < 0)
            {
                lock (_gpu) DropLayer();
                _composedFailed = true;
                return false;
            }
            lock (_gpu)
            {
                _resizeTo = (w, h);
                _clearLayer = true;
            }
            return true;
        }
        finally
        {
            CaptureNative.Release(factory);
            CaptureNative.Release(adapter);
            CaptureNative.Release(dxgi);
        }
    }

    /// <summary>The window's client area in physical pixels, at least one by one.</summary>
    private (int Width, int Height) ClientSize()
    {
        GetClientRect(_hwnd, out var r);
        return (Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
    }

    /// <summary>On the capture thread, under the GPU lock: the swap chain and the layer to the client area's size.</summary>
    private void ResizeLayer()
    {
        if (_swapChain == IntPtr.Zero || _resizeTo is not { } size) return;
        _resizeTo = null;
        var (device, _) = _session.Device();
        if (device == IntPtr.Zero) return;
        CaptureNative.Release(_layerTarget);
        CaptureNative.Release(_layer);
        _layer = _layerTarget = IntPtr.Zero;
        if (size.Width != _layerWidth || size.Height != _layerHeight)
        {
            if (ResizeBuffers(_swapChain, (uint)size.Width, (uint)size.Height) < 0) return;
        }
        var desc = new CaptureNative.D3D11_TEXTURE2D_DESC
        {
            Width = (uint)size.Width, Height = (uint)size.Height, MipLevels = 1, ArraySize = 1, Format = CaptureNative.DXGI_FORMAT_B8G8R8A8_UNORM, SampleCount = 1,
            Usage = CaptureNative.D3D11_USAGE_DEFAULT, BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE,
        };
        unsafe
        {
            if (GpuNative.CreateTexture2D(device, desc, null, out _layer) < 0 || CreateRenderTargetView(device, _layer, out _layerTarget) < 0) return;
        }
        (_layerWidth, _layerHeight) = size;
        _clearLayer = true;
        _wholeAhead = 2;
    }

    /// <summary>
    /// Under the GPU lock (the capture thread, or the UI thread's DrawNow): the layer onto the swap chain's back buffer,
    /// presented without waiting. The flip model's back buffer holds the frame before last (DXGI doesn't fill it in), so
    /// only what this frame and the frame before drew (<paramref name="drawn"/>, in the layer's pixels) is copied onto it,
    /// and DWM is told only what this frame drew (Present1's dirty rectangles): the copy and DWM's composing grow with the
    /// glass drawn, not the window (perf review P11). A rectangle inside another is left out, and where the rectangles
    /// add up to the layer or more (a video behind every piece) the whole layer is copied, or presented, at once. The whole
    /// layer is copied and presented after a clear (<paramref name="cleared"/>), for the first two frames of new buffers,
    /// and for two frames after a present that didn't plainly succeed.
    /// </summary>
    private void PresentLayer(GpuGlassRenderer renderer, List<Int32Rect> drawn, bool cleared)
    {
        if (_swapChain == IntPtr.Zero || _layer == IntPtr.Zero || GetBuffer(_swapChain, out var back) < 0) return;
        _dirty.Clear();
        foreach (var r in drawn)
        {
            int left = Math.Max(0, r.X), top = Math.Max(0, r.Y), right = Math.Min(_layerWidth, r.X + r.Width), bottom = Math.Min(_layerHeight, r.Y + r.Height);
            if (right > left && bottom > top) Add(_dirty, new CaptureNative.RECT { Left = left, Top = top, Right = right, Bottom = bottom });
        }
        var layer = (long)_layerWidth * _layerHeight;
        // The whole layer changed: cleared, new buffers, a present that failed, or nothing named in it.
        var changedWhole = cleared || _wholeAhead > 0 || _dirty.Count == 0;
        var presentWhole = changedWhole || Area(_dirty) >= layer;
        _copy.Clear();
        if (!changedWhole && !_wholeBefore)
        {
            foreach (var r in _dirty) _copy.Add(r);
            foreach (var r in _dirtyBefore) Add(_copy, r);
        }
        long copied;
        if (_copy.Count == 0 || Area(_copy) >= layer)
        {
            renderer.Copy(back, _layer);
            copied = layer;
        }
        else
        {
            foreach (var r in _copy) renderer.Copy(back, _layer, r);
            copied = Area(_copy);
        }
        Interlocked.Add(ref PixelsCopied, copied);
        Interlocked.Increment(ref Presents);
        if (CheckPresents) Check(renderer, back);
        CaptureNative.Release(back);
        var hr = presentWhole ? GpuNative.Present(_swapChain) : Present1(_swapChain, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_dirty));
        if (!presentWhole && hr == 0) Interlocked.Increment(ref PartialPresents);
        (_dirty, _dirtyBefore) = (_dirtyBefore, _dirty);
        _wholeBefore = changedWhole;
        if (_wholeAhead > 0) _wholeAhead--;
        // Not presented, or not plainly (an occluded window): which buffer comes next is unknown, so the next two are whole.
        if (hr != 0) _wholeAhead = 2;
    }

    /// <summary>Adds <paramref name="r"/> to <paramref name="rects"/> unless one there holds it, and drops any it holds.</summary>
    private static void Add(List<CaptureNative.RECT> rects, CaptureNative.RECT r)
    {
        foreach (var o in rects)
        {
            if (Holds(o, r)) return;
        }
        for (var i = rects.Count - 1; i >= 0; i--)
        {
            if (Holds(r, rects[i])) rects.RemoveAt(i);
        }
        rects.Add(r);
    }

    private static bool Holds(CaptureNative.RECT a, CaptureNative.RECT b) => a.Left <= b.Left && a.Top <= b.Top && a.Right >= b.Right && a.Bottom >= b.Bottom;

    private static long Area(List<CaptureNative.RECT> rects)
    {
        long sum = 0;
        foreach (var r in rects) sum += (long)(r.Right - r.Left) * (r.Bottom - r.Top);
        return sum;
    }

    /// <summary>The rectangles this present copies onto the back buffer; kept.</summary>
    private readonly List<CaptureNative.RECT> _copy = [];

    /// <summary>What the last present drew (its dirty rectangles), and this one's, in the layer's pixels; lists kept.</summary>
    private List<CaptureNative.RECT> _dirtyBefore = [], _dirty = [];

    /// <summary>The whole layer changed at the last present.</summary>
    private bool _wholeBefore = true;

    /// <summary>How many presents from now copy and present the whole layer: two after new buffers or a failed present.</summary>
    private int _wholeAhead = 2;

    /// <summary>How many presents named only what they drew, for the tests and the measurements.</summary>
    internal static long PartialPresents;

    /// <summary>How many presents there were, and the pixels they copied from the layer, for the measurements.</summary>
    internal static long Presents, PixelsCopied;

    /// <summary>For the tests: before each present, the back buffer and the layer are read back whole and compared, and a
    /// present whose buffer isn't the layer is counted (<see cref="PresentsOff"/>). Off, the default, nothing is read.</summary>
    internal static volatile bool CheckPresents;

    internal static long PresentsChecked, PresentsOff;

    private void Check(GpuGlassRenderer renderer, IntPtr back)
    {
        var whole = new Int32Rect(0, 0, _layerWidth, _layerHeight);
        Interlocked.Increment(ref PresentsChecked);
        if (!renderer.Read(back, whole).AsSpan().SequenceEqual(renderer.Read(_layer, whole))) Interlocked.Increment(ref PresentsOff);
    }

    /// <summary>Under the GPU lock.</summary>
    private void DropLayer()
    {
        foreach (var p in new[] { _layerTarget, _layer, _swapChain, _visual, _target, _dcomp }) CaptureNative.Release(p);
        _layerTarget = _layer = _swapChain = _visual = _target = _dcomp = IntPtr.Zero;
        _layerWidth = _layerHeight = 0;
    }

    // ---- Imaged ----

    /// <summary>On the UI thread: the drawn rectangles to WPF, which copies them on its next frame.</summary>
    private void Present(List<Int32Rect> drawn)
    {
        if (_disposed || !_locked) return;
        foreach (var r in drawn)
        {
            int right = Math.Min(r.X + r.Width, _pictureWidth), bottom = Math.Min(r.Y + r.Height, _pictureHeight);
            if (right > r.X && bottom > r.Y) _image.AddDirtyRect(new Int32Rect(r.X, r.Y, right - r.X, bottom - r.Y));
        }
        _image.Unlock();
        _locked = false;
        // Not now: WPF sends the copy as it commits its next frame, and a lock taken before that would cancel it. The
        // first frame WPF renders from here commits it; from the one after, the lock is tried.
        _skip = true;
        Wait(true);
    }

    /// <summary>Takes the picture's lock back once WPF has copied the last frame; then the capture thread may draw.</summary>
    private void Relock()
    {
        if (_disposed || _locked) return;
        _attempts++;
        if (!_image.TryLock(new Duration(TimeSpan.Zero)))
        {
            // TryLock counts a lock even when it fails (D3DImage.LockImpl), and the unlock that undoes it asks again for
            // the copy the attempt called off.
            _image.Unlock();
            Wait(true);
            return;
        }
        Wait(false);
        _locked = true;
        _canDraw = _image.IsFrontBufferAvailable;
        if (_canDraw) Want();
    }

    private void Wait(bool on)
    {
        if (on == _waiting) return;
        _waiting = on;
        if (on) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_skip)
        {
            _skip = false;
            return;
        }
        Relock();
    }

    private void OnFrontBuffer(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_image.IsFrontBufferAvailable)
        {
            _canDraw = false;
            return;
        }
        // WPF's device is back: the picture again, and every imaged piece drawn afresh.
        if (!_locked)
        {
            _image.Lock();
            _locked = true;
        }
        if (_surface9 != IntPtr.Zero) _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface9, enableSoftwareFallback: true);
        lock (_gpu) _drawn.Clear();
        _canDraw = true;
        Want();
    }

    /// <summary>Makes the shared picture: a Direct3D 11 texture on the capture's device, shared, opened by Direct3D 9Ex as
    /// the D3DImage's surface. The old one goes once the capture thread isn't drawing on it.</summary>
    private void MakePicture(int width, int height)
    {
        var (device, luid) = _session.Device();
        var device9 = device == IntPtr.Zero ? IntPtr.Zero : D3D9Bridge.For(luid);
        if (device9 == IntPtr.Zero)
        {
            Failed = true;
            Changed?.Invoke();
            return;
        }
        var desc = new CaptureNative.D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = CaptureNative.DXGI_FORMAT_B8G8R8A8_UNORM, SampleCount = 1,
            Usage = CaptureNative.D3D11_USAGE_DEFAULT, BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE, MiscFlags = D3D11_RESOURCE_MISC_SHARED,
        };
        IntPtr picture = IntPtr.Zero, target = IntPtr.Zero, resource = IntPtr.Zero, texture9 = IntPtr.Zero, surface9 = IntPtr.Zero;
        unsafe
        {
            if (GpuNative.CreateTexture2D(device, desc, null, out picture) < 0 || CreateRenderTargetView(device, picture, out target) < 0
                || CaptureNative.QueryInterface(picture, IID_IDXGIResource, out resource) < 0 || GetSharedHandle(resource, out var shared) < 0
                || CreateSharedTexture9(device9, (uint)width, (uint)height, shared, out texture9) < 0 || GetSurfaceLevel(texture9, out surface9) < 0)
            {
                foreach (var p in new[] { surface9, texture9, resource, target, picture }) CaptureNative.Release(p);
                Failed = true;
                Changed?.Invoke();
                return;
            }
        }
        CaptureNative.Release(resource);
        if (!_locked)
        {
            _image.Lock();
            _locked = true;
        }
        _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface9, enableSoftwareFallback: true);
        lock (_gpu)
        {
            DropPicture();
            (_picture, _pictureTarget, _texture9, _surface9, _pictureWidth, _pictureHeight) = (picture, target, texture9, surface9, width, height);
            foreach (var p in _pieces.Values.Where(p => !p.Composed)) _drawn.Remove(p.Id);
        }
        _canDraw = _image.IsFrontBufferAvailable;
        Want();
    }

    /// <summary>Under the GPU lock.</summary>
    private void DropPicture()
    {
        foreach (var p in new[] { _surface9, _texture9, _pictureTarget, _picture }) CaptureNative.Release(p);
        _surface9 = _texture9 = _pictureTarget = _picture = IntPtr.Zero;
        _pictureWidth = _pictureHeight = 0;
    }

    // ---- Both ----

    /// <summary>Something is to be drawn though nothing changed behind. Composed, it is drawn at the UI thread's next turn
    /// (<see cref="DrawSoon"/>): the capture thread may be waiting a long while for Windows' next frame
    /// (MonitorCapture.IdleLook), and a frame of its own couldn't end that wait. Either way the capture thread is told, for
    /// when that draw can't be made (no duplication yet).</summary>
    private void Want()
    {
        _wants = true;
        _session.Wake();
        if (_servesItself) DrawSoon();
    }

    /// <summary>
    /// Decides how each piece shows and where it is drawn. In a window DirectComposition serves, every piece is composed,
    /// at its place in the client area, in the order WPF draws them (the visual tree's), so a piece over another (a card
    /// on a page, the grip by a pane, a dialog) is drawn over its glass. What WPF draws of the lower piece over that spot
    /// (a scrolled pane's content under the grip) then shows over the upper glass rather than under it: the browser's
    /// backdrop filter reads everything painted before the piece, the lower piece's content too, so the upper glass
    /// showing it is as near as a layer beneath WPF can come. A dialog or a toast lays its own frosted copy of the stage
    /// over its glass (AeroWindow.Frost), which hides it there anyway. Imaged pieces (a window DirectComposition can't
    /// serve) are laid out on the shared picture in rows, tallest first; the picture is made again if it has grown too
    /// small.
    /// </summary>
    private void Arrange()
    {
        var composable = MakeComposition();
        var client = new System.Drawing.Point();
        ClientToScreen(_hwnd, ref client);
        var imaged = new List<Piece>();
        var order = 0;
        foreach (var p in _pieces.Values.OrderBy(TreeOrder, Comparer<IReadOnlyList<int>>.Create(Compare)))
        {
            p.Order = order++;
            if (p.Box.Width <= 0 || p.Box.Height <= 0) continue;
            if (!composable)
            {
                imaged.Add(p);
                p.Composed = false;
                continue;
            }
            var target = new Int32Rect(p.Box.X - client.X, p.Box.Y - client.Y, p.Box.Width, p.Box.Height);
            if (target != p.Target || !p.Composed)
            {
                if (p.Composed) _clearLayer = true;   // the old place is cleared
                (p.Target, p.Composed) = (target, true);
                p.Version++;
            }
        }
        if (composable)
        {
            var size = ClientSize();
            lock (_gpu)
            {
                if (size != (_layerWidth, _layerHeight)) _resizeTo = size;
            }
            _companion?.Track();
            _companion?.Shape(_pieces.Values.Where(p => p.Composed && p.Shape.Opacity > 0).Select(p =>
            {
                var visible = Rect.Intersect(p.Shape.Shape, p.Shape.Visible);
                if (!visible.IsEmpty) visible.Offset(p.Target.X, p.Target.Y);
                return (visible, p.Shape.Radii);
            }));
        }
        if (imaged.Count == 0) return;
        var width = Math.Clamp(NextPowerOfTwo(imaged.Max(p => p.Box.Width)), 1024, 8192);
        int x = 0, y = 0, row = 0;
        foreach (var p in imaged.OrderByDescending(p => p.Box.Height))
        {
            if (x + p.Box.Width > width) (x, y, row) = (0, y + row, 0);
            var target = new Int32Rect(x, y, p.Box.Width, p.Box.Height);
            if (target != p.Target)
            {
                p.Target = target;
                p.Version++;
            }
            x += p.Box.Width;
            row = Math.Max(row, p.Box.Height);
        }
        var height = Math.Max(1, y + row);
        if (_picture == IntPtr.Zero || width > _pictureWidth || height > _pictureHeight) MakePicture(width, Math.Clamp(NextPowerOfTwo(height), 256, 8192));
    }

    /// <summary>A piece's place in the order WPF draws: the child indices down the visual tree to it.</summary>
    private static IReadOnlyList<int> TreeOrder(Piece p)
    {
        var path = new List<int>();
        for (DependencyObject node = p.Element; VisualTreeHelper.GetParent(node) is { } parent; node = parent)
        {
            var count = VisualTreeHelper.GetChildrenCount(parent);
            var index = 0;
            while (index < count && !ReferenceEquals(VisualTreeHelper.GetChild(parent, index), node)) index++;
            path.Add(index);
        }
        path.Reverse();
        return path;
    }

    private static int Compare(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        }
        return a.Count.CompareTo(b.Count);   // a parent's own drawing comes before its children's
    }

    /// <summary>Publishes the pieces to the capture thread, as an array it reads without a lock.</summary>
    private void Publish()
    {
        var jobs = new List<GpuGlassJob>();
        foreach (var p in _pieces.Values.OrderBy(p => p.Order))
        {
            if (p.MapView == IntPtr.Zero || p.Target.Width <= 0) continue;
            // Composed pieces take their own shape, clip and fade; an imaged piece's are WPF's.
            jobs.Add(new GpuGlassJob(p.Id, p.Box, p.MapView, p.Target, p.Brightness, p.Sigma, p.Composed ? p.Shape : GpuGlassShape.Whole(p.Box.Width, p.Box.Height), p.Composed, p.Version));
        }
        Volatile.Write(ref _jobs, jobs.ToArray());
        Volatile.Write(ref _areas, jobs.Select(j => j.Box).ToArray());
        _servesItself = jobs.TrueForAll(j => j.Composed);
        Want();
        Changed?.Invoke();
    }

    /// <summary>A window's move map for one display scale and displacement scale: each device pixel's move (feDisplacementMap's
    /// <c>scale * s * (channel - 0.5)</c>, DisplacementField.Move) in float pairs, from a box's top left, over an area at
    /// least as large as any piece. The mirror into the box is the shader's, so one map serves every piece at every size:
    /// a pane resized with its window, a pill whose width follows its reading, a snap, draw at once with no map to make.</summary>
    private sealed class MoveMap
    {
        public int W, H;
        public IntPtr Texture, View;
    }

    private readonly Dictionary<(double Dpi, double Scale), MoveMap> _maps = [];
    private readonly HashSet<(double Dpi, double Scale)> _mapsMaking = [];
    private int _mapGeneration;

    /// <summary>Points the piece at its scales' move map, or makes (or grows) that map off the UI thread first.</summary>
    private void MakeMap(Piece p)
    {
        if (p.Box.Width <= 0 || p.Box.Height <= 0) return;
        var key = (p.Dpi, p.Scale);
        if (_maps.TryGetValue(key, out var map) && map.W >= p.Box.Width && map.H >= p.Box.Height)
        {
            if (p.MapView != map.View)
            {
                (p.MapView, p.MapFor) = (map.View, key);
                p.Version++;
            }
            return;
        }
        if (!_mapsMaking.Add(key)) return;   // one in the making: the piece takes it when it lands
        var generation = _mapGeneration;
        // Room for the largest piece now and more (a window grows): whole steps of 256 pixels, at least 1280 by 800.
        int w = Step(Math.Max(Math.Max(p.Box.Width, map?.W ?? 0), 1280)), h = Step(Math.Max(Math.Max(p.Box.Height, map?.H ?? 0), 800));
        foreach (var other in _pieces.Values)
        {
            if ((other.Dpi, other.Scale) != key) continue;
            w = Math.Max(w, Step(other.Box.Width));
            h = Math.Max(h, Step(other.Box.Height));
        }
        Interlocked.Increment(ref MapsMade);
        var (dpi, scale) = key;
        Task.Run(() =>
        {
            var field = DisplacementField.Shared(w, h, dpi);
            var moves = new float[w * h * 2];
            Parallel.For(0, h, y =>
            {
                for (var x = 0; x < w; x++)
                {
                    var (mx, my) = field.Move(x, y, scale);
                    moves[(y * w + x) * 2] = (float)mx;
                    moves[(y * w + x) * 2 + 1] = (float)my;
                }
            });
            return moves;
        }).ContinueWith(done =>
        {
            _mapsMaking.Remove(key);
            if (_disposed || generation != _mapGeneration || done.Status != TaskStatus.RanToCompletion) return;
            var (device, _) = _session.Device();
            if (device == IntPtr.Zero) return;
            var made = new MoveMap { W = w, H = h };
            unsafe
            {
                fixed (float* data = done.Result)
                {
                    var init = new D3D11_SUBRESOURCE_DATA { SysMem = (IntPtr)data, SysMemPitch = (uint)(w * 8) };
                    var desc = new CaptureNative.D3D11_TEXTURE2D_DESC
                    {
                        Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT_R32G32_FLOAT, SampleCount = 1,
                        Usage = CaptureNative.D3D11_USAGE_DEFAULT, BindFlags = D3D11_BIND_SHADER_RESOURCE,
                    };
                    if (GpuNative.CreateTexture2D(device, desc, &init, out made.Texture) < 0) return;
                }
            }
            if (CreateShaderResourceView(device, made.Texture, out made.View) < 0)
            {
                CaptureNative.Release(made.Texture);
                return;
            }
            _maps.TryGetValue(key, out var old);
            _maps[key] = made;
            // Every piece at these scales onto the new map first; the old one goes once the capture thread can't be using it.
            var larger = false;
            foreach (var piece in _pieces.Values)
            {
                if ((piece.Dpi, piece.Scale) != key) continue;
                if (piece.Box.Width > w || piece.Box.Height > h)
                {
                    larger = true;
                    continue;
                }
                (piece.MapView, piece.MapFor) = (made.View, key);
                piece.Version++;
            }
            Publish();
            if (_pieces.Values.Any(q => q.Composed)) DrawSoon();
            if (old != null)
            {
                lock (_gpu)
                {
                    CaptureNative.Release(old.View);
                    CaptureNative.Release(old.Texture);
                }
            }
            if (larger) MakeMap(_pieces.Values.First(q => (q.Dpi, q.Scale) == key && (q.Box.Width > w || q.Box.Height > h)));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static int Step(int v) => (v + 255) / 256 * 256;

    private static int NextPowerOfTwo(int v)
    {
        var p = 1;
        while (p < v) p <<= 1;
        return p;
    }
}
