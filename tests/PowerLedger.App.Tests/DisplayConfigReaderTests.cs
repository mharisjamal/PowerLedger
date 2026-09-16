using System.Reflection;
using System.Runtime.InteropServices;
using Shouldly;
using Xunit.Abstractions;

namespace PowerLedger.App.Tests;

public class DisplayConfigReaderTests(ITestOutputHelper output)
{
    private const string Dell = @"\\?\DISPLAY#DELA0B1#5&2f5a1b&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string Aoc = @"\\?\DISPLAY#AOC2402#5&2f5a1b&0&UID4354#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    private static readonly Luid Adapter = new() { LowPart = 0xC4F2, HighPart = 0 };

    private readonly FakeDisplayConfig _windows = new();

    private DisplayConfigReader Reader() => new(_windows);

    [Fact]
    public void Each_structure_is_laid_out_as_the_Windows_SDK_declares_it()
    {
        // The sizes and offsets follow from wingdi.h's declarations, member by member.
        Marshal.SizeOf<Luid>().ShouldBe(8);
        Marshal.SizeOf<DisplayConfigRational>().ShouldBe(8);
        Marshal.SizeOf<DisplayConfigPathSource>().ShouldBe(20);
        Marshal.SizeOf<DisplayConfigPathTarget>().ShouldBe(48);
        Marshal.OffsetOf<DisplayConfigPathTarget>(nameof(DisplayConfigPathTarget.ModeInfoIndex)).ShouldBe(12);
        Marshal.OffsetOf<DisplayConfigPathTarget>(nameof(DisplayConfigPathTarget.RefreshRate)).ShouldBe(28);
        Marshal.OffsetOf<DisplayConfigPathTarget>(nameof(DisplayConfigPathTarget.TargetAvailable)).ShouldBe(40);
        Marshal.SizeOf<DisplayConfigPath>().ShouldBe(72);
        Marshal.OffsetOf<DisplayConfigPath>(nameof(DisplayConfigPath.TargetInfo)).ShouldBe(20);
        Marshal.OffsetOf<DisplayConfigPath>(nameof(DisplayConfigPath.Flags)).ShouldBe(68);
        Marshal.SizeOf<DisplayConfigVideoSignal>().ShouldBe(48);
        Marshal.OffsetOf<DisplayConfigVideoSignal>(nameof(DisplayConfigVideoSignal.VSyncFreq)).ShouldBe(16);
        Marshal.SizeOf<DisplayConfigMode>().ShouldBe(64);
        Marshal.OffsetOf<DisplayConfigMode>(nameof(DisplayConfigMode.AdapterId)).ShouldBe(8);
        Marshal.OffsetOf<DisplayConfigMode>(nameof(DisplayConfigMode.TargetMode)).ShouldBe(16);
        Marshal.SizeOf<WindowsDisplayConfig.Native.DeviceInfoHeader>().ShouldBe(20);
        Marshal.SizeOf<WindowsDisplayConfig.Native.TargetDeviceName>().ShouldBe(420);
        Marshal.OffsetOf<WindowsDisplayConfig.Native.TargetDeviceName>(nameof(WindowsDisplayConfig.Native.TargetDeviceName.MonitorDevicePath)).ShouldBe(164);
        Marshal.SizeOf<WindowsDisplayConfig.Native.AdvancedColorInfo>().ShouldBe(32);
        Marshal.OffsetOf<WindowsDisplayConfig.Native.AdvancedColorInfo>(nameof(WindowsDisplayConfig.Native.AdvancedColorInfo.Flags)).ShouldBe(20);
        Marshal.SizeOf<WindowsDisplayConfig.Native.AdvancedColorInfo2>().ShouldBe(36);
        Marshal.OffsetOf<WindowsDisplayConfig.Native.AdvancedColorInfo2>(nameof(WindowsDisplayConfig.Native.AdvancedColorInfo2.Flags)).ShouldBe(20);
    }

    [Fact]
    public void Windows_is_only_asked_to_describe_its_displays_never_to_change_them()
    {
        var declared = typeof(WindowsDisplayConfig.Native).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.GetCustomAttribute<DllImportAttribute>() is not null)
            .Select(method => $"{method.GetCustomAttribute<DllImportAttribute>()!.Value} {method.Name}")
            .Distinct();

