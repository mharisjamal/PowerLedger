using System.Diagnostics;
using System.Windows;
using static PowerLedger.App.Aero.CaptureNative;

namespace PowerLedger.App.Aero;

/// <summary>
/// One monitor's live picture for the liquid glass, from DXGI desktop duplication, shared by every window of ours on that
/// monitor (the Aero window and the watts overlay each <see cref="Subscribe"/>). Our windows are left out of capture
/// (WDA_EXCLUDEFROMCAPTURE), so the picture is what is behind them.
/// <para>One background thread per monitor waits for Windows to report a new frame (AcquireNextFrame, which blocks
/// until something on the monitor changes), at most 30 a second. A frame's dirty and moved rectangles are copied on the
/// GPU into a copy of the whole desktop kept there; only the part of them inside a subscriber's region is read back to
/// the CPU, compared with what the subscriber already has, and handed over only if a pixel differs, so our own windows
/// repainting (which Windows still reports as dirty) cost a compare and nothing more. With no visible subscriber the
/// duplication is let go and the thread sleeps: nothing runs while every window with glass is hidden or minimised.</para>
/// </summary>
internal sealed class MonitorCapture : IDisposable
{
    /// <summary>The fastest the glass follows the screen: 30 frames a second.</summary>
    public static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(1.0 / 30);

    private static readonly Dictionary<IntPtr, MonitorCapture> Sessions = [];

    private readonly IntPtr _monitor;
    private readonly List<Subscriber> _subscribers = [];
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _disposed;
    private IntPtr _device, _context, _duplication, _desktop;
    private RECT _desktopBounds;
    private bool _needFull;
    private int _failures;
    private RECT[] _dirty = new RECT[64];
    private DXGI_OUTDUPL_MOVE_RECT[] _moves = new DXGI_OUTDUPL_MOVE_RECT[16];

    private MonitorCapture(IntPtr monitor)
    {
        _monitor = monitor;
        _thread = new Thread(Run) { IsBackground = true, Name = "Liquid glass capture", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>True once duplication failed for good on this monitor (unsupported, rotated, no output): its windows use the
    /// wallpaper.</summary>
    public bool Failed { get; private set; }

    /// <summary>Raised on the capture thread when <see cref="Failed"/> becomes true.</summary>
    public event Action? FailedChanged;

    /// <summary>How many frames the thread has taken from Windows, for the tests and the measurements.</summary>
    public long FramesAcquired => Interlocked.Read(ref _framesAcquired);

    private long _framesAcquired;

    /// <summary>Whether the duplication is open now (false while every subscriber is hidden).</summary>
    public bool Running => _duplication != IntPtr.Zero;

    /// <summary>The session for <paramref name="monitor"/>, made on first use.</summary>
    public static MonitorCapture For(IntPtr monitor)
    {
        lock (Sessions)
        {
            if (!Sessions.TryGetValue(monitor, out var session) || session._disposed)
            {
                session = new MonitorCapture(monitor);
                Sessions[monitor] = session;
            }
            return session;
        }
    }

    /// <summary>A subscriber for a region of the virtual screen; <paramref name="delivered"/> runs on the capture thread
    /// when its pixels changed.</summary>
    public Subscriber Subscribe(Action<Subscriber> delivered)
    {
        var subscriber = new Subscriber(this, delivered);
        lock (_subscribers) _subscribers.Add(subscriber);
        return subscriber;
    }

    internal void Unsubscribe(Subscriber subscriber)
    {
        bool empty;
        lock (_subscribers)
        {
            _subscribers.Remove(subscriber);
            empty = _subscribers.Count == 0;
        }
        if (empty)
        {
            lock (Sessions)
            {
                if (Sessions.TryGetValue(_monitor, out var session) && session == this) Sessions.Remove(_monitor);
            }
            Dispose();
        }
        else
        {
            _wake.Set();
        }
    }

    internal void Wake() => _wake.Set();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wake.Set();
    }

    private Subscriber[] Active()
    {
        lock (_subscribers) return _subscribers.Where(s => s.Active && s.Region.Width > 0 && s.Region.Height > 0).ToArray();
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        var last = TimeSpan.Zero - FrameInterval;
        try
        {
            while (!_disposed)
            {
                var active = Active();
                if (active.Length == 0 || Failed)
                {
                    Close();
                    _wake.WaitOne();
                    continue;
                }
                if (_duplication == IntPtr.Zero && !Open())
                {
                    _wake.WaitOne(TimeSpan.FromSeconds(1));   // the secure desktop, a mode change: try again shortly
                    continue;
                }
                var wait = last + FrameInterval - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    _wake.WaitOne(wait);
                    continue;
                }
                var hr = AcquireNextFrame(_duplication, (uint)FrameInterval.TotalMilliseconds, out var info, out var resource);
                if (hr == DXGI_ERROR_WAIT_TIMEOUT)
                {
                    Service(active, null);
                    continue;
                }
                if (hr < 0)
                {
                    Close();   // access lost (a mode change, the secure desktop), or the device went: open again
                    continue;
                }
                Interlocked.Increment(ref _framesAcquired);
                RECT? changed = null;
                try
                {
                    changed = Take(info, resource);
                }
                finally
                {
                    Release(resource);
                    ReleaseFrame(_duplication);
                }
                if (changed != null) last = clock.Elapsed;
                Service(active, changed);
            }
        }
        finally
        {
            Close();
            _wake.Dispose();
        }
    }

