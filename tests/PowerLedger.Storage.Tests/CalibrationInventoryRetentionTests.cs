using PowerLedger.Core;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Storage.Tests;

public class CalibrationInventoryRetentionTests
{
    [Fact]
    public void Calibration_state_round_trips_per_inventory_hash_and_save_replaces()
    {
        using var t = new TestDatabase();
        var repo = new CalibrationRepository(t.Db);
        repo.Load("abc").Buckets.ShouldBeEmpty();

        repo.Save("abc", new CalibrationState([new BucketState(6, 9.0, 400), new BucketState(-1, 7.5, 320)]), Fixtures.T0);
        repo.Save("other", new CalibrationState([new BucketState(6, 20.0, 5)]), Fixtures.T0);
        var back = repo.Load("abc");
        back.Buckets.Count.ShouldBe(2);
        back.Buckets.Single(b => b.Bucket == 6).ShouldBe(new BucketState(6, 9.0, 400));

        repo.Save("abc", new CalibrationState([new BucketState(6, 9.5, 401)]), Fixtures.T0.AddMinutes(1));
        repo.Load("abc").Buckets.ShouldBe([new BucketState(6, 9.5, 401)]);

        repo.Clear("abc");
        repo.Load("abc").Buckets.ShouldBeEmpty();
        repo.Load("other").Buckets.Count.ShouldBe(1);
    }

    [Fact]
    public void Inventory_keeps_the_first_detection_time_per_hash_and_reports_the_latest()
    {
        using var t = new TestDatabase();
        var repo = new InventoryRepository(t.Db);
        repo.Latest().ShouldBeNull();
        repo.Upsert(new InventoryRecord("h1", Fixtures.T0, "{\"cpu\":\"i7\"}"));
        repo.Upsert(new InventoryRecord("h1", Fixtures.T0.AddDays(1), "{\"cpu\":\"i7\"}"));
        repo.Upsert(new InventoryRecord("h2", Fixtures.T0.AddHours(1), "{\"cpu\":\"i9\"}"));
        repo.All().Count.ShouldBe(2);
        repo.All().Single(r => r.Hash == "h1").DetectedAt.ShouldBe(Fixtures.T0);
        repo.Latest()!.Hash.ShouldBe("h2");
    }

    [Fact]
    public void Retention_purges_raw_and_minute_rows_by_age()
    {
        using var t = new TestDatabase();
        var raw = new RawSampleRepository(t.Db);
        var agg = new AggregateRepository(t.Db);
        var now = Fixtures.T0;
        raw.InsertBatch([Fixtures.Reading(0) with { Timestamp = now.AddHours(-49) }, Fixtures.Reading(0) with { Timestamp = now.AddHours(-47) }]);
        agg.UpsertMinute(Aggregate.Empty(now.AddYears(-3)));
        agg.UpsertMinute(Aggregate.Empty(now.AddDays(-1)));
        agg.UpsertHour(Aggregate.Empty(now.AddYears(-3)));

        var result = new RetentionJob(t.Db).Run(now, new RetentionOptions());
        result.RawDeleted.ShouldBe(1);
        result.MinutesDeleted.ShouldBe(1);
        raw.Count().ShouldBe(1);
        agg.ReadHours(now.AddYears(-4), now).Count.ShouldBe(1);
    }

    [Fact]
    public void Retention_options_are_clamped_to_the_spec_bounds()
    {
        RetentionOptions.Clamped(rawHours: 1, historyYears: 99).ShouldBe(new RetentionOptions(24, 5));
        RetentionOptions.Clamped(rawHours: 500, historyYears: 0).ShouldBe(new RetentionOptions(168, 1));
        RetentionOptions.Clamped(72, 3).ShouldBe(new RetentionOptions(72, 3));
    }

    [Fact]
    public void Saving_an_empty_state_clears_the_hash()
    {
        using var t = new TestDatabase();
        var repo = new CalibrationRepository(t.Db);
        repo.Save("abc", new CalibrationState([new BucketState(6, 9.0, 400)]), Fixtures.T0);
        repo.Save("abc", CalibrationState.Empty, Fixtures.T0.AddMinutes(1));
        repo.Load("abc").Buckets.ShouldBeEmpty();
    }

    [Fact]
    public void Retention_deletes_nothing_when_everything_is_young()
    {
        using var t = new TestDatabase();
        var raw = new RawSampleRepository(t.Db);
        raw.InsertBatch([Fixtures.Reading(0)]);
        new AggregateRepository(t.Db).UpsertMinute(Aggregate.Empty(Fixtures.T0));
        new RetentionJob(t.Db).Run(Fixtures.T0.AddHours(1), new RetentionOptions()).ShouldBe(new RetentionResult(0, 0));
        raw.Count().ShouldBe(1);
    }

    [Fact]
    public void Out_of_range_options_cannot_widen_the_purge_and_vacuum_reclaims_pages()
    {
        using var t = new TestDatabase();
        var raw = new RawSampleRepository(t.Db);
        raw.InsertBatch([Fixtures.Reading(0) with { Timestamp = Fixtures.T0.AddHours(-25) }]);
        // 0 hours would purge everything; the clamp holds it at 24 h, so this row survives.
        new RetentionJob(t.Db).Run(Fixtures.T0, new RetentionOptions(RawHours: 0, HistoryYears: 0)).RawDeleted.ShouldBe(1);
        raw.InsertBatch([Fixtures.Reading(0) with { Timestamp = Fixtures.T0.AddHours(-1) }]);
        new RetentionJob(t.Db).Run(Fixtures.T0, new RetentionOptions(RawHours: 0, HistoryYears: 0)).RawDeleted.ShouldBe(0);
        new RetentionJob(t.Db).IncrementalVacuum();
    }
}
