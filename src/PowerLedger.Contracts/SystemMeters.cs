namespace PowerLedger.Contracts;

/// <summary>What measured a whole machine's draw at its input, for the machines that measure it themselves: workstations and
/// servers with a platform power meter or a baseboard management controller. Piped as an integer: append new members, never
/// renumber.</summary>
public enum SystemMeterKind
{
    None = 0,

    /// <summary>Windows' own power meter interface (the "Power Meter" counters), fed by an ACPI power meter device
    /// (ACPI000D) that measures the platform's input power. A battery's meter is never taken for one.</summary>
    PowerMeter = 1,

    /// <summary>The baseboard management controller, asked for its DCMI power reading through Windows' own IPMI driver.</summary>
    Bmc = 2,
}
