using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class PowerModelTests
{
    /// <summary>An uncalibrated laptop's rest at the test laptop's 14.6 W CPU.</summary>
    private const double Rest = PowerModel.LaptopBaselineW + (PowerModel.LaptopRestPerCpuWatt * 14.6);

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
        r.Components.PsuLoss.ShouldBe(Adapter(throughAdapter) - throughAdapter, 1e-9);
        r.TotalW.ShouldBe(Adapter(throughAdapter) + 25, 1e-9);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Fact]
    public void Without_calibration_a_monitor_running_off_the_laptop_goes_through_the_adapter_and_one_with_its_own_plug_does_not()
    {
        var monitors = new FixedDraw(new(OwnPlug: 25, FromPc: 6.2));
        var r = Laptop(monitors: monitors).Evaluate(TestData.Laptop());
        var throughAdapter = 14.6 + 4.1 + 4.2 + 6.2 + Rest;
        r.Quality.ShouldBe(Quality.Estimated);
        r.Components.Monitors.ShouldBe(25 + 6.2, 1e-9);
        r.Components.PsuLoss.ShouldBe(Adapter(throughAdapter) - throughAdapter, 1e-9);
        r.TotalW.ShouldBe(Adapter(throughAdapter) + 25, 1e-9);
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
        off.TotalW.ShouldBe(Adapter(14.6 + 4.1 + Rest + 0.2) + 0.4, 1e-9);
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
        var beforePsu = 14.6 + 4.1 + 4.2 + Rest;
        r.Quality.ShouldBe(Quality.Estimated);
        r.TotalW.ShouldBe(Adapter(beforePsu), 0.001);
        r.Components.Unattributed.ShouldBe(Rest, 1e-9);
        r.Components.Board.ShouldBe(0);
        r.Components.PsuLoss.ShouldBe(Adapter(beforePsu) - beforePsu, 0.001);
        r.Components.Sum.ShouldBe(r.TotalW, 0.001);
    }

    [Fact]
    public void An_idle_laptops_adapter_loses_more_than_a_flat_ninety_percent_says()
    {
        // 4 W of DC is a sixteenth of a 65 W adapter, where it makes about 79%, and it draws 0.15 W besides.
        var idle = Laptop().Evaluate(TestData.Laptop(cpu: 1, gpu: 0, gpuPresent: false, brightness: 0));
        var dc = 1 + 1.5 + (PowerModel.LaptopBaselineW + (PowerModel.LaptopRestPerCpuWatt * 1));
        idle.TotalW.ShouldBe(AdapterEfficiency.Wall(dc), 1e-9);
        idle.TotalW.ShouldBeGreaterThan(dc / 0.9 * 1.08);
        Laptop(options: new PowerModelOptions(LaptopAdapterEfficiency: 0.9)).Evaluate(TestData.Laptop(cpu: 1, gpu: 0, gpuPresent: false, brightness: 0))
            .TotalW.ShouldBe(dc / 0.9, 1e-9);
    }

    [Fact]
    public void An_idle_desktop_is_read_on_its_supply_s_curve_at_650_W_and_reads_more_than_the_flat_tier()
    {
        // The review's case: 60 W of DC on a 650 W Bronze unit is 3.7 W more at the wall than 85% says.
        var idle = Desktop().Evaluate(TestData.Laptop(cpu: 20, gpu: 18, brightness: null));
        const double dc = 20 + 18 + 5 + 2 + 15;
        idle.TotalW.ShouldBe(Bronze650(dc), 1e-9);
        (idle.TotalW - (dc / 0.85)).ShouldBeInRange(3, 4.5);
    }

    [Fact]
    public void An_nzxt_e_supply_is_read_as_the_gold_unit_it_is_whatever_the_profile_says()
    {
        MachineProfile.DefaultDesktop.PsuTier.ShouldBe(PsuTier.Bronze);
        var r = Desktop().Evaluate(WithPsu(DesktopTick(), 200, name: "NZXT E650"));
        r.TotalW.ShouldBe(200 / PsuEfficiency.At(PsuTier.Gold, 200 / 650.0), 1e-9);

        // A tick it gave nothing on is modelled through the same supply.
        var later = Desktop();
        later.Evaluate(WithPsu(DesktopTick(), 200, name: "NZXT E650"));
        later.Evaluate(WithPsu(DesktopTick(), null, name: null)).TotalW.ShouldBe(192 / PsuEfficiency.At(PsuTier.Gold, 192 / 650.0), 1e-9);
    }

    [Fact]
    public void On_ac_with_a_learned_baseline_the_reading_is_calibrated()
    {
        var r = Laptop(baseline: 9.0).Evaluate(TestData.Laptop());
        r.Quality.ShouldBe(Quality.Calibrated);
        r.Components.Unattributed.ShouldBe(9.0);
        r.Components.Board.ShouldBe(0);
        r.TotalW.ShouldBe(Adapter(14.6 + 4.1 + 4.2 + 9.0), 0.001);
        r.Components.Sum.ShouldBe(r.TotalW, 0.001);
    }

    [Fact]
    public void Battery_flag_without_a_rate_falls_back_to_estimate_at_full_efficiency()
    {
        var r = Laptop().Evaluate(TestData.Laptop(onBattery: true, battery: null));
        r.Quality.ShouldBe(Quality.Estimated);
        r.TotalW.ShouldBe(14.6 + 4.1 + 4.2 + Rest, 0.001);
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
        r.TotalW.ShouldBe(Bronze650(beforePsu), 0.001);
    }

    [Fact]
    public void An_all_in_one_s_built_in_panel_is_part_of_the_draw_through_its_supply()
    {
        var allInOne = MachineProfile.DefaultDesktop with { DisplayDiagonalInches = 23.8 };
        var r = Desktop(profile: allInOne).Evaluate(TestData.Laptop(cpu: 50, gpu: 120, brightness: null));
        var beforePsu = 50 + 120 + 11.25 + 2 * 2.5 + 2 + (12 + 3 * 1);
        r.Quality.ShouldBe(Quality.Estimated);
        r.Components.Display.ShouldBe(11.25, 0.001);
        r.TotalW.ShouldBe(Bronze650(beforePsu), 0.001);
        r.Components.Sum.ShouldBe(r.TotalW, 0.001);
    }

    [Fact]
    public void External_monitors_with_their_own_plugs_are_added_after_the_supply_efficiency_division()
    {
        var r = Desktop(monitors: new FixedDraw(new(OwnPlug: 50, FromPc: 0))).Evaluate(TestData.Laptop(cpu: 50, gpu: 120, brightness: null));
        var pcParts = 50 + 120 + 2 * 2.5 + 2 + (12 + 3 * 1);
        r.Components.Monitors.ShouldBe(50);
        r.Components.PsuLoss.ShouldBe(Bronze650(pcParts) - pcParts, 1e-9);
        r.TotalW.ShouldBe(Bronze650(pcParts) + 50, 1e-9);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Fact]
    public void On_a_desktop_a_monitor_running_off_the_pc_goes_through_its_supply_and_one_with_its_own_plug_does_not()
    {
        var r = Desktop(monitors: new FixedDraw(new(OwnPlug: 50, FromPc: 7))).Evaluate(TestData.Laptop(cpu: 50, gpu: 120, brightness: null));
        var throughSupply = 50 + 120 + 2 * 2.5 + 2 + (12 + 3 * 1) + 7;
        r.Quality.ShouldBe(Quality.Estimated);
        r.Components.Monitors.ShouldBe(50 + 7, 1e-9);
        r.Components.PsuLoss.ShouldBe(Bronze650(throughSupply) - throughSupply, 1e-9);
        r.TotalW.ShouldBe(Bronze650(throughSupply) + 50, 1e-9);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Fact]
    public void Desktops_are_always_estimated_even_with_a_battery_rate_or_a_baseline()
    {
        var r = Desktop(baseline: 9.0).Evaluate(TestData.Laptop(cpu: 50, gpu: 120, battery: 200, onBattery: true, brightness: null));
        r.Quality.ShouldBe(Quality.Estimated);
        r.Components.Unattributed.ShouldBe(0);
        r.Components.Board.ShouldBe(15.0);
        r.TotalW.ShouldBe(Bronze650(50 + 120 + 5 + 2 + 15), 0.001);
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
        Laptop().Evaluate(TestData.Laptop(idleSeconds: 299, cpuLoad: 0.02, gpuLoad: 0)).UserIdle.ShouldBeFalse();
        Laptop().Evaluate(TestData.Laptop(idleSeconds: 300, cpuLoad: 0.02, gpuLoad: 0)).UserIdle.ShouldBeTrue();
        Laptop(options: new PowerModelOptions(IdleThresholdSeconds: 60)).Evaluate(TestData.Laptop(idleSeconds: 61, cpuLoad: 0.02, gpuLoad: 0)).UserIdle.ShouldBeTrue();
    }

    /// <summary>Report finding 5: a PC nobody is using but that is busy (a build, an agent, a download) is in use, not idle
    /// waste. Idle is the user away and the processor under 10% and the graphics card under 5%.</summary>
    [Fact]
    public void A_PC_left_alone_but_busy_is_not_idle()
    {
        Laptop().Evaluate(TestData.Laptop(idleSeconds: 600, cpuLoad: 0.10, gpuLoad: 0)).UserIdle.ShouldBeFalse();
        Laptop().Evaluate(TestData.Laptop(idleSeconds: 600, cpuLoad: 0.02, gpuLoad: 0.05)).UserIdle.ShouldBeFalse();
        Laptop().Evaluate(TestData.Laptop(idleSeconds: 600, cpuLoad: 0.099, gpuLoad: 0.049)).UserIdle.ShouldBeTrue();
    }

    /// <summary>A graphics load nobody could read counts as idle, as the model counts its watts; a processor load that is not
    /// a number counts as none, as everywhere else in the model.</summary>
    [Fact]
    public void Loads_not_known_do_not_keep_a_PC_from_idling()
    {
        Laptop().Evaluate(TestData.Laptop(idleSeconds: 600, cpuLoad: 0.02, gpuLoad: null)).UserIdle.ShouldBeTrue();
        Laptop().Evaluate(TestData.Laptop(idleSeconds: 600, cpuLoad: double.NaN, gpuLoad: 0)).UserIdle.ShouldBeTrue();
    }

    /// <summary>With the App closed nothing reports the user's idle time, which comes in as 0: the PC counts as in use, so
    /// idle waste is never overstated.</summary>
    [Fact]
    public void Idle_time_nobody_reported_is_not_idle()
        => Laptop().Evaluate(TestData.Laptop(idleSeconds: 0, cpuLoad: 0, gpuLoad: 0)).UserIdle.ShouldBeFalse();

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
        chip.TotalW.ShouldBe(Bronze650(50 + 138 + 5 + 2 + 15), 1e-9);
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
        r.Components.PsuLoss.ShouldBe(Bronze650(throughSupply) - throughSupply, 1e-9);
        r.Components.Unattributed.ShouldBe(250 - Bronze650(throughSupply), 1e-9);
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
        r.Components.Unattributed.ShouldBe(250 - Bronze650(throughSupply) - 25, 1e-9);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Fact]
    public void Parts_that_come_to_more_than_a_ups_reading_give_a_negative_rest_and_keep_sum_equal_to_total()
    {
        var r = Desktop(profile: MachineProfile.DefaultDesktop with { UpsLoad = UpsLoad.ThisPc }).Evaluate(WithUps(DesktopTick(), 180));
        r.TotalW.ShouldBe(180, 1e-9);
        r.Components.Unattributed.ShouldBe(180 - Bronze650(192), 1e-9);
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
        r.TotalW.ShouldBe(Bronze650(192), 1e-9);
    }

    [Fact]
    public void A_ups_answering_no_watts_at_all_or_too_few_to_be_the_pc_leaves_the_total_to_the_model()
    {
        // A load comes in whole percents, so a PC drawing less than one percent of a 520 VA rating, about 2.6 W, reads as
        // zero, and so does a sensor that has died. Taken as the total it would turn this 225.9 W desktop into 0 W,
        // measured and unsuspected, with a rest of minus 225.9 W.
        var profile = MachineProfile.DefaultDesktop with { UpsLoad = UpsLoad.ThisPc };
        var zero = Desktop(profile: profile).Evaluate(WithUps(DesktopTick(), 0));
        (zero.TotalSource, zero.Quality).ShouldBe((TotalSource.Model, Quality.Estimated));
        zero.TotalW.ShouldBe(Bronze650(192), 1e-9);
        zero.Components.Unattributed.ShouldBe(0);
        zero.Components.Sum.ShouldBe(zero.TotalW, 1e-9);

        // Plan AC: a reading under a third of what was measured inside the machine, its CPU's 50 W and its GPU's 120 W, or
        // under a watt, is no reading of it either: half a watt, or the 1e-7 W of a HID report whose unit exponent was
        // misread, would book a 226 W desktop as nothing, measured. A third and up is taken.
        foreach (var watts in new[] { 0.5, 1e-7, 0.29 * 170 })
        {
            var tiny = Desktop(profile: profile).Evaluate(WithUps(DesktopTick(), watts));
            (tiny.TotalSource, tiny.Quality).ShouldBe((TotalSource.Model, Quality.Estimated));
            tiny.TotalW.ShouldBe(Bronze650(192), 1e-9);
        }
        var low = Desktop(profile: profile).Evaluate(WithUps(DesktopTick(), 0.31 * 170));
        (low.TotalSource, low.Quality).ShouldBe((TotalSource.Ups, Quality.Measured));
        low.TotalW.ShouldBe(0.31 * 170, 1e-9);
        low.Components.Sum.ShouldBe(low.TotalW, 1e-9);

        // A GPU worked out from its load says nothing of what the machine must draw, so it sets no such floor.
        var estimatedGpu = DesktopTick() with { DGpuW = null, DGpuLoad = 1 };
        Desktop(profile: profile).Evaluate(WithUps(estimatedGpu, 0.31 * 50)).TotalSource.ShouldBe(TotalSource.Ups);
    }

    [Fact]
    public void On_battery_a_stale_rate_far_under_the_parts_is_no_reading_and_the_model_stands_in()
    {
        // The owner's data: single ticks flagged on battery under heavy load with a stale 0.01 W rate, booked as measured.
        var r = Laptop().Evaluate(TestData.Laptop(cpu: 25, battery: 0.01, onBattery: true));
        (r.TotalSource, r.Quality).ShouldBe((TotalSource.Model, Quality.Estimated));
        r.TotalW.ShouldBe(25 + 4.1 + 4.2 + PowerModel.LaptopBaselineW + (PowerModel.LaptopRestPerCpuWatt * 25), 1e-9);
        r.Components.PsuLoss.ShouldBe(0);
    }

    [Fact]
    public void A_battery_rate_on_a_suspect_tick_is_no_reading_and_the_model_stands_in()
    {
        // The validator flags the seconds after an AC change, when the rate Windows gives is from before it.
        var r = Laptop(baseline: 9).Evaluate(TestData.Laptop(battery: 34.2, onBattery: true, suspect: true));
        (r.TotalSource, r.Quality).ShouldBe((TotalSource.Model, Quality.Calibrated));
        r.TotalW.ShouldBe(14.6 + 4.1 + 4.2 + 9, 1e-9);
        r.Suspect.ShouldBeTrue();
    }

    [Fact]
    public void A_laptop_charging_on_a_ups_has_the_charge_taken_off_the_ups_reading_at_the_wall()
    {
        // The UPS feeds the adapter, which feeds the laptop and its battery: 30 W of charge is in the UPS's 75 W, and more
        // than 30 W of it at the wall, since the adapter loses some of what it passes on.
        var profile = MachineProfile.DefaultLaptop with { UpsLoad = UpsLoad.ThisPc };
        var tick = WithUps(TestData.Laptop() with { BatteryChargeW = 30 }, 75);
        var r = Laptop(profile: profile).Evaluate(tick);
        var dc = 14.6 + 4.1 + 4.2 + Rest;
        var chargeAtWall = Adapter(dc + 30) - Adapter(dc);
        chargeAtWall.ShouldBeGreaterThan(30);
        (r.TotalSource, r.Quality).ShouldBe((TotalSource.Ups, Quality.Measured));
        r.TotalW.ShouldBe(75 - chargeAtWall, 1e-9);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);

        // Taking the charge off never leaves less than the parts the model knows of.
        var parts = 14.6 + 4.1 + 4.2 + (Adapter(dc) - dc);
        var floored = Laptop(profile: profile).Evaluate(WithUps(TestData.Laptop() with { BatteryChargeW = 60 }, 60));
        floored.TotalW.ShouldBe(parts, 1e-9);
        floored.Components.Unattributed.ShouldBe(0, 1e-9);
    }

    [Fact]
    public void A_desktop_has_nothing_taken_off_for_a_charge_since_any_battery_it_has_is_a_ups()
    {
        var profile = MachineProfile.DefaultDesktop with { UpsLoad = UpsLoad.ThisPc };
        Desktop(profile: profile).Evaluate(WithUps(DesktopTick() with { BatteryChargeW = 40 }, 250)).TotalW.ShouldBe(250, 1e-9);
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
        r.TotalW.ShouldBe(14.6 + 4.1 + 4.2 + Rest, 1e-9);
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
        var adapterLoss = Adapter(throughAdapter) - throughAdapter;
        (r.TotalSource, r.Quality).ShouldBe((TotalSource.Ups, Quality.Measured));
        r.TotalW.ShouldBe(40 + 25, 1e-9);
        r.Components.PsuLoss.ShouldBe(adapterLoss, 1e-9);
        r.Components.Unattributed.ShouldBe(40 - (14.6 + 4.1 + 4.2 + 6.2) - adapterLoss, 1e-9);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Fact]
    public void A_power_supply_gives_the_total_as_its_dc_output_over_its_efficiency_and_the_monitors_with_plugs_of_their_own_are_added()
    {
        // 300 W of a Corsair HX1000i's thousand is three tenths of its rating, where a unit makes 98% of its best; an HXi is
        // Platinum, whatever tier the profile says.
        var efficiency = 0.92 * 0.98;
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
        half.TotalW.ShouldBe(500 / 0.92, 1e-9);                      // an HXi is Platinum
        half.Components.Sum.ShouldBe(half.TotalW, 1e-9);

        // Two percent of the rating: a unit makes three quarters of its best there, not all of it.
        var idle = Desktop().Evaluate(WithPsu(IdleDesktopTick(), 20));
        idle.TotalW.ShouldBe(20 / (0.92 * 0.75), 1e-9);
        idle.TotalW.ShouldBeGreaterThan(20 / 0.92 * 1.3);
        idle.Components.PsuLoss.ShouldBe(20 / (0.92 * 0.75) - 20, 1e-9);
        idle.Components.Sum.ShouldBe(idle.TotalW, 1e-9);
    }

    [Fact]
    public void A_supply_whose_name_says_no_rating_is_read_on_its_tier_s_curve_at_650_W()
    {
        // With no rating to read the load against, the supply is taken for the 650 W most desktops ship with.
        var r = Desktop().Evaluate(WithPsu(IdleDesktopTick(), 20, name: "Corsair"));
        r.TotalW.ShouldBe(Bronze650(20), 1e-9);
        r.TotalW.ShouldBeGreaterThan(20 / 0.85 * 1.2);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Fact]
    public void A_supply_that_reports_what_it_draws_from_the_wall_is_the_total_and_is_divided_by_nothing()
    {
        var r = Desktop(monitors: new FixedDraw(new(OwnPlug: 25, FromPc: 7))).Evaluate(WithWall(DesktopTick(), 300));
        (r.TotalSource, r.Quality).ShouldBe((TotalSource.PowerSupplyWall, Quality.Measured));
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
        (r.TotalSource, r.Quality).ShouldBe((TotalSource.PowerSupplyWall, Quality.Measured));
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
        r.TotalW.ShouldBe(Hx1000i(192), 1e-9);                     // the supply's own name says what it is
    }

    [Fact]
    public void A_wall_reading_is_taken_before_a_dc_one_and_a_ups_before_both()
    {
        // A supply reports one figure or the other and never both; the wall one wins because it assumes no efficiency.
        var both = WithWall(WithPsu(DesktopTick(), 200), 260);
        var supply = Desktop().Evaluate(both);
        supply.TotalSource.ShouldBe(TotalSource.PowerSupplyWall);
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
        r.TotalW.ShouldBe(Hx1000i(192), 1e-9);                     // the supply's own name says what it is
    }

    [Fact]
    public void A_power_supply_answering_no_watts_or_too_few_to_be_the_pc_leaves_the_total_to_the_model()
    {
        // A running machine draws something, so nought watts is a sensor that has died rather than a reading of the rails.
        var zero = Desktop().Evaluate(WithPsu(DesktopTick(), 0));
        (zero.TotalSource, zero.Quality).ShouldBe((TotalSource.Model, Quality.Estimated));
        zero.TotalW.ShouldBe(Hx1000i(192), 1e-9);
        zero.Components.Sum.ShouldBe(zero.TotalW, 1e-9);

        // Half a watt from a machine the model puts at 200 W and more is a misread too (Plan AC), wherever it is read: on
        // the rails or at the wall.
        foreach (var tick in new[] { WithPsu(DesktopTick(), 0.5), WithWall(DesktopTick(), 0.5) })
        {
            var barely = Desktop().Evaluate(tick);
            (barely.TotalSource, barely.Quality).ShouldBe((TotalSource.Model, Quality.Estimated));
            barely.TotalW.ShouldBe(Hx1000i(192), 1e-9);
        }
    }

    [Fact]
    public void A_laptop_read_by_a_power_supply_divides_by_its_adapter_efficiency_rather_than_a_desktop_supply_tier()
    {
        // The 80 PLUS tier in the profile describes a desktop's supply, and so does its curve; a laptop is fed by an
        // adapter, and the same tick's adapter loss is worked out on the adapter's curve, so the total uses it too.
        // Read off the supply's curve instead, 45 W of the name's thousand would come to about 58 W.
        MachineProfile.DefaultLaptop.PsuTier.ShouldBe(PsuTier.Bronze);
        var r = Laptop().Evaluate(WithPsu(TestData.Laptop(), 45));
        (r.TotalSource, r.Quality).ShouldBe((TotalSource.PowerSupply, Quality.Measured));
        r.TotalW.ShouldBe(Adapter(45), 1e-9);
        r.Components.PsuLoss.ShouldBe(Adapter(45) - 45, 1e-9);
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
        var named = WithPsu(DesktopTick(), 300, name: "Thermaltake Toughpower DPS G 1000W");   // its name gives no tier
        var r = Desktop(profile: titanium, monitors: new FixedDraw(new(OwnPlug: 25, FromPc: 0))).Evaluate(named);
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
        more.TotalW.ShouldBe(200 / (0.92 * 0.97), 1e-9);     // a fifth of the rating, where a unit makes 97% of its best
    }

    [Fact]
    public void On_ac_a_whole_platform_rail_is_the_measured_total_over_the_adapters_efficiency()
    {
        var r = Laptop().Evaluate(TestData.Laptop() with { PlatformW = 27 });
        (r.TotalSource, r.Quality).ShouldBe((TotalSource.PlatformMeter, Quality.Measured));
        r.TotalW.ShouldBe(Adapter(27), 1e-9);
        r.Components.PsuLoss.ShouldBe(Adapter(27) - 27, 1e-9);
        r.Components.Cpu.ShouldBe(14.6);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Fact]
    public void On_battery_the_discharge_rate_still_comes_first_and_the_platform_rail_stands_in_where_there_is_none()
    {
        var battery = Laptop().Evaluate(TestData.Laptop(battery: 30, onBattery: true) with { PlatformW = 27 });
        (battery.TotalSource, battery.Quality).ShouldBe((TotalSource.Battery, Quality.Measured));
        battery.TotalW.ShouldBe(30, 1e-9);

        // A battery reporting relative units gives no rate, and the rail is then the measured total, with no adapter.
        var rail = Laptop().Evaluate(TestData.Laptop(battery: null, onBattery: true) with { PlatformW = 27 });
        (rail.TotalSource, rail.Quality).ShouldBe((TotalSource.PlatformMeter, Quality.Measured));
        rail.TotalW.ShouldBe(27, 1e-9);
        rail.Components.PsuLoss.ShouldBe(0);
        rail.Components.Sum.ShouldBe(rail.TotalW, 1e-9);
    }

    [Fact]
    public void A_platform_rail_holds_the_monitors_running_off_the_pc_and_only_one_with_its_own_plug_is_added()
    {
        var r = Laptop(monitors: new FixedDraw(new(OwnPlug: 25, FromPc: 6))).Evaluate(TestData.Laptop() with { PlatformW = 27 });
        r.TotalW.ShouldBe(Adapter(27) + 25, 1e-9);
        r.Components.Monitors.ShouldBe(31);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Fact]
    public void A_platform_rail_reading_no_watts_leaves_the_total_to_the_model()
    {
        var model = Laptop().Evaluate(TestData.Laptop());
        var zero = Laptop().Evaluate(TestData.Laptop() with { PlatformW = 0 });
        (zero.TotalSource, zero.Quality).ShouldBe((TotalSource.Model, Quality.Estimated));
        zero.TotalW.ShouldBe(model.TotalW, 1e-9);
        Laptop().Evaluate(TestData.Laptop() with { PlatformW = double.NaN }).TotalSource.ShouldBe(TotalSource.Model);
    }

    [Fact]
    public void A_ups_said_to_power_this_pc_is_read_at_the_wall_and_comes_before_the_platform_rail()
    {
        var profile = MachineProfile.DefaultLaptop with { UpsLoad = UpsLoad.ThisPc };
        var r = Laptop(profile: profile).Evaluate(WithUps(TestData.Laptop(), 40) with { PlatformW = 27 });
        r.TotalSource.ShouldBe(TotalSource.Ups);
        r.TotalW.ShouldBe(40, 1e-9);
    }

    [Fact]
    public void On_ac_a_discharging_battery_is_a_floor_for_the_modelled_total()
    {
        // Under a load the adapter can't carry, the battery makes up the difference, so the machine draws at least that.
        var model = Laptop().Evaluate(TestData.Laptop());
        var heavy = Laptop().Evaluate(TestData.Laptop(battery: 80, onBattery: false));
        heavy.TotalW.ShouldBe(80, 1e-9);
        // A floor is not a measurement of the whole: the adapter's share is still unknown, so the quality stays the model's.
        (heavy.TotalSource, heavy.Quality).ShouldBe((TotalSource.Model, Quality.Estimated));
        heavy.Components.Sum.ShouldBe(heavy.TotalW, 1e-9);

        var light = Laptop().Evaluate(TestData.Laptop(battery: 5, onBattery: false));
        light.TotalW.ShouldBe(model.TotalW, 1e-9);
    }

    [Fact]
    public void The_floor_also_holds_under_a_platform_rail_and_counts_an_own_plug_monitor_on_top()
    {
        var rail = Laptop().Evaluate(TestData.Laptop(battery: 60, onBattery: false) with { PlatformW = 27 });
        rail.TotalW.ShouldBe(60, 1e-9);
        rail.TotalSource.ShouldBe(TotalSource.PlatformMeter);

        var plugged = Laptop(monitors: new FixedDraw(new(OwnPlug: 25, FromPc: 0))).Evaluate(TestData.Laptop(battery: 80, onBattery: false));
        plugged.TotalW.ShouldBe(80 + 25, 1e-9);
        plugged.Components.Sum.ShouldBe(plugged.TotalW, 1e-9);
    }

    [Fact]
    public void A_desktop_takes_no_floor_from_a_battery_since_any_it_has_is_a_ups()
    {
        var model = Desktop().Evaluate(DesktopTick());
        Desktop().Evaluate(DesktopTick() with { BatteryRateW = 900 }).TotalW.ShouldBe(model.TotalW, 1e-9);
    }

    [Fact]
    public void Only_a_discharge_on_ac_is_a_floor()
    {
        TestData.Laptop(battery: 12, onBattery: false).DischargeOnAcW.ShouldBe(12);
        TestData.Laptop(battery: 12, onBattery: true).DischargeOnAcW.ShouldBeNull();
        TestData.Laptop(battery: null, onBattery: false).DischargeOnAcW.ShouldBeNull();
        TestData.Laptop(battery: 0, onBattery: false).DischargeOnAcW.ShouldBeNull();
    }

    /// <summary>A desktop tick: the CPU drawing 50 W and the GPU 120 W, with no built-in panel. The default desktop adds 5 W
    /// of memory, 2 W of storage and 15 W of board and fans, so 192 W go through its Bronze supply.</summary>
    private static Sample DesktopTick() => TestData.Laptop(cpu: 50, gpu: 120, brightness: null);

    /// <summary>An idle desktop tick, its CPU at 5 W and its GPU at 3 W.</summary>
    private static Sample IdleDesktopTick() => TestData.Laptop(cpu: 5, gpu: 3, brightness: null);

    private static Sample WithUps(Sample s, double? watts, UpsPowerSource source = UpsPowerSource.ActivePower)
        => s with { UpsOutputW = watts, UpsSource = source, UpsName = "APC Back-UPS ES 850G2" };

    /// <summary>A tick with a supply's DC output on it. The name is a real one, and says the supply is rated for 1000 W,
    /// which is the rating the efficiency curve is read at.</summary>
    private static Sample WithPsu(Sample s, double? watts, string? name = "Corsair HX1000i")
        => s with { PsuOutputW = watts, PsuName = name };

    /// <summary>A tick with what a supply says it draws from the wall on it, as a Corsair's own total is read.</summary>
    private static Sample WithWall(Sample s, double? watts) => s with { PsuWallW = watts, PsuName = "Corsair HX1000i" };

    /// <summary>What the default desktop's Bronze supply, taken to be rated 650 W, draws from the wall for this much DC.</summary>
    private static double Bronze650(double dc) => dc / PsuEfficiency.At(PsuTier.Bronze, dc / PsuEfficiency.DefaultRatedW);

    /// <summary>What the test supply, a Corsair HX1000i (Platinum, 1000 W), draws from the wall for this much DC.</summary>
    private static double Hx1000i(double dc) => dc / PsuEfficiency.At(PsuTier.Platinum, dc / 1000);

    /// <summary>What the default laptop's 65 W adapter draws from the wall for this much DC.</summary>
    private static double Adapter(double dc) => AdapterEfficiency.Wall(dc);

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
