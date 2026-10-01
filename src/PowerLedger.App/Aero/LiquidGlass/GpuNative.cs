using System.Runtime.InteropServices;

namespace PowerLedger.App.Aero;

/// <summary>
/// The Direct3D 11, DXGI and Direct3D 9Ex calls the liquid glass's GPU path makes (GpuGlass.cs), through each
/// interface's vtable by slot in the order d3d11.h, dxgi.h, dxgi1_5.h and d3d9.h declare them. Direct3D 9Ex is only
/// the bridge WPF's D3DImage takes: it opens our Direct3D 11 texture, shared, as a surface WPF can copy on the GPU.
/// </summary>
internal static unsafe class GpuNative
{
    public const int D3D_FEATURE_LEVEL_11_0 = 0xb000;
    public const int D3D11_BIND_SHADER_RESOURCE = 0x8;
    public const int D3D11_BIND_RENDER_TARGET = 0x20;
    public const int D3D11_BIND_UNORDERED_ACCESS = 0x80;
    public const int D3D11_BIND_CONSTANT_BUFFER = 0x4;
    public const int D3D11_RESOURCE_MISC_SHARED = 0x2;
    public const int D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS = 0x20;
    public const int D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST = 4;
    public const int DXGI_FORMAT_R16G16B16A16_FLOAT = 10;
    public const int DXGI_FORMAT_R16G16_UINT = 36;
    public const int DXGI_FORMAT_R32_TYPELESS = 39;
    public const int DXGI_FORMAT_R32_UINT = 42;
    public const int D3D11_UAV_DIMENSION_BUFFER = 1;
    public const int D3D11_BUFFER_UAV_FLAG_RAW = 0x1;

    public const int D3DDEVTYPE_HAL = 1;
    public const int D3DCREATE_FPU_PRESERVE = 0x2;
    public const int D3DCREATE_MULTITHREADED = 0x4;
    public const int D3DCREATE_HARDWARE_VERTEXPROCESSING = 0x40;
    public const int D3DSWAPEFFECT_DISCARD = 1;
    public const int D3DUSAGE_RENDERTARGET = 1;
    public const int D3DFMT_A8R8G8B8 = 21;
    public const int D3DPOOL_DEFAULT = 0;
    public const int D3D_SDK_VERSION = 32;

