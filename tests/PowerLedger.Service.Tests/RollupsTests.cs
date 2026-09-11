using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class RollupsTests
{
    private const double Gap = 5;

    [Fact]
    public void Each_minute_with_raw_rows_is_folded_and_empty_minutes_are_skipped()
    {
        using var t = new TestDatabase();
        var (raw, aggregates) = Repositories(t);
        raw.InsertBatch([.. Seconds(0, 60), .. Seconds(120, 60)]);   // minutes 0 and 2; minute 1 is empty
        new Rollups(raw, aggregates).FoldRange(T(0), T(3), Gap);

        var minutes = aggregates.ReadMinutes(T(0), T(3));
        minutes.Select(m => m.Start).ShouldBe(new[] { T(0), T(2) });
        minutes[0].SampleCount.ShouldBe(60);
        minutes[0].EnergyWh.ShouldBe(60 * 36 / 3600.0, 1e-9);
    }

    [Fact]
    public void Ticks_from_a_second_run_inside_a_folded_minute_are_added_not_replaced()
    {
        using var t = new TestDatabase();
        var (raw, aggregates) = Repositories(t);
        var rollups = new Rollups(raw, aggregates);
        raw.InsertBatch([.. Seconds(0, 20)]);
        rollups.FoldRange(T(0), T(1), Gap);
        raw.InsertBatch([.. Seconds(30, 20)]);
        rollups.FoldRange(T(0), T(1), Gap);
        aggregates.ReadMinutes(T(0), T(1)).Single().SampleCount.ShouldBe(40);
    }

    [Fact]
    public void An_hour_is_folded_only_once_it_is_complete()
    {
        using var t = new TestDatabase();
        var (raw, aggregates) = Repositories(t);
        var rollups = new Rollups(raw, aggregates);
        raw.InsertBatch([.. Seconds(0, 1800)]);                       // 10:00 to 10:30
        rollups.FoldRange(T(0), T(30), Gap);
        aggregates.ReadHours(T(0), T(60)).ShouldBeEmpty();
        rollups.FoldRange(T(30), T(60), Gap);
        var hour = aggregates.ReadHours(T(0), T(60)).Single();
        hour.SampleCount.ShouldBe(1800);
        hour.EnergyWh.ShouldBe(1800 * 36 / 3600.0, 1e-9);
    }

    [Fact]
    public void Catch_up_folds_what_a_crashed_run_left_unfolded()
    {
        using var t = new TestDatabase();
        var (raw, aggregates) = Repositories(t);
        var rollups = new Rollups(raw, aggregates);
        raw.InsertBatch([.. Seconds(0, 180)]);                        // 10:00 to 10:03
        rollups.FoldRange(T(0), T(1), Gap);                          // the old run folded 10:00, then died
        rollups.CatchUp(T(5), TimeSpan.FromHours(48), Gap);
        aggregates.ReadMinutes(T(0), T(5)).Select(m => m.SampleCount).ShouldBe(new[] { 60, 60, 60 });
        aggregates.ReadHours(T(0), T(60)).ShouldBeEmpty();           // 10:00 is still running
    }

    [Fact]
    public void Catch_up_folds_a_complete_hour_that_was_never_folded()
    {
        using var t = new TestDatabase();
        var (raw, aggregates) = Repositories(t);
        var rollups = new Rollups(raw, aggregates);
        raw.InsertBatch([.. Seconds(3480, 240)]);                     // 10:58 to 11:02
        rollups.FoldRange(T(58), T(59), Gap);                        // only 10:58 was folded before the crash
        rollups.CatchUp(T(63), TimeSpan.FromHours(48), Gap);
        aggregates.ReadHours(T(0), T(60)).Single().SampleCount.ShouldBe(120);
        aggregates.ReadMinutes(T(60), T(63)).Count.ShouldBe(2);
    }

    [Fact]
    public void Catch_up_on_an_empty_database_does_nothing()
    {
        using var t = new TestDatabase();
        var (raw, aggregates) = Repositories(t);
        new Rollups(raw, aggregates).CatchUp(T(5), TimeSpan.FromHours(48), Gap);
        aggregates.LastMinuteStart().ShouldBeNull();
    }

    private static DateTimeOffset T(int minute) => Samples.T0.AddMinutes(minute);

    private static IEnumerable<PowerLedger.Core.Reading> Seconds(int from, int count)
        => Enumerable.Range(from, count).Select(s => Readings.At(s));

    private static (RawSampleRepository Raw, AggregateRepository Aggregates) Repositories(TestDatabase t)
        => (new RawSampleRepository(t.Db), new AggregateRepository(t.Db));
}
