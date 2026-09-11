using PowerLedger.Contracts;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class SessionTrackerTests
{
    private static readonly DateTimeOffset Now = Samples.T0;

    [Theory]
    [InlineData(2, SessionReason.Boot)]
    [InlineData(600, SessionReason.ServiceStart)]
    public void A_clean_start_is_a_boot_when_windows_has_only_just_started(int uptimeMinutes, SessionReason expected)
    {
        using var t = new TestDatabase();
        var tracker = new SessionTracker(new SessionRepository(t.Db));
        tracker.Start(Now, TimeSpan.FromMinutes(uptimeMinutes), lastTick: null).ShouldBe(expected);
        tracker.Current.ShouldNotBeNull();
    }

    [Fact]
    public void A_session_left_open_is_closed_at_its_last_tick_and_the_new_one_is_crash_recovered()
    {
        using var t = new TestDatabase();
        var sessions = new SessionRepository(t.Db);
        sessions.Open(SessionReason.Boot, Now.AddHours(-2));
        var tracker = new SessionTracker(sessions);

        tracker.Start(Now, TimeSpan.FromHours(3), lastTick: Now.AddMinutes(-7)).ShouldBe(SessionReason.CrashRecovered);

        var all = sessions.List(Now.AddDays(-1), Now.AddDays(1));
        all[0].End.ShouldBe(Now.AddMinutes(-7));
        all[0].EndReason.ShouldBe(SessionReason.CrashRecovered);
        all[1].Reason.ShouldBe(SessionReason.CrashRecovered);
        all[1].End.ShouldBeNull();
    }

    [Fact]
    public void A_crashed_session_with_no_ticks_ends_where_it_began()
    {
        using var t = new TestDatabase();
        var sessions = new SessionRepository(t.Db);
        sessions.Open(SessionReason.Boot, Now.AddHours(-2));
        new SessionTracker(sessions).Start(Now, TimeSpan.FromHours(3), lastTick: Now.AddHours(-5));
        sessions.List(Now.AddDays(-1), Now.AddDays(1))[0].End.ShouldBe(Now.AddHours(-2));
    }

    [Fact]
    public void Suspend_then_resume_then_stop_writes_two_sessions()
    {
        using var t = new TestDatabase();
        var sessions = new SessionRepository(t.Db);
        var tracker = new SessionTracker(sessions);
        tracker.Start(Now, TimeSpan.FromHours(3), null);
        tracker.End(Now.AddMinutes(10), SessionReason.Suspend);
        tracker.End(Now.AddMinutes(11), SessionReason.Shutdown);    // nothing open: a no-op
        tracker.Resume(Now.AddMinutes(30));
        tracker.Resume(Now.AddMinutes(31));                         // already open: a no-op
        tracker.End(Now.AddMinutes(40), SessionReason.ServiceStop);

        sessions.List(Now.AddDays(-1), Now.AddDays(1))
            .Select(s => (s.Reason, s.EndReason, s.Start, s.End))
            .ShouldBe(new (SessionReason, SessionReason?, DateTimeOffset, DateTimeOffset?)[]
            {
                (SessionReason.ServiceStart, SessionReason.Suspend, Now, Now.AddMinutes(10)),
                (SessionReason.Resume, SessionReason.ServiceStop, Now.AddMinutes(30), Now.AddMinutes(40)),
            });
    }
}
