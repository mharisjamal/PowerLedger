using PowerLedger.Core;

namespace PowerLedger.Storage;

/// <summary>Read side of the report: range totals, daily bars and chart series, with cost priced at query time.</summary>
public sealed class ReportQueries(SqliteDatabase db)
{
    /// <summary>Ranges up to this length read minute rows; longer ranges read hour rows.</summary>
    public static readonly TimeSpan MinuteResolutionLimit = TimeSpan.FromDays(3);

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

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

    /// <summary>
    /// The range cut into buckets of <paramref name="bucket"/> from <paramref name="from"/> (spec §7 GetSeries), made from
    /// the rows the totals read, so a chart and its totals agree. A row counts wholly in the bucket where it starts, so a
    /// bucket shorter than an hour wants a range short enough to read minute rows. AvgW is each bucket's energy over its
    /// on-time. GapSeconds is laid back: each sleep is spread backwards from the moment the machine woke over the buckets
    /// it covered, so a chart shows it where it happened, and no bucket holds more sleep than its own length.
    /// </summary>
    public List<Aggregate> Series(DateTimeOffset from, DateTimeOffset to, TimeSpan bucket)
    {
        var count = Math.Max(1, (int)Math.Ceiling((to - from) / bucket));
        var buckets = new Aggregate[count];
        var asleep = new double[count];
        for (var i = 0; i < count; i++) buckets[i] = Aggregate.Empty(from + i * bucket);

        var window = Load(from, to);
        var rows = window.Hours.Select(h => (Row: h, Length: Hour)).Concat(window.Minutes.Select(m => (Row: m, Length: Minute)));
        foreach (var (row, length) in rows)
        {
            var index = Math.Clamp((int)Math.Floor((row.Start - from) / bucket), 0, count - 1);
            buckets[index] = buckets[index].Plus(row with { GapSeconds = 0 });
            // A sleep is stored in the row where the machine woke, and the time on in that row came after the wake.
            var woke = row.Start + length - TimeSpan.FromSeconds(Math.Min(row.OnSeconds, length.TotalSeconds));
            LayBack(asleep, woke, row.GapSeconds, from, bucket);
        }
        return [.. buckets.Select((b, i) => b with
        {
            AvgW = b.OnSeconds > 0 ? b.EnergyWh * 3600 / b.OnSeconds : 0,
            GapSeconds = Math.Min(asleep[i], bucket.TotalSeconds),
        })];
    }

    /// <summary>
    /// The rows for a range, and the window they cover: the range widened to whole rows. A short range reads minute rows
    /// throughout, so it is exact to the minute; an hour row stands in only for an hour whose minutes retention has purged.
    /// A long range reads hour rows, which are kept forever, and minute rows only past the last hour the hourly job has
    /// folded, so the read stays bounded and an hour in progress is never lost. No hour is ever counted twice.
    /// </summary>
    private Window Load(DateTimeOffset from, DateTimeOffset to)
    {
        var repo = new AggregateRepository(db);
        var hourFrom = Floor(from, Hour);
        var hourTo = Ceiling(to, Hour);
        var minuteFrom = Floor(from, Minute);
        var minuteTo = Ceiling(to, Minute);

        if (to - from <= MinuteResolutionLimit)
        {
            var minutes = repo.ReadMinutes(minuteFrom, minuteTo);
            var covered = minutes.Select(m => Floor(m.Start, Hour)).ToHashSet();
            var purged = repo.ReadHours(hourFrom, hourTo).Where(h => !covered.Contains(h.Start)).ToList();
            return purged.Count > 0 ? new Window(hourFrom, hourTo, purged, minutes) : new Window(minuteFrom, minuteTo, [], minutes);
        }

        var hours = repo.ReadHours(hourFrom, hourTo);
        var tailFrom = Max(minuteFrom, hours.Count > 0 ? hours[^1].Start + Hour : hourTo - MinuteResolutionLimit);
        var tail = tailFrom < minuteTo ? repo.ReadMinutes(tailFrom, minuteTo) : [];
        return hours.Count > 0 ? new Window(hourFrom, hourTo, hours, tail) : new Window(minuteFrom, minuteTo, [], tail);
    }

    /// <summary>Spreads a sleep of <paramref name="seconds"/> that ended at <paramref name="end"/> backwards over the buckets
    /// it covered. Sleep before the range is dropped.</summary>
    private static void LayBack(double[] asleep, DateTimeOffset end, double seconds, DateTimeOffset from, TimeSpan bucket)
    {
        var cursor = end;
        while (seconds > 0 && cursor > from)
        {
            var index = (int)Math.Floor((cursor - from - TimeSpan.FromTicks(1)) / bucket);
            var start = from + index * bucket;
            var taken = Math.Min(seconds, (cursor - start).TotalSeconds);
            if (index < asleep.Length) asleep[index] += taken;
            seconds -= taken;
            cursor = start;
        }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

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

    /// <summary>The rows a range reads, hour rows and minute rows apart so a series knows each row's length.</summary>
    private sealed record Window(DateTimeOffset From, DateTimeOffset To, List<Aggregate> Hours, List<Aggregate> Minutes)
    {
        public List<Aggregate> Rows { get; } = [.. Hours.Concat(Minutes).OrderBy(r => r.Start)];
    }
}
