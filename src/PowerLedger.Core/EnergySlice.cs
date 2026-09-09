namespace PowerLedger.Core;

/// <summary>Energy contributed by one tick. DisplayWh covers the internal panel plus opted-in external monitors;
/// RestWh is everything else (broader than Components.Rest, which excludes the itemised parts such as RAM, board and PSU loss).
/// A gap tick (see <see cref="EnergyIntegrator"/>) carries only GapSeconds.</summary>
public sealed record EnergySlice(
    double Wh, double CpuWh, double GpuWh, double DisplayWh, double RestWh,
    double IdleOnWh, double IdleOffWh,
    double IdleOnSeconds, double IdleOffSeconds,
    double OnSeconds, double BatterySeconds, double GapSeconds)
{
    public bool Gap => GapSeconds > 0;

    public static EnergySlice Nothing { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public static EnergySlice GapOf(double seconds) => Nothing with { GapSeconds = seconds };
}
