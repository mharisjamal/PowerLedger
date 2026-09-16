using System.Runtime.InteropServices;
using Microsoft.Win32;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>What one monitor answered in a read, under the device path Windows names it by: its brightness, when it was asked
/// for that and gave one, and its power state, when its power mode is one. It has at least one of the two.</summary>
internal sealed record DdcReading(string DevicePath, double? Brightness, MonitorPowerState? Power);

internal interface IBrightnessReader
{
    /// <summary>Every monitor that answered, with its power state and, when that was due, its brightness. Slow (about 40 ms a
    /// request, with 50 ms between one monitor's requests, and more the first time, when each is asked what it supports):
    /// call it off the UI thread.</summary>
    IReadOnlyList<DdcReading> Read();
}

/// <summary>
/// DDC/CI brightness and power mode (spec §5), read-only and careful, because Microsoft warns many monitors implement the
/// commands badly: each physical monitor is asked for its capabilities once, and read only if it reports brightness
/// support; a monitor that says it has no brightness is then left alone for the rest of the session - never asked anything
/// again; nothing is ever written. A monitor that reports brightness is asked for its power mode (MCCS VCP code D6) at
/// every read, and before that for its brightness once its last answer to that is <see cref="BrightnessEvery"/> old, each
/// request <see cref="RequestGap"/> after it answered the one before. GetMonitorCapabilities, GetMonitorBrightness and
/// GetVCPFeatureAndVCPFeatureReply for code D6 alone are the only requests a monitor is sent, all three of them Get
/// requests, one read at a time. A display none of whose monitors has a request due isn't opened, and every handle a read
/// opens is destroyed before it returns.
///
/// A monitor that says it has no brightness isn't asked its power mode either. No capability flag covers power mode, so
/// supporting brightness, the standard's commonest control, is the only sign a monitor answers the standard's requests
/// properly; without it the request would go to a monitor nothing has vouched for, and the user's Count tick decides for it
/// instead. A monitor that answers what it supports or its brightness in a read but fails its power-mode request is taken
/// not to support power mode, and isn't asked it again until a display change or a resume. One that fails its power-mode
/// request when that is all it is asked in a read has answered nothing, so it has failed as a whole, as below.
///
/// How long a monitor that fails a call is left alone depends on whether it has given a brightness this session. One that
/// hasn't is left alone for the rest of the session, as its firmware may be one the requests upset. One that has given a
/// brightness has shown they don't, and most often fails for a reason Windows doesn't announce - it was switched off at
/// its own button, set to another input, or is still waking - so it is left alone only for a while: the next read asks it
/// again, each failure in a row after that doubles the wait, up to <see cref="LongestWait"/>, and answering its brightness
/// or its power-mode request ends it.
///
/// A display change (a monitor plugged in or out, or display settings changed) or a resume from sleep ends all of this:
/// either forgets everything every monitor has answered, whether it supports power mode and when it last gave its
/// brightness included, and every failure, because a monitor commonly fails, or answers wrongly, while it is waking up or
/// while it is being plugged in. Only which monitors have given a brightness is kept, as neither event changes a monitor's
/// firmware. <see cref="Dispose"/> must be called once the reader is no longer wanted: the events it listens for come from
/// the static <see cref="SystemEvents"/> class, which would otherwise keep the reader alive for as long as the process
/// runs.
/// </summary>
internal sealed class DdcBrightness : IBrightnessReader, IDisposable
{
    /// <summary>MC_CAPS_BRIGHTNESS: the monitor supports GetMonitorBrightness. "GetMonitorCapabilities function
    /// (highlevelmonitorconfigurationapi.h)" names the flag; the value is the Windows SDK header's.</summary>
    internal const uint BrightnessCapability = 0x2;

    /// <summary>VCP code D6, Power Mode, in VESA's Monitor Control Command Set: the only code a monitor is ever asked for with
    /// GetVCPFeatureAndVCPFeatureReply.</summary>
    internal const byte PowerModeCode = 0xD6;

