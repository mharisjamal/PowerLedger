using System.Globalization;
using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>A labelled figure in a report: "LED bulb", "274 hours", "a 10 W bulb".</summary>
internal sealed record ReportLine(string Name, string Value, string Note);

/// <summary>How much of the on-time each quality covered.</summary>
internal sealed record QualityMix(double Measured, double Calibrated, double Estimated);

/// <summary>One day's energy for the daily bars.</summary>
internal sealed record DayBar(DateOnly Day, double Kwh);

/// <summary>
/// Everything a report shows (spec §9 Report), written out. The Report screen, the PDF and the monthly job all read this,
/// so they always agree.
/// </summary>
internal sealed record ReportData(
    string Title, string Period, bool HasData,
    string Energy, string Cost, string CostNote, string Co2, string Co2Note,
    string Average, string Peak, string PeakAt,
    string On, string IdleOn, string IdleOff, string Asleep, string Unmonitored,
    string IdleWaste, string IdleWasteNote, string Advice,
    IReadOnlyList<PartRow> Parts, IReadOnlyList<ReportLine> Equivalents,
    QualityMix Quality, string QualityText, IReadOnlyList<DayBar> Days)
{
    public static ReportData Empty { get; } = new(
        "", "", false, Format.Missing, Format.Missing, "", Format.Missing, "", Format.Missing, Format.Missing, "",
        Format.Missing, Format.Missing, Format.Missing, Format.Missing, Format.Missing, Format.Missing, "", "",
        [], [], new QualityMix(0, 0, 0), "", []);

    /// <summary>The report for a range, priced as history priced it, with CO₂ at <paramref name="co2KgPerKwh"/> and the
    /// suggestion from Windows' <paramref name="sleep"/> timeouts.</summary>
    public static ReportData From(RangeReport report, SleepTimeouts sleep, double co2KgPerKwh, TimeZoneInfo zone, CultureInfo culture)
    {
        var t = report.Totals;
        var (first, last) = Ranges.Covered(report.Range, zone);
        decimal? price = t.Currency is not null && !t.CostIsPartial && t.EnergyKwh > 0 ? t.Cost / (decimal)t.EnergyKwh : null;
        var idle = t.IdleOnKwh + t.IdleOffKwh;
        var idleShare = Format.Percent(t.EnergyKwh > 0 ? idle / t.EnergyKwh : 0, culture) + " of the energy";
        return new ReportData(
            report.Range.Title,
            Ranges.Span(first, last, culture),
            t.OnHours > 0 || t.AsleepHours > 0,
            Format.Kwh(t.EnergyKwh, culture),
            t.Currency is { } currency ? Money.Format(t.Cost, currency, culture) : Format.Missing,
            CostNoteOf(t, price, culture),
            Format.Kg(Core.Co2.Kg(t.EnergyKwh, co2KgPerKwh), culture) + " kg",   // Co2 alone would be the property
            $"at {co2KgPerKwh.ToString("0.00", culture)} kg / kWh",
            Format.WholeWatts(t.AvgW, culture),
            Format.WholeWatts(t.PeakW, culture),
            t.PeakAt is { } at && t.PeakW > 0
                ? "at " + TimeZoneInfo.ConvertTime(at, zone).ToString(first == last ? "HH:mm" : "HH:mm, ddd d MMM", culture)
                : "",
            Format.Duration(t.OnHours), Format.Duration(t.IdleOnHours), Format.Duration(t.IdleOffHours),
            Format.Duration(t.AsleepHours), Format.Duration(t.UnmonitoredHours),
            Format.Kwh(idle, culture) + " kWh",
            price is { } rate && t.Currency is { } money
                ? $"≈ {Money.Format(decimal.Round((decimal)idle * rate, 2), money, culture)} · {idleShare}"
                : t.EnergyKwh > 0 ? idleShare : "",
            IdleAdvice.For(idle, t.IdleOnKwh, t.EnergyKwh, sleep),
            Bands.Rows(t, culture, withTotal: false),
            EquivalentsOf(t.EnergyKwh, culture),
            new QualityMix(t.MeasuredShare, t.CalibratedShare, t.EstimatedShare),
            t.MeasuredShare + t.CalibratedShare + t.EstimatedShare > 0
                ? $"{Format.Percent(t.MeasuredShare, culture)} measured · {Format.Percent(t.CalibratedShare, culture)} calibrated · {Format.Percent(t.EstimatedShare, culture)} estimated"
                : "no readings",
            Bars(first, Ranges.LocalDay(report.Range.Through.AddTicks(-1), zone), report.Days));
    }

    /// <summary>The average price over the range, which follows any tariff change; or why there is none.</summary>
    private static string CostNoteOf(RangeTotals t, decimal? price, CultureInfo culture)
    {
        if (t.Currency is not { } currency) return "no tariff set";
        if (t.CostIsPartial) return "partial: energy priced in an earlier currency is left out";
        return price is { } p ? Money.Rate(decimal.Round(p, 4), currency, culture) + " / kWh on average" : "";
    }

    /// <summary>Spec §9's everyday equivalents, from Core's round assumptions.</summary>
    private static IReadOnlyList<ReportLine> EquivalentsOf(double kwh, CultureInfo culture) =>
    [
        new("LED bulb", Count(Comparisons.LedBulbHours(kwh), culture) + " hours", "a 10 W bulb"),
        new("Phone charges", Count(Comparisons.PhoneCharges(kwh), culture), "15 Wh each"),
        new("Electric car", Count(Comparisons.EvKm(kwh), culture) + " km", "at 0.18 kWh / km"),
    ];

    private static string Count(double value, CultureInfo culture) => value < 10 ? value.ToString("0.0", culture) : value.ToString("N0", culture);

    /// <summary>A bar for every day of the range, those without readings at zero.</summary>
    private static IReadOnlyList<DayBar> Bars(DateOnly first, DateOnly last, IReadOnlyList<DayTotals> days)
    {
        var energy = days.ToDictionary(d => d.Day, d => d.EnergyKwh);
        var count = Math.Max(1, last.DayNumber - first.DayNumber + 1);
        return [.. Enumerable.Range(0, count).Select(i => first.AddDays(i)).Select(day => new DayBar(day, energy.GetValueOrDefault(day)))];
    }
}
