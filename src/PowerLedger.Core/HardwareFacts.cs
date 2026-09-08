namespace PowerLedger.Core;

/// <summary>Numbers from the hardware inventory that the model needs for sensorless fallbacks.</summary>
public sealed record HardwareFacts(double CpuTdpW, double GpuTdpW)
{
    public static HardwareFacts LaptopDefaults { get; } = new(CpuTdpW: 15, GpuTdpW: 25);
    public static HardwareFacts DesktopDefaults { get; } = new(CpuTdpW: 65, GpuTdpW: 75);
}
