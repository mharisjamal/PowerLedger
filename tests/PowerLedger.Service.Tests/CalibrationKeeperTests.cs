using PowerLedger.Core;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class CalibrationKeeperTests
{
    private static readonly CalibrationOptions Quick = new(HalfLifeSamples: 10, MinBucketSamples: 1, MinTotalSamples: 2);
    private static readonly DateTimeOffset Now = Samples.T0;

    [Fact]
    public void Using_a_hash_loads_what_was_learned_for_it()
    {
        using var t = new TestDatabase();
        var repository = new CalibrationRepository(t.Db);
        var trained = new CalibrationLearner(Quick);
        Feed(trained, 3);
        repository.Save("abc", trained.Export(), Now);

        var keeper = new CalibrationKeeper(repository, Quick, TimeSpan.FromMinutes(10));
        keeper.Use("abc", Now);
        keeper.Hash.ShouldBe("abc");
        keeper.Learner.TotalSamples.ShouldBe(3);
    }

    [Fact]
    public void Switching_machines_saves_the_old_learner_first()
    {
        using var t = new TestDatabase();
        var repository = new CalibrationRepository(t.Db);
        var keeper = new CalibrationKeeper(repository, Quick, TimeSpan.FromMinutes(10));
        keeper.Use("old", Now);
        Feed(keeper.Learner, 3);
        keeper.Use("new", Now);
        keeper.Learner.TotalSamples.ShouldBe(0);
        Loaded(repository, "old").TotalSamples.ShouldBe(3);
    }

    [Fact]
    public void Saves_happen_on_the_timer_not_on_every_tick()
    {
        using var t = new TestDatabase();
        var repository = new CalibrationRepository(t.Db);
        var keeper = new CalibrationKeeper(repository, Quick, TimeSpan.FromMinutes(10));
        keeper.Use("abc", Now);
        Feed(keeper.Learner, 2);
        keeper.SaveIfDue(Now.AddMinutes(9));
        repository.Load("abc").Buckets.ShouldBeEmpty();
        keeper.SaveIfDue(Now.AddMinutes(10));
        repository.Load("abc").Buckets.ShouldNotBeEmpty();
    }

    [Fact]
    public void Reset_forgets_in_memory_and_in_storage()
    {
        using var t = new TestDatabase();
        var repository = new CalibrationRepository(t.Db);
        var keeper = new CalibrationKeeper(repository, Quick, TimeSpan.FromMinutes(10));
        keeper.Use("abc", Now);
        Feed(keeper.Learner, 3);
        keeper.Save(Now);
        keeper.Reset(Now);
        keeper.Learner.TotalSamples.ShouldBe(0);
        repository.Load("abc").Buckets.ShouldBeEmpty();
    }

    [Fact]
    public void Status_counts_battery_samples_and_trusted_buckets()
    {
        using var t = new TestDatabase();
        var keeper = new CalibrationKeeper(new CalibrationRepository(t.Db), Quick, TimeSpan.FromMinutes(10));
        keeper.Use("abc", Now);
        Feed(keeper.Learner, 3);   // every tick at 50 % brightness: one bucket
        keeper.Status().ShouldBe(new PowerLedger.Contracts.CalibrationStatus(3, 2, 1, CalibrationBuckets.Count));
    }

    private static void Feed(CalibrationLearner learner, int ticks)
    {
        for (var i = 0; i < ticks; i++) learner.Observe(Samples.At(Now.AddSeconds(i), batteryW: 20), cpuW: 8, gpuW: 0, displayW: 3);
    }

    private static CalibrationLearner Loaded(CalibrationRepository repository, string hash)
    {
        var learner = new CalibrationLearner(Quick);
        learner.Import(repository.Load(hash));
        return learner;
    }
}
