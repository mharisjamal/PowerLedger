using System.Globalization;
using PowerLedger.Storage;

namespace PowerLedger.Service;

/// <summary>When the daily purge and the weekly vacuum are due (spec §7): the purge at 03:00 local, or at the first chance
/// after it when the machine was asleep or off then; the vacuum a week after the last one.</summary>
internal static class RetentionSchedule
{
    public static readonly TimeSpan VacuumEvery = TimeSpan.FromDays(7);
    private static readonly TimeSpan PurgeTimeOfDay = TimeSpan.FromHours(3);

    /// <summary>The latest 03:00 local at or before <paramref name="now"/>.</summary>
    public static DateTimeOffset LatestPurgeTime(DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var candidate = At(today, zone);
        return candidate <= now ? candidate : At(today.AddDays(-1), zone);
    }

    public static bool PurgeDue(DateTimeOffset now, DateTimeOffset? lastPurge, TimeZoneInfo zone)
        => lastPurge is not { } last || last < LatestPurgeTime(now, zone);

    public static bool VacuumDue(DateTimeOffset now, DateTimeOffset? lastVacuum)
        => lastVacuum is not { } last || now - last >= VacuumEvery;

    /// <summary>03:00 local on <paramref name="date"/>, or the first valid local time after it when a clock change skips it.</summary>
    private static DateTimeOffset At(DateTime date, TimeZoneInfo zone)
    {
        var wall = date + PurgeTimeOfDay;
        while (zone.IsInvalidTime(wall)) wall = wall.AddMinutes(30);
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }
}

/// <summary>Runs the purge and the vacuum when due, remembering the last runs in the settings table so a restart does not repeat them.</summary>
internal sealed class RetentionRunner(SqliteDatabase database, TimeZoneInfo zone)
{
    internal const string LastPurgeKey = "retention.last-purge";
    internal const string LastVacuumKey = "retention.last-vacuum";

    private readonly SettingsRepository _settings = new(database);
    private readonly RetentionJob _job = new(database);
    private bool _loaded;
    private DateTimeOffset? _lastPurge;
    private DateTimeOffset? _lastVacuum;

    /// <returns>What the purge removed when it ran; null when nothing was due.</returns>
    public RetentionResult? RunIfDue(DateTimeOffset now, RetentionOptions options)
    {
        if (!_loaded)
        {
            _lastPurge = Read(LastPurgeKey);
            _lastVacuum = Read(LastVacuumKey);
            _loaded = true;
        }
        if (!RetentionSchedule.PurgeDue(now, _lastPurge, zone)) return null;

        var result = _job.Run(now, options);
        _lastPurge = now;
        Write(LastPurgeKey, now);
        if (RetentionSchedule.VacuumDue(now, _lastVacuum))
        {
            _job.IncrementalVacuum();
            _lastVacuum = now;
            Write(LastVacuumKey, now);
        }
        return result;
    }

    private DateTimeOffset? Read(string key)
        => _settings.Get(key) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;

    private void Write(string key, DateTimeOffset at) => _settings.Set(key, at.ToString("O", CultureInfo.InvariantCulture));
}
