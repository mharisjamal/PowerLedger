using PowerLedger.Core;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Storage.Tests;

public class RawSampleRepositoryTests
{
    [Fact]
    public void A_batch_round_trips_every_field()
    {
        using var t = new TestDatabase();
        var repo = new RawSampleRepository(t.Db);
        var batch = Fixtures.Minute(0);
        // Every REAL column gets a distinct value and every pair of flag columns differs somewhere, so a swapped column cannot round-trip.
        var odd = Fixtures.Reading(60, idle: true, displayOn: false) with
        {
            Components = new Components(1, 2, 3, 4, 5, 6, 7, 8, 9, 10),
            DeltaSeconds = 0.98,
            Suspect = true,
            GpuLoad = null,
            Brightness = null,
        };
        var odd2 = Fixtures.Reading(61) with { SessionLocked = true, Suspect = true };
        repo.InsertBatch([.. batch, odd, odd2]);

        var back = repo.Read(Fixtures.T0, Fixtures.T0.AddMinutes(2));
        back.Count.ShouldBe(62);
        back[0].ShouldBe(batch[0]);
        back[59].ShouldBe(batch[59]);
        back[60].ShouldBe(odd);
        back[61].ShouldBe(odd2);
        repo.Count().ShouldBe(62);
    }

    [Fact]
    public void Read_is_half_open_and_ordered_by_time()
    {
        using var t = new TestDatabase();
        var repo = new RawSampleRepository(t.Db);
        repo.InsertBatch(Fixtures.Minute(0));
        var back = repo.Read(Fixtures.T0.AddSeconds(10), Fixtures.T0.AddSeconds(20));
        back.Count.ShouldBe(10);
        back.First().Timestamp.ShouldBe(Fixtures.T0.AddSeconds(10));
        back.Last().Timestamp.ShouldBe(Fixtures.T0.AddSeconds(19));
    }

    [Fact]
    public void Inserting_the_same_timestamp_twice_keeps_the_latest_row()
    {
        using var t = new TestDatabase();
        var repo = new RawSampleRepository(t.Db);
        repo.InsertBatch([Fixtures.Reading(0, totalW: 30)]);
        repo.InsertBatch([Fixtures.Reading(0, totalW: 31)]);
        repo.Read(Fixtures.T0, Fixtures.T0.AddSeconds(1)).Single().TotalW.ShouldBe(31);
    }

    [Fact]
    public void Purge_removes_rows_older_than_the_cutoff()
    {
        using var t = new TestDatabase();
        var repo = new RawSampleRepository(t.Db);
        repo.InsertBatch(Fixtures.Minute(0));
        repo.PurgeBefore(Fixtures.T0.AddSeconds(45)).ShouldBe(45);
        repo.Count().ShouldBe(15);
    }

    [Fact]
    public void An_empty_batch_is_a_no_op()
    {
        using var t = new TestDatabase();
        new RawSampleRepository(t.Db).InsertBatch([]);
        new RawSampleRepository(t.Db).Count().ShouldBe(0);
    }

    [Fact]
    public void Latest_is_the_newest_tick_or_null_on_an_empty_table()
    {
        using var t = new TestDatabase();
        var repo = new RawSampleRepository(t.Db);
        repo.Latest().ShouldBeNull();
        repo.InsertBatch([.. Enumerable.Range(0, 10).Select(s => Fixtures.Reading(s))]);
        repo.Latest().ShouldBe(Fixtures.T0.AddSeconds(9));
    }
}
