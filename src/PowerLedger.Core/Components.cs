namespace PowerLedger.Core;

/// <summary>Watts attributed to each part of the machine for one tick.</summary>
/// <param name="Rest">Measured remainder (battery mode) or learned baseline (calibrated mode); 0 in estimated mode.</param>
public sealed record Components(
    double Cpu, double Gpu, double Display, double Ram, double Storage,
    double Board, double Extras, double Monitors, double PsuLoss, double Rest)
{
    public double Sum => Cpu + Gpu + Display + Ram + Storage + Board + Extras + Monitors + PsuLoss + Rest;

    public static Components Zero { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}
