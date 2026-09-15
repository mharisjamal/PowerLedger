using PowerLedger.Contracts;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class InventoryFactsTests
{
    private static InventoryFacts Laptop() => new(
        Chassis: ChassisKind.Laptop,
        CpuName: "11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz",
        GpuName: "NVIDIA GeForce MX330",
        RamSticks: 1, RamIsDdr5: false,
        SsdCount: 1, HddCount: 0,
        DisplayDiagonalInches: 15.6,
        MonitorCount: 1);

    [Fact]
    public void The_same_machine_hashes_the_same_way_every_time()
    {
        Laptop().Hash.ShouldBe(Laptop().Hash);
        Laptop().Hash.Length.ShouldBe(16);
    }

    [Fact]
    public void A_different_processor_memory_or_chassis_is_a_different_machine()
    {
        var baseline = Laptop().Hash;
        (Laptop() with { CpuName = "AMD Ryzen 7 5800U with Radeon Graphics" }).Hash.ShouldNotBe(baseline);
        (Laptop() with { RamSticks = 2 }).Hash.ShouldNotBe(baseline);
        (Laptop() with { RamIsDdr5 = true }).Hash.ShouldNotBe(baseline);
        (Laptop() with { Chassis = ChassisKind.Desktop }).Hash.ShouldNotBe(baseline);
    }

    [Fact]
    public void Docks_external_drives_and_driver_installs_do_not_relearn_the_machine()
    {
        var baseline = Laptop().Hash;
        (Laptop() with { MonitorCount = 3 }).Hash.ShouldBe(baseline);                          // docked
        (Laptop() with { DisplayDiagonalInches = 0 }).Hash.ShouldBe(baseline);                 // lid shut on the dock
        (Laptop() with { SsdCount = 2, HddCount = 1 }).Hash.ShouldBe(baseline);                // external drives
        (Laptop() with { GpuName = "Microsoft Basic Display Adapter" }).Hash.ShouldBe(baseline); // before the driver
    }

    [Fact]
    public void The_facts_become_the_profile_the_power_model_wants()
    {
        var profile = Laptop().ToProfile(MachineProfile.DefaultLaptop);

        profile.Chassis.ShouldBe(ChassisKind.Laptop);
        profile.RamSticks.ShouldBe(1);
        profile.RamIsDdr5.ShouldBeFalse();
        profile.SsdCount.ShouldBe(1);
        profile.HddCount.ShouldBe(0);
        profile.DisplayDiagonalInches.ShouldBe(15.6);
    }

    [Fact]
    public void Turning_facts_into_a_profile_keeps_the_settings_the_user_chose()
    {
        var chosen = MachineProfile.DefaultLaptop with { PsuTier = PsuTier.Gold, ExtrasWatts = 12, IncludeMonitors = true, MonitorWatts = 30 };
        var profile = Laptop().ToProfile(chosen);

        profile.PsuTier.ShouldBe(PsuTier.Gold);
        profile.ExtrasWatts.ShouldBe(12);
        profile.IncludeMonitors.ShouldBeTrue();
        profile.MonitorWatts.ShouldBe(30);
        profile.RamSticks.ShouldBe(1);                 // detection still wins for the detectable fields
    }

    [Fact]
    public void An_unknown_panel_size_leaves_the_profile_alone()
    {
        // Detected while docked with the lid shut: no built-in panel shows, but it is still there.
        var docked = Laptop() with { DisplayDiagonalInches = 0 };
        docked.ToProfile(MachineProfile.DefaultLaptop with { DisplayDiagonalInches = 13.3 }).DisplayDiagonalInches.ShouldBe(13.3);

        var desktop = Laptop() with { Chassis = ChassisKind.Desktop, DisplayDiagonalInches = 0 };
        desktop.ToProfile(MachineProfile.DefaultDesktop).DisplayDiagonalInches.ShouldBe(0);
        desktop.ToProfile(MachineProfile.DefaultDesktop with { DisplayDiagonalInches = 23.8 }).DisplayDiagonalInches.ShouldBe(23.8);
    }

    [Fact]
    public void An_all_in_one_s_built_in_panel_reaches_its_desktop_profile()
    {
        var allInOne = Laptop() with { Chassis = ChassisKind.Desktop, DisplayDiagonalInches = 23.8 };
        var profile = allInOne.ToProfile(MachineProfile.DefaultDesktop);
        profile.Chassis.ShouldBe(ChassisKind.Desktop);
        profile.DisplayDiagonalInches.ShouldBe(23.8);
    }

    [Fact]
    public void A_laptop_s_panel_size_does_not_follow_the_machine_into_a_desktop_profile()
    {
        // A tower whose UPS once passed for its own battery kept the laptop's panel size; a desktop would count it.
        var tower = Laptop() with { Chassis = ChassisKind.Desktop, DisplayDiagonalInches = 0 };
        tower.ToProfile(MachineProfile.DefaultLaptop).DisplayDiagonalInches.ShouldBe(0);
    }

    [Fact]
    public void The_facts_serialise_to_the_json_storage_keeps_beside_the_hash()
    {
        var json = Laptop().ToJson();
        json.ShouldContain("i7-1165G7");
        json.ShouldContain("MX330");
    }

    [Fact]
    public void Tdp_comes_from_the_bundled_table_when_the_model_is_known()
    {
        Laptop().CpuTdpW.ShouldBe(28);
        Laptop().GpuTdpW.ShouldBe(10);
        (Laptop() with { CpuName = "Unknown Chip" }).CpuTdpW.ShouldBeNull();
    }
}
