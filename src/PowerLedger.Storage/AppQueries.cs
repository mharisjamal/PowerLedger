using Microsoft.Data.Sqlite;
using PowerLedger.Core;

namespace PowerLedger.Storage;

/// <summary>Energy in Wh and what it cost, each hour priced at the tariff then in force, as the report prices it.</summary>
public sealed record PricedWh(double Wh, decimal Cost)
{
    public static readonly PricedWh None = new(0, 0m);
}

/// <summary>One app over a range (Plan AE, the Apps page): its share of the processor's and the graphics' energy at the
/// pack, on the bands' own scale; how long it had a process running and held the window in front; and the cost of its
/// energy. <see cref="Key"/> is the app's key in <c>app_hours</c> (Schema.V6), one of <see cref="AppQueries"/>' special
/// keys among them; <see cref="Name"/> and <see cref="Path"/> are its newest hour's.</summary>
public sealed record AppUse(string Key, string Name, string? Path, double CpuWh, double GpuWh, double RunSeconds, double FrontSeconds, decimal Cost)
{
    /// <summary>Its processor and graphics energy together.</summary>
    public double Wh => CpuWh + GpuWh;
}

/// <summary>
/// What the Apps page shows for a range, read from the hours PowerLedger counted apps in: every app with its energy and
/// cost, and the hour rows' own lines for the same hours: the screen (display_wh), the rest of the PC (rest_wh, which
/// measured mode can take below zero, left for the page to clean as the bands do), what the PC drew asleep, laid back from
/// each wake as the report lays it, what charging lost for the energy the battery gave, and the processor and graphics
/// energy of those hours no app was given (an hour PowerLedger began counting apps part way through). Apps and the lines
/// add up to the PC's total for those hours, on the bands' scale.
/// </summary>
/// <param name="From">The first hour read: the first that starts in the range, or the hour apps were first counted in
/// when that is later.</param>
/// <param name="To">The end of the last hour read.</param>
/// <param name="CountedSince">The first hour PowerLedger counted apps in, in any range; null before it ever has.</param>
public sealed record AppsRange(
    DateTimeOffset From, DateTimeOffset To, DateTimeOffset? CountedSince, IReadOnlyList<AppUse> Apps,
    PricedWh Screen, PricedWh Rest, PricedWh Asleep, PricedWh Losses, PricedWh Untraced,
    string? Currency, bool CostIsPartial)
{
    /// <summary>A range with no apps in it.</summary>
    public static AppsRange None(DateTimeOffset from, DateTimeOffset to, DateTimeOffset? countedSince, string? currency)
        => new(from, to, countedSince, [], PricedWh.None, PricedWh.None, PricedWh.None, PricedWh.None, PricedWh.None, currency, false);
}

/// <summary>
/// The read side of <c>app_hours</c> (Plan AE; Schema.V6), for the App, which opens the database read-only. Rows are by
/// the hour, so a range is read in whole hours, each hour in the range its start falls in: in a zone half an hour off UTC
/// the hour from 23:30 to 00:30 is the day before's, as a long report's hour rows are cut and its days grouped, and no
/// hour is ever in two days. The hour under way, which has no hour row yet, is read from its minute rows, as the report
/// reads it. Nothing here is ever written, shared or synced.
/// </summary>
public sealed class AppQueries(SqliteDatabase db)
{
    /// <summary>Windows' own processes, the kernel's among them.</summary>
    public const string WindowsKey = "::windows";

    /// <summary>The processor and graphics at rest, split by no app.</summary>
    public const string IdleKey = "::idle";

    /// <summary>Programs that started and ended between two readings.</summary>
    public const string ClosedKey = "::closed";

    /// <summary>Another signed in user's apps.</summary>
    public const string OtherUsersKey = "::otherusers";

