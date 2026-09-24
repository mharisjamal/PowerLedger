using System.Runtime.InteropServices;

namespace PowerLedger.Sensors;

/// <summary>One graphics adapter as DXGI describes it.</summary>
/// <param name="Description">The adapter's name, e.g. "AMD Radeon RX 7800 XT".</param>
/// <param name="VendorId">The PCI vendor: 0x1002 is AMD, 0x8086 Intel, 0x10DE NVIDIA.</param>
/// <param name="DedicatedBytes">Memory of its own. Processor graphics report a small carve-out of system memory.</param>
/// <param name="LuidLow">The low half of the adapter's LUID, which names it in the GPU Engine counters until its driver restarts.</param>
/// <param name="LuidHigh">The high half of the LUID.</param>
/// <param name="Software">A software rasteriser, such as the Microsoft Basic Render Driver.</param>
/// <param name="DeviceId">The PCI device id, e.g. 0x06D8 for a Quadro 6000, which tells one maker's cards apart; 0 when unknown.</param>
public sealed record GpuAdapter(string Description, uint VendorId, ulong DedicatedBytes, uint LuidLow, int LuidHigh, bool Software, uint DeviceId = 0)
{
    /// <summary>How the GPU Engine counter instances name this adapter, e.g. "luid_0x00000000_0x0000D1A4".</summary>
    public string CounterLuid => FormattableString.Invariant($"luid_0x{LuidHigh:X8}_0x{LuidLow:X8}");
}

/// <summary>
/// The graphics adapters Windows lists, through DXGI. Taking the list wakes a switched-off discrete GPU for most of a
/// second, as measured on the development laptop, while asking whether a list is still current wakes nothing; so the
/// list is kept, and taken again only when a driver has restarted or an adapter has come or gone. Single-threaded: the
/// sampling loop owns it.
/// </summary>
internal sealed class DxgiAdapters : IDisposable
{
    /// <summary>DXGI_ERROR_NOT_FOUND: there is no adapter at this index, so the list is complete.</summary>
    private const int NotFound = unchecked((int)0x887A0002);

    /// <summary>DXGI_ADAPTER_FLAG_SOFTWARE.</summary>
    private const uint SoftwareFlag = 2;

    private IDXGIFactory1? _factory;
    private IReadOnlyList<GpuAdapter> _adapters = [];

    /// <summary>The adapters as Windows lists them now. Throws when DXGI will not answer.</summary>
    public IReadOnlyList<GpuAdapter> Current()
    {
        if (_factory is not null && _factory.IsCurrent()) return _adapters;
        Release();
        var iid = typeof(IDXGIFactory1).GUID;
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out var factory));
        _factory = (IDXGIFactory1)factory;
        _adapters = List(_factory);
        return _adapters;
    }

    public void Dispose() => Release();

    private static List<GpuAdapter> List(IDXGIFactory1 factory)
    {
        var adapters = new List<GpuAdapter>();
        for (uint index = 0; ; index++)
        {
            var result = factory.EnumAdapters1(index, out var adapter);
            if (result == NotFound) return adapters;
            Marshal.ThrowExceptionForHR(result);
            try
            {
                Marshal.ThrowExceptionForHR(adapter.GetDesc1(out var desc));
                adapters.Add(new GpuAdapter(
                    desc.Description, desc.VendorId, desc.DedicatedVideoMemory,
                    desc.LuidLowPart, desc.LuidHighPart, (desc.Flags & SoftwareFlag) != 0, desc.DeviceId));
            }
            finally
            {
                Marshal.FinalReleaseComObject(adapter);
            }
        }
    }

    private void Release()
    {
        if (_factory is null) return;
        Marshal.FinalReleaseComObject(_factory);
        _factory = null;
    }

    [DllImport("dxgi.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CreateDXGIFactory1(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint Flags;
    }

    /// <summary>IDXGIFactory1 with IDXGIObject's and IDXGIFactory's methods in vtable order. Only the last two are called;
    /// the others hold their slots.</summary>
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumAdapters();
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();

        [PreserveSig]
        int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);

        [PreserveSig]
        [return: MarshalAs(UnmanagedType.Bool)]
        bool IsCurrent();
    }

    /// <summary>IDXGIAdapter1 with IDXGIObject's and IDXGIAdapter's methods in vtable order. Only GetDesc1 is called.</summary>
    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumOutputs();
        void GetDesc();
        void CheckInterfaceSupport();

        [PreserveSig]
        int GetDesc1(out AdapterDesc1 desc);
    }
}
