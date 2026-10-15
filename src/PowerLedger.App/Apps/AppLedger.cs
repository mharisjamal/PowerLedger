using System.Globalization;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>What a line of the Apps ledger is, for how it is drawn: an app; a line beside the apps, styled as the bands'
/// lines beside the bands (programs that already closed, other apps, other users, energy traced to no app); one of the
/// three fixed lines set apart under them (the screen, Windows and idle, the rest of the PC); or the total.</summary>
internal enum AppRowKind
{
    App,
    Beside,
    Fixed,
    Total,
}

/// <summary>The plain glyph a line shows where it has no icon of its own: a Store app, every line beside the apps and
/// every fixed line; a program's icon comes from its file, and a program without one shows <see cref="Program"/>.</summary>
internal enum AppGlyph
{
    Program,
    Store,
    Closed,
    Others,
    Users,
    Untraced,
    Screen,
    System,
    Rest,
    Total,
}

/// <summary>
/// One line of the Apps ledger (Plan AE): its energy in kWh, its share of everything the PC used, its cost, and what a
/// screen reader says for it ("Google Chrome, 0.320 kWh, 18 percent, 4 rupees"). Opened, an app shows its processor
/// and graphics energy, how long it ran and held the window in front, and whether it ran mostly in the background; a
/// line that is no app says what it holds.
/// </summary>
internal sealed record AppRow(string Key, AppRowKind Kind, string Name, string Energy, string Share, double Fraction, string Cost, string Spoken)
{
    /// <summary>The program's file, which its icon comes from; null for a Store app and for every line that is no app.</summary>
    public string? Path { get; init; }

    /// <summary>The cost as a number, in the range's currency; 0 with no tariff.</summary>
    public decimal Amount { get; init; }

    public AppGlyph Glyph { get; init; }

    /// <summary>Whether the line is opened to its details.</summary>
    public bool IsOpen { get; init; }

    /// <summary>What a line that is no app holds, in words.</summary>
    public string? Note { get; init; }

    /// <summary>The processor's energy ("0.290 kWh"), for an app and the lines beside the apps; null for the rest.</summary>
    public string? Processor { get; init; }

    /// <summary>The graphics' energy ("0.030 kWh").</summary>
    public string? Graphics { get; init; }

    /// <summary>The processor's part of the line's own energy, 0 to 1, for the bar that splits it from the graphics'.</summary>
    public double ProcessorShare { get; init; }

    /// <summary>"Ran 6h 10m", for an app.</summary>
    public string? Ran { get; init; }

    /// <summary>"In front 2h 05m", for an app, once the range has any time in front recorded.</summary>
    public string? InFront { get; init; }

    /// <summary>The app ran mostly in the background: in front for less than half the time it ran.</summary>
    public bool Background { get; init; }

    /// <summary>The opened line in words, for a screen reader's help text.</summary>
    public string Detail { get; init; } = "";

    /// <summary>Whether the line has a processor and graphics split to show.</summary>
    public bool HasSplit => Processor is not null;
}

/// <summary>
/// The Apps page's ledger (Plan AE), from a range's <see cref="AppsRange"/>: the apps most first, then the lines beside
/// them, then the three fixed lines set apart, then the total. Every share is of the whole those lines add up to, on the
/// same scale and with the same rules as the bands' table (<see cref="Bands.Rows"/>): the processor and graphics energy
/// at the pack, the screen and the rest of the PC from the hour rows, a rest below zero counted as zero, and what the PC
/// drew asleep and what charging lost beside them, here in the rest of the PC. So the lines add up to the PC's total for
/// the hours apps were counted in.
/// </summary>
internal static class AppLedger
{
    /// <summary>The most apps listed on their own; the smaller ones join Other apps.</summary>
    public const int MostApps = 30;

    /// <summary>The least energy the apps of a range must have between them before the ledger shows: what PowerLedger
    /// gathers in its first few minutes of watching them.</summary>
    public const double LeastWh = 1;

    /// <summary>Energy traced to no app under this is too small to show: it would read as nothing.</summary>
    public const double LeastUntracedWh = 0.5;

    // The fixed lines' and the total's keys, beside the apps' own (AppQueries' special keys).
    public const string ScreenKey = "::screen";
    public const string SystemKey = "::system";
    public const string RestKey = "::rest";
    public const string UntracedKey = "::untraced";
    public const string TotalKey = "::total";

    /// <summary>Whether <paramref name="key"/> is one of the service's special keys rather than an app's.</summary>
    public static bool IsSpecial(string key) => key.StartsWith("::", StringComparison.Ordinal);

