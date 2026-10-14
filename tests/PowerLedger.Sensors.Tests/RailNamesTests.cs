using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

/// <summary>
/// The rail names beyond Intel's and AMD's: Qualcomm's own (libqcperf's list, github.com/qualcomm/libqcperf,
/// power_telemetry.c) and Microsoft's EMI rail naming scheme (learn.microsoft.com, powermeter/rail_naming).
/// </summary>
public class RailNamesTests
{
    /// <summary>Every instance name libqcperf looks for under <c>\Energy Meter(*)</c>.</summary>
    private static readonly string[] Libqcperf =
    [
        "CPU_CLUSTER_0", "CPU_CLUSTER_1", "CPU_CLUSTER_2", "GPU", "PSU_USB", "SYS", "SYSTEM", "USBC_TOTAL", "ROP", "NPU",
        "MULTIMEDIA", "INFRA", "MEMORY", "SOC",
    ];

    [Fact]
    public void Qualcomm_s_sys_rail_is_the_whole_platform_as_its_system_rail_is()
    {
        EnergyMeter.Classify("SYS").ShouldBe(RailKind.Platform);
        EnergyMeter.Classify("sys").ShouldBe(RailKind.Platform);
        EnergyMeter.WhyUnavailable(["SYS", "_Total"]).ShouldBeNull();

        var reading = EnergyMeter.Summarise([new Rail("SOC", 6000, 1), new Rail("SYS", 14000, 2)]);

        reading.PackageW.ShouldNotBeNull().ShouldBe(6.0, 1e-9);
        reading.PlatformW.ShouldNotBeNull().ShouldBe(14.0, 1e-9);
    }

    [Fact]
    public void Sys_and_system_are_one_rail_under_two_names_and_are_counted_once()
    {
        EnergyMeter.Summarise([new Rail("SYS", 14000, 1), new Rail("SYSTEM", 14000, 2)]).PlatformW.ShouldNotBeNull().ShouldBe(14.0, 1e-9);
    }

    [Fact]
    public void The_charger_input_is_read_for_itself_and_is_never_the_platform_s_draw()
    {
        // It holds the battery's charge and the charger's own losses, so it is never the total (the battery rule).
        EnergyMeter.Classify("PSU_USB").ShouldBe(RailKind.ChargerInput);

        var reading = EnergyMeter.Summarise([new Rail("SYS", 14000, 1), new Rail("PSU_USB", 30000, 2)]);

        reading.PlatformW.ShouldNotBeNull().ShouldBe(14.0, 1e-9);
        reading.ChargerInputW.ShouldNotBeNull().ShouldBe(30.0, 1e-9);
        EnergyMeter.WhyUnavailable(["PSU_USB"]).ShouldBe("this machine's energy meter has no processor package rail");
    }

    [Fact]
    public void Libqcperf_s_whole_set_gives_the_soc_the_sys_rail_and_the_parts_and_sums_nothing_unplaced()
    {
        foreach (var name in new[] { "USBC_TOTAL", "ROP", "INFRA", "MULTIMEDIA" }) EnergyMeter.Classify(name).ShouldBe(RailKind.Unplaced);

        var watts = new Dictionary<string, ulong>
        {
            ["CPU_CLUSTER_0"] = 2500, ["CPU_CLUSTER_1"] = 1500, ["CPU_CLUSTER_2"] = 500, ["GPU"] = 800, ["PSU_USB"] = 40000,
            ["SYS"] = 14000, ["SYSTEM"] = 14000, ["USBC_TOTAL"] = 9000, ["ROP"] = 3000, ["NPU"] = 300, ["MULTIMEDIA"] = 700,
            ["INFRA"] = 900, ["MEMORY"] = 700, ["SOC"] = 6000,
        };
        var reading = EnergyMeter.Summarise([.. Libqcperf.Select(name => new Rail(name, watts[name], 1))]);

        reading.PackageW.ShouldNotBeNull().ShouldBe(6.0, 1e-9);
        reading.CoresW.ShouldNotBeNull().ShouldBe(4.5, 1e-9);
        reading.IntegratedGpuW.ShouldNotBeNull().ShouldBe(0.8, 1e-9);
        reading.MemoryW.ShouldNotBeNull().ShouldBe(0.7, 1e-9);
        reading.PlatformW.ShouldNotBeNull().ShouldBe(14.0, 1e-9);
        reading.ChargerInputW.ShouldNotBeNull().ShouldBe(40.0, 1e-9);
    }

    [Fact]
    public void Microsoft_s_exact_rail_name_wins_over_its_parts()
    {
        foreach (var name in new[] { "GPU_TOP", "gpu_bottom", "GPU_MEM" }) EnergyMeter.Classify(name).ShouldBe(RailKind.IntegratedGpu);

        var exact = EnergyMeter.Summarise([
            new Rail("SOC", 6000, 1), new Rail("GPU", 800, 2), new Rail("GPU_TOP", 500, 3), new Rail("GPU_BOTTOM", 300, 4),
        ]);
        exact.IntegratedGpuW.ShouldNotBeNull().ShouldBe(0.8, 1e-9);

        var parts = EnergyMeter.Summarise([new Rail("SOC", 6000, 1), new Rail("GPU_TOP", 500, 2), new Rail("GPU_BOTTOM", 300, 3)]);
        parts.IntegratedGpuW.ShouldNotBeNull().ShouldBe(0.8, 1e-9);
        parts.PackageW.ShouldNotBeNull().ShouldBe(6.0, 1e-9);
    }

