using System.Runtime.InteropServices;

namespace PowerLedger.App;

/// <summary>A brightness read from one monitor, with the device path Windows names it by.</summary>
internal sealed record DdcReading(string DevicePath, double Brightness);

internal interface IBrightnessReader
{
    /// <summary>Every monitor that answered. Slow (about 40 ms a monitor, more the first time, when each is asked what it
    /// supports): call it off the UI thread.</summary>
    IReadOnlyList<DdcReading> Read();
}

/// <summary>
/// DDC/CI brightness (spec §5), read-only and careful, because Microsoft warns many monitors implement the commands badly:
/// each physical monitor is asked for its capabilities once, and read only if it reports brightness support; a monitor
/// that fails any call is not asked again while the App runs; nothing is ever written. GetMonitorCapabilities and
/// GetMonitorBrightness are the only requests a monitor is sent, one read at a time, and every handle a read opens is
/// destroyed before it returns.
/// </summary>
internal sealed class DdcBrightness(IMonitorCalls windows) : IBrightnessReader
{
    /// <summary>MC_CAPS_BRIGHTNESS: the monitor supports GetMonitorBrightness. "GetMonitorCapabilities function
    /// (highlevelmonitorconfigurationapi.h)" names the flag; the value is the Windows SDK header's.</summary>
    internal const uint BrightnessCapability = 0x2;

    /// <summary>How a monitor's device interface path begins; Contracts' MonitorKeys reads the rest.</summary>
    private const string DisplayPath = @"\\?\DISPLAY#";

    private readonly Lock _gate = new();

    /// <summary>Monitors that said they support brightness, so are read without being asked again.</summary>
    private readonly HashSet<string> _readable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Monitors never asked anything again: they failed a call, or don't support brightness.</summary>
    private readonly HashSet<string> _leftAlone = new(StringComparer.OrdinalIgnoreCase);

    public DdcBrightness()
        : this(new WindowsMonitorCalls())
    {
    }

    public IReadOnlyList<DdcReading> Read()
    {
        lock (_gate)
        {
            var readings = new List<DdcReading>();
            foreach (var display in Displays())
            {
                try
                {
                    ReadDisplay(display, readings);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    // Windows wouldn't list, open or close this display's monitors. No monitor failed a call, so the next
                    // read tries again.
                }
            }
            return readings;
        }
    }

    /// <summary>Where the current setting sits between the monitor's minimum and maximum, from 0 to 1, or null when the
    /// monitor gives no range. The settings have no real-world units ("Using the High-Level Monitor Configuration
    /// Functions"), so the share of the range is all a reading says.</summary>
    internal static double? Normalise(uint minimum, uint current, uint maximum)
        => maximum > minimum ? Math.Clamp(((double)current - minimum) / ((double)maximum - minimum), 0, 1) : null;

    private IReadOnlyList<IntPtr> Displays()
    {
        try
        {
            return windows.Displays();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return [];
        }
    }

    private void ReadDisplay(IntPtr display, List<DdcReading> readings)
    {
        var attached = windows.Attached(display).Where(monitor => monitor.Active).ToList();
        if (!attached.Exists(monitor => Askable(monitor.DevicePath))) return;   // nothing to ask, so no handles to open
        if (windows.Open(display) is not { } physical) return;
        try
        {
            if (!Matched(attached, physical)) return;
            for (var index = 0; index < physical.Count; index++)
            {
                var path = attached[index].DevicePath;
                if (Askable(path) && Ask(physical[index].Handle, path) is { } brightness) readings.Add(new DdcReading(path, brightness));
            }
        }
        finally
        {
            windows.Close(physical);
        }
    }

    private bool Askable(string path) => path.StartsWith(DisplayPath, StringComparison.OrdinalIgnoreCase) && !_leftAlone.Contains(path);

    /// <summary>
    /// Whether each physical monitor is the attached monitor at the same place in its list. Windows documents neither
    /// order, though in practice they agree, so the counts must match and so must each pair's descriptions. Otherwise
    /// the handles can't be told apart, and none is asked rather than one monitor's brightness being reported under
    /// another's name. Nothing was asked of the monitors, so none is left alone for it: the next read looks again.
    /// </summary>
    private static bool Matched(List<AttachedMonitor> attached, IReadOnlyList<PhysicalMonitor> physical)
        => attached.Count == physical.Count
           && attached.Zip(physical).All(pair => string.Equals(pair.First.Description, pair.Second.Description, StringComparison.OrdinalIgnoreCase));

