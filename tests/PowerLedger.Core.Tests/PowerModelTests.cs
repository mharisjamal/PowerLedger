using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class PowerModelTests
{
    private static PowerModel Laptop(
        double? baseline = null, PowerModelOptions? options = null, MachineProfile? profile = null, IMonitorDraw? monitors = null)
        => new(profile ?? MachineProfile.DefaultLaptop, HardwareFacts.LaptopDefaults, options ?? new PowerModelOptions(), new FixedBaseline(baseline), monitors);

    private static PowerModel Desktop(double? baseline = null, MachineProfile? profile = null, IMonitorDraw? monitors = null)
        => new(profile ?? MachineProfile.DefaultDesktop, HardwareFacts.DesktopDefaults, new PowerModelOptions(), new FixedBaseline(baseline), monitors);

    [Fact]
    public void On_battery_the_discharge_rate_is_the_total_and_the_remainder_becomes_rest()
    {
        var r = Laptop().Evaluate(TestData.Laptop(battery: 34.2, onBattery: true));
        r.TotalW.ShouldBe(34.2, 0.001);
        r.Quality.ShouldBe(Quality.Measured);
        r.Components.Cpu.ShouldBe(14.6);
        r.Components.Gpu.ShouldBe(4.1);
        r.Components.Display.ShouldBe(4.2, 0.001);
        r.Components.Unattributed.ShouldBe(34.2 - 14.6 - 4.1 - 4.2, 0.001);
        r.Components.PsuLoss.ShouldBe(0);
        r.Components.Sum.ShouldBe(r.TotalW, 0.001);
    }

    [Fact]
    public void Parts_exceeding_the_measured_rate_give_a_negative_rest_and_keep_sum_equal_to_total()
    {
        var r = Laptop().Evaluate(TestData.Laptop(cpu: 10, gpu: 3, battery: 8.0, onBattery: true));
        r.Quality.ShouldBe(Quality.Measured);
        r.TotalW.ShouldBe(8.0, 0.001);
        r.Components.Unattributed.ShouldBe(8.0 - 10 - 3 - 4.2, 0.001);
        r.Components.Sum.ShouldBe(r.TotalW, 0.001);
    }

    [Fact]
    public void Zero_or_negative_battery_rate_is_not_measured()
    {
        Laptop().Evaluate(TestData.Laptop(battery: 0, onBattery: true)).Quality.ShouldBe(Quality.Estimated);
        Laptop().Evaluate(TestData.Laptop(battery: -20, onBattery: true)).Quality.ShouldBe(Quality.Estimated);
        TestData.Laptop(battery: 12, onBattery: true).HasDischargeRate.ShouldBeTrue();
        TestData.Laptop(battery: 12, onBattery: false).HasDischargeRate.ShouldBeFalse();
    }

    [Fact]
    public void On_battery_a_monitor_with_a_plug_of_its_own_is_added_to_the_measured_rate()
    {
        var r = Laptop(monitors: new FixedDraw(new(OwnPlug: 25, FromPc: 0))).Evaluate(TestData.Laptop(battery: 34.2, onBattery: true));
        r.Quality.ShouldBe(Quality.Measured);
        r.Components.Monitors.ShouldBe(25);
        r.Components.Unattributed.ShouldBe(34.2 - 14.6 - 4.1 - 4.2, 1e-9);
        r.TotalW.ShouldBe(34.2 + 25, 1e-9);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Fact]
    public void On_battery_a_monitor_running_off_the_laptop_is_already_in_the_measured_rate()
    {
        // A 15.6-inch portable monitor on the laptop's USB-C port: the battery delivers what it draws.
        var r = Laptop(monitors: new FixedDraw(new(OwnPlug: 0, FromPc: 6.2))).Evaluate(TestData.Laptop(battery: 34.2, onBattery: true));
        r.Quality.ShouldBe(Quality.Measured);
        r.TotalW.ShouldBe(34.2, 1e-9);
        r.Components.Monitors.ShouldBe(6.2, 1e-9);
        r.Components.Unattributed.ShouldBe(34.2 - 14.6 - 4.1 - 4.2 - 6.2, 1e-9);
        r.Components.PsuLoss.ShouldBe(0);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Fact]
    public void On_battery_with_both_kinds_of_monitor_only_the_one_with_its_own_plug_is_added()
    {
        var r = Laptop(monitors: new FixedDraw(new(OwnPlug: 25, FromPc: 6.2))).Evaluate(TestData.Laptop(battery: 34.2, onBattery: true));
        r.Quality.ShouldBe(Quality.Measured);
        r.TotalW.ShouldBe(34.2 + 25, 1e-9);
        r.Components.Monitors.ShouldBe(25 + 6.2, 1e-9);
        r.Components.Unattributed.ShouldBe(34.2 - 14.6 - 4.1 - 4.2 - 6.2, 1e-9);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Fact]
    public void With_a_learned_baseline_a_monitor_running_off_the_laptop_goes_through_the_adapter_and_one_with_its_own_plug_does_not()
    {
        var r = Laptop(baseline: 9.0, monitors: new FixedDraw(new(OwnPlug: 25, FromPc: 6.2))).Evaluate(TestData.Laptop());
        var throughAdapter = 14.6 + 4.1 + 4.2 + 6.2 + 9.0;
        r.Quality.ShouldBe(Quality.Calibrated);
        r.Components.Monitors.ShouldBe(25 + 6.2, 1e-9);
        r.Components.Unattributed.ShouldBe(9.0);
        r.Components.PsuLoss.ShouldBe(throughAdapter / 0.9 - throughAdapter, 1e-9);
        r.TotalW.ShouldBe(throughAdapter / 0.9 + 25, 1e-9);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Fact]
    public void Without_calibration_a_monitor_running_off_the_laptop_goes_through_the_adapter_and_one_with_its_own_plug_does_not()
    {
        var monitors = new FixedDraw(new(OwnPlug: 25, FromPc: 6.2));
        var r = Laptop(monitors: monitors).Evaluate(TestData.Laptop());
        var throughAdapter = 14.6 + 4.1 + 4.2 + 6.2 + 5.0;
        r.Quality.ShouldBe(Quality.Estimated);
        r.Components.Monitors.ShouldBe(25 + 6.2, 1e-9);
        r.Components.PsuLoss.ShouldBe(throughAdapter / 0.9 - throughAdapter, 1e-9);
        r.TotalW.ShouldBe(throughAdapter / 0.9 + 25, 1e-9);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);

        // On battery with no rate to measure, nothing is lost in an adapter.
        var battery = Laptop(monitors: monitors).Evaluate(TestData.Laptop(onBattery: true, battery: null));
        battery.Quality.ShouldBe(Quality.Estimated);
        battery.Components.PsuLoss.ShouldBe(0, 1e-9);
        battery.TotalW.ShouldBe(throughAdapter + 25, 1e-9);
        battery.Components.Sum.ShouldBe(battery.TotalW, 1e-9);
    }

    [Fact]
    public void The_monitors_are_asked_what_they_draw_with_the_display_as_it_is_and_a_model_given_none_counts_none()
    {
        var monitors = new FixedDraw(on: new(OwnPlug: 25, FromPc: 6.2), off: new(OwnPlug: 0.4, FromPc: 0.2));
        Laptop(monitors: monitors).Evaluate(TestData.Laptop(displayOn: true)).Components.Monitors.ShouldBe(25 + 6.2, 1e-9);
        var off = Laptop(monitors: monitors).Evaluate(TestData.Laptop(displayOn: false));
        off.Components.Monitors.ShouldBe(0.4 + 0.2, 1e-9);
        off.TotalW.ShouldBe((14.6 + 4.1 + 5.0 + 0.2) / 0.9 + 0.4, 1e-9);
        off.Components.Sum.ShouldBe(off.TotalW, 1e-9);
        Laptop().Evaluate(TestData.Laptop()).Components.Monitors.ShouldBe(0);
        Desktop().Evaluate(TestData.Laptop(cpu: 50, gpu: 120, brightness: null)).Components.Monitors.ShouldBe(0);
    }

    [Fact]
    public void The_monitors_are_asked_once_and_the_caller_is_given_what_the_reading_counted_them_at()
    {
        var monitors = new RisingDraw();
        var r = Laptop(monitors: monitors).Evaluate(TestData.Laptop(battery: 34.2, onBattery: true), out var counted);

        monitors.Calls.ShouldBe(1);
        counted.ShouldBe(new MonitorWatts(OwnPlug: 25, FromPc: 6.2));
        r.Components.Monitors.ShouldBe(counted.Total, 1e-9);
        r.Components.Unattributed.ShouldBe(34.2 - 14.6 - 4.1 - 4.2 - counted.FromPc, 1e-9);
        r.TotalW.ShouldBe(34.2 + counted.OwnPlug, 1e-9);

        Laptop().Evaluate(TestData.Laptop(), out var none);
        none.ShouldBe(new MonitorWatts(OwnPlug: 0, FromPc: 0));
    }

    [Fact]
    public void On_ac_without_calibration_the_laptop_uses_the_default_baseline_and_adapter_efficiency()
    {
        var r = Laptop().Evaluate(TestData.Laptop());
        var beforePsu = 14.6 + 4.1 + 4.2 + 5.0;
        r.Quality.ShouldBe(Quality.Estimated);
        r.TotalW.ShouldBe(beforePsu / 0.9, 0.001);
        r.Components.Unattributed.ShouldBe(5.0);
        r.Components.Board.ShouldBe(0);
        r.Components.PsuLoss.ShouldBe(beforePsu / 0.9 - beforePsu, 0.001);
        r.Components.Sum.ShouldBe(r.TotalW, 0.001);
    }

    [Fact]
    public void On_ac_with_a_learned_baseline_the_reading_is_calibrated()
    {
        var r = Laptop(baseline: 9.0).Evaluate(TestData.Laptop());
        r.Quality.ShouldBe(Quality.Calibrated);
        r.Components.Unattributed.ShouldBe(9.0);
        r.Components.Board.ShouldBe(0);
        r.TotalW.ShouldBe((14.6 + 4.1 + 4.2 + 9.0) / 0.9, 0.001);
        r.Components.Sum.ShouldBe(r.TotalW, 0.001);
    }

    [Fact]
    public void Battery_flag_without_a_rate_falls_back_to_estimate_at_full_efficiency()
    {
        var r = Laptop().Evaluate(TestData.Laptop(onBattery: true, battery: null));
        r.Quality.ShouldBe(Quality.Estimated);
        r.TotalW.ShouldBe(14.6 + 4.1 + 4.2 + 5.0, 0.001);
        r.OnBattery.ShouldBeTrue();
    }

    [Fact]
    public void Laptop_extras_count_in_estimated_mode_but_are_inside_the_learned_baseline()
    {
        var profile = MachineProfile.DefaultLaptop with { ExtrasWatts = 10 };
        Laptop(profile: profile).Evaluate(TestData.Laptop()).Components.Extras.ShouldBe(10);
        Laptop(baseline: 9.0, profile: profile).Evaluate(TestData.Laptop()).Components.Extras.ShouldBe(0);
    }

    [Fact]
    public void Desktop_estimate_adds_ram_storage_board_fans_and_divides_by_psu_efficiency()
    {
        var r = Desktop().Evaluate(TestData.Laptop(cpu: 50, gpu: 120, brightness: null));
        var beforePsu = 50 + 120 + 0 + 2 * 2.5 + 2 + (12 + 3 * 1);
        r.Components.Display.ShouldBe(0);
        r.Components.Ram.ShouldBe(5.0);
        r.Components.Storage.ShouldBe(2.0);
        r.Components.Board.ShouldBe(15.0);
        r.TotalW.ShouldBe(beforePsu / 0.85, 0.001);
    }

    [Fact]
    public void An_all_in_one_s_built_in_panel_is_part_of_the_draw_through_its_supply()
    {
        var allInOne = MachineProfile.DefaultDesktop with { DisplayDiagonalInches = 23.8 };
        var r = Desktop(profile: allInOne).Evaluate(TestData.Laptop(cpu: 50, gpu: 120, brightness: null));
        var beforePsu = 50 + 120 + 11.25 + 2 * 2.5 + 2 + (12 + 3 * 1);
        r.Quality.ShouldBe(Quality.Estimated);
        r.Components.Display.ShouldBe(11.25, 0.001);
        r.TotalW.ShouldBe(beforePsu / 0.85, 0.001);
        r.Components.Sum.ShouldBe(r.TotalW, 0.001);
    }

    [Fact]
    public void External_monitors_with_their_own_plugs_are_added_after_the_supply_efficiency_division()
    {
        var r = Desktop(monitors: new FixedDraw(new(OwnPlug: 50, FromPc: 0))).Evaluate(TestData.Laptop(cpu: 50, gpu: 120, brightness: null));
        var pcParts = 50 + 120 + 2 * 2.5 + 2 + (12 + 3 * 1);
        r.Components.Monitors.ShouldBe(50);
        r.Components.PsuLoss.ShouldBe(pcParts / 0.85 - pcParts, 1e-9);
        r.TotalW.ShouldBe(pcParts / 0.85 + 50, 1e-9);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Fact]
    public void On_a_desktop_a_monitor_running_off_the_pc_goes_through_its_supply_and_one_with_its_own_plug_does_not()
    {
        var r = Desktop(monitors: new FixedDraw(new(OwnPlug: 50, FromPc: 7))).Evaluate(TestData.Laptop(cpu: 50, gpu: 120, brightness: null));
        var throughSupply = 50 + 120 + 2 * 2.5 + 2 + (12 + 3 * 1) + 7;
        r.Quality.ShouldBe(Quality.Estimated);
        r.Components.Monitors.ShouldBe(50 + 7, 1e-9);
        r.Components.PsuLoss.ShouldBe(throughSupply / 0.85 - throughSupply, 1e-9);
        r.TotalW.ShouldBe(throughSupply / 0.85 + 50, 1e-9);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Fact]
    public void Desktops_are_always_estimated_even_with_a_battery_rate_or_a_baseline()
    {
        var r = Desktop(baseline: 9.0).Evaluate(TestData.Laptop(cpu: 50, gpu: 120, battery: 200, onBattery: true, brightness: null));
        r.Quality.ShouldBe(Quality.Estimated);
        r.Components.Unattributed.ShouldBe(0);
        r.Components.Board.ShouldBe(15.0);
        r.TotalW.ShouldBe((50 + 120 + 5 + 2 + 15) / 0.85, 0.001);
    }

    [Fact]
    public void Ddr5_sticks_and_hard_drives_use_their_own_constants()
    {
        var profile = MachineProfile.DefaultDesktop with { RamIsDdr5 = true, HddCount = 1 };
        var r = Desktop(profile: profile).Evaluate(TestData.Laptop(cpu: 50, gpu: 120, brightness: null));
        r.Components.Ram.ShouldBe(2 * 1.5, 0.001);
        r.Components.Storage.ShouldBe(2 + 6, 0.001);
    }

    [Fact]
    public void Missing_cpu_sensor_uses_idle_plus_tdp_times_load()
        => Laptop().Evaluate(TestData.Laptop(cpu: null, cpuLoad: 0.5)).Components.Cpu.ShouldBe(2 + (15 - 2) * 0.5, 0.001);

    [Fact]
    public void Cpu_tdp_override_wins_over_inventory_facts()
    {
        var profile = MachineProfile.DefaultLaptop with { CpuTdpOverrideW = 28 };
        Laptop(profile: profile).Evaluate(TestData.Laptop(cpu: null, cpuLoad: 1.0)).Components.Cpu.ShouldBe(28, 0.001);
    }

    [Fact]
    public void Gpu_tdp_override_wins_over_inventory_facts()
    {
        var profile = MachineProfile.DefaultLaptop with { GpuTdpOverrideW = 45 };
        Laptop(profile: profile).Evaluate(TestData.Laptop(gpu: null, gpuLoad: 1.0)).Components.Gpu.ShouldBe(45, 0.001);
    }

    [Fact]
    public void Missing_gpu_sensor_uses_idle_plus_tdp_times_load_and_absent_gpu_is_zero()
    {
        Laptop().Evaluate(TestData.Laptop(gpu: null, gpuLoad: 0.4)).Components.Gpu.ShouldBe(3 + (25 - 3) * 0.4, 0.001);
        Laptop().Evaluate(TestData.Laptop(gpu: null, gpuPresent: false)).Components.Gpu.ShouldBe(0);
    }

    [Fact]
    public void Loads_outside_zero_to_one_are_clamped_and_a_missing_gpu_load_means_idle()
    {
        Laptop().Evaluate(TestData.Laptop(cpu: null, cpuLoad: 1.5)).Components.Cpu.ShouldBe(15, 0.001);
        Laptop().Evaluate(TestData.Laptop(cpu: null, cpuLoad: -0.5)).Components.Cpu.ShouldBe(2, 0.001);
        Laptop().Evaluate(TestData.Laptop(gpu: null, gpuLoad: null)).Components.Gpu.ShouldBe(3, 0.001);
    }

    [Fact]
    public void Non_finite_sensor_values_fall_back_like_missing_ones()
    {
        Laptop().Evaluate(TestData.Laptop(cpu: double.NaN, cpuLoad: 0.5)).Components.Cpu.ShouldBe(2 + (15 - 2) * 0.5, 0.001);
        Laptop().Evaluate(TestData.Laptop(gpu: double.PositiveInfinity, gpuLoad: 0.4)).Components.Gpu.ShouldBe(3 + (25 - 3) * 0.4, 0.001);
        Laptop().Evaluate(TestData.Laptop(cpu: null, cpuLoad: double.NaN)).Components.Cpu.ShouldBe(2, 0.001);
        Laptop().Evaluate(TestData.Laptop(battery: double.NaN, onBattery: true)).Quality.ShouldBe(Quality.Estimated);
    }

    [Fact]
    public void User_is_idle_at_or_beyond_the_threshold()
    {
        Laptop().Evaluate(TestData.Laptop(idleSeconds: 299)).UserIdle.ShouldBeFalse();
        Laptop().Evaluate(TestData.Laptop(idleSeconds: 300)).UserIdle.ShouldBeTrue();
        Laptop(options: new PowerModelOptions(IdleThresholdSeconds: 60)).Evaluate(TestData.Laptop(idleSeconds: 61)).UserIdle.ShouldBeTrue();
    }

    [Fact]
    public void Suspect_and_lock_flags_pass_through()
    {
        var r = Laptop().Evaluate(TestData.Laptop(suspect: true, locked: true));
        r.Suspect.ShouldBeTrue();
        r.SessionLocked.ShouldBeTrue();
    }

    [Fact]
    public void A_bad_profile_or_option_yields_a_zeroed_suspect_reading_not_a_non_finite_one()
    {
        var extras = MachineProfile.DefaultLaptop with { ExtrasWatts = double.NaN };
        var bad = Laptop(profile: extras).Evaluate(TestData.Laptop());
        bad.TotalW.ShouldBe(0);
        bad.Suspect.ShouldBeTrue();
        bad.Components.ShouldBe(Components.Zero);

        var noEfficiency = new PowerModel(MachineProfile.DefaultLaptop, HardwareFacts.LaptopDefaults, new PowerModelOptions(LaptopAdapterEfficiency: 0), new FixedBaseline(null));
        noEfficiency.Evaluate(TestData.Laptop()).TotalW.ShouldBe(0);
    }

    [Fact]
    public void A_monitors_draw_that_is_not_a_number_yields_a_zeroed_suspect_reading_too()
    {
        var ownPlug = Laptop(monitors: new FixedDraw(new(OwnPlug: double.NaN, FromPc: 0))).Evaluate(TestData.Laptop(battery: 34.2, onBattery: true));
        ownPlug.TotalW.ShouldBe(0);
        ownPlug.Suspect.ShouldBeTrue();

        var fromPc = Laptop(monitors: new FixedDraw(new(OwnPlug: 0, FromPc: double.NaN))).Evaluate(TestData.Laptop(battery: 34.2, onBattery: true));
        fromPc.TotalW.ShouldBe(0);
        fromPc.Suspect.ShouldBeTrue();
        Laptop(monitors: new FixedDraw(new(OwnPlug: 0, FromPc: double.NaN))).Evaluate(TestData.Laptop()).Suspect.ShouldBeTrue();
    }

    [Fact]
    public void A_chip_or_package_gpu_reading_counts_the_rest_of_the_card_at_fifteen_percent_more()
    {
        PowerModel.RestOfCardFactor.ShouldBe(1.15);
        Desktop().Evaluate(DesktopTick() with { DGpuScope = GpuPowerScope.Package }).Components.Gpu.ShouldBe(120 * 1.15, 1e-9);
        Desktop().Evaluate(DesktopTick() with { DGpuScope = GpuPowerScope.Board }).Components.Gpu.ShouldBe(120, 1e-9);

        var chip = Desktop().Evaluate(DesktopTick() with { DGpuScope = GpuPowerScope.ChipOnly });
        chip.GpuScope.ShouldBe(GpuPowerScope.ChipOnly);
        chip.Components.Gpu.ShouldBe(120 * 1.15, 1e-9);
        chip.TotalW.ShouldBe((50 + 138 + 5 + 2 + 15) / 0.85, 1e-9);
        chip.Components.Sum.ShouldBe(chip.TotalW, 1e-9);
    }

    [Fact]
    public void A_gpu_the_model_estimates_from_its_load_is_not_scaled_whatever_its_scope()
        => Laptop().Evaluate(TestData.Laptop(gpu: null, gpuLoad: 0.4) with { DGpuScope = GpuPowerScope.ChipOnly })
            .Components.Gpu.ShouldBe(3 + (25 - 3) * 0.4, 1e-9);

    [Fact]
    public void Each_reading_says_where_its_total_came_from()
    {
        Laptop().Evaluate(TestData.Laptop(battery: 34.2, onBattery: true)).TotalSource.ShouldBe(TotalSource.Battery);
        Laptop(baseline: 9.0).Evaluate(TestData.Laptop()).TotalSource.ShouldBe(TotalSource.Model);
        Laptop().Evaluate(TestData.Laptop()).TotalSource.ShouldBe(TotalSource.Model);
        Desktop().Evaluate(DesktopTick()).TotalSource.ShouldBe(TotalSource.Model);
    }

    [Fact]
    public void A_ups_said_to_power_this_pc_gives_the_total_and_the_monitors_with_plugs_of_their_own_are_added()
    {
        var profile = MachineProfile.DefaultDesktop with { UpsLoad = UpsLoad.ThisPc };
        var r = Desktop(profile: profile, monitors: new FixedDraw(new(OwnPlug: 25, FromPc: 7))).Evaluate(WithUps(DesktopTick(), 250));
        var throughSupply = 50 + 120 + 5 + 2 + 15 + 7.0;
        (r.TotalSource, r.Quality).ShouldBe((TotalSource.Ups, Quality.Measured));
        r.TotalW.ShouldBe(250 + 25, 1e-9);
        (r.Components.Cpu, r.Components.Gpu, r.Components.Ram, r.Components.Storage, r.Components.Board).ShouldBe((50.0, 120.0, 5.0, 2.0, 15.0));
        r.Components.Monitors.ShouldBe(25 + 7, 1e-9);
        r.Components.PsuLoss.ShouldBe(throughSupply / 0.85 - throughSupply, 1e-9);
        r.Components.Unattributed.ShouldBe(250 - throughSupply / 0.85, 1e-9);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Fact]
    public void A_ups_said_to_power_this_pc_and_its_monitors_is_the_total_with_every_monitor_inside_it()
    {
        var profile = MachineProfile.DefaultDesktop with { UpsLoad = UpsLoad.ThisPcAndMonitors };
        var r = Desktop(profile: profile, monitors: new FixedDraw(new(OwnPlug: 25, FromPc: 7))).Evaluate(WithUps(DesktopTick(), 250));
        var throughSupply = 50 + 120 + 5 + 2 + 15 + 7.0;
        (r.TotalSource, r.Quality).ShouldBe((TotalSource.Ups, Quality.Measured));
        r.TotalW.ShouldBe(250, 1e-9);
        r.Components.Monitors.ShouldBe(25 + 7, 1e-9);
        r.Components.Unattributed.ShouldBe(250 - throughSupply / 0.85 - 25, 1e-9);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Fact]
    public void Parts_that_come_to_more_than_a_ups_reading_give_a_negative_rest_and_keep_sum_equal_to_total()
    {
        var r = Desktop(profile: MachineProfile.DefaultDesktop with { UpsLoad = UpsLoad.ThisPc }).Evaluate(WithUps(DesktopTick(), 180));
        r.TotalW.ShouldBe(180, 1e-9);
        r.Components.Unattributed.ShouldBe(180 - 192 / 0.85, 1e-9);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Theory]
    [InlineData(UpsPowerSource.ActivePower, Quality.Measured)]
    [InlineData(UpsPowerSource.LoadOfRatedWatts, Quality.Measured)]
    [InlineData(UpsPowerSource.LoadOfRatedVoltAmps, Quality.Estimated)]
    public void A_ups_total_is_measured_unless_it_came_from_the_load_of_its_rated_volt_amperes(UpsPowerSource source, Quality quality)
    {
        var r = Desktop(profile: MachineProfile.DefaultDesktop with { UpsLoad = UpsLoad.ThisPc }).Evaluate(WithUps(DesktopTick(), 250, source));
        (r.TotalSource, r.Quality).ShouldBe((TotalSource.Ups, quality));
        r.TotalW.ShouldBe(250, 1e-9);
    }

    [Theory]
    [InlineData(UpsLoad.NotSaid, 250.0, UpsPowerSource.ActivePower)]
    [InlineData(UpsLoad.More, 250.0, UpsPowerSource.ActivePower)]
    [InlineData(UpsLoad.ThisPc, 250.0, UpsPowerSource.None)]
    [InlineData(UpsLoad.ThisPc, null, UpsPowerSource.ActivePower)]
    [InlineData(UpsLoad.ThisPcAndMonitors, double.NaN, UpsPowerSource.ActivePower)]
    [InlineData(UpsLoad.ThisPcAndMonitors, -5.0, UpsPowerSource.ActivePower)]
    [InlineData(UpsLoad.ThisPc, 0.0, UpsPowerSource.ActivePower)]
    public void A_ups_is_not_used_until_the_user_says_it_powers_this_pc_alone_or_with_its_monitors_and_it_gives_watts(
        UpsLoad load, double? watts, UpsPowerSource source)
    {
        var r = Desktop(profile: MachineProfile.DefaultDesktop with { UpsLoad = load }).Evaluate(WithUps(DesktopTick(), watts, source));
        (r.TotalSource, r.Quality).ShouldBe((TotalSource.Model, Quality.Estimated));
        r.TotalW.ShouldBe(192 / 0.85, 1e-9);
    }

    [Fact]
    public void A_ups_answering_no_watts_at_all_leaves_the_total_to_the_model_and_the_first_watt_above_zero_takes_it()
    {
        // A load comes in whole percents, so a PC drawing less than one percent of a 520 VA rating, about 2.6 W, reads as
        // zero, and so does a sensor that has died. Taken as the total it would turn this 225.9 W desktop into 0 W,
        // measured and unsuspected, with a rest of minus 225.9 W.
        var profile = MachineProfile.DefaultDesktop with { UpsLoad = UpsLoad.ThisPc };
        var zero = Desktop(profile: profile).Evaluate(WithUps(DesktopTick(), 0));
        (zero.TotalSource, zero.Quality).ShouldBe((TotalSource.Model, Quality.Estimated));
        zero.TotalW.ShouldBe(192 / 0.85, 1e-9);
        zero.Components.Unattributed.ShouldBe(0);
        zero.Components.Sum.ShouldBe(zero.TotalW, 1e-9);

        var barely = Desktop(profile: profile).Evaluate(WithUps(DesktopTick(), 0.5));
        (barely.TotalSource, barely.Quality).ShouldBe((TotalSource.Ups, Quality.Measured));
        barely.TotalW.ShouldBe(0.5, 1e-9);
        barely.Components.Sum.ShouldBe(barely.TotalW, 1e-9);
    }

    [Fact]
    public void On_battery_a_laptops_discharge_rate_wins_over_a_ups()
    {
        var profile = MachineProfile.DefaultLaptop with { UpsLoad = UpsLoad.ThisPc };
        var r = Laptop(profile: profile).Evaluate(WithPsu(WithUps(TestData.Laptop(battery: 34.2, onBattery: true), 60), 50));
        (r.TotalSource, r.Quality).ShouldBe((TotalSource.Battery, Quality.Measured));
        r.TotalW.ShouldBe(34.2, 1e-9);
    }

    [Fact]
    public void A_laptop_running_on_its_own_battery_is_not_read_by_a_ups_or_a_power_supply()
    {
        // Unplugged, the laptop draws from its battery alone. Without a discharge rate, as just after it's unplugged, the
        // model estimates it.
        var profile = MachineProfile.DefaultLaptop with { UpsLoad = UpsLoad.ThisPc };
        var r = Laptop(profile: profile).Evaluate(WithWall(WithPsu(WithUps(TestData.Laptop(battery: null, onBattery: true), 3), 3), 3));
        (r.TotalSource, r.Quality).ShouldBe((TotalSource.Model, Quality.Estimated));
        r.TotalW.ShouldBe(14.6 + 4.1 + 4.2 + 5.0, 1e-9);
    }

    [Fact]
    public void A_desktop_its_ups_keeps_running_through_a_power_cut_still_takes_the_ups_reading()
    {
        // Windows shows a UPS on USB as the desktop's battery, and says the desktop runs on it while the mains are out.
        var profile = MachineProfile.DefaultDesktop with { UpsLoad = UpsLoad.ThisPc };
        var r = Desktop(profile: profile).Evaluate(WithUps(TestData.Laptop(cpu: 50, gpu: 120, brightness: null, battery: 240, onBattery: true), 250));
        (r.TotalSource, r.Quality).ShouldBe((TotalSource.Ups, Quality.Measured));
        r.TotalW.ShouldBe(250, 1e-9);
    }

    [Fact]
    public void On_ac_a_laptop_on_a_ups_keeps_its_modelled_parts_and_adapter_loss_and_the_rest_is_what_the_ups_reading_leaves()
    {
        var profile = MachineProfile.DefaultLaptop with { UpsLoad = UpsLoad.ThisPc };
        var r = Laptop(baseline: 9.0, profile: profile, monitors: new FixedDraw(new(OwnPlug: 25, FromPc: 6.2))).Evaluate(WithUps(TestData.Laptop(), 40));
        var throughAdapter = 14.6 + 4.1 + 4.2 + 6.2 + 9.0;
        var adapterLoss = throughAdapter / 0.9 - throughAdapter;
        (r.TotalSource, r.Quality).ShouldBe((TotalSource.Ups, Quality.Measured));
        r.TotalW.ShouldBe(40 + 25, 1e-9);
        r.Components.PsuLoss.ShouldBe(adapterLoss, 1e-9);
        r.Components.Unattributed.ShouldBe(40 - (14.6 + 4.1 + 4.2 + 6.2) - adapterLoss, 1e-9);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Fact]
    public void A_power_supply_gives_the_total_as_its_dc_output_over_its_efficiency_and_the_monitors_with_plugs_of_their_own_are_added()
    {
        // 300 W of a Corsair HX1000i's thousand is three tenths of its rating, where a Gold unit makes 98% of its best.
        var efficiency = 0.90 * 0.98;
        var profile = MachineProfile.DefaultDesktop with { PsuTier = PsuTier.Gold };
        var r = Desktop(profile: profile, monitors: new FixedDraw(new(OwnPlug: 25, FromPc: 7))).Evaluate(WithPsu(DesktopTick(), 300));
        (r.TotalSource, r.Quality).ShouldBe((TotalSource.PowerSupply, Quality.Measured));
        r.TotalW.ShouldBe(300 / efficiency + 25, 1e-9);
        (r.Components.Cpu, r.Components.Gpu, r.Components.Ram, r.Components.Storage, r.Components.Board).ShouldBe((50.0, 120.0, 5.0, 2.0, 15.0));
        r.Components.Monitors.ShouldBe(25 + 7, 1e-9);
        r.Components.PsuLoss.ShouldBe(300 / efficiency - 300, 1e-9);
        r.Components.Unattributed.ShouldBe(300 - (50 + 120 + 5 + 2 + 15 + 7), 1e-9);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Fact]
    public void A_desktop_supply_is_read_off_its_tier_s_curve_at_the_load_it_is_really_carrying()
    {
        // A tier is one number only at half load, which is what 80 PLUS certifies and where a supply is at its best. Below
        // that a real unit falls away steeply, so a big supply idling is understated by a fifth and more by the flat figure.
        var half = Desktop().Evaluate(WithPsu(DesktopTick(), 500));
        half.TotalW.ShouldBe(500 / 0.85, 1e-9);
        half.Components.Sum.ShouldBe(half.TotalW, 1e-9);

        // Two percent of the rating: a Bronze unit makes three quarters of its best there, not all of it.
        var idle = Desktop().Evaluate(WithPsu(DesktopTick(), 20));
        idle.TotalW.ShouldBe(20 / (0.85 * 0.75), 1e-9);
        idle.TotalW.ShouldBeGreaterThan(20 / 0.85 * 1.3);
        idle.Components.PsuLoss.ShouldBe(20 / (0.85 * 0.75) - 20, 1e-9);
        idle.Components.Sum.ShouldBe(idle.TotalW, 1e-9);
    }

    [Fact]
    public void A_supply_whose_name_says_no_rating_is_read_at_its_tier_s_flat_figure()
    {
        // With no rating there is no load to read the curve at, so the certified half-load figure is all there is to go on.
        var r = Desktop().Evaluate(WithPsu(DesktopTick(), 20, name: "Corsair"));
        r.TotalW.ShouldBe(20 / 0.85, 1e-9);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Fact]
    public void A_supply_that_reports_what_it_draws_from_the_wall_is_the_total_and_is_divided_by_nothing()
    {
        var r = Desktop(monitors: new FixedDraw(new(OwnPlug: 25, FromPc: 7))).Evaluate(WithWall(DesktopTick(), 300));
        (r.TotalSource, r.Quality).ShouldBe((TotalSource.PowerSupply, Quality.Measured));
        r.TotalW.ShouldBe(300 + 25, 1e-9);
        r.Components.PsuLoss.ShouldBe(0);            // the supply's own loss is already inside what it draws from the wall
        (r.Components.Cpu, r.Components.Gpu, r.Components.Ram, r.Components.Storage, r.Components.Board).ShouldBe((50.0, 120.0, 5.0, 2.0, 15.0));
        r.Components.Monitors.ShouldBe(25 + 7, 1e-9);
        r.Components.Unattributed.ShouldBe(300 - (50 + 120 + 5 + 2 + 15 + 7), 1e-9);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Fact]
    public void A_wall_reading_is_no_more_divided_on_a_laptop_than_on_a_desktop()
    {
        var r = Laptop().Evaluate(WithWall(TestData.Laptop(), 45));
        (r.TotalSource, r.Quality).ShouldBe((TotalSource.PowerSupply, Quality.Measured));
        r.TotalW.ShouldBe(45, 1e-9);
        r.Components.PsuLoss.ShouldBe(0);
        r.Components.Unattributed.ShouldBe(45 - (14.6 + 4.1 + 4.2), 1e-9);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Theory]
    [InlineData(false, 300.0)]
    [InlineData(true, null)]
    [InlineData(true, double.NaN)]
    [InlineData(true, -1.0)]
    [InlineData(true, 0.0)]
    public void A_wall_reading_is_not_used_while_reading_the_supply_is_off_or_when_it_gives_no_watts(bool read, double? watts)
    {
        var r = Desktop(profile: MachineProfile.DefaultDesktop with { ReadPowerSupply = read }).Evaluate(WithWall(DesktopTick(), watts));
        (r.TotalSource, r.Quality).ShouldBe((TotalSource.Model, Quality.Estimated));
        r.TotalW.ShouldBe(192 / 0.85, 1e-9);
    }

    [Fact]
    public void A_wall_reading_is_taken_before_a_dc_one_and_a_ups_before_both()
    {
        // A supply reports one figure or the other and never both; the wall one wins because it assumes no efficiency.
        var both = WithWall(WithPsu(DesktopTick(), 200), 260);
        var supply = Desktop().Evaluate(both);
        supply.TotalSource.ShouldBe(TotalSource.PowerSupply);
        supply.TotalW.ShouldBe(260, 1e-9);
        supply.Components.Sum.ShouldBe(supply.TotalW, 1e-9);

        var ups = Desktop(profile: MachineProfile.DefaultDesktop with { UpsLoad = UpsLoad.ThisPc }).Evaluate(WithUps(both, 250));
        ups.TotalSource.ShouldBe(TotalSource.Ups);
        ups.TotalW.ShouldBe(250, 1e-9);
        ups.Components.Sum.ShouldBe(ups.TotalW, 1e-9);
    }

    [Theory]
    [InlineData(false, 300.0)]
    [InlineData(true, null)]
    [InlineData(true, double.PositiveInfinity)]
    [InlineData(true, -1.0)]
    [InlineData(true, 0.0)]
    public void A_power_supply_is_not_used_while_reading_it_is_off_or_when_it_gives_no_watts(bool read, double? watts)
    {
        var r = Desktop(profile: MachineProfile.DefaultDesktop with { ReadPowerSupply = read }).Evaluate(WithPsu(DesktopTick(), watts));
        (r.TotalSource, r.Quality).ShouldBe((TotalSource.Model, Quality.Estimated));
        r.TotalW.ShouldBe(192 / 0.85, 1e-9);
    }

    [Fact]
    public void A_power_supply_answering_no_watts_leaves_the_total_to_the_model_and_the_first_watt_above_zero_takes_it()
    {
        // A running machine draws something, so nought watts is a sensor that has died rather than a reading of the rails.
        var zero = Desktop().Evaluate(WithPsu(DesktopTick(), 0));
        (zero.TotalSource, zero.Quality).ShouldBe((TotalSource.Model, Quality.Estimated));
        zero.TotalW.ShouldBe(192 / 0.85, 1e-9);
        zero.Components.Sum.ShouldBe(zero.TotalW, 1e-9);

        // Half a watt of a thousand is the very bottom of the curve, where a Bronze unit makes under a third of its best.
        var barely = Desktop().Evaluate(WithPsu(DesktopTick(), 0.5));
        (barely.TotalSource, barely.Quality).ShouldBe((TotalSource.PowerSupply, Quality.Measured));
        barely.TotalW.ShouldBe(0.5 / (0.85 * 0.315), 1e-9);
        barely.Components.Sum.ShouldBe(barely.TotalW, 1e-9);
    }

    [Fact]
    public void A_laptop_read_by_a_power_supply_divides_by_its_adapter_efficiency_rather_than_a_desktop_supply_tier()
    {
        // The 80 PLUS tier in the profile describes a desktop's supply, and so does its curve; a laptop is fed by an
        // adapter, and the same tick's adapter loss is worked out with the adapter's efficiency, so the total uses it too.
        // Read off the curve instead, 45 W of the name's thousand would come to 60.5 W.
        MachineProfile.DefaultLaptop.PsuTier.ShouldBe(PsuTier.Bronze);
        var r = Laptop().Evaluate(WithPsu(TestData.Laptop(), 45));
        (r.TotalSource, r.Quality).ShouldBe((TotalSource.PowerSupply, Quality.Measured));
        r.TotalW.ShouldBe(45 / 0.90, 1e-9);
        r.Components.PsuLoss.ShouldBe(45 / 0.90 - 45, 1e-9);
        r.Components.Unattributed.ShouldBe(45 - (14.6 + 4.1 + 4.2), 1e-9);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);

        var stingy = Laptop(options: new PowerModelOptions(LaptopAdapterEfficiency: 0.80)).Evaluate(WithPsu(TestData.Laptop(), 45));
        stingy.TotalW.ShouldBe(45 / 0.80, 1e-9);
        stingy.Components.Sum.ShouldBe(stingy.TotalW, 1e-9);
    }

    [Fact]
    public void A_desktop_read_by_a_power_supply_still_divides_by_the_tier_of_its_own_supply()
    {
        var efficiency = 0.94 * 0.98;                    // a Titanium unit at three tenths of its rating
        var titanium = MachineProfile.DefaultDesktop with { PsuTier = PsuTier.Titanium };
        var r = Desktop(profile: titanium, monitors: new FixedDraw(new(OwnPlug: 25, FromPc: 0))).Evaluate(WithPsu(DesktopTick(), 300));
        r.TotalW.ShouldBe(300 / efficiency + 25, 1e-9);
        r.Components.PsuLoss.ShouldBe(300 / efficiency - 300, 1e-9);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Fact]
    public void A_ups_said_to_power_this_pc_wins_over_the_power_supply_and_one_that_powers_more_leaves_the_total_to_it()
    {
        var both = WithPsu(WithUps(DesktopTick(), 250), 200);
        var ups = Desktop(profile: MachineProfile.DefaultDesktop with { UpsLoad = UpsLoad.ThisPc }).Evaluate(both);
        ups.TotalSource.ShouldBe(TotalSource.Ups);
        ups.TotalW.ShouldBe(250, 1e-9);

        var more = Desktop(profile: MachineProfile.DefaultDesktop with { UpsLoad = UpsLoad.More }).Evaluate(both);
        more.TotalSource.ShouldBe(TotalSource.PowerSupply);
        more.TotalW.ShouldBe(200 / (0.85 * 0.97), 1e-9);     // a fifth of the rating, where a Bronze unit makes 97% of its best
    }

    /// <summary>A desktop tick: the CPU drawing 50 W and the GPU 120 W, with no built-in panel. The default desktop adds 5 W
    /// of memory, 2 W of storage and 15 W of board and fans, so 192 W go through its Bronze supply.</summary>
    private static Sample DesktopTick() => TestData.Laptop(cpu: 50, gpu: 120, brightness: null);

    private static Sample WithUps(Sample s, double? watts, UpsPowerSource source = UpsPowerSource.ActivePower)
        => s with { UpsOutputW = watts, UpsSource = source, UpsName = "APC Back-UPS ES 850G2" };

    /// <summary>A tick with a supply's DC output on it. The name is a real one, and says the supply is rated for 1000 W,
    /// which is the rating the efficiency curve is read at.</summary>
    private static Sample WithPsu(Sample s, double? watts, string? name = "Corsair HX1000i")
        => s with { PsuOutputW = watts, PsuName = name };

    /// <summary>A tick with what a supply says it draws from the wall on it, as a Corsair's own total is read.</summary>
    private static Sample WithWall(Sample s, double? watts) => s with { PsuWallW = watts, PsuName = "Corsair HX1000i" };

    private sealed class FixedDraw(MonitorWatts on, MonitorWatts off = default) : IMonitorDraw
    {
        public MonitorWatts Watts(bool displayOn) => displayOn ? on : off;
    }

    /// <summary>Draws more each time it is asked, as the monitors would seem to if detection changed them between two asks.</summary>
    private sealed class RisingDraw : IMonitorDraw
    {
        public int Calls { get; private set; }

        public MonitorWatts Watts(bool displayOn)
        {
            Calls++;
            return new MonitorWatts(OwnPlug: 25 * Calls, FromPc: 6.2 * Calls);
        }
    }
}
