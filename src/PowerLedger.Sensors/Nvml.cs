using System.Runtime.InteropServices;
using System.Text;

namespace PowerLedger.Sensors;

/// <param name="Present">True when at least one NVIDIA GPU answered.</param>
/// <param name="PowerWatts">Current draw, or null on a card with no power measurement hardware.</param>
/// <param name="LoadFraction">Utilisation 0..1, or null when the card declines to answer.</param>
public readonly record struct GpuReading(bool Present, double? PowerWatts, double? LoadFraction);

/// <summary>One NVIDIA GPU as NVML describes it.</summary>
/// <param name="Name">"Quadro 6000" from an older driver, "NVIDIA GeForce RTX 4070" from a newer one; empty when NVML
/// would not say.</param>
/// <param name="DeviceId">The PCI device id, e.g. 0x06D8 for a Quadro 6000; 0 when NVML would not say.</param>
/// <param name="MemoryBytes">The card's own memory; 0 when NVML would not say.</param>
public readonly record struct NvmlDevice(string Name, uint DeviceId, ulong MemoryBytes);

/// <summary>
/// The NVIDIA management library, which ships with the display driver. No package reference and no driver of
/// ours: if there is no NVIDIA GPU, loading the library simply fails and the source reports itself unsupported.
/// <see cref="NvmlLibrary"/> finds it in System32, NVSMI or the driver's DriverStore folder; the DllImports' own
/// search stays System32 alone. Every GPU NVML lists is opened, so a workstation with two cards has both read.
/// </summary>
public sealed class Nvml : IDisposable
{
    private const int Success = 0;
    private const int Uninitialized = 1;
    private const int DriverNotLoaded = 9;
    private const int GpuIsLost = 15;
    private const int NameLength = 96;          // NVML_DEVICE_NAME_V2_BUFFER_SIZE; older drivers use 64
    private const uint NvidiaVendor = 0x10DE;

    private readonly List<IntPtr> _handles = [];
    private readonly List<NvmlDevice> _devices = [];
    private bool _initialised;

    /// <summary>The library is looked for where the driver put it, not only in System32 (see <see cref="NvmlLibrary"/>).</summary>
    static Nvml() => NvmlLibrary.Register();

    public Nvml() : this(Environment.Is64BitProcess)
    {
    }

    /// <summary>Test seam: a 32-bit process never loads NVML, which has no 32-bit version.</summary>
    internal Nvml(bool is64BitProcess)
    {
        if (Bitness.SixtyFourBitOnly("NVIDIA's NVML", is64BitProcess) is { } reason)
        {
            Unavailable = reason;
            return;
        }
        try
        {
            if (nvmlInit_v2() != Success)
            {
                Unavailable = "NVML would not start";
                return;
            }
            _initialised = true;

            if (nvmlDeviceGetCount_v2(out var count) != Success || count == 0)
            {
                Unavailable = "no NVIDIA GPU";
                return;
            }
            for (uint index = 0; index < count; index++)
            {
                // A GPU NVML will not open, as one the driver denies access to, is left to Windows' load counters.
                if (nvmlDeviceGetHandleByIndex_v2(index, out var device) != Success) continue;
                _handles.Add(device);
                _devices.Add(new NvmlDevice(Name(device), DeviceId(device), Memory(device)));
            }
            if (_handles.Count == 0)
            {
                Unavailable = "NVML would not open the GPU";
                return;
            }
            Available = true;
        }
        catch (DllNotFoundException)
        {
            Unavailable = "no NVIDIA driver installed";
        }
        catch (EntryPointNotFoundException)
        {
            Unavailable = "the NVIDIA driver is too old";
        }
    }

    /// <summary>True when a GPU is open and can be asked.</summary>
    public bool Available { get; }

    /// <summary>Why there is nothing to ask, for the status screen; null when a GPU is available.</summary>
    public string? Unavailable { get; }

    /// <summary>The GPUs that are open, in NVML's order; <see cref="Read"/> takes an index into it.</summary>
    public IReadOnlyList<NvmlDevice> Devices => _devices;

