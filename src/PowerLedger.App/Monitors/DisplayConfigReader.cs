using System.Runtime.InteropServices;

namespace PowerLedger.App;

/// <summary>How Windows drives one monitor, under the device path it names the monitor by: the physical refresh rate, in
/// hertz, and whether HDR is on.</summary>
internal sealed record DisplayReading(string DevicePath, double RefreshHz, bool Hdr);

internal interface IDisplayReader
{
    /// <summary>Every monitor Windows drives on an active display path, with its refresh rate and whether HDR is on. Quick, as
    /// it asks Windows alone, never a monitor.</summary>
    IReadOnlyList<DisplayReading> Read();
}

/// <summary>
/// The refresh rate and HDR state of each monitor Windows drives (Plan K), from Windows' display configuration:
/// QueryDisplayConfig's active paths, then DisplayConfigGetDeviceInfo for each path's monitor and its color state. These
/// are Windows' own records, so no monitor is sent anything, and a monitor that doesn't answer over its cable is read as
/// well as one that does.
///
/// A path's rate is its target's refreshRate, except on a path Windows boosts between a virtual rate and the monitor's own
/// (Dynamic Refresh Rate), where refreshRate is the virtual one and the physical rate is the target mode's vSyncFreq.
/// Windows is asked with QDC_VIRTUAL_REFRESH_RATE_AWARE, which only Windows 11 knows; Windows 10, which refuses it, is asked
/// again without it, having no virtual rates to tell apart. HDR is ADVANCED_COLOR_INFO_2's highDynamicRangeUserEnabled,
/// where Windows has that request, and ADVANCED_COLOR_INFO's advancedColorEnabled where it doesn't. A monitor Windows gives
/// no color state for either way is taken not to have HDR on, as a reading has no word for "can't tell" and its refresh
/// rate is still worth reporting. A path whose monitor, or a refresh rate to believe, can't be read gives nothing.
/// </summary>
internal sealed class DisplayConfigReader(IDisplayConfig windows) : IDisplayReader
{
    /// <summary>QDC_ONLY_ACTIVE_PATHS ("QueryDisplayConfig function (winuser.h)"): the paths Windows is driving now.</summary>
    internal const uint OnlyActivePaths = 0x2;

    /// <summary>QDC_VIRTUAL_REFRESH_RATE_AWARE (the same page): the caller knows a path's refresh rate can be a virtual one.
    /// Windows 11 and later.</summary>
    internal const uint VirtualRefreshRateAware = 0x40;

    /// <summary>DISPLAYCONFIG_PATH_ACTIVE ("DISPLAYCONFIG_PATH_INFO structure (wingdi.h)").</summary>
    internal const uint PathActive = 0x1;

    /// <summary>DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE (the same page): the path's target names its desktop image and its
    /// target mode in two halves of one index.</summary>
    internal const uint SupportVirtualMode = 0x8;

    /// <summary>DISPLAYCONFIG_PATH_BOOST_REFRESH_RATE (the same page): Dynamic Refresh Rate. Windows 11 and later.</summary>
    internal const uint BoostRefreshRate = 0x10;

    /// <summary>DISPLAYCONFIG_MODE_INFO_TYPE_TARGET ("DISPLAYCONFIG_MODE_INFO_TYPE enumeration (wingdi.h)").</summary>
    internal const uint TargetModeType = 2;

    /// <summary>ERROR_INVALID_PARAMETER, which QueryDisplayConfig gives for flags it doesn't know.</summary>
    internal const int InvalidParameter = 87;

    /// <summary>highDynamicRangeUserEnabled, bit 5 of DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2's union: the user has switched
    /// HDR on. Its bits, from bit 0, are advancedColorSupported, advancedColorActive, reserved1,
    /// advancedColorLimitedByPolicy, highDynamicRangeSupported, highDynamicRangeUserEnabled, wideColorSupported and
    /// wideColorUserEnabled.</summary>
    internal const uint HdrUserEnabled = 0x20;

    /// <summary>advancedColorEnabled, bit 1 of DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO's union, after
    /// advancedColorSupported.</summary>
    internal const uint AdvancedColorEnabled = 0x2;

    /// <summary>The slowest refresh rate to believe, which is also the slowest the service takes.</summary>
    private const double SlowestHz = 1;

    /// <summary>The fastest refresh rate to believe, which is also the fastest the service takes.</summary>
    private const double FastestHz = 1000;

    public DisplayConfigReader()
        : this(new WindowsDisplayConfig())
    {
    }

