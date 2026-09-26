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

/// <summary>Whether a trend is the good news (green), the bad (red) or neither (muted ink); see <see cref="DashboardMaths.Sense"/>.</summary>
internal enum TrendSense
{
    Neutral,
    Good,
    Bad,
}

/// <summary>One of the Dashboard's three cards.</summary>
/// <param name="Label">The small label over the number: "Power now".</param>
/// <param name="Big">The number with its unit: "34.2 W", or a dash.</param>
/// <param name="Small">The line under it: the reading's source, or the cost.</param>
/// <param name="Trend">What the trend slot says: "12%", "Measured", "first month"; null for nothing.</param>
/// <param name="Kind">How to draw it.</param>
/// <param name="Fill">How much of the bar is filled, 0 to 1; the rest is hatched.</param>
internal sealed record KpiCard(string Label, string Big, string Small, string? Trend, TrendKind Kind, double Fill)
{
    /// <summary>Whether a fall is the good news: true for energy, which costs.</summary>
    public bool LowerIsBetter { get; init; }

    /// <summary>The period the card covers, on its period button: "Since start", "Today"; null for a card whose period is
    /// its own and fixed, which the view names.</summary>
    public string? Period { get; init; }

    /// <summary>What the trend is against, beside the cost: "vs last week"; or, for a card with no bar, the line in its
    /// place: "since 20 July, 0.30 kWh a day on average". Null for a card whose view says it, or for nothing to say.</summary>
    public string? Against { get; init; }

    /// <summary>Whether the card has its striped bar and trend; since the start there is nothing to measure it against.</summary>
    public bool HasBar { get; init; } = true;
}

/// <summary>One row of "Where the power went".</summary>
/// <param name="Glyph">A Segoe Fluent glyph for the part.</param>
/// <param name="NowW">The part's watts now, as the live budget shows them: "12.3 W".</param>
/// <param name="Energy">Its energy over the range: "284 Wh" or "2.74 kWh".</param>
/// <param name="Share">Its share of the four parts over the range, 0 to 1.</param>
/// <param name="Quality">How its live figure is got, for the chip; null without a reading.</param>
/// <param name="Trend">Against the same length of time before the range: "12%"; null with nothing to compare.</param>
internal sealed record DashboardPart(
    Part Part, string Name, string Glyph, string NowW, string Energy, double Share, Quality? Quality, string? Trend, TrendKind Kind)
{
    /// <summary>Whether a fall is the good news: true for a part's energy, which costs.</summary>
    public bool LowerIsBetter { get; init; }

    /// <summary>What the part is, in a few words under its name: "NVIDIA GeForce RTX 3060"; null when Windows doesn't say.</summary>
    public string? Model { get; init; }
}

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

/// <summary>What the Energy used card covers, as its period menu offers it: everything this PC has recorded, the default;
/// today; the calendar week so far; the calendar month so far. Saved in ui.json by name.</summary>
internal enum EnergyPeriod
{
    SinceStart,
    Today,
    ThisWeek,
    ThisMonth,
}

/// <summary>The parts table's range chooser: Today, 7 days, 30 days.</summary>
internal enum PartsRange
{
    Today,
    SevenDays,
    ThirtyDays,
}
