using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class EnergyMeterSourceTests
{
    private static EnergyMeterSource From(EnergyMeterReading reading, bool available = true)
        => new(() => reading, available, available ? null : "no rails");

    [Fact]
    public void The_package_rail_becomes_the_cpu_reading_and_the_graphics_rail_rides_along()
    {
        var draft = new SampleDraft();
        From(new EnergyMeterReading(PackageW: 8.3, CoresW: 4.0, IntegratedGpuW: 0.35, MemoryW: null)).Contribute(draft);

        draft.CpuPackageW.ShouldBe(8.3);
        draft.IGpuW.ShouldBe(0.35);
    }

    [Fact]
    public void A_missing_package_rail_leaves_the_cpu_unclaimed_rather_than_zero()
    {
        var draft = new SampleDraft();
        From(new EnergyMeterReading(null, null, null, null)).Contribute(draft);

        draft.CpuPackageW.ShouldBeNull();
        draft.IGpuW.ShouldBeNull();
    }

    [Fact]
    public void An_unavailable_meter_says_so_and_is_never_asked()
    {
        var source = new EnergyMeterSource(() => throw new InvalidOperationException("should not be called"), available: false, unavailable: "no rails");
        source.Supported.ShouldBeFalse();
        source.Unavailable.ShouldBe("no rails");
        source.Name.ShouldBe("energy-meter");
    }

    [Fact]
    public void The_source_never_touches_fields_that_belong_to_others()
    {
        var draft = new SampleDraft { BatteryRateW = 34.2, CpuLoad = 0.5, Brightness = 0.6 };
        From(new EnergyMeterReading(8.3, null, null, null)).Contribute(draft);

        draft.BatteryRateW.ShouldBe(34.2);
        draft.CpuLoad.ShouldBe(0.5);
        draft.Brightness.ShouldBe(0.6);
    }
}
