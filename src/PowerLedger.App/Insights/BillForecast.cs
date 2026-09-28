namespace PowerLedger.App;

/// <summary>
/// The Bill forecast's maths (Aero look design §4). Each day left in the month is forecast as the median energy of the same
/// weekday over the last 8 weeks, which a single odd day hardly moves; a weekday not seen in that time takes the month's
/// daily mean so far. The cost is at the month's average price, as <see cref="MonthOutlook"/> prices its projection, so a
/// tariff change within the month is followed.
/// <para>The band is honest about how good that rule has been on this PC: every day of the last 8 weeks is forecast from
/// the days before it alone, the misses are kept, and 1000 months of misses are drawn from them (with a fixed seed, so the
/// same history always gives the same band) and summed over the days left. The 10th and 90th percentiles of those sums
/// bound the band, so it is wide for a PC whose use swings and narrow for a steady one, and holds the month's real cost
/// about four times in five (design §7).</para>
/// </summary>
internal static class BillForecasts
{
    /// <summary>How far back the weekday medians and the backtest look.</summary>
    public const int Weeks = 8;

    public const int Draws = 1000;

    /// <summary>Any fixed number: what matters is that the band never flickers between two reads of the same history.</summary>
    public const int Seed = 7466;

    public const double LowQuantile = 0.1;

    public const double HighQuantile = 0.9;

    /// <param name="days">Whole days of history, any order; days before the last 8 weeks and from today on are ignored.</param>
    /// <param name="today">The local day under way.</param>
    /// <param name="todayLeft">The share of today still to come, 0 to 1.</param>
    /// <param name="monthKwh">What the month has used so far, today's part included.</param>
    /// <param name="price">The month's average price per kWh; 0 with no tariff.</param>
    /// <param name="currency">The tariff's currency; null with no tariff.</param>
    public static BillForecast From(IReadOnlyList<DayUse> days, DateOnly today, double todayLeft, double monthKwh, decimal price, string? currency)
    {
        var since = today.AddDays(-7 * Weeks);
        var recent = days.Where(d => d.Day >= since && d.Day < today).OrderBy(d => d.Day).ToList();
        if (recent.Count < BillForecast.DaysNeeded) return BillForecast.NotReady(recent.Count, currency);

        var end = new DateOnly(today.Year, today.Month, 1).AddMonths(1);
        var weights = new List<(DateOnly Day, double Weight)> { (today, Math.Clamp(todayLeft, 0, 1)) };
        for (var day = today.AddDays(1); day < end; day = day.AddDays(1)) weights.Add((day, 1));

        var projected = monthKwh + weights.Sum(w => w.Weight * Predict(recent, w.Day));
        var shared = weights.Select(w => Shared(recent.Count(d => d.Day.DayOfWeek == w.Day.DayOfWeek))).ToList();
        var (under, over) = Band(Misses(recent), weights.Select(w => w.Weight).ToList(), weights.Select(w => (int)w.Day.DayOfWeek).ToList(), shared);
        var low = Math.Min(projected, Math.Max(monthKwh, projected + under));
        var high = Math.Max(projected, projected + over);
        return new BillForecast(Cost(projected, price), Cost(low, price), Cost(high, price), currency, recent.Count, true, Kwh(projected), Kwh(low), Kwh(high));
    }

    /// <summary>A day's energy from the days known before it: the median of its weekday; else the mean of its month's
    /// days; else the mean of them all.</summary>
    internal static double Predict(IReadOnlyList<DayUse> known, DateOnly day)
    {
        var same = known.Where(d => d.Day.DayOfWeek == day.DayOfWeek).Select(d => d.Kwh).ToList();
        if (same.Count > 0) return Stats.Median(same);
        var month = known.Where(d => d.Day.Year == day.Year && d.Day.Month == day.Month).Select(d => d.Kwh).ToList();
        if (month.Count > 0) return month.Average();
        return known.Count > 0 ? known.Average(d => d.Kwh) : 0;
    }

    /// <summary>
    /// Each day's actual energy less what the rule would have said from the days before it. Only days whose weekday had
    /// been seen before are backtested, when there are any: the forecast's own days all have theirs (a week of history
    /// holds every weekday), and the first week's fallback guesses would make the band wider than the forecast deserves.
    /// </summary>
    internal static List<double> Misses(IReadOnlyList<DayUse> recent)
    {
        var withWeekday = new List<double>(recent.Count);
        var all = new List<double>(recent.Count);
        for (var i = 1; i < recent.Count; i++)
        {
            var known = recent.Take(i).ToList();
            var miss = recent[i].Kwh - Predict(known, recent[i].Day);
            all.Add(miss);
            if (known.Any(d => d.Day.DayOfWeek == recent[i].Day.DayOfWeek)) withWeekday.Add(miss);
        }
        return withWeekday.Count > 0 ? withWeekday : all;
    }

    /// <summary>
    /// How much of a day's miss its weekday's other days left in the month share. A forecast day misses by its own noise
    /// and by how far its weekday's median, from <paramref name="samples"/> days, is off; that second part is the same
    /// for every Monday left. A median of n draws is off by about π / 2n of the noise's variance, so the shared share is
    /// (π / 2n) / (1 + π / 2n). Summing the misses as if each day were independent would leave the band too narrow.
    /// </summary>
    internal static double Shared(int samples) => samples <= 0 ? 1 : MedianNoise / samples / (1 + MedianNoise / samples);

    private const double MedianNoise = Math.PI / 2;

    /// <summary>
    /// The 10th and 90th percentiles of the misses, centred, summed over the days left, by bootstrap: each draw picks one miss per
    /// weekday for what its days share (<paramref name="shared"/>) and one per day for the rest, each weighted by
    /// <paramref name="weights"/> (a part of today counts for its part).
    /// </summary>
    internal static (double Under, double Over) Band(
        IReadOnlyList<double> misses, IReadOnlyList<double> weights, IReadOnlyList<int> weekdays, IReadOnlyList<double> shared)
    {
        if (misses.Count == 0) return (0, 0);
        // Centred on their median: the misses' centre over a few dozen days is mostly chance, and summed over the days
        // left it would shift the whole band off the forecast (in the backtests, 72 % coverage where the design asks about
        // 80 %); the median, not the mean, so one spike day widens the band upwards without dragging it down.
        var centre = Stats.Median(misses);
        misses = [.. misses.Select(m => m - centre)];
        var random = new Random(Seed);
        var sums = new double[Draws];
        var common = new double[7];
        for (var draw = 0; draw < Draws; draw++)
        {
            for (var day = 0; day < common.Length; day++) common[day] = misses[random.Next(misses.Count)];
            double sum = 0;
            for (var i = 0; i < weights.Count; i++)
                sum += weights[i] * (Math.Sqrt(shared[i]) * common[weekdays[i]] + Math.Sqrt(1 - shared[i]) * misses[random.Next(misses.Count)]);
            sums[draw] = sum;
        }
        Array.Sort(sums);
        return (Stats.Quantile(sums, LowQuantile), Stats.Quantile(sums, HighQuantile));
    }

    private static decimal Cost(double kwh, decimal price) => decimal.Round((decimal)Math.Max(0, kwh) * price, 2);

    /// <summary>To the watt-hour: a sum of hour rows carries float noise the page would never show.</summary>
    private static double Kwh(double kwh) => Math.Round(Math.Max(0, kwh), 3);
}
