using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class RetentionTests
{
    private static readonly TimeZoneInfo Plus2 = TimeZoneInfo.CreateCustomTimeZone("PL+2", TimeSpan.FromHours(2), "PL+2", "PL+2");

    private static DateTimeOffset Local(int day, int hour, int minute = 0) => new(2026, 9, day, hour, minute, 0, TimeSpan.FromHours(2));

    [Fact]
    public void The_latest_purge_time_is_today_at_three_once_it_has_passed_and_yesterday_before()
    {
        RetentionSchedule.LatestPurgeTime(Local(12, 10), Plus2).ShouldBe(Local(12, 3));
        RetentionSchedule.LatestPurgeTime(Local(12, 2, 59), Plus2).ShouldBe(Local(11, 3));
        RetentionSchedule.LatestPurgeTime(Local(12, 3), Plus2).ShouldBe(Local(12, 3));
    }

    [Fact]
    public void A_purge_is_due_when_it_never_ran_or_last_ran_before_the_latest_three_oclock()
    {
        RetentionSchedule.PurgeDue(Local(12, 10), lastPurge: null, Plus2).ShouldBeTrue();
        RetentionSchedule.PurgeDue(Local(12, 10), Local(12, 3, 1), Plus2).ShouldBeFalse();
        RetentionSchedule.PurgeDue(Local(12, 10), Local(11, 23), Plus2).ShouldBeTrue();   // asleep at 03:00: purge on waking
    }

    [Fact]
    public void A_three_oclock_that_the_clock_skips_becomes_the_first_valid_time_after_it()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2000, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 3, 29),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 4, 0, 0), 10, 25));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Skip3", TimeSpan.Zero, "Skip3", "Skip3", "Skip3 summer", [rule]);
        var noon = new DateTimeOffset(2026, 3, 29, 12, 0, 0, TimeSpan.FromHours(1));
        RetentionSchedule.LatestPurgeTime(noon, zone).ShouldBe(new DateTimeOffset(2026, 3, 29, 4, 0, 0, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void The_vacuum_is_due_a_week_after_the_last()
    {
        RetentionSchedule.VacuumDue(Local(12, 3), null).ShouldBeTrue();
        RetentionSchedule.VacuumDue(Local(12, 3), Local(6, 3)).ShouldBeFalse();
        RetentionSchedule.VacuumDue(Local(12, 3), Local(5, 3)).ShouldBeTrue();
    }

    [Fact]
    public void The_runner_purges_once_and_a_restart_does_not_purge_again_the_same_day()
    {
        using var t = new TestDatabase();
        var raw = new RawSampleRepository(t.Db);
        var now = Local(12, 10);
        raw.InsertBatch([Readings.At(now.AddHours(-50)), Readings.At(now.AddHours(-1))]);

        new RetentionRunner(t.Db, Plus2).RunIfDue(now, new RetentionOptions()).ShouldNotBeNull().RawDeleted.ShouldBe(1);
        raw.Count().ShouldBe(1);
        new RetentionRunner(t.Db, Plus2).RunIfDue(now.AddHours(1), new RetentionOptions()).ShouldBeNull();
    }
}
