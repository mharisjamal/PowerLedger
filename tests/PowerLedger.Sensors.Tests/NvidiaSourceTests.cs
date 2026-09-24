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
    public void A_switched_off_gpu_draws_nothing_and_nvml_is_never_asked()
    {
        // Measured on the development laptop: the MX330 sits in D3 on 39 ticks out of 40.
        var asked = false;
        var source = new NvidiaSource(() => { asked = true; return new GpuReading(true, null, 0.5); },
            present: true, unavailable: null, poweredOff: () => true);

        var draft = new SampleDraft();
        source.Contribute(draft);

        draft.DGpuPresent.ShouldBeTrue();
        draft.DGpuW.ShouldBe(0);
        draft.DGpuLoad.ShouldBe(0);
        asked.ShouldBeFalse();
    }

    [Fact]
    public void Every_card_is_added_with_its_name_pci_device_and_rating()
    {
        var source = new NvidiaSource(
        [
            new NvidiaCard("Quadro 6000", 0x06D8, new GpuRating(204, false), () => new GpuReading(true, null, 0.5), static () => false),
            new NvidiaCard("NVIDIA GeForce GTX 1080", 0x1B80, new GpuRating(180, false), () => new GpuReading(true, 150, 0.9), static () => false),
        ], present: true, unavailable: null, loadsEveryCard: true);

        var draft = new SampleDraft();
        source.Contribute(draft);

        draft.Gpus.Count.ShouldBe(2);
        draft.Gpus.ShouldAllBe(card => card.VendorId == DiscreteGpu.NvidiaVendor);
        draft.Gpus.Select(card => (card.Name, card.DeviceId, card.Watts, card.Load, card.Rating!.Value.Watts))
            .ShouldBe([("Quadro 6000", 0x06D8u, (double?)null, (double?)0.5, 204.0), ("NVIDIA GeForce GTX 1080", 0x1B80u, 150, 0.9, 180)]);
    }

    [Fact]
    public void Only_the_card_windows_has_switched_off_draws_nothing_and_is_not_asked()
    {
        var asked = 0;
        var source = new NvidiaSource(
        [
            new NvidiaCard("A", 1, null, () => { asked++; return new GpuReading(true, 99, 0.9); }, static () => true),
            new NvidiaCard("B", 2, null, () => new GpuReading(true, 40, 0.3), static () => false),
        ], present: true, unavailable: null, loadsEveryCard: true);

        var draft = new SampleDraft();
        source.Contribute(draft);

        draft.Gpus.Select(card => card.Watts).ShouldBe([0, 40]);
        asked.ShouldBe(0);
    }

    [Fact]
    public void A_card_that_breaks_leaves_the_tick_with_none_of_nvmls_cards()
    {
        // A lost GPU throws, the sampler backs the source off, and the tick keeps nothing half-read from it.
        var source = new NvidiaSource(
        [
            new NvidiaCard("A", 1, null, () => new GpuReading(true, 99, 0.9), static () => false),
            new NvidiaCard("B", 2, null, () => throw new InvalidOperationException("NVML error 15"), static () => false),
        ], present: true, unavailable: null, loadsEveryCard: true);

        var draft = new SampleDraft();
        Should.Throw<InvalidOperationException>(() => source.Contribute(draft));

        draft.Gpus.ShouldBeEmpty();
    }

    [Fact]
    public void The_source_is_named_for_the_status_screen()
        => From(1, 1).Name.ShouldBe("nvidia-gpu");
}
