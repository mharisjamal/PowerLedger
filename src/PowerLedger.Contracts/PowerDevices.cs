namespace PowerLedger.Contracts;

/// <summary>What a discrete GPU's watts cover, as its vendor's library measured them. Piped and stored as an integer: append
/// new members, never renumber.</summary>
public enum GpuPowerScope
{
    /// <summary>The whole card: chip, memory, voltage regulators and fans (NVML, AMD's total board power, Intel's card
    /// domain).</summary>
    Board = 0,

    /// <summary>The chip and its memory only (AMD's ASIC or TGP power): the card draws more, which the model estimates.</summary>
    ChipOnly = 1,

    /// <summary>The GPU package only (Intel's package domain): the card draws more, which the model estimates.</summary>
    Package = 2,
}

/// <summary>Where a reading's total came from. Piped as an integer: append new members, never renumber.</summary>
public enum TotalSource
{
    /// <summary>The power model, from the parts it reads and the machine profile.</summary>
    Model = 0,

    /// <summary>The laptop's battery discharge rate.</summary>
    Battery = 1,

    /// <summary>The output a UPS reports, for what the user says it powers.</summary>
    Ups = 2,

    /// <summary>What a power supply reports: the wall power it draws, or its DC output with its efficiency allowed for.</summary>
    PowerSupply = 3,
}

/// <summary>What the outlets of a UPS attached over USB power, as the user says. Stored as an integer: append new members,
/// never renumber.</summary>
public enum UpsLoad
{
    /// <summary>The user hasn't said, so the UPS's reading isn't used.</summary>
    NotSaid = 0,

    /// <summary>This PC and nothing else.</summary>
    ThisPc = 1,

    /// <summary>This PC and the external monitors PowerLedger counts, and nothing else.</summary>
    ThisPcAndMonitors = 2,

    /// <summary>More than this PC and its monitors, so the reading can't stand for them and isn't used.</summary>
    More = 3,
}

/// <summary>How a UPS's output watts were found, from the most exact to the least. Piped as an integer: append new members,
/// never renumber.</summary>
public enum UpsPowerSource
{
    None = 0,

    /// <summary>The UPS reports its real output power.</summary>
    ActivePower = 1,

    /// <summary>Its load percentage of its rated watts: whole percents, so steps of 1% of the rating.</summary>
    LoadOfRatedWatts = 2,

    /// <summary>Its load percentage of its rated volt-amperes, at an assumed power factor of 0.8: an estimate.</summary>
    LoadOfRatedVoltAmps = 3,
}

/// <summary>A kind of device that reports power over USB. Piped as an integer: append new members, never renumber.</summary>
public enum PowerDeviceKind
{
    Ups = 0,
    PowerSupply = 1,
}

/// <summary>A UPS or power supply the service reads, for the App to show and ask about.</summary>
/// <param name="Name">"APC Back-UPS ES 850G2", "Corsair HX1000i".</param>
/// <param name="Watts">A UPS's output watts or a power supply's DC output watts, as last read; null when not read yet.</param>
/// <param name="How">How the watts were found, e.g. "load 27% of 520 W" or "DC output, all rails".</param>
public sealed record PowerDeviceStatus(PowerDeviceKind Kind, string Name, double? Watts, string How);
