namespace PowerLedger.Core;

/// <summary>Learned "rest of system" watts for a calibration bucket, or null when nothing was learned yet.</summary>
public interface IBaselineProvider
{
    double? GetBaseline(int bucket);
}
