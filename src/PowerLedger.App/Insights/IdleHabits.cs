namespace PowerLedger.App;

/// <summary>
/// Habits' maths (Aero look design §4): when the PC sits awake and idle, from the last 4 weeks of hour rows.
/// <list type="bullet">
/// <item>The heatmap holds each weekday and hour's idle energy (display on or off: either way the PC is awake for nobody),
/// averaged over the dates it was seen, so a PC with three weeks of history is not shown a quarter lighter.</item>
/// <item>The worst window is the 2 hours in a row, across all weekdays, that idle costs most; it may run past midnight.</item>
/// <item>The saving is what sleeping after 10 idle minutes would have cut, scaled to an average month. Hour rows know
/// how long the PC idled, not in how many spells, so an hour's idle time is taken as one spell, which the first 10
/// minutes of are kept awake; unless the hour before idled for at least 50 minutes, when the spell began earlier and all
/// of it would have been asleep.</item>
/// </list>
/// Nothing is said until there is a week of whole days (<see cref="BillForecast.DaysNeeded"/>).
/// </summary>
internal static class HabitsFinder
{
    public const int Weeks = 4;

    public const int WindowHours = 2;

    /// <summary>How long the PC stays awake idle before the sleep the saving supposes.</summary>
    public static readonly TimeSpan SleepAfter = TimeSpan.FromMinutes(10);

    /// <summary>An hour idle at least this long is taken to end its spell still idle.</summary>
    public static readonly TimeSpan CarriesOver = TimeSpan.FromMinutes(50);

    /// <summary>The average month, 365.25 / 12 days.</summary>
    public const double DaysPerMonth = 365.25 / 12;

    public static IdleHabits? From(IReadOnlyList<HourUse> hours, DateOnly today, decimal price, string? currency)
    {
        var since = today.AddDays(-7 * Weeks);
        var whole = HourUse.WholeDays(hours, today).Where(d => d.Day >= since).Select(d => d.Day).ToHashSet();
        if (whole.Count < BillForecast.DaysNeeded) return null;

        var sums = new double[IdleHabits.Days, IdleHabits.Hours];
        var dates = new HashSet<DateOnly>[IdleHabits.Days, IdleHabits.Hours];
        double savedKwh = 0;
        for (var i = 0; i < hours.Count; i++)
        {
            var hour = hours[i];
            if (!hour.Seen || !whole.Contains(DateOnly.FromDateTime(hour.Local))) continue;
            var (day, of) = ((int)hour.Local.DayOfWeek, hour.Local.Hour);
            sums[day, of] += hour.IdleKwh * 1000;
            (dates[day, of] ??= []).Add(DateOnly.FromDateTime(hour.Local));
            savedKwh += Saved(hour, i > 0 ? hours[i - 1] : null);
        }

        var heatmap = new double[IdleHabits.Days, IdleHabits.Hours];
        var byHour = new double[IdleHabits.Hours];
        for (var day = 0; day < IdleHabits.Days; day++)
        {
            for (var of = 0; of < IdleHabits.Hours; of++)
            {
                heatmap[day, of] = dates[day, of] is { Count: > 0 } seen ? sums[day, of] / seen.Count : 0;
                byHour[of] += heatmap[day, of];
            }
        }

        var worst = 0;
        for (var start = 1; start < IdleHabits.Hours; start++)
            if (Window(byHour, start) > Window(byHour, worst)) worst = start;

        var monthKwh = savedKwh / whole.Count * DaysPerMonth;
        return new IdleHabits(heatmap, worst, WindowHours, decimal.Round((decimal)monthKwh * price, 2), monthKwh, currency);
    }

    /// <summary>What sleeping after <see cref="SleepAfter"/> would have cut from <paramref name="hour"/>'s idle energy.</summary>
    internal static double Saved(HourUse hour, HourUse? before)
    {
        if (hour.IdleKwh <= 0 || hour.IdleSeconds <= 0) return 0;
        if (before is { Seen: true } b && b.IdleSeconds >= CarriesOver.TotalSeconds) return hour.IdleKwh;
        return hour.IdleKwh * Math.Max(0, hour.IdleSeconds - SleepAfter.TotalSeconds) / hour.IdleSeconds;
    }

    private static double Window(double[] byHour, int start)
    {
        double sum = 0;
        for (var i = 0; i < WindowHours; i++) sum += byHour[(start + i) % byHour.Length];
        return sum;
    }
}
