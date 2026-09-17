using PowerLedger.Contracts;

namespace PowerLedger.Core;

/// <summary>Raw sensor values for one tick. Null means the source had no value this tick.</summary>
/// <param name="DeltaSeconds">Seconds since the previous tick, from a monotonic clock.</param>
/// <param name="IGpuW">Informational only. On Intel it is already inside CpuPackageW; never add it to the CPU figure.</param>
/// <param name="CpuLoad">0..1.</param>
/// <param name="BatteryRateW">Discharge watts (positive) while on battery; null when unknown.</param>
/// <param name="Brightness">0..1 for the internal panel; null when unavailable.</param>
/// <param name="Suspect">Set by the validator when a value was replaced or looks implausible.</param>
/// <param name="DGpuScope">What <paramref name="DGpuW"/> covers when a vendor library measured it.</param>
/// <param name="UpsOutputW">The output watts a UPS attached over USB reports, for everything on its outlets; null when
/// there is none or it didn't answer.</param>
/// <param name="UpsSource">How <paramref name="UpsOutputW"/> was found.</param>
/// <param name="UpsName">The UPS's name, for the status screen.</param>
/// <param name="PsuOutputW">The DC output watts a power supply reports over USB, all rails; null when there is none, it
/// didn't answer, reading it is turned off, or it reports what it draws from the wall instead. The wall figure is this
/// over the supply's efficiency.</param>
/// <param name="PsuWallW">The AC watts a power supply reports it is drawing from the wall, for the units that report
/// that rather than their DC output; null otherwise. This is wall power already: it is the total, like a UPS's
/// reading, and must never be divided by an efficiency. At most one of this and <paramref name="PsuOutputW"/> is set.</param>
/// <param name="PsuName">The power supply's name, for the status screen.</param>
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
    bool Suspect,
    GpuPowerScope DGpuScope = GpuPowerScope.Board,
    double? UpsOutputW = null,
    UpsPowerSource UpsSource = UpsPowerSource.None,
    string? UpsName = null,
    double? PsuOutputW = null,
    string? PsuName = null,
    double? PsuWallW = null)
{
    /// <summary>True when the tick carries a usable discharge rate: on battery, finite, and above zero
    /// (zero or negative means charging or a transition blip). The model and the calibration learner both gate on this.</summary>
    public bool HasDischargeRate => OnBattery && BatteryRateW is { } rate && double.IsFinite(rate) && rate > 0;
}
