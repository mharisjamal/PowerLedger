using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Storage.Tests;

public class AggregateRepositoryTests
{
    private static Aggregate Minute(int index, double watts = 30, Quality q = Quality.Measured)
        => Downsampler.ToMinute(Fixtures.T0.AddMinutes(index), Fixtures.Minute(index * 60, watts, q));

    [Fact]
    public void Minute_rows_round_trip_every_field()
    {
        using var t = new TestDatabase();
        var repo = new AggregateRepository(t.Db);
        var m = Minute(0);
        repo.UpsertMinute(m);
        repo.ReadMinutes(Fixtures.T0, Fixtures.T0.AddMinutes(1)).Single().ShouldBe(m);
    }

    [Fact]
    public void Reads_are_half_open_ordered_and_per_table()
    {
        using var t = new TestDatabase();
        var repo = new AggregateRepository(t.Db);
        for (var i = 0; i < 5; i++) repo.UpsertMinute(Minute(i));
        repo.UpsertHour(Downsampler.ToHour(Fixtures.T0, [Minute(0), Minute(1)]));

        var minutes = repo.ReadMinutes(Fixtures.T0.AddMinutes(1), Fixtures.T0.AddMinutes(4));
        minutes.Select(m => m.Start).ShouldBe([Fixtures.T0.AddMinutes(1), Fixtures.T0.AddMinutes(2), Fixtures.T0.AddMinutes(3)]);
        repo.ReadHours(Fixtures.T0, Fixtures.T0.AddHours(1)).Single().EnergyWh.ShouldBe(1.0, 1e-9);
    }

    [Fact]
    public void Upsert_replaces_an_existing_row()
    {
        using var t = new TestDatabase();
        var repo = new AggregateRepository(t.Db);
        repo.UpsertMinute(Minute(0, watts: 30));
        repo.UpsertMinute(Minute(0, watts: 60));
        repo.ReadMinutes(Fixtures.T0, Fixtures.T0.AddMinutes(1)).Single().AvgW.ShouldBe(60, 1e-9);
    }

    [Fact]
    public void Last_starts_are_null_when_empty_and_the_newest_row_otherwise()
    {
        using var t = new TestDatabase();
        var repo = new AggregateRepository(t.Db);
        repo.LastMinuteStart().ShouldBeNull();
        repo.LastHourStart().ShouldBeNull();
        repo.UpsertMinute(Minute(3));
        repo.UpsertMinute(Minute(1));
        repo.LastMinuteStart().ShouldBe(Fixtures.T0.AddMinutes(3));
    }

    [Fact]
    public void Purge_removes_old_minutes_only()
    {
        using var t = new TestDatabase();
        var repo = new AggregateRepository(t.Db);
        for (var i = 0; i < 5; i++) repo.UpsertMinute(Minute(i));
        repo.PurgeMinutesBefore(Fixtures.T0.AddMinutes(3)).ShouldBe(3);
        repo.ReadMinutes(Fixtures.T0, Fixtures.T0.AddHours(1)).Count.ShouldBe(2);
    }

    [Fact]
    public void Every_column_maps_to_its_own_field()
    {
        // Distinct value per field so any two swapped columns fail the round trip.
        using var t = new TestDatabase();
        var repo = new AggregateRepository(t.Db);
        var distinct = new Aggregate(Fixtures.T0.AddMinutes(9), 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18);
        repo.UpsertMinute(distinct);
        repo.UpsertHour(distinct);
        repo.ReadMinutes(Fixtures.T0.AddMinutes(9), Fixtures.T0.AddMinutes(10)).Single().ShouldBe(distinct);
        repo.ReadHours(Fixtures.T0.AddMinutes(9), Fixtures.T0.AddMinutes(10)).Single().ShouldBe(distinct);
    }
}