    public static readonly Guid IID_IDXGIResource = new("035f3ab4-482e-4e50-b41f-8a7f8bd8960b");
    public static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    public static readonly Guid IID_IDXGIOutput5 = new("80a07424-ab52-42eb-833c-0c42fd282d98");

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_BUFFER_DESC
    {
        public uint ByteWidth;
        public int Usage;
        public uint BindFlags, CPUAccessFlags, MiscFlags, StructureByteStride;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_SUBRESOURCE_DATA
    {
        public IntPtr SysMem;
        public uint SysMemPitch, SysMemSlicePitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_VIEWPORT
    {
        public float TopLeftX, TopLeftY, Width, Height, MinDepth, MaxDepth;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_UNORDERED_ACCESS_VIEW_DESC
    {
        public int Format;
        public int ViewDimension;
        public uint FirstElement, NumElements, Flags, Padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_ADAPTER_DESC
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_OUTDUPL_DESC
    {
        public uint Width, Height, RefreshNumerator, RefreshDenominator;
        public int Format, ScanlineOrdering, Scaling;
        public int Rotation;
        public int DesktopImageInSystemMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3DPRESENT_PARAMETERS
    {
        public uint BackBufferWidth, BackBufferHeight;
        public int BackBufferFormat;
        public uint BackBufferCount;
        public int MultiSampleType;
        public uint MultiSampleQuality;
        public int SwapEffect;
        public IntPtr DeviceWindow;
        public int Windowed, EnableAutoDepthStencil, AutoDepthStencilFormat;
        public uint Flags, FullScreenRefreshRateInHz, PresentationInterval;
    }

    [DllImport("d3d9.dll")]
    public static extern int Direct3DCreate9Ex(uint sdkVersion, out IntPtr d3d9ex);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    private static void** V(IntPtr self) => *(void***)self;

    // ID3D11Device
    public static int CreateBuffer(IntPtr device, in D3D11_BUFFER_DESC desc, out IntPtr buffer)
    {
        fixed (D3D11_BUFFER_DESC* d = &desc)
        fixed (IntPtr* b = &buffer)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_BUFFER_DESC*, IntPtr, IntPtr*, int>)V(device)[3])(device, d, IntPtr.Zero, b);
        }
    }

    public static int CreateTexture2D(IntPtr device, in CaptureNative.D3D11_TEXTURE2D_DESC desc, D3D11_SUBRESOURCE_DATA* data, out IntPtr texture)
    {
        fixed (CaptureNative.D3D11_TEXTURE2D_DESC* d = &desc)
        fixed (IntPtr* t = &texture)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, CaptureNative.D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)V(device)[5])(device, d, data, t);
        }
    }

    public static int CreateShaderResourceView(IntPtr device, IntPtr resource, out IntPtr view)
    {
        fixed (IntPtr* v = &view) return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)V(device)[7])(device, resource, IntPtr.Zero, v);
    }

    public static int CreateUnorderedAccessView(IntPtr device, IntPtr resource, in D3D11_UNORDERED_ACCESS_VIEW_DESC desc, out IntPtr view)
    {
        fixed (D3D11_UNORDERED_ACCESS_VIEW_DESC* d = &desc)
        fixed (IntPtr* v = &view)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, D3D11_UNORDERED_ACCESS_VIEW_DESC*, IntPtr*, int>)V(device)[8])(device, resource, d, v);
        }
    }

    public static int CreateRenderTargetView(IntPtr device, IntPtr resource, out IntPtr view)
    {
        fixed (IntPtr* v = &view) return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)V(device)[9])(device, resource, IntPtr.Zero, v);
    }

    public static int CreateVertexShader(IntPtr device, byte[] code, out IntPtr shader)
    {
        fixed (byte* c = code)
        fixed (IntPtr* s = &shader)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)V(device)[12])(device, c, (nuint)code.Length, IntPtr.Zero, s);
        }
    }

    public static int CreatePixelShader(IntPtr device, byte[] code, out IntPtr shader)
    {
        fixed (byte* c = code)
        fixed (IntPtr* s = &shader)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)V(device)[15])(device, c, (nuint)code.Length, IntPtr.Zero, s);
        }
    }

    public static int CreateComputeShader(IntPtr device, byte[] code, out IntPtr shader)
    {
        fixed (byte* c = code)
        fixed (IntPtr* s = &shader)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)V(device)[18])(device, c, (nuint)code.Length, IntPtr.Zero, s);
        }
    }

    public static int GetFeatureLevel(IntPtr device) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)V(device)[37])(device);

    public static int GetDeviceRemovedReason(IntPtr device) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)V(device)[39])(device);

    // ID3D11DeviceContext
    public static void PSSetShaderResources(IntPtr context, uint slot, IntPtr view)
        => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)V(context)[8])(context, slot, 1, &view);

    public static void PSSetShader(IntPtr context, IntPtr shader)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, void>)V(context)[9])(context, shader, IntPtr.Zero, 0);

    public static void VSSetShader(IntPtr context, IntPtr shader)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, void>)V(context)[11])(context, shader, IntPtr.Zero, 0);

    public static void Draw(IntPtr context, uint vertices) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, void>)V(context)[13])(context, vertices, 0);

    public static void PSSetConstantBuffers(IntPtr context, uint slot, IntPtr buffer)
        => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)V(context)[16])(context, slot, 1, &buffer);

    public static void IASetPrimitiveTopology(IntPtr context, int topology)
        => ((delegate* unmanaged[Stdcall]<IntPtr, int, void>)V(context)[24])(context, topology);

    public static void OMSetRenderTargets(IntPtr context, IntPtr view)
    {
        if (view == IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, IntPtr, void>)V(context)[33])(context, 0, null, IntPtr.Zero);
        else ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, IntPtr, void>)V(context)[33])(context, 1, &view, IntPtr.Zero);
    }

    public static void Dispatch(IntPtr context, uint x, uint y) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, void>)V(context)[41])(context, x, y, 1);

    public static void RSSetViewport(IntPtr context, in D3D11_VIEWPORT viewport)
    {
        fixed (D3D11_VIEWPORT* v = &viewport) ((delegate* unmanaged[Stdcall]<IntPtr, uint, D3D11_VIEWPORT*, void>)V(context)[44])(context, 1, v);
    }

    public static void CopyResource(IntPtr context, IntPtr destination, IntPtr source)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)V(context)[47])(context, destination, source);

    public static void UpdateSubresource(IntPtr context, IntPtr resource, void* data, uint rowPitch)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, void*, uint, uint, void>)V(context)[48])(context, resource, 0, IntPtr.Zero, data, rowPitch, 0);

    public static void ClearUnorderedAccessViewUint(IntPtr context, IntPtr view)
    {
        var zero = stackalloc uint[4];
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, void>)V(context)[51])(context, view, zero);
    }

    public static void CSSetShaderResources(IntPtr context, uint slot, IntPtr view)
        => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)V(context)[67])(context, slot, 1, &view);

    public static void CSSetUnorderedAccessView(IntPtr context, IntPtr view)
        => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, uint*, void>)V(context)[68])(context, 0, 1, &view, null);

    public static void CSSetShader(IntPtr context, IntPtr shader)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, void>)V(context)[69])(context, shader, IntPtr.Zero, 0);

    public static void CSSetConstantBuffers(IntPtr context, uint slot, IntPtr buffer)
        => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, void>)V(context)[71])(context, slot, 1, &buffer);

    public static void ClearState(IntPtr context) => ((delegate* unmanaged[Stdcall]<IntPtr, void>)V(context)[110])(context);

    public static void Flush(IntPtr context) => ((delegate* unmanaged[Stdcall]<IntPtr, void>)V(context)[111])(context);

    // IDXGIDevice: 7 GetAdapter. IDXGIAdapter: 8 GetDesc. IDXGIResource: 8 GetSharedHandle.
    public static int GetAdapter(IntPtr dxgiDevice, out IntPtr adapter)
    {
        fixed (IntPtr* a = &adapter) return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)V(dxgiDevice)[7])(dxgiDevice, a);
    }

    public static int GetAdapterDesc(IntPtr adapter, out DXGI_ADAPTER_DESC desc)
    {
        fixed (DXGI_ADAPTER_DESC* d = &desc) return ((delegate* unmanaged[Stdcall]<IntPtr, DXGI_ADAPTER_DESC*, int>)V(adapter)[8])(adapter, d);
    }

    public static int GetSharedHandle(IntPtr resource, out IntPtr handle)
    {
        fixed (IntPtr* h = &handle) return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)V(resource)[8])(resource, h);
    }

    // IDXGIOutput5: 26 DuplicateOutput1. IDXGIOutputDuplication: 7 GetDesc.
    public static int DuplicateOutput1(IntPtr output5, IntPtr device, int[] formats, out IntPtr duplication)
    {
        fixed (int* f = formats)
        fixed (IntPtr* d = &duplication)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, int*, IntPtr*, int>)V(output5)[26])(output5, device, 0, (uint)formats.Length, f, d);
        }
    }

    public static void GetDuplicationDesc(IntPtr duplication, out DXGI_OUTDUPL_DESC desc)
    {
        fixed (DXGI_OUTDUPL_DESC* d = &desc) ((delegate* unmanaged[Stdcall]<IntPtr, DXGI_OUTDUPL_DESC*, void>)V(duplication)[7])(duplication, d);
    }

    // IDirect3D9: 4 GetAdapterCount. IDirect3D9Ex: 20 CreateDeviceEx, 21 GetAdapterLUID.
    public static uint GetAdapterCount(IntPtr d3d9) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(d3d9)[4])(d3d9);

    public static int GetAdapterLuid(IntPtr d3d9ex, uint adapter, out long luid)
    {
        fixed (long* l = &luid) return ((delegate* unmanaged[Stdcall]<IntPtr, uint, long*, int>)V(d3d9ex)[21])(d3d9ex, adapter, l);
    }

    public static int CreateDeviceEx(IntPtr d3d9ex, uint adapter, IntPtr window, ref D3DPRESENT_PARAMETERS parameters, out IntPtr device)
    {
        fixed (D3DPRESENT_PARAMETERS* p = &parameters)
        fixed (IntPtr* d = &device)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, IntPtr, uint, D3DPRESENT_PARAMETERS*, IntPtr, IntPtr*, int>)V(d3d9ex)[20])(
                d3d9ex, adapter, D3DDEVTYPE_HAL, window, D3DCREATE_HARDWARE_VERTEXPROCESSING | D3DCREATE_MULTITHREADED | D3DCREATE_FPU_PRESERVE, p, IntPtr.Zero, d);
        }
    }

    // IDirect3DDevice9: 23 CreateTexture. IDirect3DTexture9: 18 GetSurfaceLevel.
    public static int CreateSharedTexture9(IntPtr device9, uint width, uint height, IntPtr shared, out IntPtr texture)
    {
        var handle = shared;
        fixed (IntPtr* t = &texture)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, int, int, IntPtr*, IntPtr*, int>)V(device9)[23])(
                device9, width, height, 1, D3DUSAGE_RENDERTARGET, D3DFMT_A8R8G8B8, D3DPOOL_DEFAULT, t, &handle);
        }
    }

    public static int GetSurfaceLevel(IntPtr texture9, out IntPtr surface)
    {
        fixed (IntPtr* s = &surface) return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(texture9)[18])(texture9, 0, s);
    }
}
