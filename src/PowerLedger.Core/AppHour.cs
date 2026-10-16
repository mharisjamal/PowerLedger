namespace PowerLedger.Core;

/// <summary>One app's figures in one hour as the service gathers them; see <see cref="AppHourRow"/>.</summary>
public sealed class AppTotals(string key, string? path, string? name = null, int user = AppUsers.Machine)
{
    private long _runTick = long.MinValue;
    private long _frontTick = long.MinValue;

    public string Key { get; } = key;

    /// <summary>Who ran it (<see cref="AppUsers"/>).</summary>
    public int User { get; } = user;

    /// <summary>The image of the first process seen, in its own case, for the App's icon; null for the special keys.</summary>
    public string? Path { get; internal set; } = path;

    /// <summary>The name the row was stored with, when the hour was read back; null until a row is written.</summary>
    public string? Name { get; set; } = name;

    public double CpuWh { get; set; }

    public double GpuWh { get; set; }

    public double RunSeconds { get; private set; }

    public double FrontSeconds { get; private set; }

    /// <summary>The app had a process running through tick <paramref name="tick"/>; a tick counts once, however many it had.</summary>
    public void Run(long tick, double seconds)
    {
        if (tick == _runTick) return;
        _runTick = tick;
        RunSeconds += seconds;
    }

    /// <summary>The app held the window in front through tick <paramref name="tick"/>, in some session; a tick counts once.</summary>
    public void Front(long tick, double seconds)
    {
        if (tick == _frontTick) return;
        _frontTick = tick;
        FrontSeconds += seconds;
    }

    internal void Restore(AppHourRow row)
    {
        CpuWh = row.CpuWh;
        GpuWh = row.GpuWh;
        RunSeconds = row.RunSeconds;
        FrontSeconds = row.FrontSeconds;
    }
}

/// <summary>
/// The apps' energy in one hour, as the service gathers it tick by tick (Plan AE): each person's apps apart (review item
/// 10), at most <see cref="MostApps"/> apps in all, any more together under each person's <see cref="AppKeys.Other"/>; written
/// as each person's busiest <see cref="Busiest"/>, Other apps for the rest, and the special keys, which are the machine's.
/// Nothing is lost on the way: every figure an app is given is in some row. An hour stored by an earlier run carries on from
/// its rows. Single-threaded.
/// </summary>
public sealed class AppHour
{
    /// <summary>The most apps an hour keeps apart in memory.</summary>
    public const int MostApps = 500;

    /// <summary>The most apps an hour is written with for each person, besides the special keys.</summary>
    public const int Busiest = 25;

    private readonly Dictionary<(int User, string Key), AppTotals> _apps = [];
    private int _ordinary;

    public AppHour(DateTimeOffset start, IEnumerable<AppHourRow>? stored = null)
    {
        Start = start;
        foreach (var row in stored ?? [])
        {
            var app = Add(Owner(row.User, row.Key), row.Key, row.Path, row.Name);
            app.Restore(row);
        }
    }

    public DateTimeOffset Start { get; }

    /// <summary>Set by whoever adds to the hour, cleared once it is written.</summary>
    public bool Changed { get; set; }

    /// <summary>The machine's totals for <paramref name="key"/>; see <see cref="For(int, string, string?)"/>.</summary>
    public AppTotals For(string key, string? path) => For(AppUsers.Machine, key, path);

    /// <summary>The totals <paramref name="user"/>'s <paramref name="key"/> goes into: its own, or that person's Other apps
    /// once the hour holds <see cref="MostApps"/> apps. The special keys but Other apps are always the machine's own.</summary>
    public AppTotals For(int user, string key, string? path)
    {
        user = Owner(user, key);
        if (_apps.TryGetValue((user, key), out var app)) return app;
        if (!AppKeys.IsSpecial(key) && _ordinary >= MostApps) return For(user, AppKeys.Other, null);
        return Add(user, key, AppKeys.IsSpecial(key) ? null : path, null);
    }

    /// <summary>The rows to write: for each person, the busiest <see cref="Busiest"/> apps by energy, then by time in front,
    /// and Other apps holding its own figures and every app past the busiest, their energy and time in front added up and
    /// its running time the longest of theirs; then each special key. Rows with nothing in them are left out.</summary>
    /// <param name="name">The name of an app whose row has none yet.</param>
    public List<AppHourRow> Rows(Func<AppTotals, string> name)
    {
        List<AppHourRow> rows = [];
        var users = _apps.Keys.Select(id => id.User).Distinct().Order();
        foreach (var user in users)
        {
            var ranked = _apps.Values
                .Where(app => app.User == user && !AppKeys.IsSpecial(app.Key) && (app.CpuWh + app.GpuWh > 0 || app.FrontSeconds > 0))
                .OrderByDescending(app => app.CpuWh + app.GpuWh)
                .ThenByDescending(app => app.FrontSeconds)
                .ThenByDescending(app => app.RunSeconds)
                .ThenBy(app => app.Key, StringComparer.Ordinal)
                .ToList();
            rows.AddRange(ranked.Take(Busiest).Select(app => Row(app, app.Name ??= name(app))));
            double cpu = 0, gpu = 0, run = 0, front = 0;
            if (_apps.TryGetValue((user, AppKeys.Other), out var other)) (cpu, gpu, run, front) = (other.CpuWh, other.GpuWh, other.RunSeconds, other.FrontSeconds);
            foreach (var app in ranked.Skip(Busiest))
            {
                cpu += app.CpuWh;
                gpu += app.GpuWh;
                run = Math.Max(run, app.RunSeconds);
                front += app.FrontSeconds;
            }
            if (cpu + gpu + run + front > 0) rows.Add(new AppHourRow(AppKeys.Other, AppKeys.SpecialName(AppKeys.Other)!, null, cpu, gpu, run, front, user));
        }

        foreach (var app in _apps.Values)
        {
            if (app.Key == AppKeys.Other || !AppKeys.IsSpecial(app.Key)) continue;
            if (app.CpuWh + app.GpuWh + app.RunSeconds + app.FrontSeconds > 0) rows.Add(Row(app, AppKeys.SpecialName(app.Key) ?? app.Name ?? app.Key));
        }
        return rows;
    }

    /// <summary>Who an app's figures belong to: the special keys but Other apps are the machine's, whoever ran them.</summary>
    private static int Owner(int user, string key) => AppKeys.IsSpecial(key) && key != AppKeys.Other ? AppUsers.Machine : user;

    private static AppHourRow Row(AppTotals app, string name) => new(app.Key, name, app.Path, app.CpuWh, app.GpuWh, app.RunSeconds, app.FrontSeconds, app.User);

    private AppTotals Add(int user, string key, string? path, string? name)
    {
        var app = new AppTotals(key, path, name, user);
        _apps[(user, key)] = app;
        if (!AppKeys.IsSpecial(key)) _ordinary++;
        return app;
    }
}
