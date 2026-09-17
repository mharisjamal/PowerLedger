using System.Runtime.InteropServices;

namespace PowerLedger.Sensors;

/// <summary>What a power domain covers, as Level Zero's extended power properties report it (zes_power_domain_t).</summary>
internal enum PowerDomain
{
    Unknown = 0,

    /// <summary>The whole card: chip, memory, regulators and fans.</summary>
    Card = 1,

    /// <summary>The graphics package alone, which leaves out the rest of the card.</summary>
    Package = 2,

    Stack = 3,
    Memory = 4,
    Gpu = 5,
}

/// <summary>What Level Zero says about one device, as far as choosing a card to read goes.</summary>
/// <param name="Type">zes_device_type_t; only <see cref="GpuType"/> is graphics.</param>
/// <param name="VendorId">PCI vendor id, 0x8086 for Intel.</param>
/// <param name="DeviceId">PCI device id, e.g. 0x56A0 for an Arc A770.</param>
/// <param name="CoreFlags">The core device property flags, whose bit 0 means integrated.</param>
/// <param name="ExtendedFlags">The extended device property flags, whose bit 0 means integrated too; zero when the
/// driver would not fill them in.</param>
internal readonly record struct SysmanDevice(uint Type, uint VendorId, uint DeviceId, uint CoreFlags, uint ExtendedFlags)
{
    /// <summary>ZE_DEVICE_TYPE_GPU.</summary>
    public const uint GpuType = 1;

    /// <summary>ZES_DEVICE_PROPERTY_FLAG_INTEGRATED, the same bit in both sets of flags.</summary>
    private const uint IntegratedFlag = 1;

    public bool IsGpu => Type == GpuType;

    /// <summary>True for graphics built into the processor, whose watts the processor's package reading already counts.</summary>
    public bool Integrated => ((CoreFlags | ExtendedFlags) & IntegratedFlag) != 0;
}

/// <param name="Microjoules">The device's monotonic energy counter, in microjoules.</param>
/// <param name="Microseconds">When the device captured that figure, in microseconds on the driver's own clock: good for
/// the time between two readings and nothing else.</param>
internal readonly record struct SysmanEnergy(ulong Microjoules, ulong Microseconds);

/// <summary>
/// The Level Zero Sysman calls <see cref="ArcSource"/> makes, one method per call, so a test can stand in for the
/// graphics driver. Each returns that call's ze_result_t, where 0 is success, and may throw what a missing or
/// mismatched library throws.
/// </summary>
internal interface ISysman
{
    /// <summary>zesInit.</summary>
    int Init();

    /// <summary>zesDriverGet.</summary>
    int GetDrivers(out IntPtr[] drivers);

    /// <summary>zesDeviceGet.</summary>
    int GetDevices(IntPtr driver, out IntPtr[] devices);

    /// <summary>zesDeviceGetProperties, with the extended device properties chained on.</summary>
    int GetDeviceProperties(IntPtr device, out SysmanDevice properties);

    /// <summary>zesDeviceEnumPowerDomains.</summary>
    int GetPowerDomains(IntPtr device, out IntPtr[] domains);

    /// <summary>zesPowerGetProperties, with the extended power properties chained on, for what the domain covers.</summary>
    int GetPowerDomain(IntPtr domain, out PowerDomain kind);

    /// <summary>zesPowerGetEnergyCounter.</summary>
    int GetEnergyCounter(IntPtr domain, out SysmanEnergy energy);
}

/// <summary>
/// Intel's Level Zero Sysman library, which ships with the graphics driver as ze_loader.dll. No package reference and
/// no driver of ours: where there is no Intel driver, loading the library simply fails and the Arc source reports
/// itself unsupported. Sysman is started with zesInit rather than the deprecated ZES_ENABLE_SYSMAN environment
/// variable, and it needs no elevation: the probe on the development laptop answered from an ordinary process.
/// The structures below are the ones from zes_api.h and ze_api.h that this file reads, laid out for 64-bit Windows,
/// the only kind PowerLedger ships. Offsets that are not read are left as room for the driver to write into.
/// </summary>
internal sealed class LevelZeroSysman : ISysman
{
    private const string Library = "ze_loader.dll";
    private const int Success = 0;

    /// <summary>ZES_STRUCTURE_TYPE_DEVICE_PROPERTIES.</summary>
    private const int DevicePropertiesType = 0x1;

    /// <summary>ZE_STRUCTURE_TYPE_DEVICE_PROPERTIES, for the core properties inside them.</summary>
    private const int CoreDevicePropertiesType = 0x3;

    /// <summary>ZES_STRUCTURE_TYPE_POWER_PROPERTIES.</summary>
    private const int PowerPropertiesType = 0xd;

    /// <summary>ZES_STRUCTURE_TYPE_POWER_LIMIT_EXT_DESC.</summary>
    private const int PowerLimitType = 0x27;

    /// <summary>ZES_STRUCTURE_TYPE_POWER_EXT_PROPERTIES.</summary>
    private const int PowerExtendedPropertiesType = 0x28;

    /// <summary>ZES_STRUCTURE_TYPE_DEVICE_EXT_PROPERTIES.</summary>
    private const int DeviceExtendedPropertiesType = 0x2d;

    private delegate int Listing(ref uint count, IntPtr[]? handles);

    public int Init() => zesInit(0);

    public int GetDrivers(out IntPtr[] drivers) => List(zesDriverGet, out drivers);

