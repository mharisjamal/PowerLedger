using System.Runtime.InteropServices;
using System.Windows;
using static PowerLedger.App.Aero.CaptureNative;
using static PowerLedger.App.Aero.GpuNative;

namespace PowerLedger.App.Aero;

/// <summary>One piece to draw on the GPU path: where its box lies on the virtual screen (physical pixels), its source map,
/// where it goes on its window's picture, and the recipe's numbers at its display scale.</summary>
/// <summary>How a piece shows: <see cref="Shape"/> its rectangle as WPF shows it (a spring's scale), in the box's pixels,
/// its corners <see cref="Radii"/>; <see cref="Visible"/> what its ancestors' clips leave of it; <see cref="Opacity"/>
/// theirs. A whole, square, opaque piece is <see cref="Whole"/>.</summary>
internal readonly record struct GpuGlassShape(Rect Shape, CornerRadius Radii, Rect Visible, double Opacity)
{
    public static GpuGlassShape Whole(int width, int height) => new(new Rect(0, 0, width, height), default, new Rect(0, 0, width, height), 1);
}

internal sealed record GpuGlassJob(int Id, Int32Rect Box, IntPtr MapView, Int32Rect Target, float Brightness, double Sigma, GpuGlassShape Shape, bool Composed, int Version);

/// <summary>
/// The GPU path's drawing, on one Direct3D 11 device and only on its monitor's capture thread (the immediate context is
/// single threaded): the recipe's three passes (Shaders/GpuGlass.hlsl: Bright, Across, Down) over the duplicated desktop, and
/// the compare that tells a real change behind a window from the window's own repainting. The desktop copy before the
/// latest frame is kept for that compare, on the GPU.
/// </summary>
internal sealed unsafe class GpuGlassRenderer : IDisposable
{
    /// <summary>The most bilinear pairs a side the shaders' taps hold (Taps[16], two a register).</summary>
    public const int MaxPairs = 32;

    private static readonly Lazy<Dictionary<string, byte[]>> Code = new(() =>
        new[] { "Fullscreen", "Bright", "Across", "Down", "Compare" }.ToDictionary(n => n, n =>
        {
            using var stream = typeof(GpuGlassRenderer).Assembly.GetManifestResourceStream($"PowerLedger.App.Aero.LiquidGlass.Shaders.GpuGlass.{n}.cso")
                ?? throw new InvalidOperationException($"The {n} shader isn't embedded.");
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }));

