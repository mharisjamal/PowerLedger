namespace PowerLedger.Core;

/// <summary>Energy contributed by one tick. RestWh is everything that is not CPU, GPU or display
/// (broader than Components.Rest, which excludes the itemised parts such as RAM, board and PSU loss).</summary>
public sealed record EnergySlice(
    double Wh, double CpuWh, double GpuWh, double DisplayWh, double RestWh,
    double IdleOnWh, double IdleOffWh,
    double IdleOnSeconds, double IdleOffSeconds,
    double OnSeconds, double BatterySeconds, double GapSeconds, bool Gap)
{
    public static EnergySlice Nothing { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false);

    public static EnergySlice GapOf(double seconds) => Nothing with { GapSeconds = seconds, Gap = true };
}
