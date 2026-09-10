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
}
