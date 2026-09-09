using PowerLedger.Core;

namespace PowerLedger.Storage;

/// <summary>Read side of the report: range totals and daily bars, with cost priced at query time.</summary>
public sealed class ReportQueries(SqliteDatabase db)
{
    /// <summary>Ranges up to this length read minute rows; longer ranges read hour rows.</summary>
    public static readonly TimeSpan MinuteResolutionLimit = TimeSpan.FromDays(3);

    /// <summary>Totals and daily bars from one read of the rows and one read of the tariffs, so the two always agree.</summary>
    public (RangeTotals Totals, List<DayTotals> Days) Report(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        var window = Load(from, to);
        var schedule = new TariffRepository(db).Schedule();
        return (Summarise(window, schedule), Days(window.Rows, schedule, zone));
    }

    /// <summary>Totals for the range. The window is widened to whole rows of the resolution used, so no partial row is dropped.</summary>
    public RangeTotals Totals(DateTimeOffset from, DateTimeOffset to)
        => Summarise(Load(from, to), new TariffRepository(db).Schedule());

    /// <summary>One bucket per local calendar day that has rows, oldest first. Days are cut at local midnight; in a zone
    /// whose offset is not a whole number of hours, a range long enough to read hour rows shifts that cut to the enclosing UTC hour.</summary>
    public List<DayTotals> DailyBuckets(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
        => Days(Load(from, to).Rows, new TariffRepository(db).Schedule(), zone);

    private Window Load(DateTimeOffset from, DateTimeOffset to)
    {
        var repo = new AggregateRepository(db);
        var minute = TimeSpan.FromMinutes(1);
        var hour = TimeSpan.FromHours(1);
        var wantMinutes = to - from <= MinuteResolutionLimit;
        if (wantMinutes)
        {
            var rows = repo.ReadMinutes(Floor(from, minute), Ceiling(to, minute));
            if (rows.Count > 0) return new Window(Floor(from, minute), Ceiling(to, minute), rows);
        }

        // Minute rows are purged after a year or two; hour rows are kept forever, so an old short range still reports.
        var hourRows = repo.ReadHours(Floor(from, hour), Ceiling(to, hour));
        return hourRows.Count > 0 || !wantMinutes
            ? new Window(Floor(from, hour), Ceiling(to, hour), hourRows)
            : new Window(Floor(from, minute), Ceiling(to, minute), []);
    }

    private static RangeTotals Summarise(Window w, TariffSchedule schedule)
    {
        double energy = 0, cpu = 0, gpu = 0, display = 0, rest = 0, idleOn = 0, idleOff = 0;
        double onS = 0, idleOnS = 0, idleOffS = 0, gapS = 0, measuredS = 0, calibratedS = 0, estimatedS = 0, peak = 0;
        DateTimeOffset? peakAt = null;
        foreach (var r in w.Rows)
        {
            energy += r.EnergyWh; cpu += r.CpuWh; gpu += r.GpuWh; display += r.DisplayWh; rest += r.RestWh;
            idleOn += r.IdleOnWh; idleOff += r.IdleOffWh;
            onS += r.OnSeconds; idleOnS += r.IdleOnSeconds; idleOffS += r.IdleOffSeconds; gapS += r.GapSeconds;
            measuredS += r.MeasuredSeconds; calibratedS += r.CalibratedSeconds; estimatedS += r.EstimatedSeconds;
            if (r.MaxW > peak) { peak = r.MaxW; peakAt = r.Start; }
        }

        var qualityS = measuredS + calibratedS + estimatedS;
        var onHours = onS / 3600.0;
        var asleepHours = gapS / 3600.0;
        var cost = schedule.Cost(w.Rows.Select(r => (r.Start, r.EnergyWh)));
        return new RangeTotals(
            From: w.From, To: w.To,
            EnergyKwh: energy / 1000,
            Cost: cost.Amount,
            Currency: cost.Currency,
            CostIsPartial: cost.Partial,
            AvgW: onHours > 0 ? energy / onHours : 0,
            PeakW: peak, PeakAt: peakAt,
            OnHours: onHours,
            IdleOnHours: idleOnS / 3600.0,
            IdleOffHours: idleOffS / 3600.0,
            AsleepHours: asleepHours,
            UnmonitoredHours: Math.Max(0, (w.To - w.From).TotalHours - onHours - asleepHours),
            CpuKwh: cpu / 1000, GpuKwh: gpu / 1000, DisplayKwh: display / 1000, RestKwh: rest / 1000,
            IdleOnKwh: idleOn / 1000, IdleOffKwh: idleOff / 1000,
            MeasuredShare: Share(measuredS, qualityS),
            CalibratedShare: Share(calibratedS, qualityS),
            EstimatedShare: Share(estimatedS, qualityS));
    }

    private static List<DayTotals> Days(List<Aggregate> rows, TariffSchedule schedule, TimeZoneInfo zone)
        => rows
            .GroupBy(r => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(r.Start, zone).DateTime))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var cost = schedule.Cost(g.Select(r => (r.Start, r.EnergyWh)));
                return new DayTotals(
                    g.Key,
                    EnergyKwh: g.Sum(r => r.EnergyWh) / 1000,
                    Cost: cost.Amount,
                    Currency: cost.Currency,
                    CostIsPartial: cost.Partial,
                    OnHours: g.Sum(r => r.OnSeconds) / 3600.0,
                    PeakW: g.Max(r => r.MaxW),
                    IdleOnKwh: g.Sum(r => r.IdleOnWh) / 1000,
                    IdleOffKwh: g.Sum(r => r.IdleOffWh) / 1000);
            })
            .ToList();

    private static DateTimeOffset Floor(DateTimeOffset t, TimeSpan unit) => t.AddTicks(-(t.Ticks % unit.Ticks));

    private static DateTimeOffset Ceiling(DateTimeOffset t, TimeSpan unit)
    {
        var remainder = t.Ticks % unit.Ticks;
        return remainder == 0 ? t : t.AddTicks(unit.Ticks - remainder);
    }

    private static double Share(double part, double whole) => whole > 0 ? part / whole : 0;

    private sealed record Window(DateTimeOffset From, DateTimeOffset To, List<Aggregate> Rows);
}
