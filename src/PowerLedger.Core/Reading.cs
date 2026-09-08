using PowerLedger.Contracts;

namespace PowerLedger.Core;

/// <summary>One tick after the power model: whole-system watts plus how much to trust them.</summary>
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
    bool Suspect);
