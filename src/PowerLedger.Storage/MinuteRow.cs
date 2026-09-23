namespace PowerLedger.Storage;

/// <summary>
/// One minute as data sharing sends it (data-sharing design §3), a row of <c>outbox_minutes</c>. Watts and loads are
/// averages over the minute's seconds; seconds are sums of the readings' Δt.
/// </summary>
/// <param name="StartMs">The minute's start, UTC milliseconds.</param>
/// <param name="Day">The local day the minute starts on, <c>yyyy-MM-dd</c>.</param>
/// <param name="Minute">Minutes from the local day's start, 0–1499, so a 25-hour day fits.</param>
/// <param name="GpuLoad">Null when no reading in the minute had one; so is <paramref name="Brightness"/>.</param>
/// <param name="Samples">How many readings went in.</param>
/// <param name="TotalSource">Where the total came from for most of the minute, as <c>TotalSource</c>'s number.</param>
/// <param name="GpuScope">What the card's figure covered for most of the minute, as <c>GpuPowerScope</c>'s number.</param>
/// <param name="MeasuredMask">The parts measured for at least half the minute, as <c>MeasuredParts</c>' bits.</param>
public sealed record MinuteRow(
    long StartMs, string Day, int Minute,
    double AvgW, double MaxW,
    double CpuW, double GpuW, double DisplayW, double RamW, double StorageW,
    double BoardW, double ExtrasW, double MonitorsW, double PsuLossW, double UnattributedW,
    double CpuLoad, double? GpuLoad, double? Brightness,
    double DisplayOnS, double IdleS, double LockedS, double BatteryS,
    double MeasuredS, double CalibratedS, double EstimatedS,
    int Samples, int TotalSource, int GpuScope, int MeasuredMask);
