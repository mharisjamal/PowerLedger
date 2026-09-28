namespace PowerLedger.App;

/// <summary>
/// The Insights page's four findings (Aero look design §4), worked out on the PC from the hour rows the history already
/// holds, and nothing sent anywhere. <see cref="Read"/> is the whole contract between the maths (agent I) and the views
/// that show it (agents D and I): it reads history, so it is called off the UI thread, and it never throws for want of
/// data. A finding it can't make yet says so (<see cref="BillForecast.Ready"/>, an empty anomaly list, null habits).
/// </summary>
internal interface IInsights
{
    /// <summary>The findings as they stand at <paramref name="now"/>, with days, weekdays and hours counted in
    /// <paramref name="zone"/>.</summary>
    InsightsReport Read(DateTimeOffset now, TimeZoneInfo zone);
}

/// <summary>Everything the Insights page, the Dashboard's forecast slot and the bell show, from one read.</summary>
/// <param name="Anomalies">Unusual hours, newest first; the bell shows at most three a day (design §4).</param>
/// <param name="Habits">Null until there is a week of hour rows to draw the heatmap from.</param>
internal sealed record InsightsReport(BillForecast Forecast, IReadOnlyList<UsageAnomaly> Anomalies, IdleHabits? Habits, CarbonEstimate Carbon);

/// <summary>
/// The likely cost at the month's end, with the range it will most likely fall in (design §4: the remaining days as the
/// median of the same weekday over the last 8 weeks; the range the 10th to 90th percentile of backtested errors). Not
/// <see cref="Ready"/> until <see cref="DaysOfData"/> reaches a week, when the page says "Needs a week of data" and the
/// figures are 0.
/// </summary>
/// <param name="Currency">The tariff's currency code; null when no tariff is set, and the costs are then 0.</param>
/// <param name="ProjectedKwh">The likely energy at the month's end, in kWh, with or without a tariff, so a PC with none still
/// sees a forecast; <paramref name="LowKwh"/> and <paramref name="HighKwh"/> are its range.</param>
internal sealed record BillForecast(
    decimal ProjectedCost, decimal Low, decimal High, string? Currency, int DaysOfData, bool Ready,
    double ProjectedKwh = 0, double LowKwh = 0, double HighKwh = 0)
{
    /// <summary>Days of history the forecast needs before it says anything.</summary>
    public const int DaysNeeded = 7;

    /// <summary>The forecast before there is a week of data.</summary>
    public static BillForecast NotReady(int daysOfData, string? currency) => new(0, 0, 0, currency, daysOfData, false);
}

/// <summary>An hour that used far more than it normally does at that time on that weekday (design §4: over the median
/// plus 3.5 robust deviations, and at least 30 Wh over it).</summary>
/// <param name="Hour">The start of the hour, local time.</param>
/// <param name="Kwh">What the hour used.</param>
/// <param name="NormalKwh">The median for that weekday and hour over the last 8 weeks.</param>
/// <param name="Times">How many times the normal it was, for "3.2× normal"; <see cref="double.PositiveInfinity"/> when
/// the normal is 0.</param>
internal sealed record UsageAnomaly(DateTimeOffset Hour, double Kwh, double NormalKwh, double Times);

/// <summary>
/// When the PC sits on and idle (design §4), over the last 4 weeks of hour rows.
/// </summary>
/// <param name="Heatmap">Idle watt-hours, averaged per week, indexed [(int)DayOfWeek, hour of day]: 7 × 24.</param>
/// <param name="WorstWindowStart">The local hour of day, 0 to 23, the costliest idle window starts at, across the week;
/// the window may run past midnight.</param>
/// <param name="WorstWindowHours">How long that window is; 2 as the design has it.</param>
/// <param name="SavingPerMonthCost">What sleeping after 10 idle minutes would save a month, in <paramref name="Currency"/>.</param>
/// <param name="SavingPerMonthKwh">The same saving in kWh.</param>
/// <param name="Currency">The tariff's currency code; null when no tariff is set, and the cost is then 0.</param>
internal sealed record IdleHabits(
    double[,] Heatmap, int WorstWindowStart, int WorstWindowHours, decimal SavingPerMonthCost, double SavingPerMonthKwh, string? Currency)
{
    public const int Days = 7;

    public const int Hours = 24;
}

/// <summary>The CO₂ the PC's energy stands for (design §4): kWh times the grid's factor.</summary>
/// <param name="MonthKg">This month so far.</param>
/// <param name="SinceStartKg">Since the first row in the history.</param>
/// <param name="GramsPerKwh">The factor used.</param>
/// <param name="Source">Where the factor came from, as the page says it: the user's own setting, the region's figure with
/// its source and year, or the world average.</param>
internal sealed record CarbonEstimate(double MonthKg, double SinceStartKg, double GramsPerKwh, string Source);