    /// <summary>Copies the frame's changes into the desktop copy; returns their bounds on the monitor, or null for none.</summary>
    private RECT? Take(DXGI_OUTDUPL_FRAME_INFO info, IntPtr resource)
    {
        if (info.LastPresentTime == 0 && !_needFull) return null;   // only the pointer moved
        if (QueryInterface(resource, IID_ID3D11Texture2D, out var texture) < 0) return null;
        try
        {
            int width = _desktopBounds.Right - _desktopBounds.Left, height = _desktopBounds.Bottom - _desktopBounds.Top;
            if (_needFull)
            {
                _needFull = false;
                CopySubresourceRegion(_context, _desktop, 0, 0, texture, new D3D11_BOX { Right = (uint)width, Bottom = (uint)height, Back = 1 });
                return new RECT { Right = width, Bottom = height };
            }
            if (info.TotalMetadataBufferSize == 0) return null;
            var bounds = new RECT { Left = int.MaxValue, Top = int.MaxValue, Right = int.MinValue, Bottom = int.MinValue };
            int moves, dirty;
            while (GetFrameMoveRects(_duplication, _moves, out moves) == DXGI_ERROR_MORE_DATA) _moves = new DXGI_OUTDUPL_MOVE_RECT[moves + 8];
            while (GetFrameDirtyRects(_duplication, _dirty, out dirty) == DXGI_ERROR_MORE_DATA) _dirty = new RECT[dirty + 16];
            for (var i = 0; i < Math.Min(moves, _moves.Length); i++) Copy(texture, _moves[i].Destination, ref bounds);
            for (var i = 0; i < Math.Min(dirty, _dirty.Length); i++) Copy(texture, _dirty[i], ref bounds);
            return bounds.Right > bounds.Left ? bounds : null;
        }
        finally
        {
            CaptureNative.Release(texture);
        }
    }

    private const int DXGI_ERROR_MORE_DATA = unchecked((int)0x887A0003);

    private void Copy(IntPtr texture, RECT r, ref RECT bounds)
    {
        int width = _desktopBounds.Right - _desktopBounds.Left, height = _desktopBounds.Bottom - _desktopBounds.Top;
        r.Left = Math.Clamp(r.Left, 0, width);
        r.Right = Math.Clamp(r.Right, 0, width);
        r.Top = Math.Clamp(r.Top, 0, height);
        r.Bottom = Math.Clamp(r.Bottom, 0, height);
        if (r.Right <= r.Left || r.Bottom <= r.Top) return;
        // A moved rectangle's destination is already in the new frame's image: copying it as dirty is exact.
        CopySubresourceRegion(_context, _desktop, r.Left, r.Top, texture, new D3D11_BOX { Left = (uint)r.Left, Top = (uint)r.Top, Right = (uint)r.Right, Bottom = (uint)r.Bottom, Back = 1 });
        bounds.Left = Math.Min(bounds.Left, r.Left);
        bounds.Top = Math.Min(bounds.Top, r.Top);
        bounds.Right = Math.Max(bounds.Right, r.Right);
        bounds.Bottom = Math.Max(bounds.Bottom, r.Bottom);
    }

    /// <summary>Reads back what each subscriber needs: the changed part of its region, or all of it after it moved.</summary>
    private void Service(Subscriber[] active, RECT? changed)
    {
        foreach (var subscriber in active)
        {
            var (region, full) = subscriber.Snapshot();
            // The region on this monitor's texture.
            var local = new Int32Rect(region.X - _desktopBounds.Left, region.Y - _desktopBounds.Top, region.Width, region.Height);
            int width = _desktopBounds.Right - _desktopBounds.Left, height = _desktopBounds.Bottom - _desktopBounds.Top;
            int left = Math.Max(0, local.X), top = Math.Max(0, local.Y);
            int right = Math.Min(width, local.X + local.Width), bottom = Math.Min(height, local.Y + local.Height);
            if (right <= left || bottom <= top) continue;
            int x0, y0, x1, y1;
            if (full)
            {
                (x0, y0, x1, y1) = (left, top, right, bottom);
            }
            else if (changed is { } c)
            {
                (x0, y0, x1, y1) = (Math.Max(left, c.Left), Math.Max(top, c.Top), Math.Min(right, c.Right), Math.Min(bottom, c.Bottom));
                if (x1 <= x0 || y1 <= y0) continue;
            }
            else
            {
                continue;
            }
            subscriber.ReadBack(_device, _context, _desktop, region, new Int32Rect(x0, y0, x1 - x0, y1 - y0), new Int32Rect(local.X, local.Y, 0, 0));
        }
    }

