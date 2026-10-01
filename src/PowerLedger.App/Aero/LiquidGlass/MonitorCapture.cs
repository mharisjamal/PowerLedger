using System.Diagnostics;
using System.Windows;
using static PowerLedger.App.Aero.CaptureNative;

namespace PowerLedger.App.Aero;

/// <summary>What the GPU path of one window asks of its monitor's capture (GpuGlassWindow), on the capture thread.</summary>
internal interface IGpuGlassConsumer
{
    /// <summary>False while the window is hidden or minimised: nothing is drawn for it.</summary>
    bool Active { get; }

    /// <summary>The boxes its pieces read, in physical pixels on the virtual screen: only a change there matters.</summary>
    IReadOnlyList<Int32Rect> Areas { get; }

    /// <summary>A piece is new, moved or resized, or the window's picture is new: draw even with nothing changed.</summary>
    bool WantsFrame { get; }

    /// <summary>The top-level window the pieces are in: a change wholly inside it may be its own repainting.</summary>
    IntPtr Window { get; }

    /// <summary>Draws the pieces that <paramref name="changed"/> (physical pixels on the virtual screen, compared: something
    /// behind really changed) touches, and any that want drawing. Null: nothing changed behind.</summary>
    void Frame(GpuGlassRenderer renderer, IReadOnlyList<Int32Rect>? changed);

    /// <summary>The device went (removed, reset): drop what was made on it, and make it again on the next one.</summary>
    void DeviceLost();
}

/// <summary>
/// One monitor's live picture for the liquid glass, from DXGI desktop duplication, shared by every window of ours on that
/// monitor (the Aero window and the watts overlay). Our windows are left out of capture (WDA_EXCLUDEFROMCAPTURE), so the
/// picture is what is behind them.
/// <para>One background thread per monitor waits for Windows to report a new frame (AcquireNextFrame, which blocks
/// until something on the monitor changes), at most as often as <see cref="LiquidGlassGovernor"/> allows (30 a second
/// within the CPU budget). A frame's dirty and moved rectangles are copied on the GPU into a copy of the whole desktop
/// kept there. On the GPU path (<see cref="IGpuGlassConsumer"/>) the part of them under a window's pieces is compared on
/// the GPU with the copy before, and the pieces are drawn on the GPU only if a pixel really differs: our own windows
/// repainting, which Windows still reports as dirty, cost one compare. Nothing is read back to the CPU but the compare's
/// one word. On the CPU path (<see cref="Subscriber"/>, the fallback) the part under a window is read back and compared on
/// the CPU. With no visible window the duplication is let go and the thread sleeps: nothing runs while every window with
/// glass is hidden or minimised. The device outlives a pause; it is made again only when Windows removes it.</para>
/// </summary>
internal sealed class MonitorCapture : IDisposable
{
    private const int DXGI_ERROR_MORE_DATA = unchecked((int)0x887A0003);

    private static readonly Dictionary<IntPtr, MonitorCapture> Sessions = [];

    private readonly IntPtr _monitor;
    private readonly List<Subscriber> _subscribers = [];
    private readonly List<IGpuGlassConsumer> _consumers = [];
    private readonly object _deviceGate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _disposed;
    private IntPtr _device, _context, _duplication, _desktop;
    private long _adapterLuid;
    private GpuGlassRenderer? _renderer;
    private int _desktopFormat = DXGI_FORMAT_B8G8R8A8_UNORM;
    private float _sdrWhite;
    private RECT _desktopBounds;
    private volatile int _needFull;
    private int _failures;
    private int _deviceResets;
    private RECT[] _dirty = new RECT[64];
    private DXGI_OUTDUPL_MOVE_RECT[] _moves = new DXGI_OUTDUPL_MOVE_RECT[16];
    private readonly List<RECT> _changed = [];
    private long _framesAcquired;

