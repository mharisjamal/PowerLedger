using System.Runtime.InteropServices;

namespace PowerLedger.App.Aero;

/// <summary>
/// The Win32, DXGI and Direct3D 11 calls the liquid glass's capture needs, thin. The COM calls go through each
/// interface's vtable by slot (the order in dxgi.h, dxgi1_2.h and d3d11.h), so no interop package is needed.
/// </summary>
internal static unsafe class CaptureNative
{
    public const uint WDA_NONE = 0;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    public const int MONITOR_DEFAULTTONEAREST = 2;

    public const int S_OK = 0;
    public const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    public const int DXGI_ERROR_UNSUPPORTED = unchecked((int)0x887A0004);
    public const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
    public const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    public const int DXGI_ERROR_SESSION_DISCONNECTED = unchecked((int)0x887A0028);
    public const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);
    public const int E_ACCESSDENIED = unchecked((int)0x80070005);

    public const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    public const int D3D11_USAGE_DEFAULT = 0;
    public const int D3D11_USAGE_STAGING = 3;
    public const int D3D11_CPU_ACCESS_READ = 0x20000;
    public const int D3D11_MAP_READ = 1;
    public const int D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    public const int D3D11_SDK_VERSION = 7;
    public const int DXGI_MODE_ROTATION_UNSPECIFIED = 0;
    public const int DXGI_MODE_ROTATION_IDENTITY = 1;

    public static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    public static readonly Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    public static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DXGI_OUTPUT_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public RECT DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_OUTDUPL_FRAME_INFO
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public int PointerX, PointerY, PointerVisible;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_OUTDUPL_MOVE_RECT
    {
        public int SourceX, SourceY;
        public RECT Destination;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public uint SampleCount, SampleQuality;
        public int Usage;
        public uint BindFlags, CPUAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_BOX
    {
        public uint Left, Top, Front, Right, Bottom, Back;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr Data;
        public uint RowPitch, DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int Size;
        public RECT Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("dxgi.dll")]
    public static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    [DllImport("d3d11.dll")]
    public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels, uint levels,
        uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    private static void** Vtable(IntPtr self) => *(void***)self;

    public static uint Release(IntPtr self) => self == IntPtr.Zero ? 0 : ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtable(self)[2])(self);

    public static int QueryInterface(IntPtr self, in Guid iid, out IntPtr result)
    {
        fixed (Guid* id = &iid)
        fixed (IntPtr* r = &result)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vtable(self)[0])(self, id, r);
        }
    }

    // IDXGIFactory: 7 EnumAdapters. IDXGIAdapter: 7 EnumOutputs. IDXGIOutput: 7 GetDesc. IDXGIOutput1: 22 DuplicateOutput.
    public static int EnumAdapters(IntPtr factory, uint index, out IntPtr adapter)
    {
        fixed (IntPtr* a = &adapter) return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vtable(factory)[7])(factory, index, a);
    }

    public static int EnumOutputs(IntPtr adapter, uint index, out IntPtr output)
    {
        fixed (IntPtr* o = &output) return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vtable(adapter)[7])(adapter, index, o);
    }

    public static int GetOutputDesc(IntPtr output, out DXGI_OUTPUT_DESC desc)
    {
        var buffer = stackalloc byte[128];
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)Vtable(output)[7])(output, buffer);
        desc = Marshal.PtrToStructure<DXGI_OUTPUT_DESC>((IntPtr)buffer);
        return hr;
    }

    public static int DuplicateOutput(IntPtr output1, IntPtr device, out IntPtr duplication)
    {
        fixed (IntPtr* d = &duplication) return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Vtable(output1)[22])(output1, device, d);
    }

    // IDXGIOutputDuplication: 8 AcquireNextFrame, 9 GetFrameDirtyRects, 10 GetFrameMoveRects, 14 ReleaseFrame.
    public static int AcquireNextFrame(IntPtr duplication, uint timeoutMs, out DXGI_OUTDUPL_FRAME_INFO info, out IntPtr resource)
    {
        fixed (DXGI_OUTDUPL_FRAME_INFO* i = &info)
        fixed (IntPtr* r = &resource)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, DXGI_OUTDUPL_FRAME_INFO*, IntPtr*, int>)Vtable(duplication)[8])(duplication, timeoutMs, i, r);
        }
    }

    public static int GetFrameDirtyRects(IntPtr duplication, RECT[] rects, out int count)
    {
        uint required;
        int hr;
        fixed (RECT* r = rects)
        {
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, RECT*, uint*, int>)Vtable(duplication)[9])(duplication, (uint)(rects.Length * sizeof(RECT)), r, &required);
        }
        count = (int)(required / sizeof(RECT));
        return hr;
    }

    public static int GetFrameMoveRects(IntPtr duplication, DXGI_OUTDUPL_MOVE_RECT[] rects, out int count)
    {
        uint required;
        int hr;
        fixed (DXGI_OUTDUPL_MOVE_RECT* r = rects)
        {
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, DXGI_OUTDUPL_MOVE_RECT*, uint*, int>)Vtable(duplication)[10])(duplication,
                (uint)(rects.Length * sizeof(DXGI_OUTDUPL_MOVE_RECT)), r, &required);
        }
        count = (int)(required / sizeof(DXGI_OUTDUPL_MOVE_RECT));
        return hr;
    }

    public static int ReleaseFrame(IntPtr duplication) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtable(duplication)[14])(duplication);

    // ID3D11Device: 5 CreateTexture2D. ID3D11DeviceContext: 14 Map, 15 Unmap, 46 CopySubresourceRegion.
    public static int CreateTexture2D(IntPtr device, in D3D11_TEXTURE2D_DESC desc, out IntPtr texture)
    {
        fixed (D3D11_TEXTURE2D_DESC* d = &desc)
        fixed (IntPtr* t = &texture)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)Vtable(device)[5])(device, d, IntPtr.Zero, t);
        }
    }

    public static int Map(IntPtr context, IntPtr resource, out D3D11_MAPPED_SUBRESOURCE mapped)
    {
        fixed (D3D11_MAPPED_SUBRESOURCE* m = &mapped)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, D3D11_MAPPED_SUBRESOURCE*, int>)Vtable(context)[14])(context, resource, 0, D3D11_MAP_READ, 0, m);
        }
    }

    public static void Unmap(IntPtr context, IntPtr resource) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Vtable(context)[15])(context, resource, 0);

    public static void CopySubresourceRegion(IntPtr context, IntPtr destination, int x, int y, IntPtr source, in D3D11_BOX box)
    {
        fixed (D3D11_BOX* b = &box)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, D3D11_BOX*, void>)Vtable(context)[46])(
                context, destination, 0, (uint)x, (uint)y, 0, source, 0, b);
        }
    }
}
