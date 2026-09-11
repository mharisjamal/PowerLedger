using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Sensors;

namespace PowerLedger.Service;

/// <summary>Core and Sensors types turned into the Contracts shapes that travel on the pipe.</summary>
internal static class Frames
{
    /// <summary>The reading, plus which parts the tick's sample measured rather than modelled (Plan B's provenance gap).</summary>
    public static ReadingFrame From(Reading r, Sample s) => new(
        r.Timestamp, r.DeltaSeconds, r.TotalW, r.Quality,
        new ComponentWatts(
            r.Components.Cpu, r.Components.Gpu, r.Components.Display, r.Components.Ram, r.Components.Storage,
            r.Components.Board, r.Components.Extras, r.Components.Monitors, r.Components.PsuLoss, r.Components.Unattributed),
        CpuMeasured: s.CpuPackageW is { } cpu && double.IsFinite(cpu),
        GpuMeasured: s.DGpuW is { } gpu && double.IsFinite(gpu),
        r.OnBattery, r.DisplayOn, r.UserIdle, r.SessionLocked, r.CpuLoad, r.GpuLoad, r.Brightness, r.Suspect);

    public static SourceStatus From(SourceHealth health)
        => new(health.Name, health.Supported, health.Unavailable, health.Failures, health.LastError);
}
