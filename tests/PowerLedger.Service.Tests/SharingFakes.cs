using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Sensors;
using PowerLedger.Service.Sharing;
using PowerLedger.Storage;

namespace PowerLedger.Service.Tests;

/// <summary>A desktop with every part PowerLedger reads, for building data-sharing reports in tests.</summary>
internal static class SharingFakes
{
    public const string InstallId = "0f8fad5b-d9cb-469f-a165-70867728950e";

    public static readonly DateTimeOffset At = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

    public static Consent AllOn { get; } = new(ConsentText.Version, true, true, true, true);

    public static HostFacts Host { get; } = new("0.6.0", "10.0.26200", "x64", 16, 31.9);

    public static InventoryFacts Facts { get; } = new(
        ChassisKind.Desktop, "AMD Ryzen 7 7800X3D 8-Core Processor", "NVIDIA GeForce RTX 4070", 2, RamIsDdr5: true, SsdCount: 1, HddCount: 0,
        DisplayDiagonalInches: 0, MonitorCount: 1);

    public static ServiceSettings Settings { get; } = ServiceSettings.Default with
    {
        Profile = MachineProfile.DefaultDesktop with
        {
            RamIsDdr5 = true, PsuTier = PsuTier.Gold, UpsLoad = UpsLoad.ThisPc, GpuTdpOverrideW = 220, ExtrasWatts = 4.5,
        },
    };

    public static MonitorStatus Monitor { get; } = new()
    {
        Key = "GSM5B7F-7MKZG34",
        Instance = @"DISPLAY\GSM5B7F\5&1A2B3C&0&UID4352",
        Name = "LG ULTRAGEAR",
        Inches = 27,
        Width = 2560,
        Height = 1440,
        OnWatts = 24.31,
        RefreshHz = 165,
        Hdr = false,
        Source = MonitorSource.Model,
        Counted = true,
        OwnPlug = true,
    };

    public static IReadOnlyList<SourceStatus> Sources { get; } =
    [
        new("energy-meter", true, null, 0, null),
        new("nvidia-gpu", true, null, 0, null),
        new("amd-gpu", false, "No AMD graphics driver is installed.", 0, null),
        new("battery", true, null, 2, "The battery didn't answer."),
        new("power-supply", true, "No power supply found on USB.", 0, null),
        new("ups", true, null, 0, null),
    ];

    public static ServiceStatus Status(IReadOnlyList<SourceStatus>? sources = null, IReadOnlyList<MonitorStatus>? monitors = null,
        IReadOnlyList<PowerDeviceStatus>? devices = null) => new(
        "0.6.0+abcdef", At.AddHours(-1), 3600, sources ?? Sources, 0, 0, new CalibrationStatus(0, 0, 0, 0), Facts.Hash, 1_000_000,
        null, null, Last: null,
        Monitors: monitors ?? [Monitor],
        PowerDevices: devices ??
        [
            new PowerDeviceStatus(PowerDeviceKind.Ups, "APC Back-UPS ES 850G2", 142, "real output power"),
            new PowerDeviceStatus(PowerDeviceKind.PowerSupply, "Corsair HX1000i", 312, "DC output, all rails"),
        ]);

    /// <summary>A minute of the reference report's kind, with figures that need rounding.</summary>
    public static MinuteRow Minute(int index, string day = "2026-09-24") => new(
        StartMs: LocalDays.Start(LocalDays.Parse(day), TimeZoneInfo.Utc).AddMinutes(index).ToUnixTimeMilliseconds(), day, index,
        AvgW: 142.34, MaxW: 160.26, CpuW: 35.21, GpuW: 40.49, DisplayW: 0, RamW: 6, StorageW: 3.1, BoardW: 20, ExtrasW: 4.5,
        MonitorsW: 24.31, PsuLossW: 13.2, UnattributedW: -0.04,
        CpuLoad: 0.12149, GpuLoad: 0.2, Brightness: null,
        DisplayOnS: 60, IdleS: 0, LockedS: 0, BatteryS: 0, MeasuredS: 0, CalibratedS: 0, EstimatedS: 60.04,
        Samples: 60, TotalSource: 0, GpuScope: 0, MeasuredMask: 3);

    public static DayEvents Events { get; } = new(
        new Dictionary<string, SourceDay> { ["battery"] = new(5, "The battery didn't answer.") },
        [new CrashReport(At, "app", "0.6.0", ["System.InvalidOperationException"], "Collection was modified.", "   at PowerLedger.App.Now.Refresh()")],
        new UsageCounts("2026-09-24", 3, new Dictionary<string, int> { ["now"] = 5, ["history"] = 2 }, new Dictionary<string, int> { ["theme"] = 1 },
            0, 0, 12, "dark", "en-US"));

    /// <summary>Everything a report of <see cref="AllOn"/> is built from.</summary>
    public static ReportInputs Inputs(Consent? consent = null) => new(
        Host, InstallId, consent ?? AllOn, "2026-09-24", 60, [Minute(600), Minute(601), Minute(602)], Events, Status(), Facts, Settings,
        new Tariff(At.AddDays(-30), 0.25m, "USD"), DiscreteGpu: true, WithHardware: true, Names: new ScrubNames("alice", "DESKTOP-TEST", "CONTOSO"));
}