    private readonly IntPtr _device, _context, _desktop;
    private readonly int _width, _height, _format;
    private readonly float _sdrWhite;
    private readonly CaptureNative.RECT _bounds;
    private IntPtr _vs, _bright, _across, _down, _compare, _glass, _area, _linear, _desktopView, _previous, _previousView, _differs, _differsView, _differsRead, _syncRead;
    private readonly Scratch _brightened = new(), _blurred = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct GlassConstants
    {
        public int BoxX, BoxY, SizeX, SizeY, OutX, OutY, Margin, Pairs, LimitX, LimitY;
        public float Brightness, SdrWhite, SourceWidth, SourceHeight, Centre, Pad;
        public float RadiusTopLeft, RadiusTopRight, RadiusBottomRight, RadiusBottomLeft;
        public float ShapeLeft, ShapeTop, ShapeRight, ShapeBottom;
        public float VisibleLeft, VisibleTop, VisibleRight, VisibleBottom;
        public float Opacity, Pad2, Pad3, Pad4;
        public fixed float Taps[64];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_SAMPLER_DESC
    {
        public int Filter, AddressU, AddressV, AddressW;
        public float MipLODBias;
        public uint MaxAnisotropy;
        public int ComparisonFunc;
        public float Border0, Border1, Border2, Border3, MinLOD, MaxLOD;
    }

    /// <summary>A render target the passes draw into and read from, grown as pieces need.</summary>
    private sealed class Scratch
    {
        public IntPtr Texture, Target, View;
        public int Width, Height;

        public bool Fit(IntPtr device, int width, int height)
        {
            if (Texture != IntPtr.Zero && Width >= width && Height >= height) return true;
            Release();
            int w = Math.Max(width, Width), h = Math.Max(height, Height);
            var desc = new CaptureNative.D3D11_TEXTURE2D_DESC
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT_B8G8R8A8_UNORM, SampleCount = 1, Usage = D3D11_USAGE_DEFAULT,
                BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE,
            };
            if (GpuNative.CreateTexture2D(device, desc, null, out Texture) < 0) return false;
            if (CreateRenderTargetView(device, Texture, out Target) < 0) return false;
            if (CreateShaderResourceView(device, Texture, out View) < 0) return false;
            (Width, Height) = (w, h);
            return true;
        }

        public void Release()
        {
            CaptureNative.Release(View);
            CaptureNative.Release(Target);
            CaptureNative.Release(Texture);
            View = Target = Texture = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AreaConstants
    {
        public int X, Y, Width, Height, Slot, Pad0, Pad1, Pad2;
    }

    private GpuGlassRenderer(IntPtr device, IntPtr context, IntPtr desktop, int width, int height, int format, float sdrWhite, CaptureNative.RECT bounds)
    {
        (_device, _context, _desktop, _width, _height, _format, _sdrWhite, _bounds) = (device, context, desktop, width, height, format, sdrWhite, bounds);
    }

    /// <summary>How many pieces have been drawn, for the tests and the measurements.</summary>
    internal static long PiecesDrawn;

    /// <summary>How many compares have run, and how many found a difference, for the tests and the measurements.</summary>
    internal static long Compares, Differences;

    /// <summary>The renderer for a desktop copy of <paramref name="width"/> by <paramref name="height"/>, or null when this
    /// device can't run it (feature level under 11_0, or a call failed): the window takes the CPU path.</summary>
    public static GpuGlassRenderer? Make(IntPtr device, IntPtr context, IntPtr desktop, int width, int height, int format, float sdrWhite, CaptureNative.RECT bounds)
    {
        if (GetFeatureLevel(device) < D3D_FEATURE_LEVEL_11_0) return null;
        var renderer = new GpuGlassRenderer(device, context, desktop, width, height, format, sdrWhite, bounds);
        if (renderer.Build()) return renderer;
        renderer.Dispose();
        return null;
    }

    /// <summary>Where the desktop copy lies on the virtual screen.</summary>
    public Int32Rect Bounds => new(_bounds.Left, _bounds.Top, _bounds.Right - _bounds.Left, _bounds.Bottom - _bounds.Top);

    private bool Build()
    {
        var code = Code.Value;
        if (CreateVertexShader(_device, code["Fullscreen"], out _vs) < 0) return false;
        if (CreatePixelShader(_device, code["Bright"], out _bright) < 0) return false;
        if (CreatePixelShader(_device, code["Across"], out _across) < 0) return false;
        // Bilinear and clamped, as Skia's GPU blur reads its pairs of texels.
        var linear = new D3D11_SAMPLER_DESC { Filter = 0x15, AddressU = 3, AddressV = 3, AddressW = 3, ComparisonFunc = 1, MaxLOD = float.MaxValue };
        IntPtr state;
        if (((delegate* unmanaged[Stdcall]<IntPtr, D3D11_SAMPLER_DESC*, IntPtr*, int>)(*(void***)_device)[23])(_device, &linear, &state) < 0) return false;   // CreateSamplerState
        _linear = state;
        if (CreatePixelShader(_device, code["Down"], out _down) < 0) return false;
        if (CreateComputeShader(_device, code["Compare"], out _compare) < 0) return false;
        if (CreateBuffer(_device, new D3D11_BUFFER_DESC { ByteWidth = (uint)sizeof(GlassConstants), BindFlags = D3D11_BIND_CONSTANT_BUFFER }, out _glass) < 0) return false;
        if (CreateBuffer(_device, new D3D11_BUFFER_DESC { ByteWidth = (uint)sizeof(AreaConstants), BindFlags = D3D11_BIND_CONSTANT_BUFFER }, out _area) < 0) return false;
        if (CreateShaderResourceView(_device, _desktop, out _desktopView) < 0) return false;
        var previous = new CaptureNative.D3D11_TEXTURE2D_DESC
        {
            Width = (uint)_width, Height = (uint)_height, MipLevels = 1, ArraySize = 1, Format = _format, SampleCount = 1, Usage = D3D11_USAGE_DEFAULT,
            BindFlags = D3D11_BIND_SHADER_RESOURCE,
        };
        if (GpuNative.CreateTexture2D(_device, previous, null, out _previous) < 0) return false;
        if (CreateShaderResourceView(_device, _previous, out _previousView) < 0) return false;
        // One word a window, raw, for the compare to set; and a staging copy to read it.
        var words = new D3D11_BUFFER_DESC { ByteWidth = 256, BindFlags = D3D11_BIND_UNORDERED_ACCESS, MiscFlags = D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS };
        if (CreateBuffer(_device, words, out _differs) < 0) return false;
        var raw = new D3D11_UNORDERED_ACCESS_VIEW_DESC { Format = DXGI_FORMAT_R32_TYPELESS, ViewDimension = D3D11_UAV_DIMENSION_BUFFER, NumElements = 64, Flags = D3D11_BUFFER_UAV_FLAG_RAW };
        if (CreateUnorderedAccessView(_device, _differs, raw, out _differsView) < 0) return false;
        if (CreateBuffer(_device, new D3D11_BUFFER_DESC { ByteWidth = 256, Usage = D3D11_USAGE_STAGING, CPUAccessFlags = D3D11_CPU_ACCESS_READ }, out _differsRead) < 0) return false;
        var one = new CaptureNative.D3D11_TEXTURE2D_DESC
        {
            Width = 1, Height = 1, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT_B8G8R8A8_UNORM, SampleCount = 1, Usage = D3D11_USAGE_STAGING,
            CPUAccessFlags = D3D11_CPU_ACCESS_READ,
        };
        return GpuNative.CreateTexture2D(_device, one, null, out _syncRead) >= 0;
    }

    /// <summary>Compares each rectangle (on the desktop copy) with the copy before it, on the GPU; bit n of the answer is
    /// set when any texel of consumer n's rectangles differs. One word is read back.</summary>
    public uint Differs(IReadOnlyList<(int Consumer, CaptureNative.RECT Rect)> areas)
    {
        Interlocked.Increment(ref Compares);
        ClearUnorderedAccessViewUint(_context, _differsView);
        CSSetShader(_context, _compare);
        CSSetShaderResources(_context, 0, _desktopView);
        CSSetShaderResources(_context, 1, _previousView);
        CSSetUnorderedAccessView(_context, _differsView);
        CSSetConstantBuffers(_context, 1, _area);
        foreach (var (consumer, r) in areas)
        {
            var constants = new AreaConstants { X = r.Left, Y = r.Top, Width = r.Right - r.Left, Height = r.Bottom - r.Top, Slot = Math.Min(consumer, 63) };
            UpdateSubresource(_context, _area, &constants, 0);
            Dispatch(_context, (uint)(constants.Width + 15) / 16, (uint)(constants.Height + 15) / 16);
        }
        CSSetUnorderedAccessView(_context, IntPtr.Zero);
        CSSetShaderResources(_context, 0, IntPtr.Zero);
        CSSetShaderResources(_context, 1, IntPtr.Zero);
        CopyResource(_context, _differsRead, _differs);
        if (Map(_context, _differsRead, out var mapped) < 0) return uint.MaxValue;
        uint bits = 0;
        var words = (uint*)mapped.Data;
        for (var i = 0; i < 32; i++)
        {
            if (words[i] != 0) bits |= 1u << i;
        }
        Unmap(_context, _differsRead);
        if (bits != 0) Interlocked.Increment(ref Differences);
        return bits;
    }

    /// <summary>Copies the frame's changed rectangles into the copy before, for the next compare.</summary>
    public void Remember(IReadOnlyList<CaptureNative.RECT> changed)
    {
        foreach (var r in changed)
        {
            CopySubresourceRegion(_context, _previous, r.Left, r.Top, _desktop,
                new D3D11_BOX { Left = (uint)r.Left, Top = (uint)r.Top, Right = (uint)r.Right, Bottom = (uint)r.Bottom, Back = 1 });
        }
    }

    /// <summary>Draws <paramref name="job"/>'s piece onto <paramref name="target"/> (a render target view of its window's
    /// picture) at its target rectangle: Bright and Across into scratch textures, then Down onto the target.</summary>
    public bool Render(GpuGlassJob job, IntPtr target)
    {
        int w = job.Target.Width, h = job.Target.Height;
        if (w <= 0 || h <= 0 || job.MapView == IntPtr.Zero) return false;
        var (centre, pairs) = LiquidGlassEffects.BlurKernel(job.Sigma);
        if (pairs.Length > MaxPairs) pairs = pairs[..MaxPairs];
        // The margin the pairs reach into: the farthest pair's two texels, and one more.
        var margin = 2 * pairs.Length + 1;
        if (!_brightened.Fit(_device, w + 2 * margin, h + 2 * margin) || !_blurred.Fit(_device, w, h + 2 * margin)) return false;
        var constants = new GlassConstants
        {
            BoxX = job.Box.X - _bounds.Left, BoxY = job.Box.Y - _bounds.Top, SizeX = w, SizeY = h, OutX = job.Target.X, OutY = job.Target.Y,
            Margin = margin, Pairs = pairs.Length, LimitX = _width, LimitY = _height, Brightness = job.Brightness, SdrWhite = _sdrWhite, Centre = (float)centre,
            RadiusTopLeft = (float)job.Shape.Radii.TopLeft, RadiusTopRight = (float)job.Shape.Radii.TopRight,
            RadiusBottomRight = (float)job.Shape.Radii.BottomRight, RadiusBottomLeft = (float)job.Shape.Radii.BottomLeft,
            ShapeLeft = (float)job.Shape.Shape.Left, ShapeTop = (float)job.Shape.Shape.Top, ShapeRight = (float)job.Shape.Shape.Right, ShapeBottom = (float)job.Shape.Shape.Bottom,
            VisibleLeft = (float)job.Shape.Visible.Left, VisibleTop = (float)job.Shape.Visible.Top, VisibleRight = (float)job.Shape.Visible.Right,
            VisibleBottom = (float)job.Shape.Visible.Bottom, Opacity = (float)job.Shape.Opacity,
        };
        for (var i = 0; i < pairs.Length; i++)
        {
            constants.Taps[2 * i] = (float)pairs[i].Offset;
            constants.Taps[2 * i + 1] = (float)pairs[i].Weight;
        }
        IASetPrimitiveTopology(_context, D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        VSSetShader(_context, _vs);
        var sampler = _linear;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)(*(void***)_context)[10])(_context, 0, 1, &sampler);   // PSSetSamplers
        // Bright: the box and its margin, mirrored, into the first scratch texture's top left.
        UpdateSubresource(_context, _glass, &constants, 0);
        PSSetConstantBuffers(_context, 0, _glass);
        OMSetRenderTargets(_context, _brightened.Target);
        RSSetViewport(_context, new D3D11_VIEWPORT { Width = w + 2 * margin, Height = h + 2 * margin, MaxDepth = 1 });
        PSSetShader(_context, _bright);
        PSSetShaderResources(_context, 0, _desktopView);
        Draw(_context, 3);
        // Across: the box's columns, every row of the margin too, into the second.
        OMSetRenderTargets(_context, _blurred.Target);
        (constants.SourceWidth, constants.SourceHeight) = (_brightened.Width, _brightened.Height);
        UpdateSubresource(_context, _glass, &constants, 0);
        RSSetViewport(_context, new D3D11_VIEWPORT { Width = w, Height = h + 2 * margin, MaxDepth = 1 });
        PSSetShader(_context, _across);
        PSSetShaderResources(_context, 1, _brightened.View);
        Draw(_context, 3);
        // Down, the displacement and the trip through linearRGB: onto the piece's rectangle of the window's picture.
        OMSetRenderTargets(_context, target);
        (constants.SourceWidth, constants.SourceHeight) = (_blurred.Width, _blurred.Height);
        UpdateSubresource(_context, _glass, &constants, 0);
        RSSetViewport(_context, new D3D11_VIEWPORT { TopLeftX = job.Target.X, TopLeftY = job.Target.Y, Width = w, Height = h, MaxDepth = 1 });
        PSSetShader(_context, _down);
        PSSetShaderResources(_context, 1, _blurred.View);
        PSSetShaderResources(_context, 2, job.MapView);
        Draw(_context, 3);
        PSSetShaderResources(_context, 1, IntPtr.Zero);
        PSSetShaderResources(_context, 2, IntPtr.Zero);
        OMSetRenderTargets(_context, IntPtr.Zero);
        Interlocked.Increment(ref PiecesDrawn);
        return true;
    }

    /// <summary>Clears a render target to transparent.</summary>
    public void Clear(IntPtr target)
    {
        var clear = stackalloc float[4];
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, float*, void>)(*(void***)_context)[50])(_context, target, clear);   // ClearRenderTargetView
    }

    /// <summary>Copies one whole texture onto another of its size.</summary>
    public void Copy(IntPtr destination, IntPtr source) => CopyResource(_context, destination, source);

    /// <summary>Waits until the GPU has finished everything asked of it so far (a texel of <paramref name="texture"/> read
    /// back after it), so another device (WPF's) reads finished pixels.</summary>
    public void Finish(IntPtr texture)
    {
        CopySubresourceRegion(_context, _syncRead, 0, 0, texture, new D3D11_BOX { Right = 1, Bottom = 1, Back = 1 });
        if (Map(_context, _syncRead, out _) >= 0) Unmap(_context, _syncRead);
    }

    /// <summary>Reads a rectangle of a BGRA texture back to the CPU, for the tests.</summary>
    public byte[] Read(IntPtr texture, Int32Rect rect)
    {
        var desc = new CaptureNative.D3D11_TEXTURE2D_DESC
        {
            Width = (uint)rect.Width, Height = (uint)rect.Height, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT_B8G8R8A8_UNORM, SampleCount = 1,
            Usage = D3D11_USAGE_STAGING, CPUAccessFlags = D3D11_CPU_ACCESS_READ,
        };
        if (GpuNative.CreateTexture2D(_device, desc, null, out var staging) < 0) return [];
        try
        {
            CopySubresourceRegion(_context, staging, 0, 0, texture,
                new D3D11_BOX { Left = (uint)rect.X, Top = (uint)rect.Y, Right = (uint)(rect.X + rect.Width), Bottom = (uint)(rect.Y + rect.Height), Back = 1 });
            if (Map(_context, staging, out var mapped) < 0) return [];
            var pixels = new byte[rect.Width * rect.Height * 4];
            for (var y = 0; y < rect.Height; y++) Marshal.Copy(mapped.Data + y * (int)mapped.RowPitch, pixels, y * rect.Width * 4, rect.Width * 4);
            Unmap(_context, staging);
            return pixels;
        }
        finally
        {
            CaptureNative.Release(staging);
        }
    }

    public void Dispose()
    {
        foreach (var p in new[] { _vs, _bright, _across, _down, _compare, _glass, _area, _linear, _desktopView, _previousView, _previous, _differsView, _differs, _differsRead, _syncRead })
        {
            CaptureNative.Release(p);
        }
        _vs = _bright = _across = _down = _compare = _glass = _area = _linear = _desktopView = _previousView = _previous = _differsView = _differs = _differsRead = _syncRead = IntPtr.Zero;
        _brightened.Release();
        _blurred.Release();
    }
}
