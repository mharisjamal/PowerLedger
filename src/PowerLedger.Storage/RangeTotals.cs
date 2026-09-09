namespace PowerLedger.Storage;

/// <summary>Everything the Report screen shows for one range. Asleep = range length minus on-time.
/// CostIsPartial is true when some energy was priced under a different currency and left out of Cost.</summary>
public sealed record RangeTotals(
    DateTimeOffset From, DateTimeOffset To,
    double EnergyKwh, decimal Cost, string? Currency, bool CostIsPartial,
    double AvgW, double PeakW, DateTimeOffset? PeakAt,
    double OnHours, double IdleOnHours, double IdleOffHours, double AsleepHours,
    double CpuKwh, double GpuKwh, double DisplayKwh, double RestKwh,
    double IdleOnKwh, double IdleOffKwh,
    double MeasuredShare, double CalibratedShare, double EstimatedShare);

public sealed record DayTotals(DateOnly Day, double EnergyKwh, decimal Cost, double OnHours, double PeakW, double IdleOnKwh, double IdleOffKwh);
