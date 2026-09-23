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

    /// <summary>A portable monitor on a laptop, which the list doesn't know, taken to run off the laptop.</summary>
    public static MonitorStatus Portable { get; } = new()
    {
        Key = "AUS1601-L9LMTF012345", Instance = @"DISPLAY\AUS1601\5&2F5A1B&0&UID4355", Name = "MB16AC", Inches = 15.6, Width = 1920,
        Height = 1080, OnWatts = 7.5, SleepWatts = 0.2, Source = MonitorSource.Estimate, Counted = true, OwnPlug = false,
        OwnPlugByDefault = false, WattsNow = 7.5,
    };

    /// <summary>A service with <see cref="Dell"/> and <see cref="Aoc"/> attached.</summary>
    public static ServiceStatus WithMonitors(params MonitorStatus[] monitors)
        => Running() with { Monitors = monitors.Length > 0 ? monitors : [Dell, Aoc] };

    /// <summary>A UPS on USB whose watts come from its load percentage of its rated watts.</summary>
    public static PowerDeviceStatus Ups { get; } = new(PowerDeviceKind.Ups, "APC Back-UPS ES 850G2", 142, "load of its rated watts");

    /// <summary>A power supply that reports its DC output on USB.</summary>
    public static PowerDeviceStatus PowerSupply { get; } = new(PowerDeviceKind.PowerSupply, "Corsair HX1000i", 312.4, "DC output, all rails");

    /// <summary>A service reading <see cref="Ups"/> and <see cref="PowerSupply"/>, with the usual monitors attached.</summary>
    public static ServiceStatus WithPowerDevices(params PowerDeviceStatus[] devices)
        => WithMonitors() with { PowerDevices = devices.Length > 0 ? devices : [Ups, PowerSupply] };

    /// <summary>A service that has answered with a sharing status (data-sharing design §5).</summary>
    public static ServiceStatus WithSharing(
        Consent consent, string? installId = null, DateTimeOffset? lastSentAt = null, long? lastSentBytes = null,
        string? problem = null, bool rejected = false, int daysWaiting = 0)
        => Running() with { Sharing = new SharingStatus(consent, installId, lastSentAt, lastSentBytes, problem, rejected, daysWaiting) };
}
