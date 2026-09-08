using PowerLedger.Core;

namespace PowerLedger.Core.Tests;

internal static class TestData
{
    public static readonly DateTimeOffset T0 = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A laptop tick. Defaults: on AC, CPU 14.6 W, dGPU 4.1 W, brightness 60 %, display on, user active.</summary>
    public static Sample Laptop(
        double? cpu = 14.6, double? gpu = 4.1, bool gpuPresent = true,
        double? battery = null, bool onBattery = false,
        double? brightness = 0.6, bool displayOn = true,
        double idleSeconds = 0, double cpuLoad = 0.3, double? gpuLoad = 0.3,
        double delta = 1.0, DateTimeOffset? ts = null, bool suspect = false, bool locked = false)
        => new(
            Timestamp: ts ?? T0, DeltaSeconds: delta,
            CpuPackageW: cpu, IGpuW: 2.0, CpuLoad: cpuLoad,
            DGpuW: gpu, DGpuLoad: gpuLoad, DGpuPresent: gpuPresent,
            BatteryRateW: battery, OnBattery: onBattery,
            Brightness: brightness, DisplayOn: displayOn, MonitorCount: 0,
            UserIdleSeconds: idleSeconds, SessionLocked: locked, Suspect: suspect);
}

internal sealed class FixedBaseline(double? value) : IBaselineProvider
{
    public double? GetBaseline(int bucket) => value;
}
