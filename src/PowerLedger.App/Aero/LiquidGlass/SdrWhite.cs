using System.Runtime.InteropServices;

namespace PowerLedger.App.Aero;

/// <summary>
/// Where SDR white sits on an HDR monitor, in scRGB (1.0 is 80 nits): Windows' "SDR content brightness", which it
/// shows SDR windows at, a browser's among them. The GPU path divides an HDR desktop by it to see the glass's backdrop as
/// the browser's 8 bit sRGB would be. From the display configuration (DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL) of
/// the path whose source is the monitor's GDI device; 1.0 where Windows doesn't say.
/// </summary>
internal static class SdrWhite
{
    private const uint SourceNameType = 1;    // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
    private const uint SdrWhiteLevelType = 11;  // DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceName
    {
        public WindowsDisplayConfig.Native.DeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string GdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WhiteLevel
    {
        public WindowsDisplayConfig.Native.DeviceInfoHeader Header;
        public uint SdrWhiteLevel;
    }

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref SourceName request);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref WhiteLevel request);

    public static float Of(string gdiDeviceName)
    {
        try
        {
            if (new WindowsDisplayConfig().Query(DisplayConfigReader.OnlyActivePaths, out var paths, out _) != 0) return 1;
            foreach (var path in paths)
            {
                var source = new SourceName
                {
                    Header = new() { Type = SourceNameType, Size = (uint)Marshal.SizeOf<SourceName>(), AdapterId = path.SourceInfo.AdapterId, Id = path.SourceInfo.Id },
                };
                if (DisplayConfigGetDeviceInfo(ref source) != 0 || !string.Equals(source.GdiDeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase)) continue;
                var white = new WhiteLevel
                {
                    Header = new() { Type = SdrWhiteLevelType, Size = (uint)Marshal.SizeOf<WhiteLevel>(), AdapterId = path.TargetInfo.AdapterId, Id = path.TargetInfo.Id },
                };
                return DisplayConfigGetDeviceInfo(ref white) == 0 && white.SdrWhiteLevel > 0 ? white.SdrWhiteLevel / 1000f : 1;
            }
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
        {
        }
        return 1;
    }
}