    /// <summary>The wait between a monitor's answer to one request and the next request it is sent, so that a monitor slow to
    /// finish one request isn't sent the next at once.</summary>
    internal static readonly TimeSpan RequestGap = TimeSpan.FromMilliseconds(50);

    /// <summary>How old a monitor's last answer to its brightness request must be before a read asks for its brightness
    /// again: five reads. Its power mode is asked at every read, so that a monitor switched off, if it still answers, is
    /// noticed within a minute.</summary>
    internal static readonly TimeSpan BrightnessEvery = TimeSpan.FromMinutes(5);

    /// <summary>How long a monitor that has given a brightness is left alone after it first fails: one read, so the next
    /// scheduled read asks it again.</summary>
    internal static readonly TimeSpan FirstWait = BrightnessReporter.ReadEvery;

    /// <summary>The longest a monitor that has given a brightness is left alone, however many reads in a row it has
    /// failed.</summary>
    internal static readonly TimeSpan LongestWait = TimeSpan.FromHours(1);

    /// <summary>How a monitor's device interface path begins; Contracts' MonitorKeys reads the rest.</summary>
    private const string DisplayPath = @"\\?\DISPLAY#";

    /// <summary>How early a read may start and still end a wait, or find a brightness due: half a read. Reads are scheduled
    /// a read apart, but one that starts a moment late and the next, which starts on time, are a little less than that
    /// apart, and the read due when a wait ends, or when a brightness is <see cref="BrightnessEvery"/> old, should still
    /// ask.</summary>
    private static readonly TimeSpan Leeway = BrightnessReporter.ReadEvery / 2;

    private readonly IMonitorCalls _windows;
    private readonly TimeProvider _clock;
    private readonly Action<TimeSpan> _pause;
    private readonly ISystemEvents _events;

    /// <summary>Held for the whole of a read, so reads run one at a time. A display change or a resume never takes it
    /// (<see cref="Reset"/>).</summary>
    private readonly Lock _gate = new();

    /// <summary>Monitors that said they support brightness, so are read without asking again what they support. Cleared,
    /// with the next five, by a display change or a resume.</summary>
    private readonly HashSet<string> _readable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Monitors that said they don't support brightness: never asked anything again until cleared.</summary>
    private readonly HashSet<string> _unsupported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Monitors that answered their other requests in a read but failed their power-mode request, so are taken not
    /// to support power mode: never asked it again until cleared.</summary>
    private readonly HashSet<string> _noPowerMode = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The <see cref="TimeProvider.GetTimestamp"/> at which each monitor last answered its brightness request, which
    /// decides when its brightness is next due. An answer without a range counts, so a monitor that gives one isn't asked
    /// for its brightness at every read.</summary>
    private readonly Dictionary<string, long> _brightnessAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Monitors that failed a call without having given a brightness this session: left alone until
    /// cleared.</summary>
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Monitors that have given a brightness and failed since, each with how long it is left alone and the
    /// <see cref="TimeProvider.GetTimestamp"/> of its last failure. Answering its brightness or its power-mode request takes
    /// a monitor off, and so does clearing.</summary>
    private readonly Dictionary<string, (TimeSpan Wait, long At)> _waiting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Monitors that have given a brightness this session, which decides how a failure is taken; never cleared.
    /// Every one of these seven collections is keyed by device path and holds at most one entry per monitor Windows has
    /// ever reported this run, so together they are bounded by the physical monitors the PC has shown - a handful at
    /// most - never by how long the App runs or how often it reads.</summary>
    private readonly HashSet<string> _answered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>1 once a display change or a resume has happened that no read has acted on yet.</summary>
    private int _resetDue;

    public DdcBrightness()
        : this(new WindowsMonitorCalls(), TimeProvider.System, Thread.Sleep, new WindowsSystemEvents())
    {
    }

