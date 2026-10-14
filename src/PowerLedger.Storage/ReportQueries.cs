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
        return (Summarise(window, schedule, to), Days(window, schedule, zone, to));
    }

    /// <summary>Totals for the range. The window is widened to whole rows of the resolution used, so no partial row is dropped.</summary>
    public RangeTotals Totals(DateTimeOffset from, DateTimeOffset to)
        => Summarise(Load(from, to), new TariffRepository(db).Schedule(), to);

    /// <summary>One bucket per local calendar day that has rows or sleep, oldest first. Days are cut at local midnight; in a
    /// zone whose offset is not a whole number of hours, a range long enough to read hour rows shifts that cut to the
    /// enclosing UTC hour.</summary>
    public List<DayTotals> DailyBuckets(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
        => Days(Load(from, to), new TariffRepository(db).Schedule(), zone, to);

    /// <summary>
    /// The range cut into buckets of <paramref name="bucket"/> from <paramref name="from"/> (spec §7 GetSeries), made from
    /// the rows the totals read, so a chart and its totals agree. A row counts wholly in the bucket where it starts, so a
    /// bucket shorter than an hour wants a range short enough to read minute rows. AvgW is each bucket's energy over its
    /// on-time. GapSeconds is laid back: each sleep is spread backwards from the moment the machine woke over the buckets
    /// it covered, so a chart shows it where it happened, and no bucket holds more sleep than its own length; AsleepWh, what
    /// the machine drew asleep, is laid back with it, evenly over the sleep. Given
    /// <paramref name="zone"/>, day buckets are cut at its local midnights instead, so across a clock change each is still
    /// one calendar day, of 23 or 25 hours; shorter buckets ignore it.
    /// </summary>
    public List<Aggregate> Series(DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, TimeZoneInfo? zone = null)
    {
        var cut = new Cut(from, bucket, bucket == TimeSpan.FromDays(1) ? zone : null);
        var count = cut.Count(to);
        var buckets = new Aggregate[count];
        var asleep = new double[count];
        var asleepWh = new double[count];
        for (var i = 0; i < count; i++) buckets[i] = Aggregate.Empty(cut.Start(i));

        var window = Load(from, to);
        foreach (var (row, length) in window.Sized)
        {
            var index = Math.Clamp(cut.Index(row.Start), 0, count - 1);
            buckets[index] = buckets[index].Plus(row with { GapSeconds = 0, AsleepWh = 0 });
            LayBack(asleep, asleepWh, Woke(row, length), row.GapSeconds, row.AsleepWh, from, cut);
        }
        return [.. buckets.Select((b, i) => b with
        {
            AvgW = b.OnSeconds > 0 ? b.EnergyWh * 3600 / b.OnSeconds : 0,
            GapSeconds = Math.Min(asleep[i], (cut.Start(i + 1) - cut.Start(i)).TotalSeconds),
            AsleepWh = asleepWh[i],
        })];
    }

    /// <summary>
    /// The rows for a range, and the window they cover: the range widened to whole rows. A short range reads minute rows
    /// throughout, so it is exact to the minute; an hour row stands in only for an hour whose minutes retention has purged,
    /// one older than the oldest minute row kept (report finding 7: an hour with no minutes in the range is otherwise one
    /// the PC slept through in it, and its row holds minutes from outside the range, before a range that starts mid hour,
    /// as Today does in a zone half an hour off UTC). A long range reads hour rows, which are kept forever, and minute rows
    /// only past the last hour the hourly job has folded, so the read stays bounded and an hour in progress is never lost.
    /// No hour is ever counted twice.
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
            if (purged.Count > 0 && repo.FirstMinuteStart() is { } kept) purged.RemoveAll(h => h.Start >= kept);
            return purged.Count > 0 ? new Window(hourFrom, hourTo, purged, minutes) : new Window(minuteFrom, minuteTo, [], minutes);
        }

        var hours = repo.ReadHours(hourFrom, hourTo);
        var tailFrom = Max(minuteFrom, hours.Count > 0 ? hours[^1].Start + Hour : hourTo - MinuteResolutionLimit);
        var tail = tailFrom < minuteTo ? repo.ReadMinutes(tailFrom, minuteTo) : [];
        return hours.Count > 0 ? new Window(hourFrom, hourTo, hours, tail) : new Window(minuteFrom, minuteTo, [], tail);
    }

    /// <summary>Spreads a sleep of <paramref name="seconds"/> that ended at <paramref name="end"/> backwards over the buckets
    /// it covered, and the <paramref name="wh"/> drawn over it evenly with it. Sleep before the range is dropped.</summary>
    private static void LayBack(double[] asleep, double[] asleepWh, DateTimeOffset end, double seconds, double wh, DateTimeOffset from, Cut cut)
    {
        var perSecond = seconds > 0 ? wh / seconds : 0;
        var cursor = end;
        while (seconds > 0 && cursor > from)
        {
            var index = cut.Index(cursor - TimeSpan.FromTicks(1));
            var start = cut.Start(index);
            var taken = Math.Min(seconds, (cursor - start).TotalSeconds);
            if (index < asleep.Length)
            {
                asleep[index] += taken;
                asleepWh[index] += taken * perSecond;
            }
            seconds -= taken;
            cursor = start;
        }
    }

    /// <summary>
    /// Each row's sleep laid back from its wake, as a chart lays it, and only its part inside the window up to
    /// <paramref name="end"/>: a night's sleep from 22:00 is 7 hours of a day that starts at midnight, not 9 (report
    /// finding 7). With it, what the machine drew asleep in that part (accuracy A10): the row's asleep energy, shared evenly
    /// over its sleep.
    /// </summary>
    private static IEnumerable<(Aggregate Row, DateTimeOffset From, DateTimeOffset To, double Wh)> Sleeps(Window w, DateTimeOffset end)
    {
        foreach (var (row, length) in w.Sized)
        {
            if (row.GapSeconds <= 0) continue;
            var woke = Woke(row, length);
            var from = Max(woke - TimeSpan.FromSeconds(row.GapSeconds), w.From);
            var to = Min(woke, end);
            if (to > from) yield return (row, from, to, row.AsleepWh * (to - from).TotalSeconds / row.GapSeconds);
        }
    }

    /// <summary>When the machine woke from the sleep a row holds: a sleep is stored in the row where the machine woke, and
    /// the time on in that row came after the wake.</summary>
    private static DateTimeOffset Woke(Aggregate row, TimeSpan length)
        => row.Start + length - TimeSpan.FromSeconds(Math.Min(row.OnSeconds, length.TotalSeconds));

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    /// <param name="to">The range's end. The window runs on to the next whole row, but time past the end has not happened
    /// yet, so it is neither asleep nor unmonitored (report finding 7).</param>
    private RangeTotals Summarise(Window w, TariffSchedule schedule, DateTimeOffset to)
    {
        double energy = 0, counted = 0, battery = 0, cpu = 0, gpu = 0, display = 0, rest = 0, idleOn = 0, idleOff = 0;
        double onS = 0, idleOnS = 0, idleOffS = 0, gapS = 0, measuredS = 0, calibratedS = 0, estimatedS = 0, peak = 0;
        DateTimeOffset? peakAt = null;
        Aggregate? peakRow = null;
        foreach (var r in w.Rows)
        {
            // What the battery gave counts at the wall it was charged from (accuracy A9); the average stays at the pack.
            energy += r.EnergyWh; counted += r.AwakeCountedWh; battery += r.BatteryWh;
            cpu += r.CpuWh; gpu += r.GpuWh; display += r.DisplayWh; rest += r.RestWh;
            idleOn += r.IdleOnWh; idleOff += r.IdleOffWh;
            onS += r.OnSeconds; idleOnS += r.IdleOnSeconds; idleOffS += r.IdleOffSeconds;
            measuredS += r.MeasuredSeconds; calibratedS += r.CalibratedSeconds; estimatedS += r.EstimatedSeconds;
            if (r.MaxW > peak) { peak = r.MaxW; peakAt = r.Start; peakRow = r; }
        }

        // Each sleep only as far as it falls in the window, and what the machine drew asleep then (accuracy A10), which
        // counts in the energy and its cost, priced as the row the sleep is stored in.
        var end = Max(w.From, Min(w.To, to));
        double asleep = 0;
        var asleepPriced = new List<(DateTimeOffset, double)>();
        foreach (var (row, from, until, wh) in Sleeps(w, end))
        {
            gapS += (until - from).TotalSeconds;
            asleep += wh;
            asleepPriced.Add((row.Start, wh));
        }

        // An hour row's start is no time for its peak (report finding 19): the minute it was in, while the hour's minutes
        // are kept; once retention has purged them, nothing says when in the hour it was, so no time is given.
        if (peakRow is not null && w.Hours.Exists(h => ReferenceEquals(h, peakRow)))
            peakAt = new AggregateRepository(db).ReadMinutes(peakRow.Start, peakRow.Start + Hour).MaxBy(m => m.MaxW)?.Start;

        var qualityS = measuredS + calibratedS + estimatedS;
        var onHours = onS / 3600.0;
        var asleepHours = gapS / 3600.0;
        var cost = schedule.Cost(w.Rows.Select(r => (r.Start, r.AwakeCountedWh)).Concat(asleepPriced));
        return new RangeTotals(
            From: w.From, To: w.To,
            EnergyKwh: (counted + asleep) / 1000,
            Cost: cost.Amount,
            Currency: cost.Currency,
            CostIsPartial: cost.Partial,
            AvgW: onHours > 0 ? energy / onHours : 0,
            PeakW: peak, PeakAt: peakAt,
            OnHours: onHours,
            IdleOnHours: idleOnS / 3600.0,
            IdleOffHours: idleOffS / 3600.0,
            AsleepHours: asleepHours,
            UnmonitoredHours: Math.Max(0, (end - w.From).TotalHours - onHours - asleepHours),
            CpuKwh: cpu / 1000, GpuKwh: gpu / 1000, DisplayKwh: display / 1000, RestKwh: rest / 1000,
            IdleOnKwh: idleOn / 1000, IdleOffKwh: idleOff / 1000,
            MeasuredShare: Share(measuredS, qualityS),
            CalibratedShare: Share(calibratedS, qualityS),
            EstimatedShare: Share(estimatedS, qualityS),
            AsleepKwh: asleep / 1000,
            BatteryKwh: battery * BatteryRoundTrip.Factor / 1000);
    }

    /// <summary>A bucket per local day that has rows or sleep. What the machine drew asleep goes to the days the sleep
    /// spanned, cut at local midnight, and only as far as it falls in the window up to <paramref name="to"/>, as the totals
    /// count it, so the days add up to them; it is priced as the row the sleep is stored in, as the totals price it.</summary>
    private static List<DayTotals> Days(Window w, TariffSchedule schedule, TimeZoneInfo zone, DateTimeOffset to)
    {
        var asleep = new Dictionary<DateOnly, List<(DateTimeOffset Start, double Wh)>>();
        foreach (var (row, from, until, wh) in Sleeps(w, Max(w.From, Min(w.To, to))))
        {
            for (var cursor = until; cursor > from;)
            {
                var day = LocalDay(cursor - TimeSpan.FromTicks(1), zone);
                var start = Max(Midnight(day, zone), from);
                if (!asleep.TryGetValue(day, out var pieces)) asleep[day] = pieces = [];
                pieces.Add((row.Start, wh * (cursor - start).TotalSeconds / (until - from).TotalSeconds));
                cursor = start;
            }
        }

        var awake = w.Rows.GroupBy(r => LocalDay(r.Start, zone)).ToDictionary(g => g.Key, g => g.ToList());
        return [.. awake.Keys.Union(asleep.Keys).Order().Select(day =>
        {
            var rows = awake.GetValueOrDefault(day) ?? [];
            var slept = asleep.GetValueOrDefault(day) ?? [];
            var asleepWh = slept.Sum(piece => piece.Wh);
            var cost = schedule.Cost(rows.Select(r => (r.Start, r.AwakeCountedWh)).Concat(slept));
            return new DayTotals(
                day,
                EnergyKwh: (rows.Sum(r => r.AwakeCountedWh) + asleepWh) / 1000,
                Cost: cost.Amount,
                Currency: cost.Currency,
                CostIsPartial: cost.Partial,
                OnHours: rows.Sum(r => r.OnSeconds) / 3600.0,
                PeakW: rows.Count > 0 ? rows.Max(r => r.MaxW) : 0,
                IdleOnKwh: rows.Sum(r => r.IdleOnWh) / 1000,
                IdleOffKwh: rows.Sum(r => r.IdleOffWh) / 1000,
                AsleepKwh: asleepWh / 1000);
        })];
    }

    private static DateOnly LocalDay(DateTimeOffset at, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);

    /// <summary>A day's local midnight, or the first valid time after it where a clock change skips midnight.</summary>
    private static DateTimeOffset Midnight(DateOnly day, TimeZoneInfo zone)
    {
        var wall = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(wall)) wall = wall.AddMinutes(15);
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }

    private static DateTimeOffset Floor(DateTimeOffset t, TimeSpan unit) => t.AddTicks(-(t.Ticks % unit.Ticks));

    private static DateTimeOffset Ceiling(DateTimeOffset t, TimeSpan unit)
    {
        var remainder = t.Ticks % unit.Ticks;
        return remainder == 0 ? t : t.AddTicks(unit.Ticks - remainder);
    }

    private static double Share(double part, double whole) => whole > 0 ? part / whole : 0;

    /// <summary>Where a series' buckets begin: every <c>bucket</c> from the range's start, or, given a zone, at each of its
    /// local midnights, the first bucket starting at the range's own start.</summary>
    private sealed class Cut(DateTimeOffset from, TimeSpan bucket, TimeZoneInfo? zone)
    {
        private readonly DateOnly _firstDay = zone is null ? default : LocalDay(from, zone);

        /// <summary>How many buckets a range ending at <paramref name="to"/> holds; one at least.</summary>
        public int Count(DateTimeOffset to) => zone is null
            ? Math.Max(1, (int)Math.Ceiling((to - from) / bucket))
            : to > from ? Math.Max(1, Index(to - TimeSpan.FromTicks(1)) + 1) : 1;

        /// <summary>The bucket <paramref name="at"/> falls in, counted from the first; outside the range, past either end.</summary>
        public int Index(DateTimeOffset at) => zone is null
            ? (int)Math.Floor((at - from) / bucket)
            : LocalDay(at, zone).DayNumber - _firstDay.DayNumber;

        public DateTimeOffset Start(int index) => zone is null || index <= 0 ? from + index * bucket : Midnight(_firstDay.AddDays(index), zone);
    }

    /// <summary>The rows a range reads, hour rows and minute rows apart so a series knows each row's length.</summary>
    private sealed record Window(DateTimeOffset From, DateTimeOffset To, List<Aggregate> Hours, List<Aggregate> Minutes)
    {
        public List<Aggregate> Rows { get; } = [.. Hours.Concat(Minutes).OrderBy(r => r.Start)];

        /// <summary>Every row with its length.</summary>
        public IEnumerable<(Aggregate Row, TimeSpan Length)> Sized
            => Hours.Select(h => (h, Hour)).Concat(Minutes.Select(m => (m, Minute)));
    }
}
