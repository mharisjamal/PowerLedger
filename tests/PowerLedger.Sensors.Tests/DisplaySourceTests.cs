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

    private static readonly MonitorFacts Dell = new(
        @"DISPLAY\DELA0B1\5&2F5A1B&0&UID4353", "DELA0B1-7MKZG34", "DEL", "A0B1", "DELL U2723QE", 27, 3840, 2160);

    /// <summary>The display query on a desktop when its monitor classes list the Dell, or when WMI refuses them with
    /// <paramref name="refusal"/>. A refusal that means they have no instances is an answer, one display and no brightness
    /// as when they list the Dell; any other is WMI failing, and throws.</summary>
    private static DisplayState Query(ManagementStatus? refusal)
        => refusal is { } status && !Wmi.MeansNoInstances(status) ? throw new ManagementException(status.ToString()) : new DisplayState(null, 1);

    /// <summary>The monitors the inventory finds when the classes list the Dell, or when WMI refuses them with
    /// <paramref name="refusal"/>: none when the refusal means they have no instances, and no answer otherwise.</summary>
    private static IReadOnlyList<MonitorFacts>? Inventory(ManagementStatus? refusal)
        => refusal is not { } status ? [Dell] : Wmi.MeansNoInstances(status) ? [] : null;

    [Fact]
    public void The_monitors_found_are_handed_on_at_first_and_after_that_only_when_they_change()
    {
        IReadOnlyList<MonitorFacts> attached = [];
        var handed = new List<IReadOnlyList<MonitorFacts>>();
        var source = new DisplaySource(() => new DisplayState(0.5, 1), () => true, refreshEvery: TimeSpan.Zero,
            monitors: () => attached, detected: handed.Add);

        source.Contribute(new SampleDraft());
        handed.ShouldHaveSingleItem().ShouldBeEmpty();
        source.Contribute(new SampleDraft());
        handed.Count.ShouldBe(1);

        attached = [Dell];
        source.Contribute(new SampleDraft());
        attached = [Dell with { }];                     // the same monitor, read afresh
        source.Contribute(new SampleDraft());
        attached = [Dell with { Width = 2560, Height = 1440 }];
        source.Contribute(new SampleDraft());
        attached = [Dell with { Name = "DELL U2723QX" }];
        source.Contribute(new SampleDraft());
        attached = [];
        source.Contribute(new SampleDraft());

        handed.Count.ShouldBe(5);
        handed[1].ShouldBe([Dell]);
        handed[2].ShouldHaveSingleItem().Width.ShouldBe(2560);
        handed[3].ShouldHaveSingleItem().Name.ShouldBe("DELL U2723QX");
        handed[4].ShouldBeEmpty();
    }

    [Fact]
    public void Monitors_wmi_did_not_answer_for_are_not_handed_on_so_the_ones_handed_on_before_stay()
    {
        IReadOnlyList<MonitorFacts>? attached = [Dell];
        var handed = new List<IReadOnlyList<MonitorFacts>>();
        var source = new DisplaySource(() => new DisplayState(0.5, 1), () => true, refreshEvery: TimeSpan.Zero,
            monitors: () => attached, detected: handed.Add);

        source.Contribute(new SampleDraft());
        attached = null;                                // a class the inventory needs didn't answer
        source.Contribute(new SampleDraft());
        attached = [Dell];
        source.Contribute(new SampleDraft());

        handed.ShouldHaveSingleItem().ShouldBe([Dell]);
    }

    [Theory]
    [InlineData(ManagementStatus.NotSupported)]
    [InlineData(ManagementStatus.InvalidClass)]
    public void When_the_last_monitor_goes_and_wmi_refuses_its_classes_as_having_no_instances_none_are_handed_on(ManagementStatus refusal)
    {
        // A desktop whose only monitor was unplugged, or switched off and dropped off the cable. WMI refuses every monitor
        // class, the display query's too, rather than list none, and whoever was told of the monitor would go on counting it.
        ManagementStatus? classes = null;
        var handed = new List<IReadOnlyList<MonitorFacts>>();
        var source = new DisplaySource(() => Query(classes), () => true, refreshEvery: TimeSpan.Zero,
            monitors: () => Inventory(classes), detected: handed.Add);

        source.Contribute(new SampleDraft());
        classes = refusal;
        source.Contribute(new SampleDraft());

        handed.Count.ShouldBe(2);
        handed[0].ShouldBe([Dell]);
        handed[1].ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ManagementStatus.Timedout)]
    [InlineData(ManagementStatus.AccessDenied)]
    [InlineData(ManagementStatus.ProviderFailure)]
    public void When_wmi_refuses_the_monitor_classes_for_any_other_reason_the_monitors_handed_on_before_stay(ManagementStatus refusal)
    {
        ManagementStatus? classes = null;
        var handed = new List<IReadOnlyList<MonitorFacts>>();
        var source = new DisplaySource(() => Query(classes), () => true, refreshEvery: TimeSpan.Zero,
            monitors: () => Inventory(classes), detected: handed.Add);

        source.Contribute(new SampleDraft());
        classes = refusal;
        source.Contribute(new SampleDraft());

        handed.ShouldHaveSingleItem().ShouldBe([Dell]);
    }

    [Fact]
    public void A_fresh_source_hands_on_the_first_monitors_wmi_answers_with_though_an_older_source_handed_on_the_same()
    {
        // The service builds a fresh source whenever it replaces a sensor set, and the board must be right at once.
        IReadOnlyList<MonitorFacts>? attached = [Dell];
        var handed = new List<IReadOnlyList<MonitorFacts>>();
        DisplaySource Source() => new(() => new DisplayState(0.5, 1), () => true, refreshEvery: TimeSpan.Zero,
            monitors: () => attached, detected: handed.Add);

        Source().Contribute(new SampleDraft());
        var replacement = Source();
        attached = null;
        replacement.Contribute(new SampleDraft());
        attached = [Dell];
        replacement.Contribute(new SampleDraft());
        replacement.Contribute(new SampleDraft());

        handed.Count.ShouldBe(2);
        handed[1].ShouldBe([Dell]);
    }

    [Fact]
    public void The_monitors_are_read_only_when_the_display_query_is_due()
    {
        var reads = 0;
        var source = new DisplaySource(() => new DisplayState(0.5, 1), () => true, refreshEvery: TimeSpan.FromMinutes(1),
            monitors: () => { reads++; return []; }, detected: _ => { });

        for (var tick = 0; tick < 10; tick++) source.Contribute(new SampleDraft());
        reads.ShouldBe(1);

        source.Refresh();
        source.Contribute(new SampleDraft());
        reads.ShouldBe(2);
    }

    [Fact]
    public void With_nobody_to_hand_them_to_the_monitors_are_not_read()
    {
        var reads = 0;
        new DisplaySource(() => new DisplayState(0.5, 1), () => true, monitors: () => { reads++; return []; }).Contribute(new SampleDraft());

        reads.ShouldBe(0);
    }

    [Fact]
    public void While_the_display_query_fails_the_monitors_are_not_read_so_a_hiccup_does_not_unplug_them()
    {
        var fail = true;
        var reads = 0;
        var handed = 0;
        var source = new DisplaySource(() => fail ? throw new COMException("RPC server unavailable") : new DisplayState(0.5, 1), () => true,
            refreshEvery: TimeSpan.Zero, monitors: () => { reads++; return [Dell]; }, detected: _ => handed++);

        source.Contribute(new SampleDraft());
        reads.ShouldBe(0);

        fail = false;
        source.Contribute(new SampleDraft());
        reads.ShouldBe(1);
        handed.ShouldBe(1);
    }

    [Fact]
    public void A_handover_that_throws_costs_the_tick_nothing_it_measured_and_is_tried_again_next_time()
    {
        var handed = 0;
        var source = new DisplaySource(() => new DisplayState(0.6, 2), () => true, refreshEvery: TimeSpan.Zero,
            monitors: () => [Dell], detected: _ => { if (++handed == 1) throw new InvalidOperationException("the board is broken"); });

        var draft = new SampleDraft();
        Should.Throw<InvalidOperationException>(() => source.Contribute(draft));
        draft.Brightness.ShouldBe(0.6);
        draft.MonitorCount.ShouldBe(2);

        source.Contribute(new SampleDraft());
        handed.ShouldBe(2);
    }
}
