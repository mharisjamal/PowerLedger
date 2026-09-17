using PowerLedger.Contracts;

namespace PowerLedger.Core;

/// <summary>One tick after the power model: whole-system watts plus how much to trust them.</summary>
/// <param name="TotalSource">Where the total came from: the model, the battery, a UPS or a power supply. Not stored: a
/// reading read back from the database says the model.</param>
/// <param name="GpuScope">What the discrete GPU's measured watts covered, as the sample gave it.</param>
public sealed record Reading(
    DateTimeOffset Timestamp,
    double DeltaSeconds,
    double TotalW,
    Quality Quality,
    Components Components,
    bool OnBattery,
    bool DisplayOn,
    bool UserIdle,
    bool SessionLocked,
    double CpuLoad,
    double? GpuLoad,
    double? Brightness,
    bool Suspect,
    TotalSource TotalSource = TotalSource.Model,
    GpuPowerScope GpuScope = GpuPowerScope.Board);
