namespace PowerLedger.Core;

/// <summary>Raw sensor values for one tick. Null means the source had no value this tick.</summary>
/// <param name="DeltaSeconds">Seconds since the previous tick, from a monotonic clock.</param>
/// <param name="IGpuW">Informational only. On Intel it is already inside CpuPackageW; never add it to the CPU figure.</param>
/// <param name="CpuLoad">0..1.</param>
/// <param name="BatteryRateW">Discharge watts (positive) while on battery; null when unknown.</param>
/// <param name="Brightness">0..1 for the internal panel; null when unavailable.</param>
/// <param name="Suspect">Set by the validator when a value was replaced or looks implausible.</param>
public sealed record Sample(
    DateTimeOffset Timestamp,
    double DeltaSeconds,
    double? CpuPackageW,
    double? IGpuW,
    double CpuLoad,
    double? DGpuW,
    double? DGpuLoad,
    bool DGpuPresent,
    double? BatteryRateW,
    bool OnBattery,
    double? Brightness,
    bool DisplayOn,
    int MonitorCount,
    double UserIdleSeconds,
    bool SessionLocked,
    bool Suspect)
{
    /// <summary>True when the tick carries a usable discharge rate: on battery, finite, and above zero
    /// (zero or negative means charging or a transition blip). The model and the calibration learner both gate on this.</summary>
    public bool HasDischargeRate => OnBattery && BatteryRateW is { } rate && double.IsFinite(rate) && rate > 0;
}
