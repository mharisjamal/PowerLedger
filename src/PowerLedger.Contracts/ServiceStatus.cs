namespace PowerLedger.Contracts;

/// <summary>What the status screen shows (spec §8).</summary>
/// <param name="Version">The service's version.</param>
/// <param name="StartedAt">When this run of the service began.</param>
/// <param name="Ticks">Readings recorded since then.</param>
/// <param name="Sources">One entry per sensor source.</param>
/// <param name="SuspectTicks">Ticks the validator marked suspect since the sensor set was built.</param>
/// <param name="SensorRestarts">Sensor sets thrown away because a read hung.</param>
/// <param name="Calibration">How far battery calibration has got.</param>
/// <param name="InventoryHash">The hardware identity calibration is keyed to.</param>
/// <param name="DatabaseBytes">The database's size on disk, write-ahead log included.</param>
/// <param name="WriteProblem">Null while writes succeed; otherwise why readings are held in memory.</param>
/// <param name="DatabaseNotice">Null normally; set when a damaged or untrusted database was set aside at start.</param>
/// <param name="Last">The most recent reading, or null before the first.</param>
public sealed record ServiceStatus(
    string Version, DateTimeOffset StartedAt, long Ticks,
    IReadOnlyList<SourceStatus> Sources, int SuspectTicks, int SensorRestarts,
    CalibrationStatus Calibration, string InventoryHash, long DatabaseBytes,
    string? WriteProblem, string? DatabaseNotice, ReadingFrame? Last);

/// <param name="Name">The source's name, e.g. "battery".</param>
/// <param name="Supported">False when this machine cannot answer at all.</param>
/// <param name="Unavailable">Why not, when it cannot.</param>
/// <param name="Failures">Ticks on which the source threw since the sensor set was built.</param>
/// <param name="LastError">The most recent failure, for the status screen.</param>
public sealed record SourceStatus(string Name, bool Supported, string? Unavailable, int Failures, string? LastError);

/// <param name="BatterySamples">Battery ticks learned from so far.</param>
/// <param name="SamplesNeeded">Battery ticks needed before any baseline is trusted.</param>
/// <param name="TrustedBuckets">Brightness buckets with a trusted baseline.</param>
/// <param name="Buckets">All brightness buckets, display-off included.</param>
public sealed record CalibrationStatus(int BatterySamples, int SamplesNeeded, int TrustedBuckets, int Buckets);
