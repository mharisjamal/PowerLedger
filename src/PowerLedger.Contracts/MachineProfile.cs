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
    /// <summary>Built-in panel diagonal. 0 means unknown on a laptop and no built-in panel on a desktop; a desktop with
    /// one, an all-in-one, gives its size so the panel counts.</summary>
    public double DisplayDiagonalInches { get; init; } = 15.6;
    public double? CpuTdpOverrideW { get; init; }
    public double? GpuTdpOverrideW { get; init; }
    /// <summary>The user's choice for each external monitor, by <see cref="MonitorChoice.Key"/>. A monitor with no choice
    /// takes PowerLedger's own figure and its guess at whether the monitor has a plug of its own, and counts as
    /// <see cref="CountMonitorsByDefault"/> says; a monitor that runs off this PC counts whatever its choice.
    /// <see cref="ExternalMonitors"/>, <see cref="IncludeMonitors"/> and <see cref="MonitorWatts"/> came before it: they stay
    /// only so settings from then can be carried over, and no longer reach the model.</summary>
    public IReadOnlyList<MonitorChoice> Monitors { get; init; } = [];

    /// <summary>Whether an external monitor with a plug of its own that the user hasn't chosen for counts; one that runs off
    /// this PC always counts. False only where the settings from before monitors were detected left monitors out, so a
    /// monitor first seen later is left out as they were.</summary>
    public bool CountMonitorsByDefault { get; init; } = true;

    /// <summary>What the outlets of a UPS read over USB power. Its reading stands for this PC only once the user has said
    /// it powers this PC, or this PC and its monitors.</summary>
    public UpsLoad UpsLoad { get; init; } = UpsLoad.NotSaid;

    /// <summary>Whether a power supply that reports over USB is read. On by default; off leaves the device to its maker's
    /// program alone.</summary>
    public bool ReadPowerSupply { get; init; } = true;

    public static MachineProfile DefaultLaptop { get; } = new();

    public static MachineProfile DefaultDesktop { get; } = new()
    {
        Chassis = ChassisKind.Desktop,
        RamSticks = 2,
        FanCount = 3,
        DisplayDiagonalInches = 0,
    };

    /// <summary>
    /// A record compares a list by reference, so a profile read back from the pipe or the database would never equal the
    /// one written. A profile compares its monitor choices one by one instead, and every other member as a record would.
    /// </summary>
    public bool Equals(MachineProfile? other) =>
        ReferenceEquals(this, other)
        || (other is not null && Members().Equals(other.Members()) && SameChoices(Monitors, other.Monitors));

    public override int GetHashCode() => HashCode.Combine(Members(), Monitors?.Count);

    /// <summary>Every member but <see cref="Monitors"/>. A member added to the profile must be added here, and a test
    /// fails until it is.</summary>
    private (ChassisKind, int, bool, int, int, int, PsuTier, double, int, bool, double, double, double?, double?, bool, UpsLoad, bool) Members() =>
        (Chassis, RamSticks, RamIsDdr5, SsdCount, HddCount, FanCount, PsuTier, ExtrasWatts, ExternalMonitors, IncludeMonitors,
            MonitorWatts, DisplayDiagonalInches, CpuTdpOverrideW, GpuTdpOverrideW, CountMonitorsByDefault, UpsLoad, ReadPowerSupply);

    private static bool SameChoices(IReadOnlyList<MonitorChoice>? a, IReadOnlyList<MonitorChoice>? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && a.SequenceEqual(b));
}
