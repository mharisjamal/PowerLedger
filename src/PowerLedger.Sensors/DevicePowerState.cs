using System.Runtime.InteropServices;

namespace PowerLedger.Sensors;

/// <summary>
/// Whether Windows has switched a device off. A switchable-graphics laptop keeps its discrete GPU in D3 almost all
/// the time, where it draws next to nothing; asking the GPU's own library about it then either fails or wakes it.
/// Windows' device power state answers in microseconds, needs no elevation and never wakes the device.
/// </summary>
internal static class DevicePowerState
{
    /// <summary>DEVICE_POWER_STATE: PowerDeviceD0 is 1, PowerDeviceD3 is 4.</summary>
    private const int PowerDeviceD3 = 4;

    private static readonly DevPropKey PowerData = new() { FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = 32 };

    /// <summary>The Plug and Play instance id of the first NVIDIA display adapter, or null when there is none.</summary>
    public static string? FindNvidiaGpu() => FindDisplayAdapter(@"PCI\VEN_10DE");

    /// <summary>The Plug and Play instance id of the first display adapter whose id starts with the given text, such as
    /// "PCI\VEN_8086&amp;DEV_56A0" for an Arc A770, or null when this machine has none.</summary>
    public static string? FindDisplayAdapter(string idPrefix) => Wmi.ReadOr(@"\\.\root\cimv2", "SELECT PNPDeviceID FROM Win32_VideoController", rows =>
    {
        foreach (var row in rows)
        {
            if (row["PNPDeviceID"] is string id && id.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase)) return id;
        }
        return null;
    }, null);

    /// <summary>True when Windows reports the device in D3. Anything it cannot answer counts as on, which never hides real draw.</summary>
    public static bool IsPoweredOff(string pnpDeviceId)
    {
        if (CM_Locate_DevNodeW(out var node, pnpDeviceId, 0) != 0) return false;
        var key = PowerData;
        var buffer = new byte[64];
        var size = (uint)buffer.Length;
        if (CM_Get_DevNode_PropertyW(node, ref key, out _, buffer, ref size, 0) != 0 || size < 8) return false;
        // CM_POWER_DATA begins with PD_Size, then PD_MostRecentPowerState.
        return BitConverter.ToInt32(buffer, 4) == PowerDeviceD3;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CM_Locate_DevNodeW(out uint node, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CM_Get_DevNode_PropertyW(uint node, ref DevPropKey key, out uint propertyType, byte[] buffer, ref uint bufferSize, uint flags);
}
