using PowerLedger.Contracts;

namespace PowerLedger.App.Tests;

/// <summary>Readings as the service would push them.</summary>
internal static class Frames
{
    /// <param name="monitors">What the external monitors counted draw, which is part of <paramref name="totalW"/>.</param>
    public static ReadingFrame At(
        DateTimeOffset at, double totalW = 34.2, Quality quality = Quality.Measured,
        double cpu = 14.6, double gpu = 4.1, double display = 4.0, bool gpuMeasured = false, double? gpuLoad = 0.31, double monitors = 0)
        => new(at, 1, totalW, quality,
               new ComponentWatts(cpu, gpu, display, 0, 0, 0, 0, monitors, 0, totalW - cpu - gpu - display - monitors),
               CpuMeasured: true, GpuMeasured: gpuMeasured,
               OnBattery: quality == Quality.Measured, DisplayOn: true, UserIdle: false, SessionLocked: false,
               CpuLoad: 0.3, GpuLoad: gpuLoad, Brightness: 0.6, Suspect: false);
}
