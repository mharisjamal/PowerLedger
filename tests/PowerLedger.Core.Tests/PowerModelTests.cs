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
