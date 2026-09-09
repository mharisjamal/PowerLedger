using PowerLedger.Core;

namespace PowerLedger.Storage;

public sealed class ReportQueries(SqliteDatabase db)
{
    /// <summary>Ranges up to this length read minute rows; longer ranges read hour rows.</summary>
    public static readonly TimeSpan MinuteResolutionLimit = TimeSpan.FromDays(3);

    public RangeTotals Totals(DateTimeOffset from, DateTimeOffset to)
    {
        var rows = Load(from, to);
        var schedule = new TariffRepository(db).Schedule();

        double energy = 0, cpu = 0, gpu = 0, display = 0, rest = 0, idleOn = 0, idleOff = 0;
        double onS = 0, idleOnS = 0, idleOffS = 0, measuredS = 0, calibratedS = 0, estimatedS = 0, peak = 0;
        DateTimeOffset? peakAt = null;
        foreach (var r in rows)
        {
            energy += r.EnergyWh; cpu += r.CpuWh; gpu += r.GpuWh; display += r.DisplayWh; rest += r.RestWh;
            idleOn += r.IdleOnWh; idleOff += r.IdleOffWh;
            onS += r.OnSeconds; idleOnS += r.IdleOnSeconds; idleOffS += r.IdleOffSeconds;
            measuredS += r.MeasuredSeconds; calibratedS += r.CalibratedSeconds; estimatedS += r.EstimatedSeconds;
            if (r.MaxW > peak) { peak = r.MaxW; peakAt = r.Start; }
        }

        var qualityS = measuredS + calibratedS + estimatedS;
        var onHours = onS / 3600.0;
        var cost = schedule.Cost(rows.Select(r => (r.Start, r.EnergyWh)));
        return new RangeTotals(
            from, to,
            EnergyKwh: energy / 1000,
            Cost: cost.Amount,
            Currency: cost.Currency,
            CostIsPartial: cost.Partial,
            AvgW: onHours > 0 ? energy / onHours : 0,
            PeakW: peak, PeakAt: peakAt,
            OnHours: onHours,
            IdleOnHours: idleOnS / 3600.0,
            IdleOffHours: idleOffS / 3600.0,
            AsleepHours: Math.Max(0, (to - from).TotalHours - onHours),
            CpuKwh: cpu / 1000, GpuKwh: gpu / 1000, DisplayKwh: display / 1000, RestKwh: rest / 1000,
            IdleOnKwh: idleOn / 1000, IdleOffKwh: idleOff / 1000,
            MeasuredShare: Share(measuredS, qualityS),
            CalibratedShare: Share(calibratedS, qualityS),
            EstimatedShare: Share(estimatedS, qualityS));
    }

    /// <summary>One bucket per local calendar day that has rows, oldest first.</summary>
    public List<DayTotals> DailyBuckets(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        var schedule = new TariffRepository(db).Schedule();
        return Load(from, to)
            .GroupBy(r => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(r.Start, zone).DateTime))
            .OrderBy(g => g.Key)
            .Select(g => new DayTotals(
                g.Key,
                EnergyKwh: g.Sum(r => r.EnergyWh) / 1000,
                Cost: schedule.Cost(g.Select(r => (r.Start, r.EnergyWh))).Amount,
                OnHours: g.Sum(r => r.OnSeconds) / 3600.0,
                PeakW: g.Max(r => r.MaxW),
                IdleOnKwh: g.Sum(r => r.IdleOnWh) / 1000,
                IdleOffKwh: g.Sum(r => r.IdleOffWh) / 1000))
            .ToList();
    }

    private List<Aggregate> Load(DateTimeOffset from, DateTimeOffset to)
    {
        var repo = new AggregateRepository(db);
        return to - from <= MinuteResolutionLimit ? repo.ReadMinutes(from, to) : repo.ReadHours(from, to);
    }

    private static double Share(double part, double whole) => whole > 0 ? part / whole : 0;
}