    /// <summary>The monitor's brightness, first asking what it supports if it has never been asked.</summary>
    private double? Ask(IntPtr monitor, string path)
    {
        try
        {
            if (!_readable.Contains(path))
            {
                // A monitor without DDC/CI, such as a laptop's own panel, fails here, as expected.
                if (windows.Capabilities(monitor) is not { } capabilities || (capabilities & BrightnessCapability) == 0) return LeaveAlone(path);
                _readable.Add(path);
            }
            return windows.Brightness(monitor) is { } setting ? Normalise(setting.Minimum, setting.Current, setting.Maximum) : LeaveAlone(path);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return LeaveAlone(path);   // a call that throws has failed like any other
        }
    }

    /// <summary>Never asks the monitor anything again while the App runs.</summary>
    private double? LeaveAlone(string path)
    {
        _readable.Remove(path);
        _leftAlone.Add(path);
        return null;
    }
}

/// <summary>A monitor attached to a display, as EnumDisplayDevicesW lists it.</summary>
/// <param name="DevicePath">Its device interface path, e.g.
/// <c>\\?\DISPLAY#DELA0B1#5&amp;2f5a1b&amp;0&amp;UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}</c>.</param>
/// <param name="Description">What Windows calls it, e.g. "Generic PnP Monitor".</param>
/// <param name="Active">Whether Windows presents it as on.</param>
internal readonly record struct AttachedMonitor(string DevicePath, string Description, bool Active);

/// <summary>A physical monitor Dxva2 opened a handle for, with the description it gives the monitor.</summary>
internal readonly record struct PhysicalMonitor(IntPtr Handle, string Description);

/// <summary>The Windows calls <see cref="DdcBrightness"/> makes, behind an interface so that its rules can be tested
/// without monitors. A call Windows refuses answers null or nothing rather than throwing.</summary>
internal interface IMonitorCalls
{
    /// <summary>The displays on the desktop: EnumDisplayMonitors' HMONITORs.</summary>
    IReadOnlyList<IntPtr> Displays();

    /// <summary>The monitors attached to a display, in Windows' order: GetMonitorInfoW for the display's device name, then
    /// EnumDisplayDevicesW.</summary>
    IReadOnlyList<AttachedMonitor> Attached(IntPtr display);

    /// <summary>The display's physical monitors from GetPhysicalMonitorsFromHMONITOR, or null when Windows won't open
    /// them. Every list this returns must be given to <see cref="Close"/>.</summary>
    IReadOnlyList<PhysicalMonitor>? Open(IntPtr display);

    /// <summary>GetMonitorCapabilities' MC_CAPS flags, or null when the monitor didn't answer.</summary>
    uint? Capabilities(IntPtr monitor);

    /// <summary>GetMonitorBrightness' minimum, current and maximum, or null when the monitor didn't answer.</summary>
    (uint Minimum, uint Current, uint Maximum)? Brightness(IntPtr monitor);

    /// <summary>DestroyPhysicalMonitors, for a list <see cref="Open"/> returned.</summary>
    void Close(IReadOnlyList<PhysicalMonitor> monitors);
}

/// <summary>The calls as Windows answers them, through User32 and Dxva2. Nothing here writes to a monitor.</summary>
internal sealed class WindowsMonitorCalls : IMonitorCalls
{
    /// <summary>More monitors than this on one display is not a count to believe; it also bounds the listing.</summary>
    private const uint MostMonitors = 16;

