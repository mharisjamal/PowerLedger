using System.Globalization;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>Energy each day's span on Aero's Dashboard (the mockup's segmented Day, Week and Month): fourteen of them.</summary>
internal enum HistorySpan
{
    Day,
    Week,
    Month,
}

/// <summary>One of Energy each day's fourteen bars: the day, week or month it starts on, the label under it ("17", or
/// "Sep" for a month), its energy, and what its tip says ("$0.27 on the 17th").</summary>
internal sealed record EnergyBar(DateOnly From, string Label, double Kwh, string Tip);

/// <summary>
/// What Aero's Dashboard shows beyond Midnight's (Aero look design §1, Plan S D3), from one pass of reads while <see
/// cref="DashboardViewModel.Detailed"/>: Power now's energy today and its change against yesterday to the same time, with
/// the tariff for the cost toggle; This month's day of the month; and Energy each day's fourteen bars of its span.
/// </summary>
/// <param name="ChangeVsYesterday">Today so far against yesterday from midnight to the same time, as a fraction of
/// yesterday's; null with nothing yesterday.</param>
/// <param name="PricePerKwh">The tariff in force now, null without one.</param>
/// <param name="Bars">Fourteen periods of <paramref name="Span"/>, oldest first, the one under way last; a period with no
/// history as nothing used.</param>
internal sealed record DashboardDetail(
    double TodayKwh, double? ChangeVsYesterday, decimal? PricePerKwh, string? Currency, int MonthDay, int MonthDays,
    HistorySpan Span, IReadOnlyList<EnergyBar> Bars);

/// <summary>
/// Energy each day's bars (the mockup's fourteen), pure: a read by the day cut into the last fourteen days, weeks (from
/// Monday) or months, the one under way last, each with its energy and its tip.
/// </summary>
internal static class EnergyBars
{
    /// <summary>How many bars the chart draws, as the mockup's.</summary>
    public const int Count = 14;

    /// <summary>The first day the read starts on: thirteen days back, the Monday thirteen weeks before this week's, or
    /// the first of the month thirteen months back.</summary>
    public static DateOnly FirstDay(HistorySpan span, DateOnly today) => Step(span, Start(span, today), 1 - Count);

    /// <summary>The fourteen bars of <paramref name="span"/> from a read by the day, oldest first.</summary>
    public static IReadOnlyList<EnergyBar> From(HistorySpan span, RangeReport? read, DateOnly today, CultureInfo culture)
    {
        var days = read?.Days ?? [];
        var start = Start(span, today);
        var bars = new List<EnergyBar>(Count);
        for (var i = Count - 1; i >= 0; i--)
        {
            var (from, until) = (Step(span, start, -i), Step(span, start, 1 - i));
            var inside = days.Where(day => day.Day >= from && day.Day < until).ToList();
            var kwh = inside.Sum(day => Math.Max(0, day.EnergyKwh));
            var currency = inside.Select(day => day.Currency).FirstOrDefault(c => c is not null);
            var figure = currency is null
                ? Format.Kwh(kwh, culture) + " kWh"
                : Money.Format(inside.Where(day => day.Currency == currency).Sum(day => day.Cost), currency, culture);
            bars.Add(new EnergyBar(from, Label(span, from, culture), kwh, Tip(span, from, figure, culture)));
        }
        return bars;
    }

    /// <summary>"17" for a day or a week (its Monday), "Sep" for a month.</summary>
    public static string Label(HistorySpan span, DateOnly from, CultureInfo culture)
        => span == HistorySpan.Month ? from.ToString("MMM", culture) : from.Day.ToString(culture);

    /// <summary>"$0.27 on the 17th", "1.84 kWh in the week from the 7th", "$5.73 in September".</summary>
    public static string Tip(HistorySpan span, DateOnly from, string figure, CultureInfo culture) => span switch
    {
        HistorySpan.Day => $"{figure} on the {Ordinal(from.Day)}",
        HistorySpan.Week => $"{figure} in the week from the {Ordinal(from.Day)}",
        _ => $"{figure} in {from.ToString("MMMM", culture)}",
    };

    /// <summary>"1st", "2nd", "3rd", "11th", "22nd".</summary>
    public static string Ordinal(int day)
        => day.ToString(CultureInfo.InvariantCulture) + (day % 100 is 11 or 12 or 13 ? "th" : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    private static DateOnly Monday(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private static DateOnly Start(HistorySpan span, DateOnly today) => span switch
    {
        HistorySpan.Day => today,
        HistorySpan.Week => Monday(today),
        _ => new DateOnly(today.Year, today.Month, 1),
    };

    private static DateOnly Step(HistorySpan span, DateOnly start, int periods) => span switch
    {
        HistorySpan.Day => start.AddDays(periods),
        HistorySpan.Week => start.AddDays(7 * periods),
        _ => start.AddMonths(periods),
    };
}
