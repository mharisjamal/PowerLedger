namespace PowerLedger.Core;

/// <summary>Watts attributed to each part of the machine for one tick.</summary>
/// <param name="Rest">Watts not itemised elsewhere: the measured remainder (battery mode), the learned baseline (calibrated mode),
/// or the default laptop baseline (estimated laptop). 0 for estimated desktops, whose parts are itemised.</param>
public sealed record Components(
    double Cpu, double Gpu, double Display, double Ram, double Storage,
    double Board, double Extras, double Monitors, double PsuLoss, double Rest)
{
    /// <summary>Every part including PsuLoss and Rest. Equals TotalW once the model has filled PsuLoss; before that it is the pre-supply figure.</summary>
    public double Sum => Cpu + Gpu + Display + Ram + Storage + Board + Extras + Monitors + PsuLoss + Rest;

    public static Components Zero { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}
