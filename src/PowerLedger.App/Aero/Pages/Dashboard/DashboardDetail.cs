using System.Globalization;
using System.Text;
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>The history table's span on Aero's Dashboard (Aero look design §1): Day, Week, Month or Year.</summary>
internal enum HistorySpan
{
    Day,
    Week,
    Month,
    Year,
}

/// <summary>One row of Aero's history table, written for the page: "Today, so far", "0.391 kWh", "$0.05", "49 W",
/// "60 W", and how most of its time was measured.</summary>
internal sealed record HistoryRow(string Period, string Energy, string Cost, string Average, string Peak, Quality Source)
{
    /// <summary>The source as the table words it.</summary>
    public string SourceName => Source.ToString();
}

/// <summary>One day of "Energy each day": its energy and its cost as the tip says it ("$0.27"), or "no tariff set".</summary>
internal sealed record DailyDay(DateOnly Day, double Kwh, string Cost);

/// <summary>
/// What Aero's Dashboard shows beyond Midnight's (Aero look design §1, Plan S D3), from one pass of reads while <see
/// cref="DashboardViewModel.Detailed"/>: Power now's energy today and its change against yesterday to the same time, with
/// the tariff for the cost toggle; This month's day of the month; Energy each day's chosen month against the one before;
/// and the history table's rows for its span.
/// </summary>
/// <param name="ChangeVsYesterday">Today so far against yesterday from midnight to the same time, as a fraction of
/// yesterday's; null with nothing yesterday.</param>
/// <param name="PricePerKwh">The tariff in force now, null without one.</param>
/// <param name="Daily">The chosen month's days, the 1st to today while it is under way, a day with no history as nothing used.</param>
/// <param name="DailyPrevious">The whole of the month before, in kWh a day.</param>
internal sealed record DashboardDetail(
    double TodayKwh, double? ChangeVsYesterday, decimal? PricePerKwh, string? Currency, int MonthDay, int MonthDays,
    DateOnly DailyMonth, IReadOnlyList<DailyDay> Daily, IReadOnlyList<double> DailyPrevious, double DailyKwh, double DailyPreviousKwh,
    HistorySpan Span, IReadOnlyList<HistoryRow> History);

/// <summary>
/// Aero's history table (Aero look design §1), pure: a daily read cut into the span's periods, newest first, each with
/// its energy, cost, average over the time on, peak and the quality most of its time had; its search; and its CSV.
/// </summary>
internal static class HistoryTable
{
    /// <summary>How many periods each span lists, the one under way among them.</summary>
    public static int Periods(HistorySpan span) => span switch
    {
        HistorySpan.Day => 7,
        HistorySpan.Week => 4,
        HistorySpan.Month => 4,
        _ => 2,
    };

    /// <summary>The first day the span's read starts on: seven days back, the Monday three weeks before this week's, the
    /// first of the month three months back, or the 1st of January last year.</summary>
    public static DateOnly FirstDay(HistorySpan span, DateOnly today) => span switch
    {
        HistorySpan.Day => today.AddDays(-6),
        HistorySpan.Week => Monday(today).AddDays(-21),
        HistorySpan.Month => new DateOnly(today.Year, today.Month, 1).AddMonths(-3),
        _ => new DateOnly(today.Year - 1, 1, 1),
    };

    /// <summary>
    /// The rows for <paramref name="span"/> from a read by the day: the period under way always, the others only where the
    /// history has a day in them, so a period before the PC had PowerLedger is left out rather than shown as nothing used.
    /// </summary>
    public static IReadOnlyList<HistoryRow> Rows(HistorySpan span, RangeReport? read, DateOnly today, CultureInfo culture)
    {
        var days = read?.Days ?? [];
        var series = read?.Series ?? [];
        var rows = new List<HistoryRow>();
        var start = Start(span, today);
        for (var i = 0; i < Periods(span); i++)
        {
            var (from, until) = (Step(span, start, -i), Step(span, start, 1 - i));
            var inside = days.Where(day => day.Day >= from && day.Day < until).ToList();
            if (i > 0 && inside.Count == 0) continue;
            var quality = series.Where(row => DateOnly.FromDateTime(row.Start.DateTime) >= from && DateOnly.FromDateTime(row.Start.DateTime) < until)
                .Aggregate((Measured: 0.0, Calibrated: 0.0, Estimated: 0.0), (sum, row) =>
                    (sum.Measured + row.MeasuredSeconds, sum.Calibrated + row.CalibratedSeconds, sum.Estimated + row.EstimatedSeconds));
            var kwh = inside.Sum(day => Math.Max(0, day.EnergyKwh));
            var on = inside.Sum(day => Math.Max(0, day.OnHours));
            var currency = inside.Select(day => day.Currency).FirstOrDefault(c => c is not null);
            rows.Add(new HistoryRow(
                Name(span, from, until.AddDays(-1), today, i == 0, culture),
                Format.Kwh(kwh, culture) + " kWh",
                currency is null ? "no tariff set" : Money.Format(inside.Where(day => day.Currency == currency).Sum(day => day.Cost), currency, culture),
                on > 0 ? Format.WholeWatts(kwh * 1000 / on, culture) + " W" : Format.Missing,
                inside.Count > 0 ? Format.WholeWatts(inside.Max(day => day.PeakW), culture) + " W" : Format.Missing,
                Dominant(quality.Measured, quality.Calibrated, quality.Estimated)));
        }
        return rows;
    }