        declared.ShouldBe(["user32.dll GetDisplayConfigBufferSizes", "user32.dll QueryDisplayConfig", "user32.dll DisplayConfigGetDeviceInfo"], ignoreOrder: true);
    }

    [Fact]
    public void A_monitor_is_read_under_its_device_path_at_its_target_refresh_rate()
    {
        _windows.Paths = [Path(1, 143_998, 1000), Path(2, 60)];
        _windows.Monitors[1] = Dell;
        _windows.Monitors[2] = Aoc;

        Reader().Read().ShouldBe([new DisplayReading(Dell, 143.998, false), new DisplayReading(Aoc, 60, false)]);
    }

    [Fact]
    public void Windows_11_is_asked_for_the_physical_refresh_rate()
    {
        _windows.Paths = [Path(1, 60)];
        _windows.Monitors[1] = Dell;

        Reader().Read();

        _windows.Queries.ShouldBe([DisplayConfigReader.OnlyActivePaths | DisplayConfigReader.VirtualRefreshRateAware]);
        DisplayConfigReader.OnlyActivePaths.ShouldBe(0x2u);
        DisplayConfigReader.VirtualRefreshRateAware.ShouldBe(0x40u);
    }

    [Fact]
    public void A_path_Windows_boosts_is_read_at_its_physical_rate_from_its_target_mode()
    {
        // Dynamic Refresh Rate moves the path between a virtual 60 Hz, which is refreshRate, and the monitor's 120 Hz.
        _windows.Paths = [Path(1, 60, flags: DisplayConfigReader.PathActive | DisplayConfigReader.BoostRefreshRate, modeIndex: 1)];
        _windows.Modes = [Source(1), Target(1, 120_000, 1000)];
        _windows.Monitors[1] = Dell;

        Reader().Read().ShouldBe([new DisplayReading(Dell, 120, false)]);
        DisplayConfigReader.BoostRefreshRate.ShouldBe(0x10u);
    }

    [Fact]
    public void A_boosted_path_that_supports_virtual_modes_names_its_target_mode_in_the_upper_half_of_its_index()
    {
        const uint flags = DisplayConfigReader.PathActive | DisplayConfigReader.SupportVirtualMode | DisplayConfigReader.BoostRefreshRate;
        _windows.Paths = [Path(1, 60, flags: flags, modeIndex: (2u << 16) | 0)];   // desktop image at 0, target mode at 2
        _windows.Modes = [DesktopImage(1), Source(1), Target(1, 165, 1)];
        _windows.Monitors[1] = Dell;

        Reader().Read().ShouldBe([new DisplayReading(Dell, 165, false)]);
        DisplayConfigReader.SupportVirtualMode.ShouldBe(0x8u);
    }

    [Theory]
    [InlineData(0xFFFFFFFFu)]    // DISPLAYCONFIG_PATH_MODE_IDX_INVALID
    [InlineData(3u)]             // past the end of the modes
    [InlineData(0u)]             // a source mode, not a target mode
    public void A_boosted_path_whose_target_mode_cant_be_found_gives_no_reading(uint modeIndex)
    {
        // Its refreshRate is the virtual rate, not what the monitor runs at, so it isn't taken instead.
        _windows.Paths = [Path(1, 60, flags: DisplayConfigReader.PathActive | DisplayConfigReader.BoostRefreshRate, modeIndex: modeIndex), Path(2, 60)];
        _windows.Modes = [Source(1), Target(1, 120, 1)];
        _windows.Monitors[1] = Dell;
        _windows.Monitors[2] = Aoc;

        Reader().Read().ShouldBe([new DisplayReading(Aoc, 60, false)]);
    }

    [Theory]
    [InlineData(60u, 0u)]         // no rate at all
    [InlineData(0u, 1u)]          // 0 Hz
    [InlineData(1001u, 1u)]       // beyond any monitor, and beyond what the service takes
    public void A_refresh_rate_that_isnt_one_to_believe_gives_no_reading(uint numerator, uint denominator)
    {
        _windows.Paths = [Path(1, numerator, denominator), Path(2, 75)];
        _windows.Monitors[1] = Dell;
        _windows.Monitors[2] = Aoc;

        Reader().Read().ShouldBe([new DisplayReading(Aoc, 75, false)]);
    }

    [Theory]
    [InlineData(true)]     // a monitor just unplugged, which Windows can list as active for a moment, its target unavailable
    [InlineData(false)]    // a path that isn't active
    public void A_path_without_a_monitor_it_drives_gives_no_reading(bool unavailable)
    {
        var idle = Path(1, 144);
        if (unavailable) idle.TargetInfo.TargetAvailable = 0;
        else idle.Flags = 0;
        _windows.Paths = [idle, Path(2, 60)];
        _windows.Monitors[1] = Dell;
        _windows.Monitors[2] = Aoc;

        Reader().Read().ShouldBe([new DisplayReading(Aoc, 60, false)]);
    }

    [Fact]
    public void A_path_whose_monitor_Windows_wont_name_gives_no_reading()
    {
        _windows.Paths = [Path(1, 144), Path(2, 60)];
        _windows.Monitors[2] = Aoc;

        Reader().Read().ShouldBe([new DisplayReading(Aoc, 60, false)]);
    }

    [Theory]
    [InlineData(0x33u, true)]     // highDynamicRangeUserEnabled, with HDR supported and advanced color on
    [InlineData(0xC3u, false)]    // advanced color on for wide color alone, which isn't HDR
    [InlineData(0x00u, false)]
    public void Whether_HDR_is_on_is_the_users_HDR_switch_where_Windows_has_the_newer_request(uint flags, bool hdr)
    {
        _windows.Paths = [Path(1, 144)];
        _windows.Monitors[1] = Dell;
        _windows.ColorInfo2[1] = flags;
        _windows.ColorInfo[1] = 0x3;   // never asked, as the newer request answered

        Reader().Read().ShouldBe([new DisplayReading(Dell, 144, hdr)]);
        _windows.AdvancedColorAsked.ShouldBe(0);
    }

    [Theory]
    [InlineData(0x3u, true)]      // advancedColorEnabled
    [InlineData(0x1u, false)]     // supported, but off
    public void Where_Windows_lacks_the_newer_request_HDR_is_advanced_color_being_on(uint flags, bool hdr)
    {
        _windows.Paths = [Path(1, 144)];
        _windows.Monitors[1] = Dell;
        _windows.ColorInfo[1] = flags;

        Reader().Read().ShouldBe([new DisplayReading(Dell, 144, hdr)]);
    }

    [Fact]
    public void A_monitor_Windows_gives_no_color_state_for_is_taken_not_to_have_HDR_on()
    {
        _windows.Paths = [Path(1, 144)];
        _windows.Monitors[1] = Dell;

        Reader().Read().ShouldBe([new DisplayReading(Dell, 144, false)]);
    }

    [Fact]
    public void Windows_10_which_refuses_the_newer_flag_is_asked_again_without_it()
    {
        _windows.Refusals[DisplayConfigReader.OnlyActivePaths | DisplayConfigReader.VirtualRefreshRateAware] = DisplayConfigReader.InvalidParameter;
        _windows.Paths = [Path(1, 60)];
        _windows.Monitors[1] = Dell;

        Reader().Read().ShouldBe([new DisplayReading(Dell, 60, false)]);

        _windows.Queries.ShouldBe([DisplayConfigReader.OnlyActivePaths | DisplayConfigReader.VirtualRefreshRateAware, DisplayConfigReader.OnlyActivePaths]);
        DisplayConfigReader.InvalidParameter.ShouldBe(87);
    }

    [Theory]
    [InlineData(5)]     // ERROR_ACCESS_DENIED: not the console session
    [InlineData(50)]    // ERROR_NOT_SUPPORTED: no WDDM driver
    [InlineData(31)]    // ERROR_GEN_FAILURE
    public void When_Windows_wont_describe_its_displays_nothing_is_read_and_it_isnt_asked_again_in_that_read(int error)
    {
        _windows.Refusals[DisplayConfigReader.OnlyActivePaths | DisplayConfigReader.VirtualRefreshRateAware] = error;
        _windows.Paths = [Path(1, 60)];
        _windows.Monitors[1] = Dell;

        Reader().Read().ShouldBeEmpty();

        _windows.Queries.Count.ShouldBe(1);
    }

    [Fact]
    public void A_call_that_throws_gives_no_reading_for_that_path_only()
    {
        _windows.Paths = [Path(1, 144), Path(2, 60)];
        _windows.Monitors[2] = Aoc;
        _windows.NameThrows[1] = new InvalidOperationException("The display went away.");

        Reader().Read().ShouldBe([new DisplayReading(Aoc, 60, false)]);
    }

    [Fact]
    public void A_query_that_throws_gives_nothing()
    {
        _windows.QueryThrows = new InvalidOperationException("No desktop.");

        Reader().Read().ShouldBeEmpty();
    }

    /// <summary>Asks this machine's own display configuration: Windows' records only, so no monitor is sent anything. Every
    /// active display has a refresh rate, and a monitor Windows names by a display's device path.</summary>
    [Fact]
    [Trait("Category", "Hardware")]
    public void This_machines_displays_are_described_with_a_device_path_and_a_refresh_rate_each()
    {
        var readings = new DisplayConfigReader().Read();

        foreach (var reading in readings) output.WriteLine($"{reading.DevicePath}: {reading.RefreshHz:0.###} Hz, HDR {(reading.Hdr ? "on" : "off")}");
        readings.ShouldNotBeEmpty();
        readings.ShouldAllBe(reading => reading.DevicePath.StartsWith(@"\\?\DISPLAY#", StringComparison.OrdinalIgnoreCase) && reading.RefreshHz >= 1 && reading.RefreshHz <= 1000);
    }

    /// <summary>An active path to target <paramref name="target"/> at <paramref name="numerator"/>/<paramref name="denominator"/>
    /// hertz, with no mode unless a test gives one.</summary>
    private static DisplayConfigPath Path(uint target, uint numerator, uint denominator = 1, uint flags = DisplayConfigReader.PathActive, uint modeIndex = 0xFFFFFFFF)
        => new()
        {
            SourceInfo = new DisplayConfigPathSource { AdapterId = Adapter, Id = target - 1, ModeInfoIndex = 0xFFFFFFFF },
            TargetInfo = new DisplayConfigPathTarget
            {
                AdapterId = Adapter,
                Id = target,
                ModeInfoIndex = modeIndex,
                RefreshRate = new DisplayConfigRational { Numerator = numerator, Denominator = denominator },
                TargetAvailable = 1,
            },
            Flags = flags,
        };

    private static DisplayConfigMode Target(uint target, uint numerator, uint denominator)
        => new()
        {
            InfoType = DisplayConfigReader.TargetModeType,
            Id = target,
            AdapterId = Adapter,
            TargetMode = new DisplayConfigVideoSignal { VSyncFreq = new DisplayConfigRational { Numerator = numerator, Denominator = denominator } },
        };

    /// <summary>A source mode. Its union holds a source's size, pixel format and position, not a signal, so whatever its bytes
    /// would read as, here a plausible 144 Hz, isn't a refresh rate.</summary>
    private static DisplayConfigMode Source(uint target)
        => new()
        {
            InfoType = 1,
            Id = target - 1,
            AdapterId = Adapter,
            TargetMode = new DisplayConfigVideoSignal { VSyncFreq = new DisplayConfigRational { Numerator = 144, Denominator = 1 } },
        };

    private static DisplayConfigMode DesktopImage(uint target) => new() { InfoType = 3, Id = target - 1, AdapterId = Adapter };

    /// <summary>Windows' display configuration as a test sets it up, keyed by target id.</summary>
    private sealed class FakeDisplayConfig : IDisplayConfig
    {
        public DisplayConfigPath[] Paths { get; set; } = [];

        public DisplayConfigMode[] Modes { get; set; } = [];

        /// <summary>The error code a query with these flags gives instead of the paths.</summary>
        public Dictionary<uint, int> Refusals { get; } = [];

        public Exception? QueryThrows { get; set; }

        public List<uint> Queries { get; } = [];

        public Dictionary<uint, string> Monitors { get; } = [];

        public Dictionary<uint, Exception> NameThrows { get; } = [];

        /// <summary>ADVANCED_COLOR_INFO_2's flags for each target; a target without is one Windows refuses the request for.</summary>
        public Dictionary<uint, uint> ColorInfo2 { get; } = [];

        /// <summary>ADVANCED_COLOR_INFO's flags for each target; a target without is one Windows refuses the request for.</summary>
        public Dictionary<uint, uint> ColorInfo { get; } = [];

        public int AdvancedColorAsked { get; private set; }

        public int Query(uint flags, out DisplayConfigPath[] paths, out DisplayConfigMode[] modes)
        {
            Queries.Add(flags);
            if (QueryThrows is { } error) throw error;
            paths = [];
            modes = [];
            if (Refusals.TryGetValue(flags, out var refusal)) return refusal;
            paths = Paths;
            modes = Modes;
            return 0;
        }

        public string? MonitorDevicePath(Luid adapter, uint target)
        {
            adapter.ShouldBe(Adapter);
            if (NameThrows.TryGetValue(target, out var error)) throw error;
            return Monitors.GetValueOrDefault(target);
        }

        public uint? AdvancedColor2(Luid adapter, uint target) => ColorInfo2.TryGetValue(target, out var flags) ? flags : null;

        public uint? AdvancedColor(Luid adapter, uint target)
        {
            AdvancedColorAsked++;
            return ColorInfo.TryGetValue(target, out var flags) ? flags : null;
        }
    }
}
