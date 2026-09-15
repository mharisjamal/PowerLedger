using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>Whether the App can reach the service.</summary>
internal enum Connection
{
    Connecting,
    Connected,
    Down,
}

/// <param name="Post">Runs an action on the UI thread.</param>
/// <param name="Background">Runs an action off the UI thread.</param>
internal sealed record UiThreads(Action<Action> Post, Action<Action> Background)
{
    /// <summary>Everything at once on the caller's thread, for tests.</summary>
    public static UiThreads Inline { get; } = new(action => action(), action => action());
}

/// <summary>One row of the power budget: a band, what it is, and its share now.</summary>
internal sealed record BudgetRow(Part Part, string Name, string Detail, string Watts, string Percent, double Share);

/// <summary>The live half of the Now screen, replaced whole on every reading.</summary>
internal sealed record LivePanel(
    double Watts, string Eyebrow, Quality? Quality, string QualityNote, IReadOnlyList<double> Spark,
    MeterRange Meter, double AverageW, double PeakW, IReadOnlyList<BudgetRow> Budget, double BudgetTotalW)
{
    public static LivePanel Waiting { get; } = new(0, "Live · waiting for the service", null, "", [], MeterRange.For(0), 0, 0, [], 0);
}

/// <summary>Today's ledger, as the user reads it.</summary>
internal sealed record TodayLedger(
    string Date, string Energy, string Cost, string Tariff, string Average, string Peak, string PeakAt,
    string On, string Idle, string IdleWasted, string Asleep, string Co2, string Co2Factor)
{
    public static TodayLedger Empty { get; } = new("", "–", "–", "", "–", "–", "", "–", "–", "", "–", "–", "");
}

/// <summary>The month-to-date ledger, as the user reads it.</summary>
internal sealed record MonthLedger(
    string Name, string Summary, string Energy, string Cost, string Projected, string ProjectedEnergy, string DailyAverage,
    string IdleWaste, string IdleWasteCost, string Lowest, string LowestDate, string Highest, string HighestDate, string NextReport)
{
    public static MonthLedger Empty { get; } = new("", "", "–", "–", "–", "", "–", "–", "", "–", "", "–", "", "");
}

/// <summary>Today's chart and its legend.</summary>
internal sealed record DayChartModel(
    IReadOnlyList<DaySlot> Slots, DateTimeOffset DayStart, DateTimeOffset Now,
    string CpuWh, string GpuWh, string DisplayWh, string RestWh)
{
    public static DayChartModel Empty { get; } = new([], default, default, "–", "–", "–", "–");
}

/// <summary>The status bar and the rail's footer.</summary>
internal sealed record StatusLine(bool Running, string Service, string Sampling, string Calibration, string Database, string? Notice)
{
    public static StatusLine Down { get; } = new(false, "", "", "", "", null);

    public string State => Running ? "Service running" : "Service not running";
}