    private MonitorCapture(IntPtr monitor)
    {
        _monitor = monitor;
        _thread = new Thread(Run) { IsBackground = true, Name = "Liquid glass capture", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Bytes read back and compared on the CPU path, over every session, for the measurements.</summary>
    internal static long BytesCompared;

    /// <summary>Time the capture threads spent working (not waiting for Windows), over every session, in ticks.</summary>
    internal static long BusyTicks;

    /// <summary>True once duplication failed for good on this monitor (unsupported, rotated, no output): its windows use the
    /// wallpaper.</summary>
    public bool Failed { get; private set; }

    /// <summary>Raised on the capture thread when <see cref="Failed"/> becomes true.</summary>
    public event Action? FailedChanged;

    /// <summary>How many frames the thread has taken from Windows, for the tests and the measurements.</summary>
    public long FramesAcquired => Interlocked.Read(ref _framesAcquired);

    /// <summary>Whether the duplication is open now (false while every window is hidden).</summary>
    public bool Running => _duplication != IntPtr.Zero;

    /// <summary>The desktop comes as scRGB half floats (an HDR monitor): the CPU path can't read it.</summary>
    public bool Hdr => _desktopFormat != DXGI_FORMAT_B8G8R8A8_UNORM;

    /// <summary>How many times the device was made again after Windows removed it.</summary>
    public int DeviceResets => _deviceResets;

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

    /// <summary>The Direct3D 11 device the monitor is duplicated on (its adapter's), made now if need be, with its
    /// adapter's LUID; a zero device when there is none to make. Device calls are free-threaded: a window makes its
    /// textures on it from the UI thread. Only this session's thread uses its immediate context.</summary>
    public (IntPtr Device, long AdapterLuid) Device()
    {
        lock (_deviceGate)
        {
            if (_device == IntPtr.Zero) MakeDevice();
            return (_device, _adapterLuid);
        }
    }

    /// <summary>A subscriber for a region of the virtual screen (the CPU path); <paramref name="delivered"/> runs on the
    /// capture thread when its pixels changed.</summary>
    public Subscriber Subscribe(Action<Subscriber> delivered)
    {
        var subscriber = new Subscriber(this, delivered);
        lock (_subscribers) _subscribers.Add(subscriber);
        return subscriber;
    }

    public void AddConsumer(IGpuGlassConsumer consumer)
    {
        lock (_subscribers) _consumers.Add(consumer);
        _wake.Set();
    }

    public void RemoveConsumer(IGpuGlassConsumer consumer)
    {
        lock (_subscribers) _consumers.Remove(consumer);
        Left();
    }

    internal void Unsubscribe(Subscriber subscriber)
    {
        lock (_subscribers) _subscribers.Remove(subscriber);
        Left();
    }

    internal void Wake() => _wake.Set();

    /// <summary>Copies the whole desktop again on the next two frames: a window was just left out of capture, and
    /// Windows may hand over a frame or two drawn before that took hold, the window in it, which no dirty rectangle
    /// would ever take out of the copy.</summary>
    public void RefreshAll()
    {
        _needFull = 2;
        Interlocked.Exchange(ref _fullUntil, Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2);
        _wake.Set();
    }

    /// <summary>Until then, every frame copies the whole desktop (half a second after <see cref="RefreshAll"/>).</summary>
    private long _fullUntil;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wake.Set();
    }

    /// <summary>The last window left: the session goes.</summary>
    private void Left()
    {
        bool empty;
        lock (_subscribers) empty = _subscribers.Count == 0 && _consumers.Count == 0;
        if (!empty)
        {
            _wake.Set();
            return;
        }
        lock (Sessions)
        {
            if (Sessions.TryGetValue(_monitor, out var session) && session == this) Sessions.Remove(_monitor);
        }
        Dispose();
    }

    private readonly List<Subscriber> _activeSubscribers = [];
    private readonly List<IGpuGlassConsumer> _activeConsumers = [];

    /// <summary>The visible subscribers and consumers now, in lists the capture thread reuses.</summary>
    private (List<Subscriber> Subscribers, List<IGpuGlassConsumer> Consumers) Active()
    {
        _activeSubscribers.Clear();
        _activeConsumers.Clear();
        lock (_subscribers)
        {
            foreach (var s in _subscribers)
            {
                if (s.Active && s.Region.Width > 0 && s.Region.Height > 0) _activeSubscribers.Add(s);
            }
            foreach (var c in _consumers)
            {
                if (c.Active) _activeConsumers.Add(c);
            }
        }
        return (_activeSubscribers, _activeConsumers);
    }

    /// <summary>The capture threads' Windows ids, for the measurements.</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentBag<int> ThreadIds = [];

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    /// <summary>The pace while frames draw nothing: Windows reports our own window's repainting (a reading rolling in)
    /// as a change under the glass, which a compare then finds is none.</summary>
    internal static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(100);

    private bool _quiet;

    private void Run()
    {
        ThreadIds.Add(GetCurrentThreadId());
        var clock = Stopwatch.StartNew();
        var last = TimeSpan.Zero - LiquidGlassGovernor.Fastest;
        try
        {
            while (!_disposed)
            {
                var (subscribers, consumers) = Active();
                if ((subscribers.Count == 0 && consumers.Count == 0) || Failed)
                {
                    CloseDuplication();
                    _wake.WaitOne();
                    continue;
                }
                if (_duplication == IntPtr.Zero && !Open())
                {
                    _wake.WaitOne(TimeSpan.FromSeconds(1));   // the secure desktop, a mode change: try again shortly
                    continue;
                }
                // A frame that drew nothing (our own window repainting, a change away from the glass) slows the next
                // look to a tenth of a second; the first frame that draws brings back the governor's pace.
                var interval = _quiet && LiquidGlassGovernor.Interval < Quiet ? Quiet : LiquidGlassGovernor.Interval;
                var wait = last + interval - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    _wake.WaitOne(wait);
                    continue;
                }
                var hr = AcquireNextFrame(_duplication, (uint)LiquidGlassGovernor.Fastest.TotalMilliseconds, out var info, out var resource);
                if (hr == DXGI_ERROR_WAIT_TIMEOUT)
                {
                    Service(subscribers, consumers, null);
                    LiquidGlassGovernor.Tick();
                    continue;
                }
                if (hr < 0)
                {
                    Lost(hr);
                    continue;
                }
                Interlocked.Increment(ref _framesAcquired);
                var busy = Stopwatch.GetTimestamp();
                RECT? changed;
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
                Service(subscribers, consumers, changed);
                LiquidGlassGovernor.Tick();
                Interlocked.Add(ref BusyTicks, Stopwatch.GetTimestamp() - busy);
            }
        }
        finally
        {
            CloseDevice();
            _wake.Dispose();
        }
    }