    public IReadOnlyList<IntPtr> Displays()
    {
        var displays = new List<IntPtr>();
        // The callback only collects the handles, so nothing can throw back through Windows' frames.
        return Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (display, _, _, _) => { displays.Add(display); return true; }, IntPtr.Zero)
            ? displays
            : [];
    }

    public IReadOnlyList<AttachedMonitor> Attached(IntPtr display)
    {
        var info = new Native.MonitorInfoEx { Size = (uint)Marshal.SizeOf<Native.MonitorInfoEx>() };
        if (!Native.GetMonitorInfoW(display, ref info) || string.IsNullOrEmpty(info.Device)) return [];
        var monitors = new List<AttachedMonitor>();
        // Given a display's device name, each index is one of its monitors, until the call fails ("EnumDisplayDevicesW
        // function (winuser.h)").
        for (uint index = 0; index < MostMonitors; index++)
        {
            var device = new Native.DisplayDevice { Size = (uint)Marshal.SizeOf<Native.DisplayDevice>() };
            if (!Native.EnumDisplayDevicesW(info.Device, index, ref device, Native.DeviceInterfaceName)) break;
            monitors.Add(new AttachedMonitor(device.DeviceId ?? "", device.DeviceString ?? "", (device.StateFlags & Native.DisplayDeviceActive) != 0));
        }
        return monitors;
    }

    public IReadOnlyList<PhysicalMonitor>? Open(IntPtr display)
    {
        if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(display, out var count) || count > MostMonitors) return null;
        if (count == 0) return [];   // nothing to open, so nothing to destroy
        var monitors = new Native.PhysicalMonitorInfo[count];
        if (!Native.GetPhysicalMonitorsFromHMONITOR(display, count, monitors)) return null;
        return [.. monitors.Select(monitor => new PhysicalMonitor(monitor.Handle, monitor.Description ?? ""))];
    }

    public uint? Capabilities(IntPtr monitor)
        => Native.GetMonitorCapabilities(monitor, out var capabilities, out _) ? capabilities : null;

    public (uint Minimum, uint Current, uint Maximum)? Brightness(IntPtr monitor)
        => Native.GetMonitorBrightness(monitor, out var minimum, out var current, out var maximum) ? (minimum, current, maximum) : null;

    public void Close(IReadOnlyList<PhysicalMonitor> monitors)
    {
        if (monitors.Count == 0) return;   // a display with no physical monitors: Open opened nothing
        // DestroyPhysicalMonitors closes each element's handle; the descriptions play no part.
        Native.DestroyPhysicalMonitors((uint)monitors.Count, [.. monitors.Select(monitor => new Native.PhysicalMonitorInfo { Handle = monitor.Handle, Description = "" })]);
    }

    /// <summary>The declarations, each checked against the Microsoft reference page its comment names.</summary>
    private static class Native
    {
        /// <summary>EDD_GET_DEVICE_INTERFACE_NAME: DeviceID then holds the monitor's device interface path, not its hardware
        /// id ("EnumDisplayDevicesW function (winuser.h)" gives 0x00000001).</summary>
        public const uint DeviceInterfaceName = 0x1;

        /// <summary>DISPLAY_DEVICE_ACTIVE: a monitor Windows presents as on ("DISPLAY_DEVICEW (wingdi.h)"; the value is
        /// wingdi.h's).</summary>
        public const uint DisplayDeviceActive = 0x1;

        /// <summary>MONITORENUMPROC ("MONITORENUMPROC (winuser.h)"): true carries on to the next display.</summary>
        [return: MarshalAs(UnmanagedType.Bool)]
        public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, IntPtr rectangle, IntPtr data);

        /// <summary>MONITORINFOEXW ("MONITORINFOEXW (winuser.h)", "MONITORINFO (winuser.h)"): MONITORINFO's size, two
        /// rectangles and flags, then the display's device name, such as \\.\DISPLAY1, in CCHDEVICENAME (32) characters.
        /// 104 bytes.</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MonitorInfoEx
        {
            public uint Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        /// <summary>DISPLAY_DEVICEW ("DISPLAY_DEVICEW (wingdi.h)"), 840 bytes. Listing a display's monitors, DeviceString is
        /// a monitor's description, and DeviceID its device interface path when asked for with
        /// <see cref="DeviceInterfaceName"/>.</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DisplayDevice
        {
            public uint Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        /// <summary>PHYSICAL_MONITOR ("PHYSICAL_MONITOR (physicalmonitorenumerationapi.h)"): a handle and a description of
        /// PHYSICAL_MONITOR_DESCRIPTION_SIZE (128) characters. The SDK header declares it under #pragma pack(1); on 64-bit
        /// Windows that changes nothing, as 8 + 256 bytes needs no padding, but Pack = 1 keeps to the header.</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
        public struct PhysicalMonitorInfo
        {
            public IntPtr Handle;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        }

        /// <summary>"EnumDisplayMonitors function (winuser.h)": with no device context and no clip, every display.</summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        /// <summary>"GetMonitorInfoW function (winuser.h)": the size must be set to MONITORINFOEX's for the device name.</summary>
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfoEx info);

        /// <summary>"EnumDisplayDevicesW function (winuser.h)": the size must be set before every call.</summary>
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayDevicesW(string device, uint deviceNumber, ref DisplayDevice displayDevice, uint flags);

        // Dxva2's functions return _BOOL, which physicalmonitorenumerationapi.h defines as BOOL. The pages are
        // "GetNumberOfPhysicalMonitorsFromHMONITOR function", "GetPhysicalMonitorsFromHMONITOR function" and
        // "DestroyPhysicalMonitors function" (physicalmonitorenumerationapi.h), "GetMonitorCapabilities function" and
        // "GetMonitorBrightness function" (highlevelmonitorconfigurationapi.h).

        [DllImport("dxva2.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

        [DllImport("dxva2.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitorInfo[] monitors);

        [DllImport("dxva2.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyPhysicalMonitors(uint count, [In] PhysicalMonitorInfo[] monitors);

        [DllImport("dxva2.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorCapabilities(IntPtr physicalMonitor, out uint capabilities, out uint supportedColorTemperatures);

        [DllImport("dxva2.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorBrightness(IntPtr physicalMonitor, out uint minimum, out uint current, out uint maximum);
    }
}