    public IReadOnlyList<DisplayReading> Read()
    {
        DisplayConfigPath[] paths;
        DisplayConfigMode[] modes;
        try
        {
            var error = windows.Query(OnlyActivePaths | VirtualRefreshRateAware, out paths, out modes);
            if (error == InvalidParameter) error = windows.Query(OnlyActivePaths, out paths, out modes);   // Windows 10
            if (error != 0) return [];
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return [];   // Windows wouldn't describe its displays this time; the next read asks again
        }
        var readings = new List<DisplayReading>();
        foreach (var path in paths)
        {
            try
            {
                if ((path.Flags & PathActive) == 0 || path.TargetInfo.TargetAvailable == 0 || RefreshHz(path, modes) is not { } hertz) continue;
                var target = path.TargetInfo;
                if (windows.MonitorDevicePath(target.AdapterId, target.Id) is not { Length: > 0 } devicePath) continue;
                readings.Add(new DisplayReading(devicePath, hertz, Hdr(target)));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // Windows wouldn't describe this path's monitor; the others are still read.
            }
        }
        return readings;
    }

    /// <summary>The monitor's physical refresh rate on this path, or null when Windows gives none to believe.</summary>
    private static double? RefreshHz(DisplayConfigPath path, DisplayConfigMode[] modes)
    {
        var rate = path.TargetInfo.RefreshRate;
        if ((path.Flags & BoostRefreshRate) != 0)
        {
            // An invalid index, 0xFFFF in the half or 0xFFFFFFFF whole, lies past the end of the modes like any other.
            var index = (path.Flags & SupportVirtualMode) != 0 ? path.TargetInfo.ModeInfoIndex >> 16 : path.TargetInfo.ModeInfoIndex;
            if (index >= (uint)modes.Length || modes[index].InfoType != TargetModeType) return null;
            rate = modes[index].TargetMode.VSyncFreq;
        }
        if (rate.Denominator == 0) return null;
        var hertz = (double)rate.Numerator / rate.Denominator;
        return hertz is >= SlowestHz and <= FastestHz ? hertz : null;
    }

    private bool Hdr(DisplayConfigPathTarget target)
        => windows.AdvancedColor2(target.AdapterId, target.Id) is { } newer
            ? (newer & HdrUserEnabled) != 0
            : windows.AdvancedColor(target.AdapterId, target.Id) is { } older && (older & AdvancedColorEnabled) != 0;
}

/// <summary>The Windows calls <see cref="DisplayConfigReader"/> makes, behind an interface so that its rules can be tested
/// without displays.</summary>
internal interface IDisplayConfig
{
    /// <summary>GetDisplayConfigBufferSizes, then QueryDisplayConfig, for these flags: the error code, and the paths and modes
    /// Windows gave when that is 0, ERROR_SUCCESS.</summary>
    int Query(uint flags, out DisplayConfigPath[] paths, out DisplayConfigMode[] modes);

    /// <summary>DisplayConfigGetDeviceInfo's GET_TARGET_NAME: the monitor's device interface path, or null when Windows
    /// refused or gave none.</summary>
    string? MonitorDevicePath(Luid adapter, uint target);

    /// <summary>DisplayConfigGetDeviceInfo's GET_ADVANCED_COLOR_INFO_2: the flags of its union, or null when Windows refused,
    /// as one from before the request does.</summary>
    uint? AdvancedColor2(Luid adapter, uint target);

    /// <summary>DisplayConfigGetDeviceInfo's GET_ADVANCED_COLOR_INFO: the flags of its union, or null when Windows
    /// refused.</summary>
    uint? AdvancedColor(Luid adapter, uint target);
}

/// <summary>LUID ("LUID structure (winnt.h)"): here, which graphics adapter a path or mode is on. 8 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Luid
{
    public uint LowPart;
    public int HighPart;
}

/// <summary>DISPLAYCONFIG_RATIONAL ("DISPLAYCONFIG_RATIONAL structure (wingdi.h)"). 8 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigRational
{
    public uint Numerator;
    public uint Denominator;
}

/// <summary>DISPLAYCONFIG_2DREGION ("DISPLAYCONFIG_2DREGION structure (wingdi.h)"). 8 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfig2DRegion
{
    public uint Width;
    public uint Height;
}