    /// <summary>Copies the frame's changes into the desktop copy, keeping them in <see cref="_changed"/>; returns their
    /// bounds on the monitor, or null for none.</summary>
    private RECT? Take(DXGI_OUTDUPL_FRAME_INFO info, IntPtr resource)
    {
        _changed.Clear();
        if (_needFull == 0 && Stopwatch.GetTimestamp() < Interlocked.Read(ref _fullUntil)) _needFull = 1;
        if (info.LastPresentTime == 0 && _needFull == 0) return null;   // only the pointer moved
        if (QueryInterface(resource, IID_ID3D11Texture2D, out var texture) < 0) return null;
        try
        {
            int width = _desktopBounds.Right - _desktopBounds.Left, height = _desktopBounds.Bottom - _desktopBounds.Top;
            if (_needFull > 0)
            {
                _needFull--;
                CopySubresourceRegion(_context, _desktop, 0, 0, texture, new D3D11_BOX { Right = (uint)width, Bottom = (uint)height, Back = 1 });
                var all = new RECT { Right = width, Bottom = height };
                _changed.Add(all);
                return all;
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
        _changed.Add(r);
        bounds.Left = Math.Min(bounds.Left, r.Left);
        bounds.Top = Math.Min(bounds.Top, r.Top);
        bounds.Right = Math.Max(bounds.Right, r.Right);
        bounds.Bottom = Math.Max(bounds.Bottom, r.Bottom);
    }

    private void Service(List<Subscriber> subscribers, List<IGpuGlassConsumer> consumers, RECT? changed)
    {
        if (!Hdr) ServiceCpu(subscribers, changed);
        ServiceGpu(consumers, changed != null);
    }

    /// <summary>The GPU path: which consumers' pieces lie under a real change (compared on the GPU), then each draws.</summary>
    private void ServiceGpu(List<IGpuGlassConsumer> consumers, bool changed)
    {
        if (consumers.Count == 0 || _renderer == null)
        {
            if (changed) _renderer?.Remember(_changed);
            return;
        }
        var touched = new List<Int32Rect>?[consumers.Count];
        if (changed)
        {
            var areas = new List<(int Consumer, RECT Rect)>();
            for (var c = 0; c < consumers.Count; c++)
            {
                foreach (var area in consumers[c].Areas)
                {
                    var box = new RECT
                    {
                        Left = area.X - _desktopBounds.Left, Top = area.Y - _desktopBounds.Top,
                        Right = area.X + area.Width - _desktopBounds.Left, Bottom = area.Y + area.Height - _desktopBounds.Top,
                    };
                    foreach (var dirty in _changed)
                    {
                        var r = new RECT { Left = Math.Max(box.Left, dirty.Left), Top = Math.Max(box.Top, dirty.Top), Right = Math.Min(box.Right, dirty.Right), Bottom = Math.Min(box.Bottom, dirty.Bottom) };
                        if (r.Right > r.Left && r.Bottom > r.Top) areas.Add((c, r));
                    }
                }
            }
            // A change that reaches past every window of ours is something else's (a video playing behind): it is real,
            // with no compare and no wait for one. Only a change wholly inside a window of ours may be that window
            // repainting itself, which the compare tells from a change behind it.
            var differs = areas.Count == 0 ? 0u : Ours(consumers, areas) ? _renderer.Differs(areas) : Touched(areas);
            _quiet = differs == 0;
            _renderer.Remember(_changed);
            for (var c = 0; c < consumers.Count; c++)
            {
                if ((differs & (1u << Math.Min(c, 31))) == 0) continue;
                touched[c] = areas.Where(a => a.Consumer == c)
                    .Select(a => new Int32Rect(a.Rect.Left + _desktopBounds.Left, a.Rect.Top + _desktopBounds.Top, a.Rect.Right - a.Rect.Left, a.Rect.Bottom - a.Rect.Top)).ToList();
            }
        }
        for (var c = 0; c < consumers.Count; c++)
        {
            if (touched[c] == null && !consumers[c].WantsFrame) continue;
            consumers[c].Frame(_renderer, touched[c]);
        }
    }

    /// <summary>Whether every changed rectangle under the glass lies wholly inside one of our windows (desktop pixels).</summary>
    private bool Ours(List<IGpuGlassConsumer> consumers, List<(int Consumer, RECT Rect)> areas)
    {
        _windows.Clear();
        foreach (var consumer in consumers)
        {
            if (CaptureNative.GetWindowRect(consumer.Window, out var w))
            {
                _windows.Add(new RECT { Left = w.Left - _desktopBounds.Left, Top = w.Top - _desktopBounds.Top, Right = w.Right - _desktopBounds.Left, Bottom = w.Bottom - _desktopBounds.Top });
            }
        }
        foreach (var dirty in _changed)
        {
            var under = false;
            foreach (var (_, area) in areas)
            {
                if (area.Left < dirty.Right && dirty.Left < area.Right && area.Top < dirty.Bottom && dirty.Top < area.Bottom) under = true;
            }
            if (!under) continue;
            var inside = false;
            foreach (var w in _windows)
            {
                if (dirty.Left >= w.Left && dirty.Top >= w.Top && dirty.Right <= w.Right && dirty.Bottom <= w.Bottom) inside = true;
            }
            if (!inside) return false;
        }
        return true;
    }

    private readonly List<RECT> _windows = [];

    /// <summary>Every consumer a changed rectangle reached, as the compare's bits.</summary>
    private static uint Touched(List<(int Consumer, RECT Rect)> areas)
    {
        uint bits = 0;
        foreach (var (consumer, _) in areas) bits |= 1u << Math.Min(consumer, 31);
        return bits;
    }

    /// <summary>The CPU path: reads back what each subscriber needs, the changed part of its region, or all of it after it
    /// moved.</summary>
    private void ServiceCpu(List<Subscriber> active, RECT? changed)
    {
        foreach (var subscriber in active)
        {
            var (region, full) = subscriber.Snapshot();
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

    /// <summary>A failed call: the device removed (made again, and every window's GPU resources with it), or the
    /// duplication lost (a mode change, the secure desktop): opened again.</summary>
    private void Lost(int hr)
    {
        var removed = hr is DXGI_ERROR_DEVICE_REMOVED or DXGI_ERROR_DEVICE_RESET_ || (_device != IntPtr.Zero && GpuNative.GetDeviceRemovedReason(_device) < 0);
        if (removed) ResetDevice();
        else CloseDuplication();
    }

    private const int DXGI_ERROR_DEVICE_RESET_ = unchecked((int)0x887A0007);

    /// <summary>The device went: every consumer drops what it made on it, and the next frame makes a new one.</summary>
    internal void ResetDevice()
    {
        CloseDevice();
        _deviceResets++;
        IGpuGlassConsumer[] consumers;
        lock (_subscribers) consumers = [.. _consumers];
        foreach (var consumer in consumers) consumer.DeviceLost();
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
            lock (_deviceGate)
            {
                if (_device == IntPtr.Zero) MakeDevice(adapter);
            }
            if (_device == IntPtr.Zero) return Retry();
            IntPtr duplication;
            int hr;
            // An HDR monitor's desktop is scRGB half floats, which only DuplicateOutput1 hands over (the renderer brings it
            // to 8 bit sRGB as the browser would show it); DuplicateOutput would fail there. DuplicateOutput1 wants the
            // process per-monitor aware (v2), as the app is; elsewhere the older call.
            if (QueryInterface(output, GpuNative.IID_IDXGIOutput5, out var output5) >= 0)
            {
                hr = GpuNative.DuplicateOutput1(output5, _device, [DXGI_FORMAT_B8G8R8A8_UNORM, GpuNative.DXGI_FORMAT_R16G16B16A16_FLOAT], out duplication);
                CaptureNative.Release(output5);
                if (hr < 0) hr = DuplicateVia1(output, out duplication);
            }
            else
            {
                hr = DuplicateVia1(output, out duplication);
            }
            if (hr == DXGI_ERROR_UNSUPPORTED)
            {
                Fail();
                return false;
            }
            if (hr < 0) return Retry();
            _duplication = duplication;
            GpuNative.GetDuplicationDesc(_duplication, out var dd);
            var format = dd.Format == GpuNative.DXGI_FORMAT_R16G16B16A16_FLOAT ? GpuNative.DXGI_FORMAT_R16G16B16A16_FLOAT : DXGI_FORMAT_B8G8R8A8_UNORM;
            var bounds = desc.DesktopCoordinates;
            if (_desktop == IntPtr.Zero || format != _desktopFormat || !Same(bounds, _desktopBounds))
            {
                CaptureNative.Release(_desktop);
                _desktop = IntPtr.Zero;
                var texture = new D3D11_TEXTURE2D_DESC
                {
                    Width = (uint)(bounds.Right - bounds.Left), Height = (uint)(bounds.Bottom - bounds.Top), MipLevels = 1, ArraySize = 1,
                    Format = format, SampleCount = 1, Usage = D3D11_USAGE_DEFAULT, BindFlags = GpuNative.D3D11_BIND_SHADER_RESOURCE,
                };
                if (CaptureNative.CreateTexture2D(_device, texture, out _desktop) < 0) return Retry();
                _desktopFormat = format;
                _desktopBounds = bounds;
                _sdrWhite = format == DXGI_FORMAT_B8G8R8A8_UNORM ? 0 : SdrWhite.Of(desc.DeviceName);
                _renderer?.Dispose();
                _renderer = GpuGlassRenderer.Make(_device, _context, _desktop, (int)texture.Width, (int)texture.Height, format, _sdrWhite, _desktopBounds);
            }
            // The whole desktop on the first two frames: a window just left out of capture can still be in the first.
            _needFull = 2;
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

    private int DuplicateVia1(IntPtr output, out IntPtr duplication)
    {
        duplication = IntPtr.Zero;
        if (QueryInterface(output, IID_IDXGIOutput1, out var output1) < 0) return DXGI_ERROR_UNSUPPORTED;   // before Windows 8
        var hr = DuplicateOutput(output1, _device, out duplication);
        CaptureNative.Release(output1);
        return hr;
    }

    private static bool Same(RECT a, RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    /// <summary>Makes the device on <paramref name="adapter"/>, or on the monitor's own when none is given. Under the
    /// device lock.</summary>
    private void MakeDevice(IntPtr adapter = default)
    {
        var own = IntPtr.Zero;
        if (adapter == IntPtr.Zero)
        {
            if (!FindOutput(out own, out var output, out _)) return;
            CaptureNative.Release(output);
            adapter = own;
        }
        try
        {
            if (D3D11CreateDevice(adapter, 0, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION, out _device, out _, out _context) < 0)
            {
                _device = _context = IntPtr.Zero;
                return;
            }
            _adapterLuid = GpuNative.GetAdapterDesc(adapter, out var ad) >= 0 ? ((long)ad.LuidHigh << 32) | ad.LuidLow : 0;
        }
        finally
        {
            CaptureNative.Release(own);
        }
    }

    /// <summary>A failure that may pass (the secure desktop, a remote session starting): three in a row count as for good.</summary>
    private bool Retry()
    {
        CloseDuplication();
        if (++_failures >= 3) Fail();
        return false;
    }

    private void Fail()
    {
        CloseDuplication();
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

    /// <summary>Lets go of the duplication only: a pause. The device and its textures stay for the next frame.</summary>
    private void CloseDuplication()
    {
        CaptureNative.Release(_duplication);
        _duplication = IntPtr.Zero;
        lock (_subscribers)
        {
            foreach (var subscriber in _subscribers) subscriber.DropTextures();
        }
    }

    private void CloseDevice()
    {
        CloseDuplication();
        lock (_deviceGate)
        {
            _renderer?.Dispose();
            _renderer = null;
            CaptureNative.Release(_desktop);
            CaptureNative.Release(_context);
            CaptureNative.Release(_device);
            _desktop = _context = _device = IntPtr.Zero;
        }
    }

    /// <summary>One window's share of a monitor's picture on the CPU path: its region, and the pixels last read there.</summary>
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
        /// any changed. <paramref name="origin"/> is the region's top left on the texture. A few rows spread over the part
        /// are looked at first: one that differs (a video) means the whole part is copied without comparing it.</summary>
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
                        ReadOnlySpan<byte> From(int row) => new((byte*)mapped.Data + (long)row * mapped.RowPitch + dx * 4, part.Width * 4);
                        Span<byte> To(int row) => _pixels.AsSpan(row * stride + dx * 4, part.Width * 4);
                        var copyAll = moved;
                        for (var probe = 0; probe < 8 && !copyAll; probe++)
                        {
                            var row = dy + (part.Height - 1) * probe / 7;
                            Interlocked.Add(ref BytesCompared, part.Width * 4);
                            copyAll = !From(row).SequenceEqual(To(row));
                        }
                        for (var row = dy; row < dy + part.Height; row++)
                        {
                            if (copyAll)
                            {
                                From(row).CopyTo(To(row));
                                continue;
                            }
                            Interlocked.Add(ref BytesCompared, part.Width * 4);
                            if (From(row).SequenceEqual(To(row))) continue;
                            From(row).CopyTo(To(row));
                            changed = true;
                        }
                        changed |= copyAll;
                    }
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
            LiquidGlassGovernor.Delivered();
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
