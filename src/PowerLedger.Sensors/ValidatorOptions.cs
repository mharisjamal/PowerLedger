namespace PowerLedger.Sensors;

/// <param name="CpuMaxW">Upper plausible bound for CPU package watts (spec §4).</param>
/// <param name="GpuMaxW">Upper plausible bound for discrete GPU watts.</param>
/// <param name="BatteryMaxW">Upper plausible bound for battery discharge watts.</param>
/// <param name="MedianWindow">How many recent values the outlier test compares against.</param>
/// <param name="OutlierFactor">A value this many times the median or more is treated as a spike.</param>
/// <param name="TransitionSeconds">How long after an AC change a tick stays marked suspect.</param>
public sealed record ValidatorOptions(
    double CpuMaxW = 400,
    double GpuMaxW = 700,
    double BatteryMaxW = 300,
    int MedianWindow = 30,
    double OutlierFactor = 3,
    double TransitionSeconds = 3);
