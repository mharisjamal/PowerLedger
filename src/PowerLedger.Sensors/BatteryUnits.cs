using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PowerLedger.Sensors;

/// <summary>
/// Whether the machine's battery reports relative units rather than milliwatts. Windows' summary of the batteries
/// (<c>SystemBatteryState</c>) gives a rate without its units; each battery's own <c>BATTERY_INFORMATION.Capabilities</c>
/// says, and with <c>BATTERY_CAPACITY_RELATIVE</c> set its capacities and rate are "in arbitrary units" rather than
/// milliwatt-hours and milliwatts (learn.microsoft.com/windows/win32/power/battery-information-str). Asked once, through
/// the battery class's own interface (SetupDi on GUID_DEVCLASS_BATTERY, then IOCTL_BATTERY_QUERY_TAG and
/// IOCTL_BATTERY_QUERY_INFORMATION), which needs no elevation and no driver of ours. Every failure answers false, which
/// keeps the rate read as milliwatts, as it always was.
/// </summary>
internal static class BatteryUnits
{
    private const uint SystemBattery = 0x80000000;       // BATTERY_SYSTEM_BATTERY
    private const uint CapacityRelative = 0x40000000;    // BATTERY_CAPACITY_RELATIVE
    private const uint ShortTerm = 0x20000000;           // BATTERY_IS_SHORT_TERM, as a UPS is

    private const uint DigcfPresent = 0x02;
    private const uint DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x03;
    private const uint OpenExisting = 3;
    private const uint IoctlBatteryQueryTag = 0x294040;
    private const uint IoctlBatteryQueryInformation = 0x294044;
    private const int BatteryInformationLevel = 0;
    private const int MaxBatteries = 16;

    private static Guid BatteryClass = new("72631E54-78A4-11D0-BCF7-00AA00B7B32A");

    /// <summary>True when any battery that powers this machine alone reports relative units. A short-term battery is a UPS,
    /// which is not the machine's own and whose units say nothing about its rate.</summary>
    public static bool AnyRelative(IEnumerable<uint> capabilities)
        => capabilities.Any(flags => (flags & ShortTerm) == 0 && (flags & CapacityRelative) != 0);

    /// <summary>Asks each battery for its capabilities; false when there is none or Windows won't say.</summary>
    public static bool Relative()
    {
        try
        {
            return AnyRelative(Capabilities());
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return false;
        }
    }

    /// <summary>Every battery's <c>BATTERY_INFORMATION.Capabilities</c>, for the status and the hardware tests.</summary>
    internal static List<uint> Capabilities()
    {
        var found = new List<uint>();
        var set = SetupDiGetClassDevsW(ref BatteryClass, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1)) return found;
        try
        {
            for (uint index = 0; index < MaxBatteries; index++)
            {
                var element = new DeviceInterfaceData { Size = (uint)Marshal.SizeOf<DeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref BatteryClass, index, ref element)) break;
                if (Path(set, ref element) is not { } path) continue;
                if (Query(path) is { } flags) found.Add(flags);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return found;
    }

    private static string? Path(IntPtr set, ref DeviceInterfaceData element)
    {
        SetupDiGetDeviceInterfaceDetailW(set, ref element, IntPtr.Zero, 0, out var size, IntPtr.Zero);
        if (size == 0) return null;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W: its cbSize is the fixed part's size, 8 in a 64-bit process and 6 in 32.
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetailW(set, ref element, buffer, size, out _, IntPtr.Zero)) return null;
            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static uint? Query(string path)
    {
        using var handle = CreateFileW(path, GenericRead | GenericWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;

        uint wait = 0;
        if (!DeviceIoControl(handle, IoctlBatteryQueryTag, ref wait, sizeof(uint), out uint tag, sizeof(uint), out _, IntPtr.Zero) || tag == 0)
        {
            return null;
        }
        var query = new BatteryQueryInformation { BatteryTag = tag, InformationLevel = BatteryInformationLevel };
        if (!DeviceIoControl(handle, IoctlBatteryQueryInformation, ref query, (uint)Marshal.SizeOf<BatteryQueryInformation>(),
                out BatteryInformation information, (uint)Marshal.SizeOf<BatteryInformation>(), out _, IntPtr.Zero))
        {
            return null;
        }
        return information.Capabilities;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public uint Size;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryQueryInformation
    {
        public uint BatteryTag;
        public int InformationLevel;
        public int AtRate;
    }

    /// <summary>BATTERY_INFORMATION, 32 bytes; only the capabilities are read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryInformation
    {
        public uint Capabilities;
        public byte Technology;
        public byte Reserved0, Reserved1, Reserved2;
        public uint Chemistry;
        public uint DesignedCapacity;
        public uint FullChargedCapacity;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
        public uint CriticalBias;
        public uint CycleCount;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr deviceInfo, ref Guid interfaceClass, uint index, ref DeviceInterfaceData element);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref DeviceInterfaceData element, IntPtr detail, uint detailSize, out uint required, IntPtr deviceInfo);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref uint input, uint inputSize, out uint output, uint outputSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref BatteryQueryInformation input, uint inputSize, out BatteryInformation output, uint outputSize, out uint returned, IntPtr overlapped);
}
