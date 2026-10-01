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
/// desktop, into one picture for the window (a Direct3D 11 texture, shared), each piece in a rectangle of its own; the
/// window shows it through one D3DImage, each piece its own rectangle (LiquidGlassBackdrop). No pixel crosses to the CPU.
/// <para>The D3DImage is kept locked while the capture thread may draw, so WPF never copies a half drawn picture: a frame
/// drawn, the UI thread marks the pieces' rectangles dirty and unlocks. WPF sends the copy as it commits its next frame
/// and signals when its render thread has made it; from the frame after, the UI thread tries the lock again (TryLock,
/// never waiting), and once it has it the capture thread may draw the next. A lock taken before the commit would cancel
/// the copy, so the first frame after unlocking is never tried.</para>
/// </summary>
internal sealed class GpuGlassWindow : IGpuGlassConsumer, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly MonitorCapture _session;
    private readonly D3DImage _image = new();
    private readonly Dictionary<LiquidGlassBackdrop, Piece> _pieces = [];
    private readonly object _gpu = new();
    private bool _waiting;
    private long _attempts;
    private readonly List<Int32Rect> _pending = [];
    private readonly Dictionary<int, int> _drawn = [];
    private GpuGlassJob[] _jobs = [];
    private Int32Rect[] _areas = [];
    private IntPtr _picture, _pictureTarget, _texture9, _surface9;
    private int _pictureWidth, _pictureHeight;
    private IntPtr _madeOn;
    private volatile bool _active;
    private volatile bool _canDraw;
    private volatile bool _wants = true;
    private bool _locked;
    private bool _disposed;
    private int _nextId;
    private int _losses;
    private DateTime _firstLoss;

    private sealed class Piece
    {
        public int Id;
        public Int32Rect Box;
        public double Dpi;
        public float Brightness;
        public double Sigma;
        public double Scale;
        public (int W, int H, double Dpi, double Scale)? MapFor;
        public IntPtr Map, MapView;
        public int MapGeneration;
        public Int32Rect Target;
        public int Version;
    }

    public GpuGlassWindow(Dispatcher dispatcher, MonitorCapture session)
    {
        _dispatcher = dispatcher;
        _session = session;
        _image.IsFrontBufferAvailableChanged += OnFrontBuffer;
        _session.AddConsumer(this);
    }

    /// <summary>The window's picture, every piece's rectangle in it.</summary>
    public D3DImage Image => _image;

    /// <summary>True once the GPU path gave out on this window (three lost devices in a minute, or it couldn't start):
    /// the window takes the CPU path.</summary>
    public bool Failed { get; private set; }

    /// <summary>Raised on the UI thread when a piece's rectangle or the picture changed, and when <see cref="Failed"/>.</summary>
    public event Action? Changed;

    /// <summary>How many frames were shown, for the tests and the measurements.</summary>
    public long Frames => Interlocked.Read(ref _frames);

    private long _frames;

    public MonitorCapture Session => _session;

    /// <summary>The path's state in a line, for the tests and the measurements.</summary>
    internal string State => $"pieces {_pieces.Count}, with maps {_pieces.Values.Count(p => p.MapView != IntPtr.Zero)}, jobs {Volatile.Read(ref _jobs).Length}, " +
        $"picture {_pictureWidth}x{_pictureHeight}, locked {_locked}, relock attempts {_attempts}, can draw {_canDraw}, wants {_wants}, active {_active}, front buffer {_image.IsFrontBufferAvailable}, frames {Frames}, software copy {typeof(D3DImage).GetField("_softwareCopy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(_image) != null}";

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

    public bool WantsFrame => _wants && _canDraw;

    /// <summary>The rectangle of the window's picture that <paramref name="piece"/> shows, or null while it has none.</summary>
    public Int32Rect? TargetOf(LiquidGlassBackdrop piece)
        => _pieces.TryGetValue(piece, out var p) && p.Target.Width > 0 && p.MapView != IntPtr.Zero && _picture != IntPtr.Zero ? p.Target : null;

    /// <summary>Adds or updates <paramref name="piece"/>: its box on the screen (physical pixels), its display scale and
    /// the recipe's numbers. On the UI thread.</summary>
    public void Update(LiquidGlassBackdrop piece, Int32Rect box, double dpi, double brightness, double sigma, double scale)
    {
        if (_disposed) return;
        if (!_pieces.TryGetValue(piece, out var p))
        {
            p = new Piece { Id = ++_nextId };
            _pieces[piece] = p;
        }
        var resized = p.Box.Width != box.Width || p.Box.Height != box.Height;
        if (p.Box == box && p.Dpi == dpi && p.Brightness == (float)brightness && p.Sigma == sigma && p.Scale == scale) return;
        (p.Box, p.Dpi, p.Brightness, p.Sigma, p.Scale) = (box, dpi, (float)brightness, sigma, scale);
        p.Version++;
        MakeMap(p);
        if (resized) Pack();
        Publish();
    }

    public void Remove(LiquidGlassBackdrop piece)
    {
        if (!_pieces.Remove(piece, out var p)) return;
        lock (_gpu)
        {
            CaptureNative.Release(p.MapView);
            CaptureNative.Release(p.Map);
        }
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
        }
        if (_locked)
        {
            _image.Unlock();
            _locked = false;
        }
    }

    /// <summary>On the capture thread: draws the pieces a change touched (and any new, moved or resized), then hands
    /// the frame to the UI thread. While the UI hasn't the picture locked, changes wait.</summary>
    public void Frame(GpuGlassRenderer renderer, IReadOnlyList<Int32Rect>? changed)
    {
        lock (_gpu)
        {
            Answer(renderer);
            if (changed != null) _pending.AddRange(changed);
            if (!_canDraw || _picture == IntPtr.Zero || _disposed)
            {
                if (changed != null) _wants = true;
                return;
            }
            var drawn = new List<Int32Rect>();
            foreach (var job in Volatile.Read(ref _jobs))
            {
                var due = !_drawn.TryGetValue(job.Id, out var version) || version != job.Version || _pending.Any(c => Touches(c, job.Box));
                if (!due || job.Target.Width <= 0) continue;
                if (!renderer.Render(job, _pictureTarget)) continue;
                _drawn[job.Id] = job.Version;
                drawn.Add(job.Target);
            }
            _pending.Clear();
            _wants = false;
            if (drawn.Count == 0) return;
            renderer.Finish(_picture);
            _canDraw = false;
            Interlocked.Increment(ref _frames);
            LiquidGlassGovernor.Delivered();
            _dispatcher.BeginInvoke(DispatcherPriority.Render, () => Present(drawn));
        }
    }

    /// <summary>On the capture thread: the device went. The UI thread drops the picture and the maps and makes them
    /// again on the next device; three losses in a minute and the window takes the CPU path.</summary>
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
            Pack();
            Publish();
        });
    }

    /// <summary>Reads <paramref name="piece"/>'s drawn pixels back, BGRA rows, on the capture thread; for the tests.</summary>
    internal Task<byte[]> ReadAsync(LiquidGlassBackdrop piece)
    {
        var done = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pieces.TryGetValue(piece, out var p))
        {
            done.SetResult([]);
            return done.Task;
        }
        _readRequests.Enqueue((p.Target, done));
        _wants = true;
        _session.Wake();
        return done.Task;
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<(Int32Rect Target, TaskCompletionSource<byte[]> Done)> _readRequests = new();

    /// <summary>On the capture thread, under the GPU lock: answers the tests' reads.</summary>
    private void Answer(GpuGlassRenderer renderer)
    {
        while (_readRequests.TryDequeue(out var request)) request.Done.TrySetResult(_picture == IntPtr.Zero ? [] : renderer.Read(_picture, request.Target));
    }

    private static bool Touches(Int32Rect a, Int32Rect b)
        => a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    /// <summary>On the UI thread: the drawn rectangles to WPF, which copies them on its next frame; then locked again.</summary>
    private void Present(List<Int32Rect> drawn)
    {
        if (_disposed || !_locked) return;
        foreach (var r in drawn)
        {
            var clipped = Int32Rect.Empty;
            int right = Math.Min(r.X + r.Width, _pictureWidth), bottom = Math.Min(r.Y + r.Height, _pictureHeight);
            if (right > r.X && bottom > r.Y) clipped = new Int32Rect(r.X, r.Y, right - r.X, bottom - r.Y);
            if (!clipped.IsEmpty) _image.AddDirtyRect(clipped);
        }
        _image.Unlock();
        _locked = false;
        // Not now: WPF sends the copy as it commits its next frame, and a lock taken before that would cancel it. The
        // first frame WPF renders from here commits it; from the one after, the lock is tried.
        _skip = true;
        Wait(true);
    }

    private bool _skip;

    /// <summary>Takes the picture's lock back once WPF has copied the last frame; then the capture thread may draw. WPF
    /// copies as it renders its next frame, so until then each of its frames tries again (CompositionTarget.Rendering,
    /// listened to only while waiting).</summary>
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
        if (_canDraw) _session.Wake();
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
        // WPF's device is back: the picture again, and every piece drawn afresh.
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

    private void Want()
    {
        _wants = true;
        _session.Wake();
    }

    /// <summary>Publishes the pieces to the capture thread, as an array it reads without a lock.</summary>
    private void Publish()
    {
        var jobs = new List<GpuGlassJob>();
        foreach (var p in _pieces.Values)
        {
            if (p.MapView == IntPtr.Zero || p.Target.Width <= 0) continue;
            jobs.Add(new GpuGlassJob(p.Id, p.Box, p.MapView, p.Target, p.Brightness, p.Sigma, p.Version));
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

    /// <summary>Lays the pieces out on the window's picture, in rows (tallest first), and makes the picture again if it
    /// has grown too small.</summary>
    private void Pack()
    {
        var pieces = _pieces.Values.Where(p => p.Box.Width > 0 && p.Box.Height > 0).OrderByDescending(p => p.Box.Height).ToList();
        var width = Math.Clamp(NextPowerOfTwo(pieces.Count == 0 ? 1 : pieces.Max(p => p.Box.Width)), 1024, 8192);
        int x = 0, y = 0, row = 0;
        foreach (var p in pieces)
        {
            if (x + p.Box.Width > width)
            {
                (x, y, row) = (0, y + row, 0);
            }
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

    /// <summary>Makes the window's picture: a Direct3D 11 texture on the capture's device, shared, opened by Direct3D 9Ex
    /// as the D3DImage's surface. The old one goes once the capture thread isn't drawing on it.</summary>
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
            (_picture, _pictureTarget, _texture9, _surface9, _pictureWidth, _pictureHeight, _madeOn) = (picture, target, texture9, surface9, width, height, device);
            _drawn.Clear();
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
        _madeOn = IntPtr.Zero;
    }

    private static int NextPowerOfTwo(int v)
    {
        var p = 1;
        while (p < v) p <<= 1;
        return p;
    }
}
