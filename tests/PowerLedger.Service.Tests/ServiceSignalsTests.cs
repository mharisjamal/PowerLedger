using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class ServiceSignalsTests
{
    [Fact]
    public void The_display_counts_as_on_and_the_session_as_unlocked_until_told_otherwise()
    {
        var signals = new ServiceSignals(new FakeTimeProvider());
        signals.DisplayOn.ShouldBeTrue();
        signals.SessionLocked.ShouldBeFalse();
        signals.DisplayOn = false;
        signals.SessionLocked = true;
        signals.DisplayOn.ShouldBeFalse();
        signals.SessionLocked.ShouldBeTrue();
    }

    [Fact]
    public void With_no_app_reporting_idle_time_is_unknown()
        => new ServiceSignals(new FakeTimeProvider()).UserIdleSeconds().ShouldBeNull();

    [Fact]
    public void A_window_shows_in_a_session_while_any_app_there_says_so()
    {
        var signals = new ServiceSignals(new FakeTimeProvider());
        signals.WindowShowingIn(1).ShouldBeFalse();                    // no App at all: nothing is showing
        signals.ReportWindow("a", 1, visible: true);
        signals.ReportWindow("b", 2, visible: false);
        signals.WindowShowingIn(1).ShouldBeTrue();
        signals.WindowShowingIn(2).ShouldBeFalse();
        signals.ReportWindow("a", 1, visible: false);
        signals.WindowShowingIn(1).ShouldBeFalse();
    }

    [Fact]
    public void A_client_that_leaves_takes_its_window_with_it()
    {
        var signals = new ServiceSignals(new FakeTimeProvider());
        signals.ReportWindow("a", 1, visible: true);
        signals.ForgetClient("a");
        signals.WindowShowingIn(1).ShouldBeFalse();
    }

    [Fact]
    public void A_window_in_a_session_windows_wouldnt_name_counts_nowhere()
    {
        var signals = new ServiceSignals(new FakeTimeProvider());
        signals.ReportWindow("a", null, visible: true);
        signals.WindowShowingIn(1).ShouldBeFalse();
    }

    [Fact]
    public void A_report_keeps_ageing_until_the_next_one_arrives()
    {
        var clock = new FakeTimeProvider();
        var signals = new ServiceSignals(clock);
        signals.ReportIdle("a", 10);
        clock.Advance(TimeSpan.FromSeconds(4));
        signals.UserIdleSeconds().ShouldNotBeNull().ShouldBe(14, 1e-9);
        signals.ReportIdle("a", 0);
        signals.UserIdleSeconds().ShouldNotBeNull().ShouldBe(0, 1e-9);
    }

    [Fact]
    public void The_most_recently_active_user_wins()
    {
        var signals = new ServiceSignals(new FakeTimeProvider());
        signals.ReportIdle("a", 600);
        signals.ReportIdle("b", 5);
        signals.UserIdleSeconds().ShouldNotBeNull().ShouldBe(5, 1e-9);
    }

    [Fact]
    public void A_report_from_an_app_that_went_quiet_is_ignored()
    {
        var clock = new FakeTimeProvider();
        var signals = new ServiceSignals(clock);
        signals.ReportIdle("a", 10);
        clock.Advance(ServiceSignals.ReportLifetime + TimeSpan.FromSeconds(1));
        signals.UserIdleSeconds().ShouldBeNull();
    }

    [Fact]
    public void A_client_that_disconnects_is_forgotten_and_nonsense_is_ignored()
    {
        var signals = new ServiceSignals(new FakeTimeProvider());
        signals.ReportIdle("a", 10);
        signals.ForgetClient("a");
        signals.ReportIdle("b", -1);
        signals.ReportIdle("c", double.NaN);
        signals.UserIdleSeconds().ShouldBeNull();
    }
}
