using PowerLedger.Core;

namespace PowerLedger.Sensors;

/// <summary>
/// One tick under construction. Each source fills only the fields it owns and leaves the rest alone,
/// so a source that fails contributes nothing instead of corrupting the tick.
/// </summary>
public sealed class SampleDraft
{
    public double? CpuPackageW { get; set; }
    public double? IGpuW { get; set; }
    public double CpuLoad { get; set; }
    public double? DGpuW { get; set; }
    public double? DGpuLoad { get; set; }
    public bool DGpuPresent { get; set; }
    public double? BatteryRateW { get; set; }
    public bool OnBattery { get; set; }
    public double? Brightness { get; set; }

    /// <summary>Assumed on until the display source says otherwise, so a missing source never invents a dark screen.</summary>
    public bool DisplayOn { get; set; } = true;

    public int MonitorCount { get; set; }
    public double UserIdleSeconds { get; set; }
    public bool SessionLocked { get; set; }

    /// <summary>Freezes the draft. Suspect is always false here; only the validator sets it.</summary>
    public Sample ToSample(DateTimeOffset timestamp, double deltaSeconds) => new(
        timestamp, deltaSeconds,
        CpuPackageW, IGpuW, CpuLoad,
        DGpuW, DGpuLoad, DGpuPresent,
        BatteryRateW, OnBattery,
        Brightness, DisplayOn, MonitorCount,
        UserIdleSeconds, SessionLocked, Suspect: false);
}
