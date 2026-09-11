using PowerLedger.Core;

namespace PowerLedger.Service.Tests;

internal static class Samples
{
    public static readonly DateTimeOffset T0 = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A plausible tick: the CPU drawing <paramref name="cpuW"/> (null when there is no energy meter), and on
    /// battery when a discharge rate is given.</summary>
    public static Sample At(DateTimeOffset timestamp, double delta = 1, double? cpuW = 8, double cpuLoad = 0.2, double? batteryW = null, double? brightness = 0.5)
        => new(
            Timestamp: timestamp, DeltaSeconds: delta, CpuPackageW: cpuW, IGpuW: null, CpuLoad: cpuLoad,
            DGpuW: null, DGpuLoad: null, DGpuPresent: false, BatteryRateW: batteryW, OnBattery: batteryW is not null,
            Brightness: brightness, DisplayOn: true, MonitorCount: 1, UserIdleSeconds: 0, SessionLocked: false, Suspect: false);
}
