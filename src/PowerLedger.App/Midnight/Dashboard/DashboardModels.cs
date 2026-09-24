using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>What a KPI card's trend slot holds (Midnight look design §4): an arrow with a percentage, up meaning more
/// than the comparison and down less; a dash for no change; a quality chip; or plain words like "first month".</summary>
internal enum TrendKind
{
    Up,
    Down,
    Flat,
    Quality,
    Text,
}

/// <summary>One of the Dashboard's three cards.</summary>
/// <param name="Label">The small label over the number: "Power now".</param>
/// <param name="Big">The number with its unit: "34.2 W", or a dash.</param>
/// <param name="Small">The line under it: the reading's source, or the cost.</param>
/// <param name="Trend">What the trend slot says: "12%", "Measured", "first month"; null for nothing.</param>
/// <param name="Kind">How to draw it.</param>
/// <param name="Fill">How much of the bar is filled, 0 to 1; the rest is hatched.</param>
internal sealed record KpiCard(string Label, string Big, string Small, string? Trend, TrendKind Kind, double Fill);

/// <summary>One row of "Where the power went".</summary>
/// <param name="Glyph">A Segoe Fluent glyph for the part.</param>
/// <param name="NowW">The part's watts now, as the live budget shows them: "12.3 W".</param>
/// <param name="Energy">Its energy over the range: "284 Wh" or "2.74 kWh".</param>
/// <param name="Share">Its share of the four parts over the range, 0 to 1.</param>
/// <param name="Quality">How its live figure is got, for the chip; null without a reading.</param>
/// <param name="Trend">Against the same length of time before the range: "12%"; null with nothing to compare.</param>
internal sealed record DashboardPart(
    Part Part, string Name, string Glyph, string NowW, string Energy, double Share, Quality? Quality, string? Trend, TrendKind Kind);

/// <summary>The chart's range pills: 1H · 1D · 1W · 1M · 1Y · All.</summary>
internal enum RangePill
{
    Hour,
    Day,
    Week,
    Month,
    Year,
    All,
}

/// <summary>The parts table's range chooser: Today, 7 days, 30 days.</summary>
internal enum PartsRange
{
    Today,
    SevenDays,
    ThirtyDays,
}