    /// <summary>Whether the range has enough to show: apps have been watched for a few minutes in it.</summary>
    public static bool HasApps(AppsRange range) => range.Apps.Sum(a => Clean(a.Wh)) >= LeastWh;

    /// <summary>The app that used the most energy in the range, or null with none yet.</summary>
    public static AppUse? Top(AppsRange range)
        => HasApps(range) ? range.Apps.Where(a => !IsSpecial(a.Key) && Clean(a.Wh) > 0).MaxBy(a => a.Wh) : null;

    /// <summary>The ledger's lines for <paramref name="range"/>, the line keyed <paramref name="open"/> opened; none
    /// while the range has too little to show (<see cref="HasApps"/>).</summary>
    public static IReadOnlyList<AppRow> Rows(AppsRange range, CultureInfo culture, string? open = null)
    {
        if (!HasApps(range)) return [];
        AppUse? Find(string key) => range.Apps.FirstOrDefault(a => a.Key == key);
        var apps = range.Apps.Where(a => !IsSpecial(a.Key) && Clean(a.Wh) > 0).OrderByDescending(a => a.Wh).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var listed = apps.Take(MostApps).ToList();
        var other = Together(AppQueries.OtherKey, [Find(AppQueries.OtherKey), .. apps.Skip(MostApps)]);
        var closed = Find(AppQueries.ClosedKey);
        var users = Find(AppQueries.OtherUsersKey);
        var windows = Find(AppQueries.WindowsKey);
        var idle = Find(AppQueries.IdleKey);
        var untraced = Clean(range.Untraced.Wh) >= LeastUntracedWh ? Clean(range.Untraced.Wh) : 0;
        var screen = Clean(range.Screen.Wh);
        var (rest, asleep, losses) = (Clean(range.Rest.Wh), Clean(range.Asleep.Wh), Clean(range.Losses.Wh));
        var whole = listed.Sum(Wh) + Wh(closed) + Wh(other) + Wh(users) + untraced + screen + Wh(windows) + Wh(idle) + rest + asleep + losses;
        var anyFront = range.Apps.Any(a => a.FrontSeconds > 0);
        var currency = range.Currency;

        AppRow Line(string key, AppRowKind kind, string name, double wh, decimal cost, AppGlyph glyph)
        {
            var fraction = whole > 0 ? wh / whole : 0;
            var energy = Format.Kwh(wh / 1000, culture);
            var spoken = $"{name}, {energy} kWh, {Math.Round(Math.Clamp(fraction, 0, 1) * 100).ToString("0", culture)} percent";
            if (currency is not null) spoken += ", " + Money.Spoken(cost, currency, culture);
            return new AppRow(key, kind, name, energy, Format.Percent(fraction, culture), fraction, currency is null ? Format.Missing : Money.Format(cost, currency, culture), spoken)
            {
                Glyph = glyph,
                IsOpen = key == open,
                Amount = currency is null ? 0 : cost,
            };
        }

        AppRow Split(AppRow row, AppUse use, string? note)
        {
            var (cpu, gpu) = (Clean(use.CpuWh), Clean(use.GpuWh));
            var (processor, graphics) = (Format.Kwh(cpu / 1000, culture), Format.Kwh(gpu / 1000, culture));
            var detail = $"Processor {processor} kWh, graphics {graphics} kWh.";
            return row with
            {
                Note = note,
                Processor = processor + " kWh",
                Graphics = graphics + " kWh",
                ProcessorShare = cpu + gpu > 0 ? cpu / (cpu + gpu) : 1,
                Detail = note is null ? detail : note + " " + detail,
            };
        }

        AppRow App(AppUse app)
        {
            var store = app.Key.StartsWith("pkg:", StringComparison.Ordinal);
            var row = Split(Line(app.Key, AppRowKind.App, app.Name, Wh(app), Clean(app.Cost), store ? AppGlyph.Store : AppGlyph.Program) with
            {
                Path = store ? null : app.Path,
            }, app, null);
            var background = anyFront && app.RunSeconds > 0 && app.FrontSeconds < app.RunSeconds / 2;
            var front = !anyFront ? "" : app.FrontSeconds > 0 ? $", {Format.SpokenDuration(app.FrontSeconds)} of it in front" : ", never in front";
            return row with
            {
                Ran = "Ran " + Time(app.RunSeconds),
                InFront = !anyFront ? null : app.FrontSeconds > 0 ? "In front " + Time(app.FrontSeconds) : "Never in front",
                Background = background,
                Detail = row.Detail + $" Ran {Format.SpokenDuration(app.RunSeconds)}{front}." + (background ? " Mostly in background." : ""),
            };
        }

        var rows = listed.Select(App).ToList();
        if (closed is not null && Wh(closed) > 0)
            rows.Add(Split(Line(closed.Key, AppRowKind.Beside, "Programs that already closed", Wh(closed), Clean(closed.Cost), AppGlyph.Closed), closed,
                "Programs that started and finished between two readings."));
        if (other is not null && Wh(other) > 0)
            rows.Add(Split(Line(AppQueries.OtherKey, AppRowKind.Beside, "Other apps", Wh(other), Clean(other.Cost), AppGlyph.Others), other,
                "Apps too small to list on their own."));
        if (users is not null && Wh(users) > 0)
            rows.Add(Split(Line(users.Key, AppRowKind.Beside, "Other users", Wh(users), Clean(users.Cost), AppGlyph.Users), users,
                "Apps of other people signed in to this PC."));
        if (untraced > 0)
        {
            const string note = "Processor and graphics energy from moments PowerLedger wasn't watching apps, such as before it began.";
            rows.Add(Line(UntracedKey, AppRowKind.Beside, "Not traced to an app", untraced, Clean(range.Untraced.Cost), AppGlyph.Untraced) with { Note = note, Detail = note });
        }

        const string screenNote = "The built-in panel and any monitors counted.";
        rows.Add(Line(ScreenKey, AppRowKind.Fixed, "Screen", screen, screen > 0 ? Clean(range.Screen.Cost) : 0, AppGlyph.Screen) with { Note = screenNote, Detail = screenNote });
        var systemNote = $"Windows' own processes {Format.Kwh(Wh(windows) / 1000, culture)} kWh, and the processor and graphics at rest {Format.Kwh(Wh(idle) / 1000, culture)} kWh.";
        rows.Add(Line(SystemKey, AppRowKind.Fixed, "Windows and idle", Wh(windows) + Wh(idle), Clean(windows?.Cost) + Clean(idle?.Cost), AppGlyph.System) with
        {
            Note = systemNote, Detail = systemNote,
        });
        var restNote = "Memory, drives, the board and the rest of the PC"
            + (asleep > 0 ? $", {Format.Kwh(asleep / 1000, culture)} kWh drawn asleep" : "")
            + (losses > 0 ? $", {Format.Kwh(losses / 1000, culture)} kWh lost charging the battery" : "") + ".";
        var restCost = (rest > 0 ? Clean(range.Rest.Cost) : 0) + (asleep > 0 ? Clean(range.Asleep.Cost) : 0) + (losses > 0 ? Clean(range.Losses.Cost) : 0);
        rows.Add(Line(RestKey, AppRowKind.Fixed, "Rest of your PC", rest + asleep + losses, restCost, AppGlyph.Rest) with { Note = restNote, Detail = restNote });

        var totalLine = Line(TotalKey, AppRowKind.Total, "Total", whole, rows.Aggregate(0m, (sum, row) => sum + row.Amount), AppGlyph.Total);
        rows.Add(totalLine with { Share = whole > 0 ? Format.Percent(1, culture) : Format.Missing, Fraction = whole > 0 ? 1 : 0, IsOpen = false });
        return rows;
    }

    /// <summary>Apps taken together as one line: their energy, time and cost summed.</summary>
    private static AppUse? Together(string key, IEnumerable<AppUse?> uses)
    {
        var list = uses.OfType<AppUse>().ToList();
        return list.Count == 0 ? null : new AppUse(
            key, key, null, list.Sum(u => Clean(u.CpuWh)), list.Sum(u => Clean(u.GpuWh)), list.Sum(u => u.RunSeconds), list.Sum(u => u.FrontSeconds),
            list.Aggregate(0m, (sum, u) => sum + Clean(u.Cost)));
    }

    /// <summary>A length of time on the page: "6h 10m", or "under a minute".</summary>
    private static string Time(double seconds) => seconds < 30 ? "under a minute" : Format.Duration(seconds / 3600);

    private static double Wh(AppUse? use) => use is null ? 0 : Clean(use.CpuWh) + Clean(use.GpuWh);

    private static double Clean(double wh) => double.IsFinite(wh) ? Math.Max(0, wh) : 0;

    private static decimal Clean(decimal? cost) => cost is { } c && c > 0 ? c : 0;
}
