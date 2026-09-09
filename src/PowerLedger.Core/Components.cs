namespace PowerLedger.Core;

/// <summary>Watts attributed to each part of the machine for one tick.</summary>
/// <param name="Unattributed">Watts not itemised elsewhere: the measured remainder (battery mode), the learned baseline (calibrated mode),
/// or the default laptop baseline (estimated laptop). 0 for estimated desktops, whose parts are itemised.
/// Narrower than EnergySlice.RestWh, which is the whole non-CPU/GPU/display energy band.</param>
public sealed record Components(
    double Cpu, double Gpu, double Display, double Ram, double Storage,
    double Board, double Extras, double Monitors, double PsuLoss, double Unattributed)
{
    /// <summary>Every part including PsuLoss and Unattributed. Equals TotalW once the model has filled PsuLoss; before that it is the pre-supply figure.</summary>
    public double Sum => Cpu + Gpu + Display + Ram + Storage + Board + Extras + Monitors + PsuLoss + Unattributed;

    public static Components Zero { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}