/// <summary>DISPLAYCONFIG_PATH_SOURCE_INFO ("DISPLAYCONFIG_PATH_SOURCE_INFO structure (wingdi.h)"): an adapter, a source id,
/// the union of modeInfoIdx with cloneGroupId and sourceModeInfoIdx, and statusFlags. 20 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigPathSource
{
    public Luid AdapterId;
    public uint Id;
    public uint ModeInfoIndex;
    public uint StatusFlags;
}

/// <summary>DISPLAYCONFIG_PATH_TARGET_INFO ("DISPLAYCONFIG_PATH_TARGET_INFO structure (wingdi.h)"). Its enumerations are
/// four bytes each, and its BOOL is an int. 48 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigPathTarget
{
    public Luid AdapterId;
    public uint Id;

    /// <summary>The union: modeInfoIdx, or, when the path has <see cref="DisplayConfigReader.SupportVirtualMode"/>,
    /// desktopModeInfoIdx in the low 16 bits and targetModeInfoIdx in the high 16.</summary>
    public uint ModeInfoIndex;

    public uint OutputTechnology;
    public uint Rotation;
    public uint Scaling;
    public DisplayConfigRational RefreshRate;
    public uint ScanLineOrdering;
    public int TargetAvailable;
    public uint StatusFlags;
}

/// <summary>DISPLAYCONFIG_PATH_INFO ("DISPLAYCONFIG_PATH_INFO structure (wingdi.h)"). 72 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigPath
{
    public DisplayConfigPathSource SourceInfo;
    public DisplayConfigPathTarget TargetInfo;
    public uint Flags;
}

/// <summary>DISPLAYCONFIG_VIDEO_SIGNAL_INFO ("DISPLAYCONFIG_VIDEO_SIGNAL_INFO structure (wingdi.h)"), which is all
/// DISPLAYCONFIG_TARGET_MODE holds: its UINT64 aligns it to eight bytes. 48 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigVideoSignal
{
    public ulong PixelRate;
    public DisplayConfigRational HSyncFreq;
    public DisplayConfigRational VSyncFreq;
    public DisplayConfig2DRegion ActiveSize;
    public DisplayConfig2DRegion TotalSize;
    public uint VideoStandard;
    public uint ScanLineOrdering;
}

/// <summary>DISPLAYCONFIG_MODE_INFO ("DISPLAYCONFIG_MODE_INFO structure (wingdi.h)"): infoType, an id and an adapter, then a
/// union of a target mode, a source mode and a desktop image. The target mode is the union's largest member, at 48 bytes,
/// so it stands for the union, and is only a target mode when infoType says so. 64 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigMode
{
    public uint InfoType;
    public uint Id;
    public Luid AdapterId;
    public DisplayConfigVideoSignal TargetMode;
}

/// <summary>The calls as Windows answers them, through User32. Nothing here changes a display.</summary>
internal sealed class WindowsDisplayConfig : IDisplayConfig
{
    /// <summary>ERROR_INSUFFICIENT_BUFFER: the configuration changed between asking how much room it needs and asking for
    /// it.</summary>
    private const int InsufficientBuffer = 122;

    /// <summary>ERROR_GEN_FAILURE, given here for a count Windows gives that isn't one to believe.</summary>
    private const int GenFailure = 31;

    /// <summary>More active paths than this is not a count to believe; it also bounds the buffers. Each path has at most a
    /// source mode, a target mode and a desktop image.</summary>
    private const uint MostPaths = 64;

    /// <summary>How many times the query is made while the configuration keeps changing under it.</summary>
    private const int Attempts = 3;

    /// <summary>DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME ("DISPLAYCONFIG_DEVICE_INFO_TYPE enumeration (wingdi.h)").</summary>
    private const uint TargetNameType = 2;

    /// <summary>DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO (the same page).</summary>
    private const uint AdvancedColorInfoType = 9;

    /// <summary>DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2 (the same page), which follows five members that count
    /// on from GET_SDR_WHITE_LEVEL, 11.</summary>
    private const uint AdvancedColorInfo2Type = 15;

    public int Query(uint flags, out DisplayConfigPath[] paths, out DisplayConfigMode[] modes)
    {
        paths = [];
        modes = [];
        for (var attempt = 1; ; attempt++)
        {
            var error = Native.GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
            if (error != 0) return error;
            if (pathCount > MostPaths || modeCount > MostPaths * 3) return GenFailure;
            var pathBuffer = new DisplayConfigPath[pathCount];
            var modeBuffer = new DisplayConfigMode[modeCount];
            error = Native.QueryDisplayConfig(flags, ref pathCount, pathBuffer, ref modeCount, modeBuffer, IntPtr.Zero);
            if (error == InsufficientBuffer && attempt < Attempts) continue;
            if (error != 0) return error;
            // Windows may fill fewer than it asked room for.
            paths = pathBuffer[..(int)Math.Min(pathCount, (uint)pathBuffer.Length)];
            modes = modeBuffer[..(int)Math.Min(modeCount, (uint)modeBuffer.Length)];
            return 0;
        }
    }

