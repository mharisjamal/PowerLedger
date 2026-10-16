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
/// energy. <see cref="Key"/> is the app's key in <c>apps</c> (Schema.V6), one of <see cref="AppQueries"/>' special keys
/// among them; <see cref="Name"/> and <see cref="Path"/> are the newest the service wrote.</summary>
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
/// reads it. Each person sees their own apps and the machine's by name; everyone else's, whenever they ran them, are
/// pooled under Other users (review item 10). Nothing here is ever written, shared or synced.
/// </summary>
/// <param name="viewer">Whose apps are shown by name (<see cref="AppUsers"/>); the account the App runs as when left out.</param>
public sealed class AppQueries(SqliteDatabase db, int? viewer = null)
{
    private readonly int _viewer = viewer ?? CurrentUser();

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

    /// <summary>Test seam: called between reading the app rows and the hour rows, where the service may write a minute.</summary>
    internal Action? AfterAppRows { get; init; }

    /// <summary>The apps and the hour rows' lines for <paramref name="from"/> to <paramref name="to"/>, most energy
    /// first. Everything is read on one connection in one read transaction, so of one moment of the database: a minute the
    /// service writes meanwhile is never counted as energy no app was given (review item 11). A database the service has not
    /// upgraded to version 6 yet reads as one with no apps.</summary>
    public AppsRange Read(DateTimeOffset from, DateTimeOffset to)
    {
        var hourFrom = Ceiling(from, Hour);
        var hourTo = Ceiling(to, Hour);
        using var c = db.Open();
        using var snapshot = c.BeginTransaction(deferred: true);
        var tariffs = TariffRepository.All(c);
        var schedule = new TariffSchedule(tariffs);
        var counted = HasTable(c) ? First(c) : null;
        if (counted is not { } first) return AppsRange.None(hourFrom, hourTo, null, schedule.Currency);
        var start = first > hourFrom ? first : hourFrom;
        // In a zone off the hour, a day's first minutes fall in an hour that began the day before: until the day's first
        // whole hour has rows, that hour under way stands in, so the day is never empty then (review item 12).
        if (hourFrom > from && Newest(c) is { } newest && newest == Floor(from, Hour)) start = newest;
        if (start >= hourTo) return AppsRange.None(hourFrom, hourTo, first, schedule.Currency);
        // Summed in the database, not row by row here: a year holds some 260,000 app rows. The hours are cut where a tariff
        // takes effect, so each span is priced at one tariff.
        var cuts = tariffs.Select(t => t.EffectiveFrom).Where(at => at > start && at < hourTo).Distinct().Order().ToList();
        var (totals, spans) = Totals(c, start, hourTo, cuts);
        var traced = Traced(c, start, hourTo);
        AfterAppRows?.Invoke();

        var partial = false;
        PricedWh Priced(IEnumerable<(DateTimeOffset Start, double Wh)> slices)
        {
            var list = slices.Where(s => double.IsFinite(s.Wh)).ToList();
            var cost = schedule.Cost(list);
            partial |= cost.Partial;
            return new PricedWh(list.Sum(s => s.Wh), cost.Amount);
        }

        // Each app's energy priced span by span, a span being the hours between two tariffs, so every hour is priced at
        // the tariff in force at its start, as the report prices a row.
        var apps = totals.Select(t => new AppUse(
                t.Key, t.Name, t.Path, t.Cpu, t.Gpu, t.Run, t.Front, Priced(spans.Select(s => (s.Start, s.Wh.GetValueOrDefault(t.Key)))).Cost))
            .OrderByDescending(a => a.Wh).ThenBy(a => a.Key, StringComparer.Ordinal).ToList();

        // The hour rows of the same hours, and the hour under way from its minutes.
        var folded = AggregateRepository.ReadHours(c, start, hourTo);
        var tailFrom = folded.Count > 0 ? folded[^1].Start + Hour : Max(start, hourTo - ReportQueries.MinuteResolutionLimit);
        var minutes = tailFrom < hourTo ? AggregateRepository.ReadMinutes(c, tailFrom, hourTo) : [];
        var sized = folded.Select(r => (Row: r, Length: Hour)).Concat(minutes.Select(r => (Row: r, Length: TimeSpan.FromMinutes(1)))).ToList();

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

    private static DateTimeOffset? Newest(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT MAX(start_ms) FROM app_hours";
        return cmd.ExecuteScalar() is long ms ? Rows.Time(ms) : null;
    }

    /// <summary>
    /// Each app's sums over the hours, with its name and path from apps, and its energy in each span between two
    /// <paramref name="cuts"/>, in one pass over the rows: grouped by the app and the span (the cuts a row's hour is past),
    /// every row of anyone but the viewer and the machine as one app, Other users, whose running time is the longest any of
    /// theirs ran each hour.
    /// </summary>
    private (List<AppTotal> Totals, List<(DateTimeOffset Start, Dictionary<string, double> Wh)> Spans) Totals(
        SqliteConnection c, DateTimeOffset from, DateTimeOffset to, List<DateTimeOffset> cuts)
    {
        const long OtherUsers = -1;
        var span = cuts.Count == 0 ? "0" : string.Join(" + ", cuts.Select((_, i) => $"(h.start_ms >= $cut{i})"));
        using var cmd = Command(c, from, to,
            $"SELECT CASE WHEN h.user_id IN (0, $viewer) THEN h.app_id ELSE {OtherUsers} END AS app, {span} AS span, " +
            "TOTAL(h.cpu_wh), TOTAL(h.gpu_wh), TOTAL(h.run_s), TOTAL(h.front_s) " +
            "FROM app_hours h WHERE h.start_ms >= $from AND h.start_ms < $to GROUP BY app, span");
        Rows.Add(cmd, "$viewer", _viewer);
        for (var i = 0; i < cuts.Count; i++) Rows.Add(cmd, $"$cut{i}", Rows.Ms(cuts[i]));
        var spans = cuts.Prepend(from).Select(at => (Start: at, Wh: new Dictionary<string, double>(StringComparer.Ordinal))).ToList();
        var apps = Apps(c);
        var named = new Dictionary<string, (string Name, string? Path)>(StringComparer.Ordinal);
        var sums = new Dictionary<string, (double Cpu, double Gpu, double Run, double Front)>(StringComparer.Ordinal);
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var id = r.GetInt64(0);
                var (key, name, path) = id == OtherUsers ? (OtherUsersKey, "Other users", (string?)null) : apps.GetValueOrDefault(id, (OtherKey, "Other apps", null));
                var (cpu, gpu) = (Finite(r.GetDouble(2)), Finite(r.GetDouble(3)));
                named[key] = (name, path);
                var sum = sums.GetValueOrDefault(key);
                sums[key] = (sum.Cpu + cpu, sum.Gpu + gpu, sum.Run + Finite(r.GetDouble(4)), sum.Front + Finite(r.GetDouble(5)));
                var wh = spans[(int)r.GetInt64(1)].Wh;
                wh[key] = wh.GetValueOrDefault(key) + cpu + gpu;
            }
        }
        if (sums.TryGetValue(OtherUsersKey, out var others)) sums[OtherUsersKey] = others with { Run = OthersRan(c, from, to) };
        var totals = sums.Select(s => new AppTotal(s.Key, named[s.Key].Name, named[s.Key].Path, s.Value.Cpu, s.Value.Gpu, s.Value.Run, s.Value.Front)).ToList();
        return (totals, spans);
    }

    /// <summary>Every app's key, name and path by its number.</summary>
    private static Dictionary<long, (string Key, string Name, string? Path)> Apps(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, key, name, path FROM apps";
        using var r = cmd.ExecuteReader();
        var apps = new Dictionary<long, (string Key, string Name, string? Path)>();
        while (r.Read()) apps[r.GetInt64(0)] = (r.GetString(1), r.GetString(2), Rows.NullableString(r, 3));
        return apps;
    }

    /// <summary>How long other people's apps ran: each hour the longest any of theirs did, added up.</summary>
    private double OthersRan(SqliteConnection c, DateTimeOffset from, DateTimeOffset to)
    {
        using var cmd = Command(c, from, to,
            "SELECT TOTAL(ran) FROM (SELECT MAX(run_s) AS ran FROM app_hours WHERE start_ms >= $from AND start_ms < $to AND user_id NOT IN (0, $viewer) GROUP BY start_ms)");
        Rows.Add(cmd, "$viewer", _viewer);
        return Finite(Convert.ToDouble(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The account the App runs as; the machine's where there is none to ask.</summary>
    private static int CurrentUser()
    {
        if (!OperatingSystem.IsWindows()) return AppUsers.Machine;
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return AppUsers.Of(identity.User?.Value);
    }

    /// <summary>The processor and graphics energy the hour's apps were given, hour by hour.</summary>
    private static Dictionary<DateTimeOffset, double> Traced(SqliteConnection c, DateTimeOffset from, DateTimeOffset to)
    {
        using var cmd = Command(c, from, to, "SELECT start_ms, TOTAL(cpu_wh + gpu_wh) FROM app_hours WHERE start_ms >= $from AND start_ms < $to GROUP BY start_ms");
        using var r = cmd.ExecuteReader();
        var traced = new Dictionary<DateTimeOffset, double>();
        while (r.Read()) traced[Rows.Time(r.GetInt64(0))] = Finite(r.GetDouble(1));
        return traced;
    }

    private static SqliteCommand Command(SqliteConnection c, DateTimeOffset from, DateTimeOffset to, string sql)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        Rows.Add(cmd, "$from", Rows.Ms(from));
        Rows.Add(cmd, "$to", Rows.Ms(to));
        return cmd;
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

    /// <summary>One app's sums over the hours read.</summary>
    private sealed record AppTotal(string Key, string Name, string? Path, double Cpu, double Gpu, double Run, double Front);
}
