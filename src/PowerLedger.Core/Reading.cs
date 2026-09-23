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
    MeasuredParts Measured = MeasuredParts.None);
