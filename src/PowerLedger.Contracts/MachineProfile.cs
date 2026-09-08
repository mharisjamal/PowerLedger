namespace PowerLedger.Contracts;

public enum ChassisKind { Desktop, Laptop }

public enum PsuTier { White, Bronze, Silver, Gold, Platinum, Titanium }

/// <summary>What the machine is made of, as detected at install time and corrected by the user.</summary>
public sealed record MachineProfile(
    ChassisKind Chassis,
    int RamSticks,
    bool RamIsDdr5,
    int SsdCount,
    int HddCount,
    int FanCount,
    PsuTier PsuTier,
    double ExtrasWatts,
    int ExternalMonitors,
    bool IncludeMonitors,
    double MonitorWatts,
    double DisplayDiagonalInches,
    double? CpuTdpOverrideW,
    double? GpuTdpOverrideW)
{
    public static MachineProfile DefaultLaptop { get; } = new(
        ChassisKind.Laptop, RamSticks: 1, RamIsDdr5: false, SsdCount: 1, HddCount: 0, FanCount: 1,
        PsuTier.Bronze, ExtrasWatts: 0, ExternalMonitors: 0, IncludeMonitors: false, MonitorWatts: 25,
        DisplayDiagonalInches: 15.6, CpuTdpOverrideW: null, GpuTdpOverrideW: null);

    public static MachineProfile DefaultDesktop { get; } = new(
        ChassisKind.Desktop, RamSticks: 2, RamIsDdr5: false, SsdCount: 1, HddCount: 0, FanCount: 3,
        PsuTier.Bronze, ExtrasWatts: 0, ExternalMonitors: 1, IncludeMonitors: false, MonitorWatts: 25,
        DisplayDiagonalInches: 0, CpuTdpOverrideW: null, GpuTdpOverrideW: null);
}
