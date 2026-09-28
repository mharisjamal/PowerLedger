namespace PowerLedger.App;

/// <summary>The Bill forecast card as the page shows it (Aero look design §4).</summary>
/// <param name="Ready">Whether there is a forecast to draw; otherwise <see cref="Cost"/> says why not.</param>
/// <param name="Cost">The likely bill, or what stands in for it: "Needs a week of data", "No tariff set".</param>
/// <param name="Range">"Likely £16.40 to £19.75"; empty when not ready.</param>
/// <param name="Note">What the figure rests on, or what is still needed.</param>
/// <param name="Maximum">The band's scale: the high end with room to spare, so the band never touches the edge.</param>
internal sealed record ForecastCard(bool Ready, string Cost, string Range, string Note, double Low, double Projected, double High, double Maximum);

/// <summary>An unusual hour as the page lists it, with what its small chart draws.</summary>
/// <param name="When">"Tue 15 Sep, 14:00 to 15:00".</param>
/// <param name="Maximum">The largest hour or normal among the rows, so every row's chart has the same scale.</param>
internal sealed record AnomalyRow(string When, string Used, string Normal, string Times, double Kwh, double NormalKwh, double Maximum);

/// <summary>The Habits card: the heatmap and what it says.</summary>
/// <param name="Heatmap">Idle Wh, [(int)DayOfWeek, hour]; null until there is a week of data.</param>
/// <param name="Worst">"Idle time costs most from 22:00 to 00:00", or the card's empty state.</param>
/// <param name="Saving">What sleeping sooner would save a month, worded as the idle advice is; empty with nothing to save.</param>
internal sealed record HabitsCard(bool Ready, double[,]? Heatmap, int WorstStart, int WorstHours, string Worst, string Saving);

/// <summary>The Carbon card.</summary>
/// <param name="Factor">"236 g CO₂ per kWh".</param>
internal sealed record CarbonCard(string Month, string SinceStart, string Factor, string Source);
