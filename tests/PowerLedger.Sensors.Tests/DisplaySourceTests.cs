using System.Management;
using System.Runtime.InteropServices;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class DisplaySourceTests
{
    [Fact]
    public void Brightness_and_monitor_count_reach_the_draft()
    {
        var draft = new SampleDraft();
        new DisplaySource(() => new DisplayState(Brightness: 0.6, MonitorCount: 2), () => true).Contribute(draft);

        draft.Brightness.ShouldBe(0.6);
        draft.MonitorCount.ShouldBe(2);
        draft.DisplayOn.ShouldBeTrue();
    }

    [Fact]
    public void A_dark_screen_keeps_its_brightness_because_the_panel_is_still_set_that_way()
    {
        var draft = new SampleDraft();
        new DisplaySource(() => new DisplayState(0.6, 1), () => false).Contribute(draft);

        draft.DisplayOn.ShouldBeFalse();
        draft.Brightness.ShouldBe(0.6);
    }

    [Fact]
    public void A_desktop_with_no_brightness_control_reports_no_brightness()
    {
        var draft = new SampleDraft();
        new DisplaySource(() => new DisplayState(null, 1), () => true).Contribute(draft);

        draft.Brightness.ShouldBeNull();
        draft.MonitorCount.ShouldBe(1);
    }

    [Fact]
    public void The_slow_query_runs_once_and_is_reused_until_it_goes_stale()
    {
        var calls = 0;
        var source = new DisplaySource(() => { calls++; return new DisplayState(0.5, 1); }, () => true, refreshEvery: TimeSpan.FromMinutes(5));

        for (var tick = 0; tick < 10; tick++) source.Contribute(new SampleDraft());

        calls.ShouldBe(1);
        source.Refresh();
        source.Contribute(new SampleDraft());
        calls.ShouldBe(2);
    }

    [Theory]
    [InlineData("query refused")]
    [InlineData("service restarting")]
    public void The_last_good_state_survives_a_query_that_throws(string failure)
    {
        var fail = false;
        Exception Failure() => failure == "query refused" ? new ManagementException("not found") : new COMException("RPC server unavailable");
        var source = new DisplaySource(
            () => fail ? throw Failure() : new DisplayState(0.7, 1),
            () => true,
            refreshEvery: TimeSpan.Zero);

        source.Contribute(new SampleDraft());
        fail = true;

        var draft = new SampleDraft();
        source.Contribute(draft);
        draft.Brightness.ShouldBe(0.7);
    }

    [Fact]
    public void A_failing_query_is_retried_soon_but_not_every_tick()
    {
        var calls = 0;
        var source = new DisplaySource(() => { calls++; throw new COMException("RPC server unavailable"); }, () => true, refreshEvery: TimeSpan.FromMinutes(5));

        var draft = new SampleDraft();
        for (var tick = 0; tick < 10; tick++) source.Contribute(draft);

        calls.ShouldBe(1);
        draft.MonitorCount.ShouldBe(1);                 // until WMI answers, one display is assumed
        draft.Brightness.ShouldBeNull();
    }
}
