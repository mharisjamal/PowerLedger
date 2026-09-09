namespace PowerLedger.Storage;

/// <summary>Everything the Report screen shows for one range. AsleepHours is time the machine slept while the service
/// was running (gaps between ticks); UnmonitoredHours is range time that produced no rows at all, such as before install.
/// CostIsPartial is true when some energy was priced under a different currency and left out of Cost.
/// From and To are the window actually covered: the requested range widened to whole rows of the resolution used.</summary>
public sealed record RangeTotals(
    DateTimeOffset From, DateTimeOffset To,
    double EnergyKwh, decimal Cost, string? Currency, bool CostIsPartial,
    double AvgW, double PeakW, DateTimeOffset? PeakAt,
    double OnHours, double IdleOnHours, double IdleOffHours, double AsleepHours, double UnmonitoredHours,
    double CpuKwh, double GpuKwh, double DisplayKwh, double RestKwh,
    double IdleOnKwh, double IdleOffKwh,
    double MeasuredShare, double CalibratedShare, double EstimatedShare);

/// <summary>One local calendar day of the report. Costs are rounded per day, so they need not sum to the range total
/// exactly; the difference is below a millionth of a unit per day.</summary>
public sealed record DayTotals(
    DateOnly Day, double EnergyKwh, decimal Cost, string? Currency, bool CostIsPartial,
    double OnHours, double PeakW, double IdleOnKwh, double IdleOffKwh);
