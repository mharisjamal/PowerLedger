using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>The Dashboard's figures (Midnight look design §4), pure: the average day, the trends and the bars.</summary>
internal static class DashboardMaths
{
    /// <summary>Under this much change either way a trend reads as flat.</summary>
    public const double FlatBelow = 0.005;

    /// <summary>The mean energy of the complete days given, in Wh; null with none. A day with no rows has no entry, so it
    /// is left out rather than counted as zero.</summary>
    public static double? AverageDayWh(IReadOnlyList<DayTotals> days) => days.Count == 0 ? null : days.Average(day => day.EnergyKwh) * 1000;

    /// <summary>Today so far against the average day up to the same time of day: the average scaled by the fraction of the
    /// day gone. The change as a fraction of what was expected; null without an average, or before the day has begun.</summary>
    public static double? TodayTrend(double todayWh, double? averageDayWh, double fractionOfDay)
    {
        if (averageDayWh is not > 0 || !(fractionOfDay > 0)) return null;
        var expected = averageDayWh.Value * Math.Min(1, fractionOfDay);
        return (todayWh - expected) / expected;
    }

    /// <summary>How much of a bar is filled: the value over its maximum, 0 to 1; none without a maximum.</summary>
    public static double Fill(double value, double max)
        => max > 0 && double.IsFinite(value) && double.IsFinite(max) ? Math.Clamp(value / max, 0, 1) : 0;

    /// <summary>This month against last, as a fraction of last month's; null with no last month to compare.</summary>
    public static double? MonthTrend(double thisWh, double? lastWh) => lastWh is > 0 ? (thisWh - lastWh.Value) / lastWh.Value : null;

    /// <summary>Which way a change points: up for more, down for less, flat for too little to mention.</summary>
    public static TrendKind Kind(double change) => Math.Abs(change) < FlatBelow ? TrendKind.Flat : change > 0 ? TrendKind.Up : TrendKind.Down;

    /// <summary>Whether a change is the good news or the bad: a rise is good unless <paramref name="lowerIsBetter"/>, as it
    /// is for energy, which costs; a fall the other way round; anything else is neither.</summary>
    public static TrendSense Sense(TrendKind kind, bool lowerIsBetter) => kind switch
    {
        TrendKind.Up => lowerIsBetter ? TrendSense.Bad : TrendSense.Good,
        TrendKind.Down => lowerIsBetter ? TrendSense.Good : TrendSense.Bad,
        _ => TrendSense.Neutral,
    };
}
