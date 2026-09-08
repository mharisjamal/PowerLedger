namespace PowerLedger.Contracts;

/// <summary>Persisted and piped as an integer: append new members, never renumber.</summary>
public enum ChassisKind
{
    Desktop = 0,
    Laptop = 1,
}

/// <summary>80 PLUS tier of a desktop power supply. Persisted as an integer: append new members, never renumber.</summary>
public enum PsuTier
{
    White = 0,
    Bronze = 1,
    Silver = 2,
    Gold = 3,
    Platinum = 4,
    Titanium = 5,
}

/// <summary>
/// What the machine is made of, as detected at install time and corrected by the user.
/// Init-only properties with initialisers: a JSON blob that lacks a property (written by an older version)
/// deserialises to the documented default instead of zero, and adding a property later is not a breaking change.
/// </summary>
public sealed record MachineProfile
{
    public ChassisKind Chassis { get; init; } = ChassisKind.Laptop;
    public int RamSticks { get; init; } = 1;
    public bool RamIsDdr5 { get; init; }
    public int SsdCount { get; init; } = 1;
    public int HddCount { get; init; }
    public int FanCount { get; init; } = 1;
    /// <summary>Only used for desktops; laptops use adapter efficiency instead.</summary>
    public PsuTier PsuTier { get; init; } = PsuTier.Bronze;
    /// <summary>User-declared extras (RGB, pumps, USB devices) in watts.</summary>
    public double ExtrasWatts { get; init; }
    public int ExternalMonitors { get; init; }
    /// <summary>External monitors are self-powered; they count only when the user opts in.</summary>
    public bool IncludeMonitors { get; init; }
    /// <summary>Watts per external monitor while the display is on.</summary>
    public double MonitorWatts { get; init; } = 25;
    /// <summary>Internal panel diagonal. 0 means unknown or no internal panel (desktops).</summary>
    public double DisplayDiagonalInches { get; init; } = 15.6;
    public double? CpuTdpOverrideW { get; init; }
    public double? GpuTdpOverrideW { get; init; }

    public static MachineProfile DefaultLaptop { get; } = new();

    public static MachineProfile DefaultDesktop { get; } = new()
    {
        Chassis = ChassisKind.Desktop,
        RamSticks = 2,
        FanCount = 3,
        ExternalMonitors = 1,
        DisplayDiagonalInches = 0,
    };
}
