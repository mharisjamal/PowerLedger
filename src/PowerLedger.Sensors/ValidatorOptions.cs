namespace PowerLedger.Sensors;

/// <param name="CpuMaxW">Upper plausible bound for CPU package watts (spec §4).</param>
/// <param name="GpuMaxW">Upper plausible bound for discrete GPU watts.</param>
/// <param name="BatteryMaxW">Upper plausible bound for battery discharge watts.</param>
/// <param name="UpsMaxW">Upper plausible bound for the output watts of a UPS on USB. The largest units a single PC hangs
/// off deliver about 3 kW, and one may hold more than the PC, so the bound is left well above them; past it the report has
/// been misread rather than the outlets loaded.</param>
/// <param name="PsuMaxW">Upper plausible bound for a PC power supply's watts, whether it reports its DC output or what it
/// draws from the wall. The largest that report over USB are rated about 1.6 kW, and the wall figure sits a little above
/// the rails, so past this the report has been misread rather than the supply loaded.</param>
/// <param name="MedianWindow">How many recent values the outlier test compares against.</param>
/// <param name="OutlierFactor">A value this many times the median or more is treated as a spike.</param>
/// <param name="TransitionSeconds">How long after an AC change a tick stays marked suspect.</param>
public sealed record ValidatorOptions(
    double CpuMaxW = 400,
    double GpuMaxW = 700,
    double BatteryMaxW = 300,
    double UpsMaxW = 5000,
    double PsuMaxW = 2000,
    int MedianWindow = 30,
    double OutlierFactor = 3,
    double TransitionSeconds = 3);
