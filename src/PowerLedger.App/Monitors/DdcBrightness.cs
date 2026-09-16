using System.Runtime.InteropServices;
using Microsoft.Win32;

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
/// each physical monitor is asked for its capabilities once, and read only if it reports brightness support, 50 ms after
/// it answered; a monitor that says it has no brightness, or that fails any call, is then left alone for the rest of the
/// session - never asked either request again; nothing is ever written. GetMonitorCapabilities and GetMonitorBrightness
/// are the only requests a monitor is sent, one read at a time, and every handle a read opens is destroyed before it
/// returns.
///
/// The one exception is a display change (a monitor plugged in or out, or display settings changed) or a resume from
/// sleep: either forgets everything every monitor has answered, because a monitor commonly fails, or answers wrongly,
/// while it is waking up or while it is being plugged in. <see cref="Dispose"/> must be called once the reader is no
/// longer wanted: the events it listens for come from the static <see cref="SystemEvents"/> class, which would otherwise
/// keep the reader alive for as long as the process runs.
/// </summary>
internal sealed class DdcBrightness : IBrightnessReader, IDisposable
{
    /// <summary>MC_CAPS_BRIGHTNESS: the monitor supports GetMonitorBrightness. "GetMonitorCapabilities function
    /// (highlevelmonitorconfigurationapi.h)" names the flag; the value is the Windows SDK header's.</summary>
    internal const uint BrightnessCapability = 0x2;

    /// <summary>The wait between a monitor's answer about what it supports and the request for its brightness, so that a
    /// monitor slow to finish one request isn't sent the next at once.</summary>
    internal static readonly TimeSpan RequestGap = TimeSpan.FromMilliseconds(50);

    /// <summary>How a monitor's device interface path begins; Contracts' MonitorKeys reads the rest.</summary>
    private const string DisplayPath = @"\\?\DISPLAY#";

    private readonly IMonitorCalls _windows;
    private readonly Action<TimeSpan> _pause;
    private readonly ISystemEvents _events;
    private readonly Lock _gate = new();