    /// <summary>Apps past an hour's busiest 25.</summary>
    public const string OtherKey = "::other";

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <summary>The apps and the hour rows' lines for <paramref name="from"/> to <paramref name="to"/>, most energy
    /// first. A database the service has not upgraded to version 6 yet reads as one with no apps.</summary>
    public AppsRange Read(DateTimeOffset from, DateTimeOffset to)
    {
        var hourFrom = Ceiling(from, Hour);
        var hourTo = Ceiling(to, Hour);
        var schedule = new TariffRepository(db).Schedule();
        List<AppHour> hours;
        DateTimeOffset start;
        DateTimeOffset? counted;
        using (var c = db.Open())
        {
            counted = HasTable(c) ? First(c) : null;
            if (counted is not { } first) return AppsRange.None(hourFrom, hourTo, null, schedule.Currency);
            start = first > hourFrom ? first : hourFrom;
            if (start >= hourTo) return AppsRange.None(hourFrom, hourTo, first, schedule.Currency);
            hours = AppRows(c, start, hourTo);
        }

        var partial = false;
        PricedWh Priced(IEnumerable<(DateTimeOffset Start, double Wh)> slices)
        {
            var list = slices.Where(s => double.IsFinite(s.Wh)).ToList();
            var cost = schedule.Cost(list);
            partial |= cost.Partial;
            return new PricedWh(list.Sum(s => s.Wh), cost.Amount);
        }

        var apps = hours.GroupBy(h => h.Key, StringComparer.Ordinal).Select(g =>
        {
            var newest = g.MaxBy(h => h.Start)!;
            return new AppUse(
                g.Key, newest.Name, newest.Path, g.Sum(h => h.Cpu), g.Sum(h => h.Gpu), g.Sum(h => h.Run), g.Sum(h => h.Front),
                Priced(g.Select(h => (h.Start, h.Cpu + h.Gpu))).Cost);
        }).OrderByDescending(a => a.Wh).ThenBy(a => a.Key, StringComparer.Ordinal).ToList();

        // The hour rows of the same hours, and the hour under way from its minutes.
        var repo = new AggregateRepository(db);
        var folded = repo.ReadHours(start, hourTo);
        var tailFrom = folded.Count > 0 ? folded[^1].Start + Hour : Max(start, hourTo - ReportQueries.MinuteResolutionLimit);
        var minutes = tailFrom < hourTo ? repo.ReadMinutes(tailFrom, hourTo) : [];
        var sized = folded.Select(r => (Row: r, Length: Hour)).Concat(minutes.Select(r => (Row: r, Length: TimeSpan.FromMinutes(1)))).ToList();

        var traced = hours.GroupBy(h => h.Start).ToDictionary(g => g.Key, g => g.Sum(h => h.Cpu + h.Gpu));
        var untraced = sized.GroupBy(s => Floor(s.Row.Start, Hour))
            .Select(g => (g.Key, Math.Max(0, g.Sum(s => s.Row.CpuWh + s.Row.GpuWh) - traced.GetValueOrDefault(g.Key))));

        return new AppsRange(
            start, hourTo, counted, apps,
            Priced(sized.Select(s => (s.Row.Start, s.Row.DisplayWh))),
            Priced(sized.Select(s => (s.Row.Start, s.Row.RestWh))),
            Priced(Asleep(sized, start, hourTo)),
            Priced(sized.Select(s => (s.Row.Start, s.Row.BatteryWh * (BatteryRoundTrip.Factor - 1)))),
            Priced(untraced),
            schedule.Currency, partial);
    }

    /// <summary>What the PC drew asleep, as the report counts it (accuracy A10): each sleep laid back from the wake and only
    /// its part inside the hours read, priced as the row the sleep is stored in.</summary>
    private static IEnumerable<(DateTimeOffset Start, double Wh)> Asleep(List<(Aggregate Row, TimeSpan Length)> sized, DateTimeOffset from, DateTimeOffset to)
    {
        foreach (var (row, length) in sized)
        {
            if (row.GapSeconds <= 0) continue;
            var woke = row.Start + length - TimeSpan.FromSeconds(Math.Min(row.OnSeconds, length.TotalSeconds));
            var began = Max(woke - TimeSpan.FromSeconds(row.GapSeconds), from);
            var ended = woke < to ? woke : to;
            if (ended > began) yield return (row.Start, row.AsleepWh * (ended - began).TotalSeconds / row.GapSeconds);
        }
    }

    private static bool HasTable(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'app_hours'";
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static DateTimeOffset? First(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT MIN(start_ms) FROM app_hours";
        return cmd.ExecuteScalar() is long ms ? Rows.Time(ms) : null;
    }

    private static List<AppHour> AppRows(SqliteConnection c, DateTimeOffset from, DateTimeOffset to)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT start_ms, app_key, name, path, cpu_wh, gpu_wh, run_s, front_s FROM app_hours WHERE start_ms >= $from AND start_ms < $to";
        Rows.Add(cmd, "$from", Rows.Ms(from));
        Rows.Add(cmd, "$to", Rows.Ms(to));
        using var r = cmd.ExecuteReader();
        var list = new List<AppHour>();
        while (r.Read())
        {
            list.Add(new AppHour(
                Rows.Time(r.GetInt64(0)), r.GetString(1), r.GetString(2), Rows.NullableString(r, 3),
                Finite(r.GetDouble(4)), Finite(r.GetDouble(5)), Finite(r.GetDouble(6)), Finite(r.GetDouble(7))));
        }
        return list;
    }

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    /// <summary>The UTC hour (or other unit) <paramref name="t"/> is in, whatever offset it carries: rows are cut by UTC.</summary>
    private static DateTimeOffset Floor(DateTimeOffset t, TimeSpan unit) => t.AddTicks(-(t.UtcTicks % unit.Ticks));

    private static DateTimeOffset Ceiling(DateTimeOffset t, TimeSpan unit)
    {
        var remainder = t.UtcTicks % unit.Ticks;
        return remainder == 0 ? t : t.AddTicks(unit.Ticks - remainder);
    }

    /// <summary>One row of <c>app_hours</c>.</summary>
    private sealed record AppHour(DateTimeOffset Start, string Key, string Name, string? Path, double Cpu, double Gpu, double Run, double Front);
}