    public int GetDevices(IntPtr driver, out IntPtr[] devices)
        => List((ref uint count, IntPtr[]? handles) => zesDeviceGet(driver, ref count, handles), out devices);

    public int GetDeviceProperties(IntPtr device, out SysmanDevice properties)
    {
        var extended = Allocate(new DeviceExtendedProperties { Type = DeviceExtendedPropertiesType });
        try
        {
            var found = new DeviceProperties { Type = DevicePropertiesType, Next = extended, CoreType = CoreDevicePropertiesType };
            var result = zesDeviceGetProperties(device, ref found);
            var extendedFlags = 0u;
            if (result == Success)
            {
                extendedFlags = Marshal.PtrToStructure<DeviceExtendedProperties>(extended).Flags;
            }
            else
            {
                // A driver older than the extended properties refuses the whole call; its core flags still say integrated.
                found = new DeviceProperties { Type = DevicePropertiesType, CoreType = CoreDevicePropertiesType };
                result = zesDeviceGetProperties(device, ref found);
            }
            properties = new SysmanDevice(found.DeviceType, found.VendorId, found.DeviceId, found.Flags, extendedFlags);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(extended);
        }
    }

    public int GetPowerDomains(IntPtr device, out IntPtr[] domains)
        => List((ref uint count, IntPtr[]? handles) => zesDeviceEnumPowerDomains(device, ref count, handles), out domains);

    public int GetPowerDomain(IntPtr domain, out PowerDomain kind)
    {
        kind = PowerDomain.Unknown;
        // The driver writes the part's factory default limit through the pointer, so it is given room rather than nothing.
        var limit = Allocate(new PowerLimit { Type = PowerLimitType });
        var extended = IntPtr.Zero;
        try
        {
            extended = Allocate(new PowerExtendedProperties { Type = PowerExtendedPropertiesType, DefaultLimit = limit });
            var properties = new PowerProperties { Type = PowerPropertiesType, Next = extended };
            var result = zesPowerGetProperties(domain, ref properties);
            if (result == Success) kind = (PowerDomain)Marshal.PtrToStructure<PowerExtendedProperties>(extended).Domain;
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(extended);
            Marshal.FreeHGlobal(limit);
        }
    }

    public int GetEnergyCounter(IntPtr domain, out SysmanEnergy energy)
    {
        var result = zesPowerGetEnergyCounter(domain, out var counter);
        energy = new SysmanEnergy(counter.Energy, counter.Timestamp);
        return result;
    }

    /// <summary>Level Zero's two-call listing: how many handles there are, then that many handles.</summary>
    private static int List(Listing list, out IntPtr[] handles)
    {
        handles = [];
        uint count = 0;
        var result = list(ref count, null);
        if (result != Success || count == 0) return result;

        var found = new IntPtr[count];
        result = list(ref count, found);
        if (result != Success) return result;
        handles = count < found.Length ? found[..(int)count] : found;
        return result;
    }

    /// <summary>Unmanaged room for a structure the driver writes into, since it is reached through a pointer.</summary>
    private static IntPtr Allocate<T>(T value) where T : struct
    {
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        Marshal.StructureToPtr(value, pointer, fDeleteOld: false);
        return pointer;
    }

    /// <summary>zes_device_properties_t: stype, pNext, then the core ze_device_properties_t from offset 16, which opens
    /// with its own stype, pNext, type, vendorId, deviceId and flags. numSubdevices and six 64-character strings follow
    /// it, up to the 776 bytes the driver expects.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 776)]
    private struct DeviceProperties
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(8)] public IntPtr Next;
        [FieldOffset(16)] public int CoreType;
        [FieldOffset(32)] public uint DeviceType;
        [FieldOffset(36)] public uint VendorId;
        [FieldOffset(40)] public uint DeviceId;
        [FieldOffset(44)] public uint Flags;
    }

    /// <summary>zes_device_ext_properties_t: stype, pNext, a 16-byte uuid, the device type and the property flags.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct DeviceExtendedProperties
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(32)] public uint DeviceType;
        [FieldOffset(36)] public uint Flags;
    }

    /// <summary>zes_power_properties_t: stype, pNext, then whether the domain is on a sub-device, whether software can
    /// change it, and its deprecated default, minimum and maximum limits in milliwatts, none of which are read here.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct PowerProperties
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(8)] public IntPtr Next;
    }

    /// <summary>zes_power_ext_properties_t: stype, pNext, what the domain covers, and a pointer to room for the part's
    /// factory default limit.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct PowerExtendedProperties
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(16)] public int Domain;
        [FieldOffset(24)] public IntPtr DefaultLimit;
    }

    /// <summary>zes_power_limit_ext_desc_t: stype, pNext, then the limit itself, which is written but not read here.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct PowerLimit
    {
        [FieldOffset(0)] public int Type;
    }

    /// <summary>zes_power_energy_counter_t.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct EnergyCounter
    {
        public ulong Energy;
        public ulong Timestamp;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int zesInit(uint flags);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int zesDriverGet(ref uint count, [Out] IntPtr[]? drivers);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int zesDeviceGet(IntPtr driver, ref uint count, [Out] IntPtr[]? devices);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int zesDeviceGetProperties(IntPtr device, ref DeviceProperties properties);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int zesDeviceEnumPowerDomains(IntPtr device, ref uint count, [Out] IntPtr[]? domains);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int zesPowerGetProperties(IntPtr domain, ref PowerProperties properties);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int zesPowerGetEnergyCounter(IntPtr domain, out EnergyCounter energy);
}
