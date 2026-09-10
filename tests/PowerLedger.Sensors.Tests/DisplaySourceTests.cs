using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class DisplaySourceTests
{
    [Fact]
    public void Brightness_and_monitor_count_reach_the_draft()
    {
        var draft = new SampleDraft();
        new DisplaySource(() => new DisplayState(Brightness: 0.6, MonitorCount: 2, DiagonalInches: 15.6), () => true).Contribute(draft);

        draft.Brightness.ShouldBe(0.6);
        draft.MonitorCount.ShouldBe(2);
        draft.DisplayOn.ShouldBeTrue();
    }

    [Fact]
    public void A_dark_screen_keeps_its_brightness_because_the_panel_is_still_set_that_way()
    {
        var draft = new SampleDraft();
        new DisplaySource(() => new DisplayState(0.6, 1, 15.6), () => false).Contribute(draft);

        draft.DisplayOn.ShouldBeFalse();
        draft.Brightness.ShouldBe(0.6);
    }

    [Fact]
    public void A_desktop_with_no_brightness_control_reports_no_brightness()
    {
        var draft = new SampleDraft();
        new DisplaySource(() => new DisplayState(null, 1, 0), () => true).Contribute(draft);

        draft.Brightness.ShouldBeNull();
        draft.MonitorCount.ShouldBe(1);
    }

    [Fact]
    public void The_slow_query_runs_once_and_is_reused_until_it_goes_stale()
    {
        var calls = 0;
        var source = new DisplaySource(() => { calls++; return new DisplayState(0.5, 1, 14); }, () => true, refreshEvery: TimeSpan.FromMinutes(5));

        for (var tick = 0; tick < 10; tick++) source.Contribute(new SampleDraft());

        calls.ShouldBe(1);
        source.Refresh();
        source.Contribute(new SampleDraft());
        calls.ShouldBe(2);
    }

    [Fact]
    public void The_last_good_state_survives_a_query_that_throws()
    {
        var fail = false;
        var source = new DisplaySource(
            () => fail ? throw new InvalidOperationException("wmi down") : new DisplayState(0.7, 1, 15.6),
            () => true,
            refreshEvery: TimeSpan.Zero);

        source.Contribute(new SampleDraft());
        fail = true;

        var draft = new SampleDraft();
        source.Contribute(draft);
        draft.Brightness.ShouldBe(0.7);
    }

    [Fact]
    public void The_panel_diagonal_is_offered_to_the_inventory_rather_than_the_tick()
    {
        var source = new DisplaySource(() => new DisplayState(0.5, 1, 17.3), () => true);
        source.Contribute(new SampleDraft());
        source.DiagonalInches.ShouldBe(17.3);
    }
}