    public string? MonitorDevicePath(Luid adapter, uint target)
    {
        var request = new Native.TargetDeviceName { Header = Header(TargetNameType, Marshal.SizeOf<Native.TargetDeviceName>(), adapter, target) };
        return Native.DisplayConfigGetDeviceInfo(ref request) == 0 && !string.IsNullOrEmpty(request.MonitorDevicePath) ? request.MonitorDevicePath : null;
    }

    public uint? AdvancedColor2(Luid adapter, uint target)
    {
        var request = new Native.AdvancedColorInfo2 { Header = Header(AdvancedColorInfo2Type, Marshal.SizeOf<Native.AdvancedColorInfo2>(), adapter, target) };
        return Native.DisplayConfigGetDeviceInfo(ref request) == 0 ? request.Flags : null;
    }

    public uint? AdvancedColor(Luid adapter, uint target)
    {
        var request = new Native.AdvancedColorInfo { Header = Header(AdvancedColorInfoType, Marshal.SizeOf<Native.AdvancedColorInfo>(), adapter, target) };
        return Native.DisplayConfigGetDeviceInfo(ref request) == 0 ? request.Flags : null;
    }

    /// <summary>A request's header: its type, the size of the whole request, and the path's adapter and target.</summary>
    private static Native.DeviceInfoHeader Header(uint type, int size, Luid adapter, uint target)
        => new() { Type = type, Size = (uint)size, AdapterId = adapter, Id = target };

    /// <summary>The declarations, each checked against the Microsoft reference page its comment names. Internal, so that a
    /// test can check each structure's layout.</summary>
    internal static class Native
    {
        /// <summary>DISPLAYCONFIG_DEVICE_INFO_HEADER ("DISPLAYCONFIG_DEVICE_INFO_HEADER structure (wingdi.h)"). 20 bytes.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct DeviceInfoHeader
        {
            public uint Type;
            public uint Size;
            public Luid AdapterId;
            public uint Id;
        }

        /// <summary>DISPLAYCONFIG_TARGET_DEVICE_NAME ("DISPLAYCONFIG_TARGET_DEVICE_NAME structure (wingdi.h)"). 420
        /// bytes.</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct TargetDeviceName
        {
            public DeviceInfoHeader Header;
            public uint Flags;
            public uint OutputTechnology;
            public ushort EdidManufactureId;
            public ushort EdidProductCodeId;
            public uint ConnectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string MonitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string MonitorDevicePath;
        }

        /// <summary>DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO (wingdi.h): the header, a union of flags, the color encoding (an
        /// enumeration) and the bits per color channel. 32 bytes.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct AdvancedColorInfo
        {
            public DeviceInfoHeader Header;
            public uint Flags;
            public uint ColorEncoding;
            public uint BitsPerColorChannel;
        }

        /// <summary>DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 (wingdi.h), which only newer Windows answers: as
        /// <see cref="AdvancedColorInfo"/>, with other flags in the union, then the active color mode (an enumeration). 36
        /// bytes.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct AdvancedColorInfo2
        {
            public DeviceInfoHeader Header;
            public uint Flags;
            public uint ColorEncoding;
            public uint BitsPerColorChannel;
            public uint ActiveColorMode;
        }

        /// <summary>"GetDisplayConfigBufferSizes function (winuser.h)": the room QueryDisplayConfig needs, for the same
        /// flags.</summary>
        [DllImport("user32.dll")]
        public static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

        /// <summary>"QueryDisplayConfig function (winuser.h)": the topology must be null unless the flags ask for
        /// QDC_DATABASE_CURRENT.</summary>
        [DllImport("user32.dll")]
        public static extern int QueryDisplayConfig(
            uint flags, ref uint pathCount, [Out] DisplayConfigPath[] paths, ref uint modeCount, [Out] DisplayConfigMode[] modes, IntPtr currentTopologyId);

        // "DisplayConfigGetDeviceInfo function (winuser.h)" takes a request that begins with its header, one declaration
        // for each request made.

        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName request);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo request);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo2 request);
    }
}