    /// <summary>The rows that mention <paramref name="query"/> in any column, in any case; all of them for a blank one.</summary>
    public static IReadOnlyList<HistoryRow> Matching(IReadOnlyList<HistoryRow> rows, string? query)
    {
        var q = query?.Trim();
        if (string.IsNullOrEmpty(q)) return rows;
        return [.. rows.Where(row => string.Join(' ', row.Period, row.Energy, row.Cost, row.Average, row.Peak, row.SourceName)
            .Contains(q, StringComparison.CurrentCultureIgnoreCase))];
    }

    /// <summary>The rows as a CSV file's lines, header first: the numbers bare, so a spreadsheet reads them as numbers.</summary>
    public static IReadOnlyList<string> Csv(HistorySpan span, IReadOnlyList<HistoryRow> rows)
    {
        static string Bare(string cell) => cell.EndsWith(" kWh", StringComparison.Ordinal) ? cell[..^4]
            : cell.EndsWith(" W", StringComparison.Ordinal) ? cell[..^2] : cell;
        static string Cell(string cell) => cell.Contains(',') || cell.Contains('"') ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : cell;
        var lines = new List<string> { $"{span},Energy (kWh),Cost,Average (W),Peak (W),Source" };
        lines.AddRange(rows.Select(row => string.Join(',',
            new[] { row.Period, Bare(row.Energy), row.Cost, Bare(row.Average), Bare(row.Peak), row.SourceName }.Select(Cell))));
        return lines;
    }

    /// <summary>The CSV's name: "PowerLedger history by day.csv".</summary>
    public static string FileName(HistorySpan span) => $"PowerLedger history by {span.ToString().ToLowerInvariant()}.csv";

    /// <summary>The CSV's lines as one file's text, CRLF as Windows writes it.</summary>
    public static string Text(IReadOnlyList<string> lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines) text.Append(line).Append("\r\n");
        return text.ToString();
    }

    private static DateOnly Monday(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private static DateOnly Start(HistorySpan span, DateOnly today) => span switch
    {
        HistorySpan.Day => today,
        HistorySpan.Week => Monday(today),
        HistorySpan.Month => new DateOnly(today.Year, today.Month, 1),
        _ => new DateOnly(today.Year, 1, 1),
    };

    private static DateOnly Step(HistorySpan span, DateOnly start, int periods) => span switch
    {
        HistorySpan.Day => start.AddDays(periods),
        HistorySpan.Week => start.AddDays(7 * periods),
        HistorySpan.Month => start.AddMonths(periods),
        _ => start.AddYears(periods),
    };

    /// <summary>"Today, so far", "Yesterday", "Sunday 6"; "This week, so far", "24 to 30 August", "31 August to 6
    /// September"; "September, so far", "August", "December 2025"; "2026, so far", "2025".</summary>
    private static string Name(HistorySpan span, DateOnly first, DateOnly last, DateOnly today, bool current, CultureInfo culture)
    {
        switch (span)
        {
            case HistorySpan.Day:
                return current ? "Today, so far" : first == today.AddDays(-1) ? "Yesterday" : first.ToString("dddd d", culture);
            case HistorySpan.Week:
                if (current) return "This week, so far";
                return first.Month == last.Month
                    ? $"{first.Day.ToString(culture)} to {last.ToString("d MMMM", culture)}"
                    : $"{first.ToString("d MMMM", culture)} to {last.ToString("d MMMM", culture)}";
            case HistorySpan.Month:
                var month = first.Year == today.Year ? first.ToString("MMMM", culture) : first.ToString("MMMM yyyy", culture);
                return current ? month + ", so far" : month;
            default:
                var year = first.Year.ToString(culture);
                return current ? year + ", so far" : year;
        }
    }

    /// <summary>The quality with the most time, ties to the better; Estimated with none, as <see cref="Aggregate.DominantQuality"/>.</summary>
    private static Quality Dominant(double measured, double calibrated, double estimated)
        => measured >= calibrated && measured >= estimated && measured > 0 ? Quality.Measured
            : calibrated >= estimated && calibrated > 0 ? Quality.Calibrated
            : Quality.Estimated;
}