    private bool Open()
    {
        if (!FindOutput(out var adapter, out var output, out var desc))
        {
            Fail();
            return false;
        }
        try
        {
            if (desc.Rotation is not (DXGI_MODE_ROTATION_IDENTITY or DXGI_MODE_ROTATION_UNSPECIFIED))
            {
                Fail();   // a rotated monitor's frames come unrotated: the wallpaper instead
                return false;
            }
            if (D3D11CreateDevice(adapter, 0, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION, out _device, out _, out _context) < 0) return Retry();
            if (QueryInterface(output, IID_IDXGIOutput1, out var output1) < 0)
            {
                Fail();   // before Windows 8
                return false;
            }
            var hr = DuplicateOutput(output1, _device, out _duplication);
            CaptureNative.Release(output1);
            if (hr == DXGI_ERROR_UNSUPPORTED)
            {
                Fail();
                return false;
            }
            if (hr < 0) return Retry();
            _desktopBounds = desc.DesktopCoordinates;
            var texture = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)(_desktopBounds.Right - _desktopBounds.Left), Height = (uint)(_desktopBounds.Bottom - _desktopBounds.Top), MipLevels = 1, ArraySize = 1,
                Format = DXGI_FORMAT_B8G8R8A8_UNORM, SampleCount = 1, Usage = D3D11_USAGE_DEFAULT,
            };
            if (CreateTexture2D(_device, texture, out _desktop) < 0) return Retry();
            _needFull = true;
            _failures = 0;
            lock (_subscribers)
            {
                foreach (var subscriber in _subscribers) subscriber.RequestFullRefresh();
            }
            return true;
        }
        finally
        {
            CaptureNative.Release(output);
            CaptureNative.Release(adapter);
        }
    }

    /// <summary>A failure that may pass (the secure desktop, a remote session starting): three in a row count as for good.</summary>
    private bool Retry()
    {
        Close();
        if (++_failures >= 3) Fail();
        return false;
    }

    private void Fail()
    {
        Close();
        if (Failed) return;
        Failed = true;
        FailedChanged?.Invoke();
    }

    private bool FindOutput(out IntPtr adapter, out IntPtr output, out DXGI_OUTPUT_DESC desc)
    {
        adapter = output = IntPtr.Zero;
        desc = default;
        if (CreateDXGIFactory1(IID_IDXGIFactory1, out var factory) < 0) return false;
        try
        {
            for (uint a = 0; EnumAdapters(factory, a, out var candidate) >= 0; a++)
            {
                for (uint o = 0; EnumOutputs(candidate, o, out var screen) >= 0; o++)
                {
                    if (GetOutputDesc(screen, out var d) >= 0 && d.Monitor == _monitor)
                    {
                        adapter = candidate;
                        output = screen;
                        desc = d;
                        return true;
                    }
                    CaptureNative.Release(screen);
                }
                CaptureNative.Release(candidate);
            }
            return false;
        }
        finally
        {
            CaptureNative.Release(factory);
        }
    }

    private void Close()
    {
        lock (_subscribers)
        {
            foreach (var subscriber in _subscribers) subscriber.DropTextures();
        }
        CaptureNative.Release(_duplication);
        CaptureNative.Release(_desktop);
        CaptureNative.Release(_context);
        CaptureNative.Release(_device);
        _duplication = _desktop = _context = _device = IntPtr.Zero;
    }

    /// <summary>One window's share of a monitor's picture: its region, and the pixels last read there.</summary>
    internal sealed class Subscriber : IDisposable
    {
        private readonly MonitorCapture _session;
        private readonly Action<Subscriber> _delivered;
        private readonly object _gate = new();
        private Int32Rect _region;
        private Int32Rect _captured;
        private bool _full = true;
        private bool _active;
        private IntPtr _staging;
        private int _stagingWidth, _stagingHeight;
        private byte[] _pixels = [];
        private Int32Rect? _dirty;
        private long _deliveries;

        internal Subscriber(MonitorCapture session, Action<Subscriber> delivered)
        {
            _session = session;
            _delivered = delivered;
        }

        public MonitorCapture Session => _session;

        /// <summary>The part of the virtual screen wanted, in physical pixels.</summary>
        public Int32Rect Region
        {
            get
            {
                lock (_gate) return _region;
            }
            set
            {
                lock (_gate)
                {
                    if (_region == value) return;
                    _region = value;
                    _full = true;
                }
                _session.Wake();
            }
        }

        /// <summary>False while the window is hidden or minimised: nothing is read for it.</summary>
        public bool Active
        {
            get => _active;
            set
            {
                if (_active == value) return;
                _active = value;
                if (value) RequestFullRefresh();
                _session.Wake();
            }
        }

        /// <summary>How many times pixels were handed over.</summary>
        public long Deliveries => Interlocked.Read(ref _deliveries);

        internal void RequestFullRefresh()
        {
            lock (_gate) _full = true;
        }

        /// <summary>The region now and whether it wants reading whole (it moved, or the desktop copy is new), the
        /// latter cleared: a change after this sets it again.</summary>
        internal (Int32Rect Region, bool Full) Snapshot()
        {
            lock (_gate)
            {
                var full = _full;
                _full = false;
                return (_region, full);
            }
        }

        /// <summary>Hands the pixels to <paramref name="use"/> under the subscriber's lock, if any changed since the last
        /// take: the region they cover, the pixels (BGRA rows, the region's width) and the part that changed. The array
        /// is the subscriber's own: copy what is needed before returning. False when nothing changed.</summary>
        public bool Take(Action<Int32Rect, byte[], Int32Rect> use)
        {
            lock (_gate)
            {
                if (_dirty is not { } dirty) return false;
                _dirty = null;
                use(_captured, _pixels, dirty);
                return true;
            }
        }

        /// <summary>Reads <paramref name="part"/> (on the monitor's texture) back into the pixels, and hands them over if
        /// any changed. <paramref name="origin"/> is the region's top left on the texture.</summary>
        internal void ReadBack(IntPtr device, IntPtr context, IntPtr desktop, Int32Rect region, Int32Rect part, Int32Rect origin)
        {
            if (_staging == IntPtr.Zero || _stagingWidth != region.Width || _stagingHeight != region.Height)
            {
                CaptureNative.Release(_staging);
                _staging = IntPtr.Zero;
                var desc = new D3D11_TEXTURE2D_DESC
                {
                    Width = (uint)region.Width, Height = (uint)region.Height, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT_B8G8R8A8_UNORM,
                    SampleCount = 1, Usage = D3D11_USAGE_STAGING, CPUAccessFlags = D3D11_CPU_ACCESS_READ,
                };
                if (CreateTexture2D(device, desc, out _staging) < 0) return;
                (_stagingWidth, _stagingHeight) = (region.Width, region.Height);
            }
            int dx = part.X - origin.X, dy = part.Y - origin.Y;
            CopySubresourceRegion(context, _staging, dx, dy, desktop,
                new D3D11_BOX { Left = (uint)part.X, Top = (uint)part.Y, Right = (uint)(part.X + part.Width), Bottom = (uint)(part.Y + part.Height), Back = 1 });
            if (Map(context, _staging, out var mapped) < 0) return;
            var changed = false;
            try
            {
                lock (_gate)
                {
                    var resized = _captured.Width != region.Width || _captured.Height != region.Height || _pixels.Length != region.Width * region.Height * 4;
                    if (resized) _pixels = new byte[region.Width * region.Height * 4];
                    var moved = resized || _captured.X != region.X || _captured.Y != region.Y;
                    _captured = region;
                    var stride = region.Width * 4;
                    unsafe
                    {
                        for (var row = dy; row < dy + part.Height; row++)
                        {
                            var from = new ReadOnlySpan<byte>((byte*)mapped.Data + (long)row * mapped.RowPitch + dx * 4, part.Width * 4);
                            var to = _pixels.AsSpan(row * stride + dx * 4, part.Width * 4);
                            if (from.SequenceEqual(to)) continue;
                            from.CopyTo(to);
                            changed = true;
                        }
                    }
                    if (moved) changed = true;
                    if (changed)
                    {
                        var now = moved ? new Int32Rect(0, 0, region.Width, region.Height) : new Int32Rect(dx, dy, part.Width, part.Height);
                        _dirty = _dirty is { } before ? Union(before, now) : now;
                    }
                }
            }
            finally
            {
                Unmap(context, _staging);
            }
            if (!changed) return;
            Interlocked.Increment(ref _deliveries);
            _delivered(this);
        }

        internal void DropTextures()
        {
            CaptureNative.Release(_staging);
            _staging = IntPtr.Zero;
            RequestFullRefresh();
        }

        public void Dispose()
        {
            _active = false;
            _session.Unsubscribe(this);
        }

        private static Int32Rect Union(Int32Rect a, Int32Rect b)
        {
            int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
            return new Int32Rect(x, y, Math.Max(a.X + a.Width, b.X + b.Width) - x, Math.Max(a.Y + a.Height, b.Y + b.Height) - y);
        }
    }
}
