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
}
