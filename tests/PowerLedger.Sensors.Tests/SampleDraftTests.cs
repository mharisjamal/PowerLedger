using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class SampleDraftTests
{
    [Fact]
    public void A_fresh_draft_claims_nothing_and_assumes_the_display_is_on()
    {
        var draft = new SampleDraft();
        draft.CpuPackageW.ShouldBeNull();
        draft.BatteryRateW.ShouldBeNull();
        draft.Brightness.ShouldBeNull();
        draft.DGpuPresent.ShouldBeFalse();
        draft.OnBattery.ShouldBeFalse();
        draft.DisplayOn.ShouldBeTrue();
        draft.MonitorCount.ShouldBe(0);
    }

    [Fact]
    public void The_draft_becomes_a_sample_with_the_given_clock_and_delta()
    {
        var at = new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
        var draft = new SampleDraft
        {
            CpuPackageW = 14.6,
            IGpuW = 2.0,
            CpuLoad = 0.3,
            BatteryRateW = 34.2,
            OnBattery = true,
            Brightness = 0.6,
            DisplayOn = false,
            MonitorCount = 2,
            UserIdleSeconds = 12,
            SessionLocked = true,
        };
        draft.AddGpu(DiscreteGpu.NvidiaVendor).Load = 0.2;

        var sample = draft.ToSample(at, deltaSeconds: 1.5);

        sample.Timestamp.ShouldBe(at);
        sample.DeltaSeconds.ShouldBe(1.5);
        sample.CpuPackageW.ShouldBe(14.6);
        sample.IGpuW.ShouldBe(2.0);
        sample.CpuLoad.ShouldBe(0.3);
        sample.DGpuW.ShouldBeNull();
        sample.DGpuLoad.ShouldBe(0.2);
        sample.DGpuPresent.ShouldBeTrue();
        sample.BatteryRateW.ShouldBe(34.2);
        sample.OnBattery.ShouldBeTrue();
        sample.Brightness.ShouldBe(0.6);
        sample.DisplayOn.ShouldBeFalse();
        sample.MonitorCount.ShouldBe(2);
        sample.UserIdleSeconds.ShouldBe(12);
        sample.SessionLocked.ShouldBeTrue();
        sample.Suspect.ShouldBeFalse();
    }

    [Fact]
    public void Every_card_found_goes_into_the_sample_with_their_totals_beside_them()
    {
        var draft = new SampleDraft();
        var quadro = draft.AddGpu(DiscreteGpu.NvidiaVendor, 0x06D8);
        quadro.Name = "Quadro 6000";
        quadro.Load = 0.5;
        quadro.Rating = new GpuRating(204, Rough: false);
        var radeon = draft.AddGpu(DiscreteGpu.AmdVendor, 0x73BF);
        radeon.Watts = 214.5;
        radeon.Scope = GpuPowerScope.ChipOnly;

        var sample = draft.ToSample(DateTimeOffset.UnixEpoch, 1);

        sample.Gpus.ShouldNotBeNull().ShouldBe(
        [
            new GpuCard("Quadro 6000", null, 0.5, GpuPowerScope.Board, 204),
            new GpuCard("", 214.5, null, GpuPowerScope.ChipOnly),
        ]);
        sample.DGpuPresent.ShouldBeTrue();
        sample.DGpuW.ShouldBeNull();              // one card is the model's to work out
        sample.DGpuLoad.ShouldBe(0.5);
        draft.DGpuPresent.ShouldBeTrue();
        draft.DGpuLoad.ShouldBe(0.5);
    }

    [Fact]
    public void A_draft_without_cards_has_none_in_its_sample()
    {
        var sample = new SampleDraft().ToSample(DateTimeOffset.UnixEpoch, 1);

        sample.Gpus.ShouldBeNull();
        sample.DGpuPresent.ShouldBeFalse();
        sample.DGpuW.ShouldBeNull();
    }
}
