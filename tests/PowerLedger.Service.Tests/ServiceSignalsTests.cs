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
    public void Someone_watches_while_an_app_shows_its_window_or_its_overlay_or_has_subscribed_without_saying()
    {
        var signals = new ServiceSignals(new FakeTimeProvider());
        signals.Watched.ShouldBeFalse();                                 // no App: nobody looks
        signals.Subscribed("a");
        signals.Watched.ShouldBeTrue();                                  // an App that says nothing of its windows may be looking
        signals.ReportWindow("a", 1, visible: false);
        signals.Watched.ShouldBeFalse();                                 // hidden in the tray
        signals.ReportWindow("a", 1, visible: false, overlay: true);
        signals.Watched.ShouldBeTrue();
        signals.WindowShowingIn(1).ShouldBeFalse();                      // the overlay is no window an update must wait for
        signals.ReportWindow("a", 1, visible: true);
        signals.Watched.ShouldBeTrue();
        signals.ForgetClient("a");
        signals.Watched.ShouldBeFalse();

        signals.ReportWindow("b", null, visible: true);                  // a session Windows wouldn't name still has a viewer
        signals.Watched.ShouldBeTrue();
    }

    [Fact]
    public void A_subscriber_that_never_says_counts_as_watching_only_for_a_short_grace()
    {
        // An App whose first status asks met a service still starting never told it its windows: it must not keep the
        // service reading every second for good.
        var clock = new FakeTimeProvider();
        var signals = new ServiceSignals(clock);
        var heard = 0;
        signals.WatchersChanged += () => heard++;
        signals.Subscribed("a");
        signals.Watched.ShouldBeTrue();
        clock.Advance(ServiceSignals.SilentGrace - TimeSpan.FromSeconds(1));
        signals.Watched.ShouldBeTrue();
        clock.Advance(TimeSpan.FromSeconds(1));
        signals.Watched.ShouldBeFalse("it said nothing within the grace");
        heard.ShouldBe(2);                                               // the loop hears it, to slow down

        signals.ReportWindow("a", 1, visible: true);                     // it speaks up later: it counts again
        signals.Watched.ShouldBeTrue();
        signals.ForgetClient("a");
        signals.Watched.ShouldBeFalse();

        signals.Subscribed("b");
        signals.ReportWindow("b", 1, visible: false);
        clock.Advance(ServiceSignals.SilentGrace);
        heard.ShouldBe(6);                                               // an App that said in time: the grace changes nothing
    }

    [Fact]
    public void A_console_run_says_it_installs_no_updates_so_an_app_still_tells_it_its_windows()
    {
        // The App tells its windows only to a service whose status speaks of updates; a console run that said nothing would
        // be left at a silent subscriber's grace, and read every five seconds under an open window.
        var console = ServiceHost.BoardFor(asService: false);
        console.Publish(SharingFakes.Status());
        console.Status.ShouldNotBeNull().Updates.ShouldBe(new Contracts.UpdateStatus(false, null, null, false));

        var installed = ServiceHost.BoardFor(asService: true);
        installed.Publish(SharingFakes.Status());
        installed.Status.ShouldNotBeNull().Updates.ShouldBeNull();      // until its update worker says
    }

    [Fact]
    public void Whoever_follows_the_watchers_hears_each_change_and_only_changes()
    {
        var signals = new ServiceSignals(new FakeTimeProvider());
        var heard = 0;
        signals.WatchersChanged += () => heard++;
        signals.Subscribed("a");
        heard.ShouldBe(1);
        signals.ReportWindow("a", 1, visible: true);
        heard.ShouldBe(1);                                               // still watched
        signals.ReportWindow("a", 1, visible: false);
        heard.ShouldBe(2);
        signals.ReportWindow("a", 1, visible: false);
        signals.Subscribed("b");
        signals.ReportWindow("b", 1, visible: false);
        heard.ShouldBe(4);                                               // b subscribed and was watched until it said
        signals.ForgetClient("a");
        heard.ShouldBe(4);
    }

    [Fact]
    public void The_service_wakes_its_loop_to_pick_its_interval_again_when_the_watchers_change()
    {
        var signals = new ServiceSignals(new FakeTimeProvider());
        var commands = new LoopCommands();
        ServiceHost.FollowWatchers(signals, commands);
        commands.Arrived.WaitOne(0).ShouldBeFalse();
        signals.Subscribed("a");
        commands.Arrived.WaitOne(0).ShouldBeTrue();
        commands.Reader.TryRead(out var command).ShouldBeTrue();
        command.ShouldBeOfType<WatchersChangedCommand>();
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