    /// <param name="pause">Waits on the reading thread; <see cref="Thread.Sleep(TimeSpan)"/> outside tests.</param>
    public DdcBrightness(IMonitorCalls windows, TimeProvider clock, Action<TimeSpan> pause, ISystemEvents events)
    {
        _windows = windows;
        _clock = clock;
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

    /// <summary>What a monitor's power mode, the value of VCP code D6, says about whether it is on, or null for a value the
    /// standard doesn't define. The Monitor Control Command Set defines 1 as on, 2 as standby and 3 as suspend, both taken as
    /// standby here, 4 as off, and 5 as switched off the way the monitor's own power button does it. A reply carries the value
    /// in two bytes, and the standard's values for this code are the low byte's, so whatever a monitor leaves in the byte above
    /// is ignored.</summary>
    internal static MonitorPowerState? PowerState(uint mode) => (mode & 0xFF) switch
    {
        1 => MonitorPowerState.On,
        2 or 3 => MonitorPowerState.Standby,
        4 or 5 => MonitorPowerState.Off,
        _ => null,
    };

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
        ForgetIfReset();
        if (!attached.Exists(monitor => Askable(monitor.DevicePath))) return;   // nothing to ask, so no handles to open
        if (_windows.Open(display) is not { } physical) return;
        try
        {
            if (!Matched(attached, physical)) return;
            for (var index = 0; index < physical.Count; index++)
            {
                ForgetIfReset();   // a display change or a resume while the monitor before was being asked
                var path = attached[index].DevicePath;
                if (Askable(path) && Ask(physical[index].Handle, path) is { } reading) readings.Add(reading);
            }
        }
        finally
        {
            _windows.Close(physical);
        }
    }

    /// <summary>Whether the monitor may be asked anything now, and has a request due: what it supports, when that isn't
    /// known, its brightness, when that is due, or its power mode, unless it has been taken not to support that.</summary>
    private bool Askable(string path)
        => path.StartsWith(DisplayPath, StringComparison.OrdinalIgnoreCase) && !_unsupported.Contains(path) && !_failed.Contains(path) && !Waiting(path)
           && (!_readable.Contains(path) || BrightnessDue(path) || !_noPowerMode.Contains(path));

    /// <summary>Whether the monitor's brightness is to be asked: it hasn't answered its brightness request since the App
    /// started or the reader last forgot, or its last answer is <see cref="BrightnessEvery"/> old.</summary>
    private bool BrightnessDue(string path)
        => !_brightnessAt.TryGetValue(path, out var at) || _clock.GetElapsedTime(at) >= BrightnessEvery - Leeway;

    /// <summary>Whether the monitor has given a brightness, failed since, and is still being left alone for it.</summary>
    private bool Waiting(string path)
        => _waiting.TryGetValue(path, out var failure) && _clock.GetElapsedTime(failure.At) < failure.Wait - Leeway;

    /// <summary>
    /// Whether each physical monitor is the attached monitor at the same place in its list. Windows documents neither
    /// order, though in practice they agree, so the counts must match and so must each pair's descriptions. Otherwise
    /// the handles can't be told apart, and none is asked rather than one monitor's answers being reported under another's
    /// name. Nothing was asked of the monitors, so none is left alone for it: the next read looks again.
    /// </summary>
    private static bool Matched(List<AttachedMonitor> attached, IReadOnlyList<PhysicalMonitor> physical)
        => attached.Count == physical.Count
           && attached.Zip(physical).All(pair => string.Equals(pair.First.Description, pair.Second.Description, StringComparison.OrdinalIgnoreCase));