    /// <summary>Monitors that said they support brightness, so are read without asking again what they support. Cleared,
    /// with the two sets below, by a display change or a resume.</summary>
    private readonly HashSet<string> _readable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Monitors that said they don't support brightness: never asked anything again until cleared.</summary>
    private readonly HashSet<string> _unsupported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Monitors that failed a call: left alone until cleared. Every one of these three sets is keyed by device
    /// path and gains at most one entry per monitor Windows has ever reported this run, so together they are bounded by
    /// the physical monitors the PC has shown - a handful at most - never by how long the App runs or how often it
    /// reads.</summary>
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);

    public DdcBrightness()
        : this(new WindowsMonitorCalls(), Thread.Sleep, new WindowsSystemEvents())
    {
    }

    /// <param name="pause">Waits on the reading thread; <see cref="Thread.Sleep(TimeSpan)"/> outside tests.</param>
    public DdcBrightness(IMonitorCalls windows, Action<TimeSpan> pause, ISystemEvents events)
    {
        _windows = windows;
        _pause = pause;
        _events = events;
        _events.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _events.PowerModeChanged += OnPowerModeChanged;
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

    /// <summary>Unsubscribes from the events this reader listens for. <see cref="SystemEvents"/>' handlers are static, so
    /// without this the reader would stay referenced, and so alive, for as long as the process runs, however long ago
    /// the App stopped wanting it.</summary>
    public void Dispose()
    {
        _events.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _events.PowerModeChanged -= OnPowerModeChanged;
    }

    private IReadOnlyList<IntPtr> Displays()
    {
        try
        {
            return _windows.Displays();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return [];
        }
    }

    private void ReadDisplay(IntPtr display, List<DdcReading> readings)
    {
        var attached = _windows.Attached(display).Where(monitor => monitor.Active).ToList();
        if (!attached.Exists(monitor => Askable(monitor.DevicePath))) return;   // nothing to ask, so no handles to open
        if (_windows.Open(display) is not { } physical) return;
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
            _windows.Close(physical);
        }
    }

    private bool Askable(string path)
        => path.StartsWith(DisplayPath, StringComparison.OrdinalIgnoreCase) && !_unsupported.Contains(path) && !_failed.Contains(path);

    /// <summary>
    /// Whether each physical monitor is the attached monitor at the same place in its list. Windows documents neither
    /// order, though in practice they agree, so the counts must match and so must each pair's descriptions. Otherwise
    /// the handles can't be told apart, and none is asked rather than one monitor's brightness being reported under
    /// another's name. Nothing was asked of the monitors, so none is left alone for it: the next read looks again.
    /// </summary>
    private static bool Matched(List<AttachedMonitor> attached, IReadOnlyList<PhysicalMonitor> physical)
        => attached.Count == physical.Count
           && attached.Zip(physical).All(pair => string.Equals(pair.First.Description, pair.Second.Description, StringComparison.OrdinalIgnoreCase));

    /// <summary>The monitor's brightness, first asking what it supports if that hasn't been remembered yet.</summary>
    private double? Ask(IntPtr monitor, string path)
    {
        try
        {
            if (!_readable.Contains(path))
            {
                // A monitor without DDC/CI, such as a laptop's own panel, fails here, as expected.
                if (_windows.Capabilities(monitor) is not { } capabilities) return Failed(path);
                if ((capabilities & BrightnessCapability) == 0)
                {
                    _unsupported.Add(path);
                    return null;
                }
                _readable.Add(path);
                _pause(RequestGap);
            }
            return _windows.Brightness(monitor) is { } setting ? Normalise(setting.Minimum, setting.Current, setting.Maximum) : Failed(path);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return Failed(path);   // a call that throws has failed like any other
        }
    }

    /// <summary>Asks the monitor nothing again for the rest of the session, until a display change or a resume clears it
    /// (<see cref="Reset"/>).</summary>
    private double? Failed(string path)
    {
        _readable.Remove(path);
        _failed.Add(path);
        return null;
    }

    /// <summary>A display change: SystemEvents.DisplaySettingsChanged, raised as much for a monitor plugged in or out as
    /// for a resolution or arrangement change - there's no way to tell those apart, so any of them clears every
    /// monitor's answer.</summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Reset();

    /// <summary>SystemEvents.PowerModeChanged also fires for a suspend and for a status change (such as a battery
    /// warning); only a resume is a reason a monitor might be worth asking again.</summary>
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Reset();
    }

    /// <summary>Forgets what every monitor has answered, so the next read asks each afresh, capabilities first.
    /// SystemEvents raises both events this reader listens for on a thread of its own - never the UI thread, and not
    /// necessarily whatever thread is running <see cref="Read"/> - so this takes the same lock <see cref="Read"/>
    /// does.</summary>
    private void Reset()
    {
        lock (_gate)
        {
            _readable.Clear();
            _unsupported.Clear();
            _failed.Clear();
        }
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

/// <summary>The two system events that clear what <see cref="DdcBrightness"/> has learned about every monitor, behind an
/// interface so a test can raise them without real hardware or a real display change. <see cref="WindowsSystemEvents"/>
/// wires this to the static <see cref="SystemEvents"/> class.</summary>
internal interface ISystemEvents
{
    /// <summary>SystemEvents.DisplaySettingsChanged: a monitor plugged in or out, or display settings changed.</summary>
    event EventHandler? DisplaySettingsChanged;

    /// <summary>SystemEvents.PowerModeChanged: the system suspended, resumed, or its status changed.</summary>
    event PowerModeChangedEventHandler? PowerModeChanged;
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

/// <summary>Forwards to the static <see cref="SystemEvents"/> class. Its handlers are static and, unlike an ordinary
/// event source, keep whatever subscribes to them alive until it unsubscribes or the process ends, so
/// <see cref="DdcBrightness.Dispose"/> removes its handlers through this rather than only dropping the App's own
/// reference to the reader.</summary>
internal sealed class WindowsSystemEvents : ISystemEvents
{
    public event EventHandler? DisplaySettingsChanged
    {
        add => SystemEvents.DisplaySettingsChanged += value;
        remove => SystemEvents.DisplaySettingsChanged -= value;
    }

    public event PowerModeChangedEventHandler? PowerModeChanged
    {
        add => SystemEvents.PowerModeChanged += value;
        remove => SystemEvents.PowerModeChanged -= value;
    }
}
