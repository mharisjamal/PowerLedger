using PowerLedger.Contracts;
using PowerLedger.Storage;

namespace PowerLedger.Service.Sharing;

/// <summary>The outbox's events as JSON (data-sharing design §4): a crash is one row; the App's usage and the sources'
/// failures are one row a day, merged.</summary>
internal static class OutboxEvents
{
    public const string Crash = "crash";
    public const string Usage = "usage";
    public const string Sources = "sources";

    /// <summary>The day's counts with <paramref name="add"/>'s added and its states taking over (UsageCounts' rule), never
    /// growing past what the counts allow.</summary>
    public static string MergeUsage(string? json, UsageCounts add)
    {
        var merged = ParseUsage(json) is { } held
            ? held with
            {
                AppOpens = Add(held.AppOpens, add.AppOpens),
                Pages = Merge(held.Pages, add.Pages),
                Settings = Merge(held.Settings, add.Settings),
                ReportsExported = Add(held.ReportsExported, add.ReportsExported),
                UpdatesInstalled = Add(held.UpdatesInstalled, add.UpdatesInstalled),
                DaysSinceFirstRun = add.DaysSinceFirstRun,
                Theme = add.Theme,
                Language = add.Language,
            }
            : add with { Pages = Merge(new Dictionary<string, int>(), add.Pages), Settings = Merge(new Dictionary<string, int>(), add.Settings) };
        return SharingJson.Write(merged, SharingJson.Default.UsageCounts);
    }

    public static UsageCounts? ParseUsage(string? json) => SharingJson.Read(json, SharingJson.Default.UsageCounts);

    /// <summary>The day's failures with <paramref name="add"/>'s added, each source keeping the last error it gave.</summary>
    public static string MergeSources(string? json, IReadOnlyDictionary<string, SourceDay> add)
    {
        var merged = SharingJson.Read(json, SharingJson.Default.DictionaryStringSourceDay) ?? [];
        foreach (var (name, day) in add)
        {
            var held = merged.GetValueOrDefault(name);
            merged[name] = new SourceDay(Add(held?.Failures ?? 0, day.Failures), day.LastError ?? held?.LastError);
        }
        return SharingJson.Write(merged, SharingJson.Default.DictionaryStringSourceDay);
    }

    public static string Write(CrashReport crash) => SharingJson.Write(crash, SharingJson.Default.CrashReport);

    /// <summary>Everything the outbox holds for <paramref name="day"/> besides its minutes. What can't be read is passed over.</summary>
    public static DayEvents Read(OutboxRepository outbox, string day)
    {
        var sources = outbox.Events(day, Sources) is [.., var latest]
            ? SharingJson.Read(latest, SharingJson.Default.DictionaryStringSourceDay)
            : null;
        var crashes = outbox.Events(day, Crash)
            .Select(json => SharingJson.Read(json, SharingJson.Default.CrashReport))
            .OfType<CrashReport>()
            .ToList();
        var usage = outbox.Events(day, Usage) is [.., var counts] ? ParseUsage(counts) : null;
        return new DayEvents(sources ?? [], crashes, usage);
    }

    private static int Add(int held, int add) => (int)Math.Clamp((long)held + add, 0, UsageCounts.MaxCount);

    /// <summary>Counts by name added together; a name beyond the most that can be counted is left out.</summary>
    private static Dictionary<string, int> Merge(IReadOnlyDictionary<string, int>? held, IReadOnlyDictionary<string, int>? add)
    {
        var merged = new Dictionary<string, int>(held ?? new Dictionary<string, int>(), StringComparer.Ordinal);
        foreach (var (name, count) in add ?? new Dictionary<string, int>())
        {
            if (merged.TryGetValue(name, out var was)) merged[name] = Add(was, count);
            else if (merged.Count < UsageCounts.MaxNames) merged[name] = Add(0, count);
        }
        return merged;
    }
}
