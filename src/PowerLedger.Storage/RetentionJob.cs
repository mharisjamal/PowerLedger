namespace PowerLedger.Storage;

/// <summary>Spec §7: raw 24–168 h (default 48), minute history 1–5 years (default 2). Hour rows are kept forever.</summary>
/// <param name="RawHours">How long raw one-second rows are kept.</param>
/// <param name="HistoryYears">How long minute rows are kept.</param>
public sealed record RetentionOptions(int RawHours = 48, int HistoryYears = 2)
{
    /// <summary>The same options with both values forced into the spec's bounds.</summary>
    public static RetentionOptions Clamped(int rawHours, int historyYears)
        => new(Math.Clamp(rawHours, 24, 168), Math.Clamp(historyYears, 1, 5));
}

/// <param name="RawDeleted">Raw rows removed.</param>
/// <param name="MinutesDeleted">Minute rows removed.</param>
public sealed record RetentionResult(int RawDeleted, int MinutesDeleted);

/// <summary>Daily housekeeping: purges expired rows and, weekly, reclaims free pages.</summary>
public sealed class RetentionJob(SqliteDatabase db)
{
    /// <summary>Purges raw and minute rows older than the retention window. Options are clamped, so no caller can widen the purge past the spec's bounds. Hour rows are never touched.</summary>
    public RetentionResult Run(DateTimeOffset now, RetentionOptions options)
    {
        var safe = RetentionOptions.Clamped(options.RawHours, options.HistoryYears);
        var raw = new RawSampleRepository(db).PurgeBefore(now.AddHours(-safe.RawHours));
        var minutes = new AggregateRepository(db).PurgeMinutesBefore(now.AddYears(-safe.HistoryYears));
        return new RetentionResult(raw, minutes);
    }

    /// <summary>Reclaims free pages a little at a time. Weekly is plenty; never run a full VACUUM.</summary>
    public void IncrementalVacuum(int pages = 2000)
    {
        using var c = db.Open();
        Migrator.Exec(c, $"PRAGMA incremental_vacuum({pages})");
    }
}
