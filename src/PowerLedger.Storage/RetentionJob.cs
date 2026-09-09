namespace PowerLedger.Storage;

/// <summary>Spec §7: raw 24–168 h (default 48), minute history 1–5 years (default 2). Hour rows are kept forever.</summary>
public sealed record RetentionOptions(int RawHours = 48, int HistoryYears = 2)
{
    public static RetentionOptions Clamped(int rawHours, int historyYears)
        => new(Math.Clamp(rawHours, 24, 168), Math.Clamp(historyYears, 1, 5));
}

public sealed record RetentionResult(int RawDeleted, int MinutesDeleted);

public sealed class RetentionJob(SqliteDatabase db)
{
    public RetentionResult Run(DateTimeOffset now, RetentionOptions options)
    {
        var raw = new RawSampleRepository(db).PurgeBefore(now.AddHours(-options.RawHours));
        var minutes = new AggregateRepository(db).PurgeMinutesBefore(now.AddYears(-options.HistoryYears));
        return new RetentionResult(raw, minutes);
    }

    /// <summary>Reclaims free pages a little at a time. Weekly is plenty; never run a full VACUUM.</summary>
    public void IncrementalVacuum(int pages = 2000)
    {
        using var c = db.Open();
        Migrator.Exec(c, $"PRAGMA incremental_vacuum({pages})");
    }
}
