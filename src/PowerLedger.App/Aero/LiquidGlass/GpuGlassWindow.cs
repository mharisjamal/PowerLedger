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
/// <item><b>Composed</b> (DirectComposition), the usual way: the window gets a DirectComposition visual beneath WPF's own
/// content (a target with topmost false), showing a swap chain the size of the window's client area. Each piece is drawn
/// at its own place in it, its corners rounded in its alpha, and DWM puts it under the WPF content wherever that is
/// clear. A frame of glass never makes WPF draw anything, and the glass moves with the window by itself. The piece
/// itself (LiquidGlassBackdrop) draws nothing there.</item>
/// <item><b>Imaged</b> (D3DImage), where DirectComposition can't serve: a layered window (the watts overlay, which WPF
/// shows through UpdateLayeredWindow), or a piece over another piece in the same window (a dialog over a page: the glass
/// beneath WPF's content would show the page's content through it). Those pieces are drawn into one shared picture,
/// each in a rectangle of its own, which the window shows through one D3DImage, each piece its own rectangle.</item>
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
        public Int32Rect Box;
        public double Dpi;
        public float Brightness;
        public double Sigma;
        public double Scale;
        public CornerRadius Radii;
        public (int W, int H, double Dpi, double Scale)? MapFor;
        public IntPtr Map, MapView;
        public int MapGeneration;
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
        // A layered window (WS_EX_LAYERED: AllowsTransparency) is shown through UpdateLayeredWindow, which
        // DirectComposition can't sit beneath: its pieces are imaged.
        _composedFailed = (GetWindowLongPtrW(hwnd, -20) & 0x80000) != 0;
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

    /// <summary>The path's state in a line, for the tests and the measurements.</summary>
    internal string State => $"pieces {_pieces.Count} ({_pieces.Values.Count(p => p.Composed)} composed), with maps {_pieces.Values.Count(p => p.MapView != IntPtr.Zero)}, " +
        $"jobs {Volatile.Read(ref _jobs).Length}, layer {_layerWidth}x{_layerHeight}, picture {_pictureWidth}x{_pictureHeight}, locked {_locked}, relock attempts {_attempts}, " +
        $"can draw {_canDraw}, wants {_wants}, active {_active}, frames {Frames}; imaged " + string.Join(" ", _pieces.Values.Where(p => !p.Composed).Select(p => $"{p.Box.X},{p.Box.Y},{p.Box.Width}x{p.Box.Height}")) +
        "; composed " + string.Join(" ", _pieces.Values.Where(p => p.Composed).Select(p => $"{p.Box.X},{p.Box.Y},{p.Box.Width}x{p.Box.Height}"));

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

    /// <summary>The rectangle of the window's shared picture an imaged piece shows, or null while it has none.</summary>
    public Int32Rect? TargetOf(LiquidGlassBackdrop piece)
        => _pieces.TryGetValue(piece, out var p) && !p.Composed && p.Target.Width > 0 && p.MapView != IntPtr.Zero && _picture != IntPtr.Zero ? p.Target : null;

    /// <summary>Adds or updates <paramref name="piece"/>: its box on the screen (physical pixels), its corners (physical
    /// pixels), its display scale and the recipe's numbers. On the UI thread.</summary>
    public void Update(LiquidGlassBackdrop piece, Int32Rect box, CornerRadius radii, double dpi, double brightness, double sigma, double scale)
    {
        if (_disposed) return;
        if (!_pieces.TryGetValue(piece, out var p))
        {
            p = new Piece { Id = ++_nextId, Element = piece };
            _pieces[piece] = p;
        }
        if (p.Box == box && p.Radii == radii && p.Dpi == dpi && p.Brightness == (float)brightness && p.Sigma == sigma && p.Scale == scale) return;
        (p.Box, p.Radii, p.Dpi, p.Brightness, p.Sigma, p.Scale) = (box, radii, dpi, (float)brightness, sigma, scale);
        p.Version++;
        MakeMap(p);
        Arrange();
        Publish();
    }

    public void Remove(LiquidGlassBackdrop piece)
    {
        if (!_pieces.Remove(piece, out var p)) return;
        lock (_gpu)
        {
            CaptureNative.Release(p.MapView);
            CaptureNative.Release(p.Map);
            _clearLayer = true;
        }
        Arrange();
        Publish();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Wait(false);
        _session.RemoveConsumer(this);
        _image.IsFrontBufferAvailableChanged -= OnFrontBuffer;
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
        if (_locked)
        {
            _image.Unlock();
            _locked = false;
        }
    }

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

    /// <summary>On the UI thread: DirectComposition for the window, beneath its WPF content, showing a swap chain the size
    /// of its client area; false where it can't (the window is layered, or DirectComposition refused).</summary>
    private bool MakeComposition()
    {
        if (_composedFailed) return false;
        if (_dcomp != IntPtr.Zero) return true;
        var (device, _) = _session.Device();
        if (device == IntPtr.Zero) return false;
        IntPtr dxgi = IntPtr.Zero, adapter = IntPtr.Zero, factory = IntPtr.Zero;
        try
        {
            if (CaptureNative.QueryInterface(device, IID_IDXGIDevice, out dxgi) < 0 || DCompositionCreateDevice(dxgi, IID_IDCompositionDevice, out _dcomp) < 0
                || CreateTargetForHwnd(_dcomp, _hwnd, topmost: false, out _target) < 0 || CreateVisual(_dcomp, out _visual) < 0
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

    /// <summary>Decides how each piece shows and where it is drawn. Every piece of a window DirectComposition can't serve
    /// is imaged, as is a piece over an earlier one it doesn't sit inside (a dialog over a page: composed, it would lie
    /// beneath the page's content). A piece inside another's pane (a card on a page) is composed after it, over its
    /// glass. The rest are composed, each at its place in the window's client area. Imaged pieces are laid out on the
    /// shared picture in rows, tallest first; the picture is made again if it has grown too small.</summary>
    private void Arrange()
    {
        var composable = MakeComposition();
        var client = new System.Drawing.Point();
        ClientToScreen(_hwnd, ref client);
        var placed = new List<Piece>();
        var imaged = new List<Piece>();
        foreach (var p in _pieces.Values.OrderBy(p => p.Id))
        {
            if (p.Box.Width <= 0 || p.Box.Height <= 0) continue;
            var composed = composable && !placed.Any(q => Touches(q.Box, p.Box) && !Inside(p, q));
            placed.Add(p);
            if (!composed)
            {
                imaged.Add(p);
                if (p.Composed) _clearLayer = true;
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

    /// <summary>Whether <paramref name="inner"/> sits in <paramref name="outer"/>'s pane: inside the control whose template
    /// holds the outer piece (a GlassPanel). A piece in no template is no pane: nothing sits inside it.</summary>
    private static bool Inside(Piece inner, Piece outer)
    {
        // The piece itself in the template, or the template part it was put in.
        var pane = outer.Element.TemplatedParent as Visual ?? (VisualTreeHelper.GetParent(outer.Element) as FrameworkElement)?.TemplatedParent as Visual;
        return pane is not null and not Window && inner.Element.IsDescendantOf(pane);
    }

    /// <summary>Publishes the pieces to the capture thread, as an array it reads without a lock.</summary>
    private void Publish()
    {
        var jobs = new List<GpuGlassJob>();
        foreach (var p in _pieces.Values.OrderBy(p => p.Id))
        {
            if (p.MapView == IntPtr.Zero || p.Target.Width <= 0) continue;
            // Composed pieces round their own corners; an imaged piece's corners are WPF's clip.
            jobs.Add(new GpuGlassJob(p.Id, p.Box, p.MapView, p.Target, p.Brightness, p.Sigma, p.Composed ? p.Radii : default, p.Composed, p.Version));
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
        var generation = ++p.MapGeneration;
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
            if (_disposed || generation != p.MapGeneration || done.Status != TaskStatus.RanToCompletion || !_pieces.ContainsValue(p)) return;
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
            lock (_gpu)
            {
                CaptureNative.Release(p.MapView);
                CaptureNative.Release(p.Map);
                (p.Map, p.MapView, p.MapFor) = (map, view, wanted);
            }
            p.Version++;
            Publish();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static int NextPowerOfTwo(int v)
    {
        var p = 1;
        while (p < v) p <<= 1;
        return p;
    }
}