    /// <summary>What the monitor answers, first asking what it supports if that hasn't been remembered yet: its brightness,
    /// when that is due, then its power mode, unless it has been taken not to support that. Null when it gives
    /// neither.</summary>
    private DdcReading? Ask(IntPtr monitor, string path)
    {
        var answered = false;   // whether the monitor has answered a request in this read
        double? brightness = null;
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
                answered = true;
            }
            if (BrightnessDue(path))
            {
                if (answered) _pause(RequestGap);
                if (_windows.Brightness(monitor) is not { } setting) return Failed(path);
                _waiting.Remove(path);   // it answered, so its failures in a row are over
                _brightnessAt[path] = _clock.GetTimestamp();
                brightness = Normalise(setting.Minimum, setting.Current, setting.Maximum);
                if (brightness is not null) _answered.Add(path);
                answered = true;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return Failed(path);   // a call that throws has failed like any other
        }
        MonitorPowerState? power = null;
        if (!_noPowerMode.Contains(path))
        {
            if (answered) _pause(RequestGap);
            if (PowerMode(monitor) is { } mode)
            {
                _waiting.Remove(path);   // it answered, so its failures in a row are over
                power = PowerState(mode);
            }
            else if (!answered) return Failed(path);   // the only request it was sent in this read, so it has failed as a whole
            else _noPowerMode.Add(path);   // it has just answered the others, so it doesn't support this one
        }
        return brightness is null && power is null ? null : new DdcReading(path, brightness, power);
    }

    /// <summary>The monitor's power mode, or null when the request failed: one that throws has failed like any other.</summary>
    private uint? PowerMode(IntPtr monitor)
    {
        try
        {
            return _windows.PowerMode(monitor);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Leaves a monitor that has given a brightness this session alone for <see cref="FirstWait"/> after its first
    /// failure in a row, and for twice as long as the time before after each one that follows, up to
    /// <see cref="LongestWait"/>. Any other monitor is asked nothing again for the rest of the session, until a display
    /// change or a resume clears it (<see cref="Reset"/>).</summary>
    private DdcReading? Failed(string path)
    {
        if (_answered.Contains(path))
        {
            // What it has said it supports is kept, so it isn't asked that again when the wait is over.
            var wait = _waiting.TryGetValue(path, out var last) ? TimeSpan.FromTicks(Math.Min(last.Wait.Ticks * 2, LongestWait.Ticks)) : FirstWait;
            _waiting[path] = (wait, _clock.GetTimestamp());
            return null;
        }
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

    /// <summary>Makes the reader forget what every monitor has answered, and every failure, so each is asked afresh,
    /// capabilities first. SystemEvents raises a handler through the synchronization context of the thread that added
    /// it, and the App creates this reader on its UI thread, so this runs on the UI thread, perhaps while a read has held
    /// <see cref="_gate"/> for seconds, waiting on a slow monitor. It must not wait for that read, so it only marks the
    /// reset as due, and the reader forgets before it next looks at a monitor: the next one in the read already running,
    /// or the first in the next read (<see cref="ForgetIfReset"/>).</summary>
    private void Reset() => Volatile.Write(ref _resetDue, 1);

    /// <summary>Forgets what every monitor has answered, and every failure, if a display change or a resume has happened
    /// since this last looked; which monitors have given a brightness is kept. Called with <see cref="_gate"/> held,
    /// before a display's monitors are looked at and before each one is asked. A reset that arrives just after this looks
    /// is still due at the next look, so none is lost, and whatever a monitor answered in between is forgotten
    /// then.</summary>
    private void ForgetIfReset()
    {
        if (Interlocked.Exchange(ref _resetDue, 0) == 0) return;
        _readable.Clear();
        _unsupported.Clear();
        _noPowerMode.Clear();
        _brightnessAt.Clear();
        _failed.Clear();
        _waiting.Clear();
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

    /// <summary>GetVCPFeatureAndVCPFeatureReply's current value for <see cref="DdcBrightness.PowerModeCode"/>, the monitor's
    /// power mode, or null when the monitor didn't answer. No other code is ever asked for.</summary>
    uint? PowerMode(IntPtr monitor);

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

    public uint? PowerMode(IntPtr monitor)
        => Native.GetVCPFeatureAndVCPFeatureReply(monitor, DdcBrightness.PowerModeCode, out _, out var current, out _) ? current : null;

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
        // "GetMonitorBrightness function" (highlevelmonitorconfigurationapi.h), and "GetVCPFeatureAndVCPFeatureReply
        // function" (lowlevelmonitorconfigurationapi.h).

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

        /// <summary>The VCP code is a BYTE. The code type it gives back is an MC_VCP_CODE_TYPE, an enumeration, so four bytes,
        /// like the current and maximum values, which are DWORDs.</summary>
        [DllImport("dxva2.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr physicalMonitor, byte code, out uint codeType, out uint current, out uint maximum);
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
