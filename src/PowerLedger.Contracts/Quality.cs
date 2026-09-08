namespace PowerLedger.Contracts;

/// <summary>How trustworthy a total-power reading is. Higher is better.</summary>
public enum Quality
{
    /// <summary>Component sum with default baselines (about ±20 %).</summary>
    Estimated = 0,
    /// <summary>Component sum with a baseline learned from battery data (about ±10 %).</summary>
    Calibrated = 1,
    /// <summary>Whole-system battery discharge rate (about ±3 %).</summary>
    Measured = 2,
}