    /// <summary>One GPU's power and load. "Not supported" leaves either null: many GeForce and Fermi cards measure no
    /// power, and Fermi GeForce cards give no utilisation either.</summary>
    public GpuReading Read(int index)
    {
        if (!Available || index < 0 || index >= _handles.Count) return new GpuReading(false, null, null);

        var device = _handles[index];
        var powerResult = nvmlDeviceGetPowerUsage(device, out var milliwatts);
        var loadResult = nvmlDeviceGetUtilizationRates(device, out var utilisation);
        ThrowIfBroken(powerResult);
        ThrowIfBroken(loadResult);

        // "Not supported" means a card with no power sensor, and other errors a card that declined this once: both are null.
        double? watts = powerResult == Success ? milliwatts / 1000.0 : null;
        double? load = loadResult == Success ? utilisation.Gpu / 100.0 : null;
        return new GpuReading(true, watts, load);
    }

    /// <summary>A lost GPU, an unloaded driver or a torn-down library is a broken source, not a missing reading:
    /// throwing lets the sampler back off and show it in status instead of charging a phantom load forever.</summary>
    private static void ThrowIfBroken(int result)
    {
        if (result is Uninitialized or DriverNotLoaded or GpuIsLost)
        {
            throw new InvalidOperationException($"NVML error {result}: the GPU or its driver is no longer available");
        }
    }

    public void Dispose()
    {
        if (!_initialised) return;
        _initialised = false;
        try
        {
            nvmlShutdown();
        }
        catch (DllNotFoundException)
        {
        }
    }

    private static string Name(IntPtr device)
    {
        var buffer = new byte[NameLength];
        if (nvmlDeviceGetName(device, buffer, NameLength) != Success) return "";
        var end = Array.IndexOf(buffer, (byte)0);
        return Encoding.ASCII.GetString(buffer, 0, end < 0 ? buffer.Length : end).Trim();
    }

    /// <summary>The PCI device id, through whichever version of the call this driver has: _v3 from about R410, _v2 before
    /// it, and the first version on the oldest. All three fill the same leading fields.</summary>
    private static uint DeviceId(IntPtr device)
    {
        PciInfo info;
        int result;
        try
        {
            result = nvmlDeviceGetPciInfo_v3(device, out info);
        }
        catch (EntryPointNotFoundException)
        {
            try
            {
                result = nvmlDeviceGetPciInfo_v2(device, out info);
            }
            catch (EntryPointNotFoundException)
            {
                try
                {
                    result = nvmlDeviceGetPciInfo(device, out info);
                }
                catch (EntryPointNotFoundException)
                {
                    return 0;
                }
            }
        }
        // pciDeviceId holds the device id in its high half and the vendor in its low half.
        return result == Success && (info.PciDeviceId & 0xFFFF) == NvidiaVendor ? info.PciDeviceId >> 16 : 0;
    }

    private static ulong Memory(IntPtr device)
    {
        try
        {
            return nvmlDeviceGetMemoryInfo(device, out var memory) == Success ? memory.Total : 0;
        }
        catch (EntryPointNotFoundException)
        {
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Utilisation
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    /// <summary>nvmlPciInfo_t as _v3 lays it out. The older versions write the same fields up to the subsystem id and at
    /// most 16 reserved bytes after it, so the 32 bytes at the end hold whichever they write.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct PciInfo
    {
        public fixed byte BusIdLegacy[16];
        public uint Domain;
        public uint Bus;
        public uint Device;
        public uint PciDeviceId;
        public uint PciSubSystemId;
        public fixed byte BusId[32];
    }

    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlShutdown();
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetName(IntPtr device, [Out] byte[] name, uint length);
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetPciInfo_v3(IntPtr device, out PciInfo pci);
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetPciInfo_v2(IntPtr device, out PciInfo pci);
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetPciInfo(IntPtr device, out PciInfo pci);
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out MemoryInfo memory);
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilisation utilisation);
}
