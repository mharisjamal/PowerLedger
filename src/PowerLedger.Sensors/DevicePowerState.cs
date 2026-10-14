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
        => Nodes.Locate(pnpDeviceId) is { } node && Nodes.PowerState(node) == PowerDeviceD3;

    /// <summary>The device tree as Windows keeps it.</summary>
    public static IDeviceNodes Nodes { get; } = new WindowsNodes();

    /// <summary>True for DEVICE_POWER_STATE's D3, the state a switched-off card is in.</summary>
    public static bool IsOff(int state) => state == PowerDeviceD3;

    private sealed class WindowsNodes : IDeviceNodes
    {
        public uint? Locate(string instanceId) => CM_Locate_DevNodeW(out var node, instanceId, 0) == 0 ? node : null;

        public int? PowerState(uint node)
        {
            var key = PowerData;
            Span<byte> buffer = stackalloc byte[64];
            var size = (uint)buffer.Length;
            if (CM_Get_DevNode_PropertyW(node, ref key, out _, ref MemoryMarshal.GetReference(buffer), ref size, 0) != 0 || size < 8) return null;
            // CM_POWER_DATA begins with PD_Size, then PD_MostRecentPowerState.
            return BitConverter.ToInt32(buffer[4..]);
        }
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
    private static extern int CM_Get_DevNode_PropertyW(uint node, ref DevPropKey key, out uint propertyType, ref byte buffer, ref uint bufferSize, uint flags);
}

/// <summary>Whether Windows has a device switched on, switched off, or can't say.</summary>
internal enum DevicePower
{
    On,
    Off,
    Unknown,
}

/// <summary>Windows' device tree, one question per method, so a test can stand in for it.</summary>
internal interface IDeviceNodes
{
    /// <summary>The device's node in the tree, or null when Windows has none by that id.</summary>
    uint? Locate(string instanceId);

    /// <summary>The node's most recent DEVICE_POWER_STATE (D0 is 1, D3 is 4), or null when Windows would not say.</summary>
    int? PowerState(uint node);
}

/// <summary>
/// One device's power state, asked of Windows as seldom as is safe. The device is found and its node located once, not every
/// tick. A device that is off is asked again only every few seconds: until it is, it is taken as still off and its library is
/// left alone, which at worst counts a card that just woke as off for that long. A device that is on is asked every time,
/// because its own library is asked next, and asking a card that has just switched off would wake it. A device that can't be
/// found, or whose node stops answering, is unknown, and looked for again a minute later rather than every tick.
/// Single-threaded: the sampling loop owns it.
/// </summary>
internal sealed class DevicePowerWatch
{
    public const long OffRecheckMilliseconds = 3_000;
    public const long FindAgainMilliseconds = 60_000;

    private readonly Func<string?> _find;
    private readonly IDeviceNodes _nodes;
    private readonly Func<long> _clock;
    private uint? _node;
    private long _findAt = long.MinValue;
    private bool _off;
    private long _askedAt;

    /// <param name="find">Finds the device's instance id; asked once, and again a minute after it fails.</param>
    /// <param name="clockMilliseconds">A monotonic clock.</param>
    public DevicePowerWatch(Func<string?> find, IDeviceNodes nodes, Func<long> clockMilliseconds)
    {
        _find = find;
        _nodes = nodes;
        _clock = clockMilliseconds;
    }

    public DevicePower State()
    {
        var now = _clock();
        if (_node is not { } node)
        {
            if (now < _findAt) return DevicePower.Unknown;
            _findAt = now + FindAgainMilliseconds;
            if (_find() is not { } id || _nodes.Locate(id) is not { } located) return DevicePower.Unknown;
            _node = node = located;
        }
        else if (_off && now - _askedAt < OffRecheckMilliseconds)
        {
            return DevicePower.Off;
        }

        if (_nodes.PowerState(node) is not { } state)
        {
            _node = null;
            _off = false;
            _findAt = now + FindAgainMilliseconds;
            return DevicePower.Unknown;
        }
        _off = DevicePowerState.IsOff(state);
        _askedAt = now;
        return _off ? DevicePower.Off : DevicePower.On;
    }
}
