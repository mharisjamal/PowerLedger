using PowerLedger.Contracts;

namespace PowerLedger.Core;

/// <summary>One tick after the power model: whole-system watts plus how much to trust them.</summary>
/// <param name="TotalSource">Where the total came from: the model, the battery, a UPS or a power supply. Stored since
/// storage version 2 and read back by <c>RawSampleRepository.ReadRange</c>; <c>Read</c> leaves it as the model, so the App
/// can still read a database the service hasn't upgraded yet. The same holds for <paramref name="GpuScope"/> and
/// <paramref name="Measured"/>.</param>
/// <param name="GpuScope">What the discrete GPU's measured watts covered, as the sample gave it.</param>
/// <param name="Measured">Which figures a device measured rather than the model worked out. The loop marks it from the
/// sample; the model leaves it empty.</param>
/// <param name="AsleepW">What the machine drew while it slept, estimated (accuracy A10), on the tick that reaches back across
/// the sleep: the energy integrator books it over that tick's gap as asleep energy. Null on every other tick, and on a gap
/// the service was never told was a sleep.</param>
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
    GpuPowerScope GpuScope = GpuPowerScope.Board,
    MeasuredParts Measured = MeasuredParts.None,
    double? AsleepW = null);
