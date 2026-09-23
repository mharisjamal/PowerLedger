namespace PowerLedger.Core;

/// <summary>Which of a reading's figures a device measured rather than the model worked out (data-sharing design §3).
/// Stored with each reading as an integer mask: add bits, never renumber.</summary>
[Flags]
public enum MeasuredParts
{
    None = 0,

    /// <summary>The processor's watts came from its energy meter.</summary>
    Cpu = 1,

    /// <summary>The graphics card's watts came from its maker's library: NVIDIA's, AMD's or Intel Arc's.</summary>
    Gpu = 2,

    /// <summary>The total came from the battery, a UPS or a power supply rather than the model.</summary>
    Total = 4,
}
