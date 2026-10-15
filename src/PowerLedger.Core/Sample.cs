using PowerLedger.Contracts;

namespace PowerLedger.Core;

/// <summary>Raw sensor values for one tick. Null means the source had no value this tick.</summary>
/// <param name="DeltaSeconds">Seconds since the previous tick, from a monotonic clock.</param>
/// <param name="IGpuW">Informational only. On Intel it is already inside CpuPackageW; never add it to the CPU figure.</param>
/// <param name="CpuLoad">0..1.</param>
/// <param name="BatteryRateW">Discharge watts (positive) whenever the machine's own battery is discharging: on battery, and
/// on AC under a load the adapter can't carry, when it is a floor for the total. Null while charging or idle, when unknown,
/// and on a battery that reports relative units rather than milliwatts. Charging is in no field of a sample but <paramref name="BatteryChargeW"/>.</param>
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
/// <param name="Gpus">Each discrete graphics card on its own, when the sensors read them card by card; the model then adds
/// them up and <paramref name="DGpuW"/>, <paramref name="DGpuLoad"/>, <paramref name="DGpuPresent"/> and
/// <paramref name="DGpuScope"/> are their <see cref="GpuCard.Totals"/>. Null for a sample that has only those four.</param>
/// <param name="PlatformW">The whole platform's DC watts from an energy-meter rail that meters it (Snapdragon X's
/// <c>system</c>), display included and battery charging excluded; null where there is no such rail.</param>
/// <param name="SystemMeterW">The machine's own input power, as a platform power meter or its management controller
/// measures it (see <see cref="SystemMeterKind"/>); null when it has none or it gave no reading. Wall power already, like
/// <paramref name="PsuWallW"/>: never divided by an efficiency.</param>
/// <param name="SystemMeter">What gave <paramref name="SystemMeterW"/>.</param>
/// <param name="SystemMeterName">What the meter is called, for the status screen.</param>
/// <param name="PanelInactive">True when Windows lists the built-in panel as showing nothing while the console display is on
/// elsewhere: a docked laptop with its lid shut, or one set to show on the second screen only. The panel then draws nothing.
/// False when it shows, and when nothing says (a machine with no built-in panel, or one Windows lists no panel for).</param>
/// <param name="BatteryChargeW">Charge watts, positive, while a laptop's own battery charges on AC; null otherwise. Never the
/// PC's consumption: the model only takes it off a total that holds it, a UPS's or an input meter's (Plan AC).</param>
/// <param name="AcTransition">Set by the validator, with <paramref name="Suspect"/>, in the seconds after the machine went on
/// or off mains, when the battery rate Windows gives is from before the change. Only this, not any doubt, makes the model
/// pass over the battery's rate.</param>
/// <param name="DGpuBusyLoad">The busiest graphics' load over every engine, video decode and copy included, 0..1: any
/// card's, or the processor's own graphics' (Plan AC review); null where nothing read it. Only the idle test reads it: a
/// film keeps a decoder busy and the PC in use, while
/// <paramref name="DGpuLoad"/>, the shaders' load, is what the card's rating turns into watts.</param>
/// <param name="Graphics">Each process's time on the graphics engines over the load counters' last window, for the Apps
/// page's split (Plan AE); null where nothing reads the counters, or before their second read.</param>
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
    double? PsuWallW = null,
    IReadOnlyList<GpuCard>? Gpus = null,
    double? PlatformW = null,
    double? SystemMeterW = null,
    SystemMeterKind SystemMeter = SystemMeterKind.None,
    string? SystemMeterName = null,
    bool PanelInactive = false,
    double? BatteryChargeW = null,
    bool AcTransition = false,
    double? DGpuBusyLoad = null,
    GraphicsWork? Graphics = null)
{
    /// <summary>True when the tick carries a usable discharge rate: on battery, finite, and above zero
    /// (zero or negative means charging or a transition blip). The model and the calibration learner both gate on this.</summary>
    public bool HasDischargeRate => OnBattery && BatteryRateW is { } rate && double.IsFinite(rate) && rate > 0;

    /// <summary>The battery's discharge while on AC, when the adapter can't carry the load alone; null otherwise. The
    /// machine draws at least this, so it is a floor for the total, never the total: the adapter delivers the rest.</summary>
    public double? DischargeOnAcW => !OnBattery && BatteryRateW is { } rate && double.IsFinite(rate) && rate > 0 ? rate : null;
}
