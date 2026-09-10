using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class NvidiaSourceTests
{
    private static NvidiaSource From(double? watts, double? load, bool present = true)
        => new(() => new GpuReading(present, watts, load), present, present ? null : "no NVIDIA GPU");

    [Fact]
    public void A_card_that_reports_power_fills_both_fields()
    {
        var draft = new SampleDraft();
        From(watts: 18.5, load: 0.42).Contribute(draft);

        draft.DGpuPresent.ShouldBeTrue();
        draft.DGpuW.ShouldBe(18.5);
        draft.DGpuLoad.ShouldBe(0.42);
    }

    [Fact]
    public void A_card_with_no_power_sensor_still_reports_presence_and_load()
    {
        // The MX330 measured on the development laptop: utilisation yes, power not supported.
        var draft = new SampleDraft();
        From(watts: null, load: 0.31).Contribute(draft);

        draft.DGpuPresent.ShouldBeTrue();
        draft.DGpuW.ShouldBeNull();
        draft.DGpuLoad.ShouldBe(0.31);
    }

    [Fact]
    public void A_machine_with_no_nvidia_card_says_so_and_claims_nothing()
    {
        var source = new NvidiaSource(() => new GpuReading(false, null, null), present: false, unavailable: "no NVIDIA GPU");
        source.Supported.ShouldBeFalse();
        source.Unavailable.ShouldBe("no NVIDIA GPU");

        var draft = new SampleDraft();
        source.Contribute(draft);
        draft.DGpuPresent.ShouldBeFalse();
        draft.DGpuW.ShouldBeNull();
    }

    [Fact]
    public void The_source_is_named_for_the_status_screen()
        => From(1, 1).Name.ShouldBe("nvidia-gpu");
}
