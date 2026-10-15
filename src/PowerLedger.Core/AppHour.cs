namespace PowerLedger.Core;

/// <summary>One app's figures in one hour as the service gathers them; see <see cref="AppHourRow"/>.</summary>
public sealed class AppTotals(string key, string? path, string? name = null)
{
    private long _runTick = long.MinValue;
    private long _frontTick = long.MinValue;

    public string Key { get; } = key;

    /// <summary>The image of the first process seen, in its own case, for the App's icon; null for the special keys.</summary>
    public string? Path { get; internal set; } = path;

    /// <summary>The name the row was stored with, when the hour was read back; null until a row is written.</summary>
    public string? Name { get; internal set; } = name;

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
/// The apps' energy in one hour, as the service gathers it tick by tick (Plan AE): at most <see cref="MostApps"/> apps of
/// their own, any more together under <see cref="AppKeys.Other"/>, and written as the busiest <see cref="Busiest"/>, the
/// special keys and Other apps for the rest. Nothing is lost on the way: every figure an app is given is in some row.
/// An hour stored by an earlier run carries on from its rows. Single-threaded.
/// </summary>
public sealed class AppHour
{
    /// <summary>The most apps an hour keeps apart in memory.</summary>
    public const int MostApps = 500;

    /// <summary>The most apps an hour is written with, besides the special keys.</summary>
    public const int Busiest = 25;

    private readonly Dictionary<string, AppTotals> _apps = new(StringComparer.Ordinal);
    private int _ordinary;

    public AppHour(DateTimeOffset start, IEnumerable<AppHourRow>? stored = null)
    {
        Start = start;
        foreach (var row in stored ?? [])
        {
            var app = Add(row.Key, row.Path, row.Name);
            app.Restore(row);
        }
    }

    public DateTimeOffset Start { get; }

    /// <summary>Set by whoever adds to the hour, cleared once it is written.</summary>
    public bool Changed { get; set; }

    /// <summary>The totals <paramref name="key"/>'s figures go into: its own, or Other apps' once the hour holds
    /// <see cref="MostApps"/> apps. The special keys always have their own.</summary>
    public AppTotals For(string key, string? path)
    {
        if (_apps.TryGetValue(key, out var app)) return app;
        if (!AppKeys.IsSpecial(key) && _ordinary >= MostApps) return For(AppKeys.Other, null);
        return Add(key, AppKeys.IsSpecial(key) ? null : path, null);
    }

    /// <summary>The rows to write: the busiest <see cref="Busiest"/> apps by energy, then by time in front, each special key,
    /// and Other apps holding its own figures and every app past the busiest, its energy and time in front added up and its
    /// running time the longest of theirs. Rows with nothing in them are left out.</summary>
    /// <param name="name">The name of an app whose row has none yet.</param>
    public List<AppHourRow> Rows(Func<AppTotals, string> name)
    {
        var ranked = _apps.Values
            .Where(app => !AppKeys.IsSpecial(app.Key) && (app.CpuWh + app.GpuWh > 0 || app.FrontSeconds > 0))
            .OrderByDescending(app => app.CpuWh + app.GpuWh)
            .ThenByDescending(app => app.FrontSeconds)
            .ThenByDescending(app => app.RunSeconds)
            .ThenBy(app => app.Key, StringComparer.Ordinal)
            .ToList();

        List<AppHourRow> rows = [.. ranked.Take(Busiest).Select(app => Row(app, app.Name ??= name(app)))];
        double cpu = 0, gpu = 0, run = 0, front = 0;
        if (_apps.TryGetValue(AppKeys.Other, out var other)) (cpu, gpu, run, front) = (other.CpuWh, other.GpuWh, other.RunSeconds, other.FrontSeconds);
        foreach (var app in ranked.Skip(Busiest))
        {
            cpu += app.CpuWh;
            gpu += app.GpuWh;
            run = Math.Max(run, app.RunSeconds);
            front += app.FrontSeconds;
        }
        if (cpu + gpu + run + front > 0) rows.Add(new AppHourRow(AppKeys.Other, AppKeys.SpecialName(AppKeys.Other)!, null, cpu, gpu, run, front));

        foreach (var app in _apps.Values)
        {
            if (app.Key == AppKeys.Other || !AppKeys.IsSpecial(app.Key)) continue;
            if (app.CpuWh + app.GpuWh + app.RunSeconds + app.FrontSeconds > 0) rows.Add(Row(app, AppKeys.SpecialName(app.Key) ?? app.Name ?? app.Key));
        }
        return rows;
    }

    private static AppHourRow Row(AppTotals app, string name) => new(app.Key, name, app.Path, app.CpuWh, app.GpuWh, app.RunSeconds, app.FrontSeconds);

    private AppTotals Add(string key, string? path, string? name)
    {
        var app = new AppTotals(key, path, name);
        _apps[key] = app;
        if (!AppKeys.IsSpecial(key)) _ordinary++;
        return app;
    }
}
