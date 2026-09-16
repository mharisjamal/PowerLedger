using PowerLedger.Contracts;

namespace PowerLedger.App.Tests;

/// <summary>Service statuses as the service would report them.</summary>
internal static class Statuses
{
    /// <summary>The device interface path Windows gives <see cref="Dell"/> in the user's session.</summary>
    public const string DellPath = @"\\?\DISPLAY#DELA0B1#5&2f5a1b&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    /// <summary>The device interface path Windows gives <see cref="Aoc"/>.</summary>
    public const string AocPath = @"\\?\DISPLAY#AOC2402#5&2f5a1b&0&UID4354#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    public static ServiceStatus Running(bool energyMeter = true, bool battery = true) => new(
        "0.1.0+b688a18", DateTimeOffset.UnixEpoch, 10,
        [
            new SourceStatus("energy-meter", energyMeter, energyMeter ? null : "this machine publishes no processor power rails", 0, null),
            new SourceStatus("battery", battery, battery ? null : "no battery fitted", 0, null),
        ],
        SuspectTicks: 0, SensorRestarts: 0, new CalibrationStatus(900, 1800, 0, 12), "hash",
        DatabaseBytes: 31L * 1024 * 1024, WriteProblem: null, DatabaseNotice: null, Last: null, Monitors: []);

    /// <summary>A monitor in Energy Star's list, which answered at 60% brightness.</summary>
    public static MonitorStatus Dell { get; } = new()
    {
        Key = "DELA0B1-J5TGC83", Instance = @"DISPLAY\DELA0B1\5&2F5A1B&0&UID4353", Name = "DELL U2723QE", Inches = 27, Width = 3840, Height = 2160,
        OnWatts = 26.9, SleepWatts = 0.3, Source = MonitorSource.Model, Counted = true, Brightness = 0.6, WattsNow = 24.3,
    };

    /// <summary>A monitor the list doesn't know, estimated from its size, whose brightness couldn't be read.</summary>
    public static MonitorStatus Aoc { get; } = new()
    {
        Key = @"DISPLAY\AOC2402\5&2F5A1B&0&UID4354", Instance = @"DISPLAY\AOC2402\5&2F5A1B&0&UID4354", Name = "24B1XH5", Inches = 23.8,
        Width = 1920, Height = 1080, OnWatts = 13.4, SleepWatts = 0.2, Source = MonitorSource.Estimate, Counted = true, WattsNow = 13.4,
    };

    /// <summary>A service with <see cref="Dell"/> and <see cref="Aoc"/> attached.</summary>
    public static ServiceStatus WithMonitors(params MonitorStatus[] monitors)
        => Running() with { Monitors = monitors.Length > 0 ? monitors : [Dell, Aoc] };
}
