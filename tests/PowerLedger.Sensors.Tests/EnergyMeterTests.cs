using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class EnergyMeterTests
{
    [Fact]
    public void Rail_names_are_classified_by_what_they_measure()
    {
        EnergyMeter.Classify("RAPL_Package0_PKG").ShouldBe(RailKind.Package);
        EnergyMeter.Classify("RAPL_Package1_PKG").ShouldBe(RailKind.Package);
        EnergyMeter.Classify("RAPL_Package0_PP0").ShouldBe(RailKind.Cores);
        EnergyMeter.Classify("RAPL_Package0_PP1").ShouldBe(RailKind.IntegratedGpu);
        EnergyMeter.Classify("RAPL_Package0_DRAM").ShouldBe(RailKind.Memory);
        EnergyMeter.Classify("Current Socket Energy").ShouldBe(RailKind.Package);
        EnergyMeter.Classify("Apu Energy").ShouldBe(RailKind.IntegratedGpu);
        EnergyMeter.Classify("VDDCR_VDD Energy").ShouldBe(RailKind.Cores);
        EnergyMeter.Classify("_Total").ShouldBe(RailKind.Ignored);
        EnergyMeter.Classify("Power Meter (0)").ShouldBe(RailKind.Ignored);
    }

    [Fact]
    public void Rails_of_the_same_kind_are_summed_across_sockets()
    {
        var reading = EnergyMeter.Summarise([
            new Rail("RAPL_Package0_PKG", 5590, 1),
            new Rail("RAPL_Package1_PKG", 4410, 2),
            new Rail("RAPL_Package0_PP1", 66, 3),
            new Rail("_Total", 99999, 4),
        ]);

        reading.PackageW.ShouldNotBeNull().ShouldBe(10.0, 1e-9);
        reading.IntegratedGpuW.ShouldNotBeNull().ShouldBe(0.066, 1e-9);
        reading.CoresW.ShouldBeNull();
        reading.MemoryW.ShouldBeNull();
    }

    [Fact]
    public void A_machine_with_no_rails_reports_nothing_rather_than_zero()
    {
        var reading = EnergyMeter.Summarise([]);
        reading.PackageW.ShouldBeNull();
        reading.CoresW.ShouldBeNull();
        reading.IntegratedGpuW.ShouldBeNull();
        reading.MemoryW.ShouldBeNull();
    }

    [Fact]
    public void A_rail_that_reads_zero_still_counts_as_a_reading()
    {
        // A memory rail the firmware never populates reads a genuine zero, which is different from absent.
        EnergyMeter.Summarise([new Rail("RAPL_Package0_DRAM", 0, 1)]).MemoryW.ShouldBe(0);
    }

    [Fact]
    public void The_meter_is_available_when_it_has_a_processor_package_rail()
    {
        EnergyMeter.WhyUnavailable(["RAPL_Package0_PKG", "RAPL_Package0_PP0", "RAPL_Package0_PP1", "RAPL_Package0_DRAM", "_Total"]).ShouldBeNull();
        EnergyMeter.WhyUnavailable(["RAPL_Package0_PKG"]).ShouldBeNull();
        EnergyMeter.WhyUnavailable(["Current Socket Energy", "Apu Energy", "VDDCR_VDD Energy"]).ShouldBeNull();
    }

    [Fact]
    public void Core_graphics_and_memory_rails_without_a_package_rail_are_no_processor_meter()
    {
        // The processor's figure comes from the package rail alone, so without one it would always be modelled.
        EnergyMeter.WhyUnavailable(["RAPL_Package0_PP0", "RAPL_Package0_PP1", "RAPL_Package0_DRAM"])
            .ShouldBe("this machine's energy meter has no processor package rail");
        EnergyMeter.WhyUnavailable(["Apu Energy", "VDDCR_VDD Energy", "_Total"])
            .ShouldBe("this machine's energy meter has no processor package rail");
    }

    [Fact]
    public void A_meter_with_no_rail_it_knows_says_it_publishes_none()
    {
        EnergyMeter.WhyUnavailable([]).ShouldBe("this machine publishes no processor power rails");
        EnergyMeter.WhyUnavailable(["_Total", "Power Meter (0)"]).ShouldBe("this machine publishes no processor power rails");
    }

    [Fact]
    public void Beside_a_package_rail_the_core_graphics_and_memory_rails_still_report()
    {
        var reading = EnergyMeter.Summarise([
            new Rail("RAPL_Package0_PKG", 8300, 1),
            new Rail("RAPL_Package0_PP0", 4000, 2),
            new Rail("RAPL_Package0_PP1", 350, 3),
            new Rail("RAPL_Package0_DRAM", 1200, 4),
        ]);

        reading.PackageW.ShouldNotBeNull().ShouldBe(8.3, 1e-9);
        reading.CoresW.ShouldNotBeNull().ShouldBe(4.0, 1e-9);
        reading.IntegratedGpuW.ShouldNotBeNull().ShouldBe(0.35, 1e-9);
        reading.MemoryW.ShouldNotBeNull().ShouldBe(1.2, 1e-9);
    }

    // Snapdragon X. The rail names are those npu-watt reads through "\Energy Meter(*)\Energy" (github.com/hotschmoe/npu-watt);
    // no PowerLedger test has met a Snapdragon machine, so these are fakes of that published set.
    private static readonly string[] Snapdragon = ["soc", "cpu_cluster_0", "cpu_cluster_1", "cpu_cluster_2", "gpu", "npu", "memory", "system"];

    [Fact]
    public void Snapdragon_rails_are_classified_by_what_they_measure_whatever_their_case_or_spelling()
    {
        foreach (var name in new[] { "soc", "SOC", "Soc" }) EnergyMeter.Classify(name).ShouldBe(RailKind.Package);
        foreach (var name in new[] { "cpu_cluster_0", "CPU_CLUSTER_1", "cpu cluster 2", "cpu_cluster0", "CPU-Cluster-3" })
        {
            EnergyMeter.Classify(name).ShouldBe(RailKind.Cores);
        }
        foreach (var name in new[] { "gpu", "GPU" }) EnergyMeter.Classify(name).ShouldBe(RailKind.IntegratedGpu);
        foreach (var name in new[] { "npu", "NPU" }) EnergyMeter.Classify(name).ShouldBe(RailKind.Npu);
        foreach (var name in new[] { "memory", "Memory" }) EnergyMeter.Classify(name).ShouldBe(RailKind.Memory);
        foreach (var name in new[] { "system", "System", "SYSTEM" }) EnergyMeter.Classify(name).ShouldBe(RailKind.Platform);
        EnergyMeter.Classify("systems").ShouldBe(RailKind.Ignored);
        EnergyMeter.Classify("gpu_mem").ShouldBe(RailKind.Ignored);
    }

    [Fact]
    public void A_snapdragon_meter_is_available_and_gives_the_package_and_the_whole_platform()
    {
        EnergyMeter.WhyUnavailable(Snapdragon).ShouldBeNull();

        var reading = EnergyMeter.Summarise([
            new Rail("soc", 6000, 1),
            new Rail("cpu_cluster_0", 2500, 2),
            new Rail("cpu_cluster_1", 1500, 3),
            new Rail("gpu", 800, 4),
            new Rail("npu", 300, 5),
            new Rail("memory", 700, 6),
            new Rail("system", 14000, 7),
            new Rail("_Total", 99999, 8),
        ]);

        // The clusters, graphics and NPU are inside the soc rail, so the package is the soc rail alone, not their sum on top.
        reading.PackageW.ShouldNotBeNull().ShouldBe(6.0, 1e-9);
        reading.CoresW.ShouldNotBeNull().ShouldBe(4.0, 1e-9);
        reading.IntegratedGpuW.ShouldNotBeNull().ShouldBe(0.8, 1e-9);
        reading.MemoryW.ShouldNotBeNull().ShouldBe(0.7, 1e-9);
        // The system rail holds everything, the display included, and nothing is added to it.
        reading.PlatformW.ShouldNotBeNull().ShouldBe(14.0, 1e-9);
    }

    [Fact]
    public void Without_a_soc_rail_the_package_is_its_outermost_parts_counted_once()
    {
        var reading = EnergyMeter.Summarise([
            new Rail("cpu_cluster_0", 2500, 1),
            new Rail("cpu_cluster_1", 1500, 2),
            new Rail("gpu", 800, 3),
            new Rail("npu", 300, 4),
        ]);

        reading.PackageW.ShouldNotBeNull().ShouldBe(5.1, 1e-9);
        reading.PlatformW.ShouldBeNull();
    }

    [Fact]
    public void An_npu_or_graphics_rail_alone_is_not_the_processor()
    {
        EnergyMeter.WhyUnavailable(["npu", "gpu"]).ShouldBe("this machine's energy meter has no processor package rail");
        EnergyMeter.Summarise([new Rail("npu", 300, 1), new Rail("gpu", 800, 2)]).PackageW.ShouldBeNull();
    }

    [Fact]
    public void A_system_rail_alone_still_measures_the_whole_platform()
    {
        EnergyMeter.WhyUnavailable(["system", "_Total"]).ShouldBeNull();
        var reading = EnergyMeter.Summarise([new Rail("system", 9500, 1)]);
        reading.PlatformW.ShouldNotBeNull().ShouldBe(9.5, 1e-9);
        reading.PackageW.ShouldBeNull();
    }

    [Fact]
    public void The_nesting_is_one_table_a_real_device_can_correct()
    {
        // Were the graphics rail found to sit beside the soc rail rather than inside it, one row changes and the package
        // then counts both, still once each.
        var corrected = QualcommRails.Table.Select(rail => rail.Key == "gpu" ? rail with { Inside = "system" } : rail).ToList();
        var reading = EnergyMeter.Summarise([
            new Rail("soc", 6000, 1),
            new Rail("cpu_cluster_0", 2500, 2),
            new Rail("gpu", 800, 3),
            new Rail("system", 14000, 4),
        ], corrected);

        reading.PackageW.ShouldNotBeNull().ShouldBe(6.8, 1e-9);
        reading.IntegratedGpuW.ShouldNotBeNull().ShouldBe(0.8, 1e-9);
        reading.PlatformW.ShouldNotBeNull().ShouldBe(14.0, 1e-9);
    }

    [Fact]
    public void Every_row_of_the_table_nests_inside_a_row_it_has()
    {
        var keys = QualcommRails.Table.Select(rail => rail.Key).ToHashSet();
        keys.Count.ShouldBe(QualcommRails.Table.Count);
        foreach (var rail in QualcommRails.Table)
        {
            if (rail.Inside is { } inside) keys.ShouldContain(inside);
        }
        QualcommRails.Table.Count(rail => rail.Inside is null).ShouldBe(1);
    }

    [Fact]
    public void Intel_and_amd_readings_have_no_platform_figure()
    {
        EnergyMeter.Summarise([new Rail("RAPL_Package0_PKG", 8300, 1), new Rail("RAPL_Package0_DRAM", 1200, 2)]).PlatformW.ShouldBeNull();
    }
}
