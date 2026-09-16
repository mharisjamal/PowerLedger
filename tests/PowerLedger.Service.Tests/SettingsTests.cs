using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class SettingsTests
{
    [Fact]
    public void The_first_run_takes_the_profile_from_detection()
    {
        var (settings, changed) = ProfilePolicy.Apply(stored: null, storedHash: null, Facts.Laptop(ramSticks: 2));
        changed.ShouldBeTrue();
        settings.Profile.Chassis.ShouldBe(ChassisKind.Laptop);
        settings.Profile.RamSticks.ShouldBe(2);
        settings.Profile.DisplayDiagonalInches.ShouldBe(15.3);
        settings.SampleIntervalSeconds.ShouldBe(ServiceSettings.Default.SampleIntervalSeconds);
    }

    [Fact]
    public void A_desktop_starts_from_the_desktop_defaults()
    {
        var (settings, _) = ProfilePolicy.Apply(null, null, Facts.Desktop());
        settings.Profile.FanCount.ShouldBe(MachineProfile.DefaultDesktop.FanCount);
        settings.Profile.SsdCount.ShouldBe(2);
        settings.Profile.DisplayDiagonalInches.ShouldBe(0);
    }

    [Fact]
    public void An_all_in_one_s_detected_panel_reaches_its_profile()
    {
        var (settings, _) = ProfilePolicy.Apply(null, null, Facts.Desktop() with { DisplayDiagonalInches = 23.8 });
        settings.Profile.Chassis.ShouldBe(ChassisKind.Desktop);
        settings.Profile.DisplayDiagonalInches.ShouldBe(23.8);
    }

    [Fact]
    public void A_tower_first_taken_for_a_laptop_leaves_the_laptop_s_panel_behind()
    {
        // Its UPS passed for its own battery; detected again, the desktop enclosure changes the hash.
        var stored = ServiceSettings.Default with { Profile = MachineProfile.DefaultLaptop with { ExtrasWatts = 6 } };
        var (settings, changed) = ProfilePolicy.Apply(stored, "laptop-hash", Facts.Desktop());
        changed.ShouldBeTrue();
        settings.Profile.Chassis.ShouldBe(ChassisKind.Desktop);
        settings.Profile.DisplayDiagonalInches.ShouldBe(0);
        settings.Profile.ExtrasWatts.ShouldBe(6);
    }

    [Fact]
    public void The_same_machine_keeps_the_users_corrections()
    {
        var facts = Facts.Laptop(ramSticks: 2);
        var corrected = ServiceSettings.Default with { Profile = MachineProfile.DefaultLaptop with { RamSticks = 4, ExtrasWatts = 6 } };
        var (settings, changed) = ProfilePolicy.Apply(corrected, facts.Hash, facts);
        changed.ShouldBeFalse();
        settings.ShouldBeSameAs(corrected);
    }

    [Fact]
    public void A_different_machine_takes_detection_but_keeps_what_the_user_chose()
    {
        var stored = ServiceSettings.Default with { SampleIntervalSeconds = 2, Profile = MachineProfile.DefaultLaptop with { RamSticks = 2, ExtrasWatts = 6 } };
        var (settings, changed) = ProfilePolicy.Apply(stored, Facts.Laptop(ramSticks: 2).Hash, Facts.Laptop(ramSticks: 4));
        changed.ShouldBeTrue();
        settings.Profile.RamSticks.ShouldBe(4);
        settings.Profile.ExtrasWatts.ShouldBe(6);
        settings.SampleIntervalSeconds.ShouldBe(2);
    }

    private const string DellKey = "DELA0B1-7MKZG34";
    private const string LgKey = @"DISPLAY\GSM5B08\7&1A2B&0&UID4354";

    [Fact]
    public void Monitors_the_user_counted_are_carried_over_counted_at_the_figure_they_typed()
    {
        var old = MachineProfile.DefaultDesktop with { ExternalMonitors = 2, IncludeMonitors = true, MonitorWatts = 30, ExtrasWatts = 6 };
        ProfilePolicy.HasMonitorsToCarryOver(old).ShouldBeTrue();

        var carried = ProfilePolicy.CarryOverMonitors(old, [DellKey, LgKey]);

        carried.Monitors.ShouldBe(
        [
            new MonitorChoice { Key = DellKey, Counted = true, Watts = 30 },
            new MonitorChoice { Key = LgKey, Counted = true, Watts = 30 },
        ]);
        carried.ShouldBe(old with { ExternalMonitors = 0, IncludeMonitors = false, Monitors = carried.Monitors });
        ProfilePolicy.HasMonitorsToCarryOver(carried).ShouldBeFalse();
    }

    [Fact]
    public void Monitors_counted_at_the_old_default_figure_take_the_figure_worked_out_for_each()
    {
        var old = MachineProfile.DefaultDesktop with { ExternalMonitors = 1, IncludeMonitors = true, MonitorWatts = 25 };

        ProfilePolicy.CarryOverMonitors(old, [DellKey]).Monitors.ShouldBe([new MonitorChoice { Key = DellKey, Counted = true, Watts = null }]);
    }

    [Fact]
    public void Choosing_to_count_monitors_is_carried_over_even_without_a_count()
    {
        var old = MachineProfile.DefaultLaptop with { ExternalMonitors = 0, IncludeMonitors = true, MonitorWatts = 18 };
        ProfilePolicy.HasMonitorsToCarryOver(old).ShouldBeTrue();

        ProfilePolicy.CarryOverMonitors(old, [DellKey]).Monitors.ShouldBe([new MonitorChoice { Key = DellKey, Counted = true, Watts = 18 }]);
    }

    [Fact]
    public void Monitors_the_user_had_but_did_not_count_are_carried_over_not_counted()
    {
        var old = MachineProfile.DefaultDesktop with { ExternalMonitors = 2, IncludeMonitors = false, MonitorWatts = 30 };
        ProfilePolicy.HasMonitorsToCarryOver(old).ShouldBeTrue();

        var carried = ProfilePolicy.CarryOverMonitors(old, [DellKey, LgKey]);

        carried.Monitors.ShouldBe([new MonitorChoice { Key = DellKey, Counted = false }, new MonitorChoice { Key = LgKey, Counted = false }]);
        carried.ExternalMonitors.ShouldBe(0);
        carried.IncludeMonitors.ShouldBeFalse();
    }

    [Fact]
    public void A_profile_without_old_monitor_settings_or_with_monitor_choices_already_has_nothing_to_carry_over()
    {
        ProfilePolicy.HasMonitorsToCarryOver(MachineProfile.DefaultLaptop).ShouldBeFalse();
        ProfilePolicy.HasMonitorsToCarryOver(MachineProfile.DefaultDesktop).ShouldBeFalse();
        ProfilePolicy.HasMonitorsToCarryOver(MachineProfile.DefaultDesktop with { MonitorWatts = 30 }).ShouldBeFalse();
        ProfilePolicy.HasMonitorsToCarryOver(MachineProfile.DefaultDesktop with
        {
            ExternalMonitors = 2, IncludeMonitors = true, Monitors = [new MonitorChoice { Key = DellKey, Counted = false }],
        }).ShouldBeFalse();
    }

    [Fact]
    public void The_store_round_trips_settings_and_the_profile_hash()
    {
        using var t = new TestDatabase();
        var store = new SettingsStore(new SettingsRepository(t.Db));
        store.Load().ShouldBeNull();
        store.ProfileHash().ShouldBeNull();
        var settings = ServiceSettings.Default with { IdleThresholdSeconds = 600 };
        store.Save(settings);
        store.SaveProfileHash("abc");
        store.Load().ShouldBe(settings);
        store.ProfileHash().ShouldBe("abc");
    }

    [Fact]
    public void Unreadable_stored_settings_count_as_none()
    {
        using var t = new TestDatabase();
        new SettingsRepository(t.Db).Set(SettingsStore.SettingsKey, "{ not json");
        new SettingsStore(new SettingsRepository(t.Db)).Load().ShouldBeNull();
    }

    [Fact]
    public void The_model_falls_back_to_the_chassis_default_for_a_processor_the_table_does_not_know()
    {
        var model = ModelFactory.Build(
            ServiceSettings.Default with { Profile = MachineProfile.DefaultDesktop }, Facts.Desktop(), new CalibrationLearner(), NoMonitors.Instance);
        model.Evaluate(Samples.At(Samples.T0, cpuW: null, cpuLoad: 1)).Components.Cpu.ShouldBe(HardwareFacts.DesktopDefaults.CpuTdpW);
    }

    [Fact]
    public void The_model_uses_the_bundled_tdp_for_a_known_processor()
    {
        var model = ModelFactory.Build(ServiceSettings.Default, Facts.Laptop(), new CalibrationLearner(), NoMonitors.Instance);
        model.Evaluate(Samples.At(Samples.T0, cpuW: null, cpuLoad: 1)).Components.Cpu.ShouldBe(28);
    }

    [Fact]
    public void The_model_counts_what_the_monitors_it_is_given_draw()
    {
        var model = ModelFactory.Build(
            ServiceSettings.Default with { Profile = MachineProfile.DefaultDesktop }, Facts.Desktop(), new CalibrationLearner(), new FixedDraw(27.5));
        model.Evaluate(Samples.At(Samples.T0)).Components.Monitors.ShouldBe(27.5);
    }

    private sealed class FixedDraw(double watts) : IMonitorDraw
    {
        public double Watts(bool displayOn) => watts;
    }
}