    [Fact]
    public void Microsoft_s_cpu_rail_holds_the_clusters_and_without_a_soc_rail_the_package_counts_each_part_once()
    {
        EnergyMeter.Classify("CPU").ShouldBe(RailKind.Cores);
        EnergyMeter.Classify("CPU_BIG").ShouldBe(RailKind.Cores);

        var reading = EnergyMeter.Summarise([
            new Rail("CPU", 4000, 1), new Rail("CPU_CLUSTER_0", 2500, 2), new Rail("CPU_CLUSTER_1", 1500, 3),
            new Rail("GPU", 800, 4), new Rail("NPU", 300, 5),
        ]);

        reading.CoresW.ShouldNotBeNull().ShouldBe(4.0, 1e-9);
        reading.PackageW.ShouldNotBeNull().ShouldBe(5.1, 1e-9);
    }

    [Fact]
    public void Display_storage_and_radio_rails_are_known_and_kept_out_of_the_processor_and_platform_figures()
    {
        EnergyMeter.Classify("DISPLAY").ShouldBe(RailKind.Display);
        EnergyMeter.Classify("DISPLAY_EDP").ShouldBe(RailKind.Display);
        EnergyMeter.Classify("STORAGE_NVME").ShouldBe(RailKind.Storage);
        EnergyMeter.Classify("WIFI_BLUETOOTH").ShouldBe(RailKind.Network);
        EnergyMeter.Classify("MBB").ShouldBe(RailKind.MobileBroadband);

        var reading = EnergyMeter.Summarise([
            new Rail("SOC", 6000, 1), new Rail("SYS", 14000, 2), new Rail("DISPLAY", 2000, 3), new Rail("STORAGE", 700, 4),
            new Rail("WIFI_WIFI", 400, 5), new Rail("WIFI_BLUETOOTH", 100, 6),
        ]);

        reading.PackageW.ShouldNotBeNull().ShouldBe(6.0, 1e-9);
        reading.PlatformW.ShouldNotBeNull().ShouldBe(14.0, 1e-9);
        EnergyMeter.WhyUnavailable(["DISPLAY", "STORAGE"]).ShouldBe("this machine's energy meter has no processor package rail");
    }

    [Fact]
    public void Intel_s_psys_rail_is_the_whole_platform_beside_the_package()
    {
        // Optional and OEM-enabled, and it leaves out charging, as the battery rule wants.
        EnergyMeter.Classify("RAPL_Package0_PSYS").ShouldBe(RailKind.Platform);
        EnergyMeter.WhyUnavailable(["RAPL_Package0_PSYS"]).ShouldBeNull();

        var reading = EnergyMeter.Summarise([new Rail("RAPL_Package0_PKG", 8300, 1), new Rail("RAPL_Package0_PSYS", 19500, 2)]);

        reading.PackageW.ShouldNotBeNull().ShouldBe(8.3, 1e-9);
        reading.PlatformW.ShouldNotBeNull().ShouldBe(19.5, 1e-9);
    }

    [Fact]
    public void A_vendor_s_package_rail_wins_over_a_named_cpu_rail_beside_it_so_nothing_is_counted_twice()
    {
        var reading = EnergyMeter.Summarise([new Rail("RAPL_Package0_PKG", 8300, 1), new Rail("RAPL_Package0_PP0", 4000, 2), new Rail("CPU", 5000, 3)]);

        reading.PackageW.ShouldNotBeNull().ShouldBe(8.3, 1e-9);
        reading.CoresW.ShouldNotBeNull().ShouldBe(4.0, 1e-9);
    }

    [Fact]
    public void Rails_worth_noting_are_listed_with_what_each_is_taken_for_and_plain_intel_or_amd_rails_are_not()
    {
        EnergyMeter.RailsWorthNoting(["RAPL_Package0_PKG", "RAPL_Package0_PP0", "RAPL_Package0_DRAM", "_Total"]).ShouldBeNull();
        EnergyMeter.RailsWorthNoting(["Current Socket Energy", "Apu Energy"]).ShouldBeNull();

        var noted = EnergyMeter.RailsWorthNoting(["SOC", "SYS", "PSU_USB", "ROP", "X2_MYSTERY", "_Total"]).ShouldNotBeNull();

        noted.ShouldContain("SOC (package)");
        noted.ShouldContain("SYS (platform)");
        noted.ShouldContain("PSU_USB (charger input)");
        noted.ShouldContain("ROP (not summed)");
        noted.ShouldContain("X2_MYSTERY (unknown)");
        noted.ShouldNotContain("_Total");
    }
}
