using System.Text.Json.Serialization;

namespace PowerLedger.Contracts;

/// <summary>Watts per part for one tick, as the power model split them. Mirrors Core's Components.</summary>
public sealed record ComponentWatts(
    double Cpu, double Gpu, double Display, double Ram, double Storage,
    double Board, double Extras, double Monitors, double PsuLoss, double Unattributed);

/// <summary>One tick as the App sees it, pushed to every subscriber (spec §8).</summary>
/// <param name="Quality">How far to trust the total.</param>
/// <param name="CpuMeasured">The processor's watts came from its energy meter this tick, not the load model.</param>
/// <param name="GpuMeasured">Every discrete card's watts were reported this tick (0 W for a card Windows has switched off);
/// false means the load model worked out at least one card's, or there is no card.</param>
/// <param name="GpuScope">What a measured GPU figure covers; a chip or package reading has the rest of the card estimated.</param>
/// <param name="Total">Where the total came from. Both came later: a frame without them is from an older service.</param>
public sealed record ReadingFrame(
    DateTimeOffset Timestamp, double DeltaSeconds, double TotalW, Quality Quality, ComponentWatts Components,
    bool CpuMeasured, bool GpuMeasured,
    bool OnBattery, bool DisplayOn, bool UserIdle, bool SessionLocked,
    double CpuLoad, double? GpuLoad, double? Brightness, bool Suspect,
    GpuPowerScope GpuScope = GpuPowerScope.Board, TotalSource Total = TotalSource.Model) : PipeMessage
{
    /// <summary>The display band: the internal panel plus the external monitors counted (spec §6).</summary>
    [JsonIgnore]
    public double DisplayBandW => Components.Display + Components.Monitors;

    /// <summary>The rest band: everything that is not CPU, GPU or display (spec §6).</summary>
    [JsonIgnore]
    public double RestBandW => TotalW - Components.Cpu - Components.Gpu - DisplayBandW;
}
