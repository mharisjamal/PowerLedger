using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

/// <summary>Whether the built-in panel is showing anything, which a docked laptop with its lid shut is not.</summary>
public class PanelActivityTests
{
    private const string Panel = @"DISPLAY\AUO4199\4&c5a02e3&0&UID8388688_0";
    private const string Dell = @"DISPLAY\DELA0B1\5&2f5a1b&0&UID4353_0";
    private const uint Internal = 0x80000000;
    private const uint DisplayPort = 10;

    private static readonly Dictionary<string, uint> Connections = new() { [Panel] = Internal, [Dell] = DisplayPort };

    [Fact]
    public void A_panel_windows_lists_as_active_is_active()
        => MonitorInventory.InternalPanelActive([(Panel, true), (Dell, true)], Connections).ShouldBe(true);

    [Fact]
    public void A_docked_laptop_s_shut_lid_leaves_its_panel_inactive_beside_the_monitor_it_shows_on()
        => MonitorInventory.InternalPanelActive([(Panel, false), (Dell, true)], Connections).ShouldBe(false);

    [Fact]
    public void A_machine_that_lists_no_built_in_panel_says_nothing_about_one()
    {
        MonitorInventory.InternalPanelActive([(Dell, true)], Connections).ShouldBeNull();
        MonitorInventory.InternalPanelActive([(Panel, false)], new Dictionary<string, uint>()).ShouldBeNull();
    }

    [Fact]
    public void The_panel_s_state_reaches_the_sample()
    {
        var shut = new SampleDraft();
        new DisplaySource(() => new DisplayState(0.6, 1, PanelActive: false), () => true).Contribute(shut);
        shut.PanelInactive.ShouldBeTrue();
        shut.ToSample(DateTimeOffset.UnixEpoch, 1).PanelInactive.ShouldBeTrue();

        foreach (bool? active in new bool?[] { true, null })
        {
            var open = new SampleDraft();
            new DisplaySource(() => new DisplayState(0.6, 1, PanelActive: active), () => true).Contribute(open);
            open.PanelInactive.ShouldBeFalse();
        }
    }
}
