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
        public (int W, int H, double Dpi, double Scale)? MapFor;
        public IntPtr Map, MapView;
        public int MapGeneration;
        public (int W, int H, double Dpi, double Scale)? MapMaking;
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
    }

    public void Remove(LiquidGlassBackdrop piece)
    {
        if (!_pieces.Remove(piece, out var p)) return;
        _clearLayer = true;
        Arrange();
        // The capture thread reads the published pieces under the GPU lock: once the piece is off them, its map can go.
        Publish();
        lock (_gpu)
        {
            CaptureNative.Release(p.MapView);
            CaptureNative.Release(p.Map);
            (p.Map, p.MapView) = (IntPtr.Zero, IntPtr.Zero);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
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
            foreach (var p in _pieces.Values)
            {
                CaptureNative.Release(p.MapView);
                CaptureNative.Release(p.Map);
            }
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
    public void Frame(GpuGlassRenderer renderer, IReadOnlyList<Int32Rect>? changed)
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
            if (composed.Count > 0 || clear) PresentLayer(renderer);
            if (imaged.Count > 0)
            {
                renderer.Finish(_picture);
                _canDraw = false;
                _dispatcher.BeginInvoke(DispatcherPriority.Render, () => Present(imaged));
            }
            if (composed.Count == 0 && imaged.Count == 0) return;
            Interlocked.Increment(ref _frames);
            LiquidGlassGovernor.Delivered();
        }
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
                foreach (var p in _pieces.Values)
                {
                    CaptureNative.Release(p.MapView);
                    CaptureNative.Release(p.Map);
                    (p.Map, p.MapView, p.MapFor) = (IntPtr.Zero, IntPtr.Zero, null);
                }
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
        _wants = true;
        _session.Wake();
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
    }

    /// <summary>On the capture thread: the layer onto the swap chain's back buffer, presented without waiting.</summary>
    private void PresentLayer(GpuGlassRenderer renderer)
    {
        if (_swapChain == IntPtr.Zero || _layer == IntPtr.Zero || GetBuffer(_swapChain, out var back) < 0) return;
        renderer.Copy(back, _layer);
        CaptureNative.Release(back);
        GpuNative.Present(_swapChain);
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

    private void Want()
    {
        _wants = true;
        _session.Wake();
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
        Want();
        Changed?.Invoke();
    }

    /// <summary>Makes the piece's source map for its size, display scale and displacement scale off the UI thread (the
    /// turbulence is shared: after the first piece this is a mirror and a pack), then its texture on the device.</summary>
    private void MakeMap(Piece p)
    {
        var wanted = (p.Box.Width, p.Box.Height, p.Dpi, p.Scale);
        if (p.MapFor == wanted || p.Box.Width <= 0 || p.Box.Height <= 0) return;
        // One map in the making a piece at a time: a piece resized on every frame of a spring makes the map for the size
        // it ends at, not one for every frame (each a pass over every pixel).
        if (p.MapMaking != null)
        {
            p.MapMaking = wanted;
            return;
        }
        p.MapMaking = wanted;
        var generation = ++p.MapGeneration;
        Interlocked.Increment(ref MapsMade);
        Task.Run(() =>
        {
            var (w, h, dpi, scale) = wanted;
            var field = DisplacementField.Shared(w, h, dpi);
            var sources = new ushort[w * h * 2];
            Parallel.For(0, h, y =>
            {
                for (var x = 0; x < w; x++)
                {
                    var (sx, sy) = field.Source(x, y, w, h, scale);
                    sources[(y * w + x) * 2] = (ushort)sx;
                    sources[(y * w + x) * 2 + 1] = (ushort)sy;
                }
            });
            return sources;
        }).ContinueWith(done =>
        {
            var next = p.MapMaking;
            p.MapMaking = null;
            if (next is { } later && later != wanted)
            {
                // Resized again while this one was made: make the map for where it is now.
                MakeMap(p);
                if (done.Status != TaskStatus.RanToCompletion) return;
            }
            if (_disposed || generation != p.MapGeneration && p.MapMaking == null || done.Status != TaskStatus.RanToCompletion || !_pieces.ContainsValue(p)) return;
            if (p.Box.Width != wanted.Item1 || p.Box.Height != wanted.Item2) return;
            var (device, _) = _session.Device();
            if (device == IntPtr.Zero) return;
            var (w, h, _, _) = wanted;
            IntPtr map, view;
            unsafe
            {
                fixed (ushort* data = done.Result)
                {
                    var init = new D3D11_SUBRESOURCE_DATA { SysMem = (IntPtr)data, SysMemPitch = (uint)(w * 4) };
                    var desc = new CaptureNative.D3D11_TEXTURE2D_DESC
                    {
                        Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT_R16G16_UINT, SampleCount = 1,
                        Usage = CaptureNative.D3D11_USAGE_DEFAULT, BindFlags = D3D11_BIND_SHADER_RESOURCE,
                    };
                    if (GpuNative.CreateTexture2D(device, desc, &init, out map) < 0) return;
                }
            }
            if (CreateShaderResourceView(device, map, out view) < 0)
            {
                CaptureNative.Release(map);
                return;
            }
            // The new map onto the published pieces first; the old one goes once the capture thread can't be using it.
            var (oldMap, oldView) = (p.Map, p.MapView);
            (p.Map, p.MapView, p.MapFor) = (map, view, wanted);
            p.Version++;
            Publish();
            lock (_gpu)
            {
                CaptureNative.Release(oldView);
                CaptureNative.Release(oldMap);
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static int NextPowerOfTwo(int v)
    {
        var p = 1;
        while (p < v) p <<= 1;
        return p;
    }
}
