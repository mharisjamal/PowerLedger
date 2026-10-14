namespace PowerLedger.Storage;

/// <summary>Everything the Report screen shows for one range. AsleepHours is time the machine slept while the service
/// was running (gaps between ticks); UnmonitoredHours is range time that produced no rows at all, such as before install.
/// EnergyKwh and Cost count what the machine drew asleep, estimated (accuracy A10), which AsleepKwh gives apart; the parts,
/// the idle figures and AvgW are the machine's awake.
/// CostIsPartial is true when some energy was priced under a different currency and left out of Cost.
/// From and To are the window actually covered: the requested range widened to whole rows of the resolution used.</summary>
public sealed record RangeTotals(
    DateTimeOffset From, DateTimeOffset To,
    double EnergyKwh, decimal Cost, string? Currency, bool CostIsPartial,
    double AvgW, double PeakW, DateTimeOffset? PeakAt,
    double OnHours, double IdleOnHours, double IdleOffHours, double AsleepHours, double UnmonitoredHours,
    double CpuKwh, double GpuKwh, double DisplayKwh, double RestKwh,
    double IdleOnKwh, double IdleOffKwh,
    double MeasuredShare, double CalibratedShare, double EstimatedShare,
    double AsleepKwh = 0);

/// <summary>One local calendar day of the report. Costs are rounded per day, so they need not sum to the range total
/// exactly; the difference is below a millionth of a unit per day. EnergyKwh and Cost count what the machine drew asleep
/// that day, which AsleepKwh gives apart: a night's sleep is split between the days it spanned.</summary>
public sealed record DayTotals(
    DateOnly Day, double EnergyKwh, decimal Cost, string? Currency, bool CostIsPartial,
    double OnHours, double PeakW, double IdleOnKwh, double IdleOffKwh,
    double AsleepKwh = 0);
