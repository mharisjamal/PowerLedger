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
            DGpuW = null,
            DGpuLoad = 0.2,
            DGpuPresent = true,
            BatteryRateW = 34.2,
            OnBattery = true,
            Brightness = 0.6,
            DisplayOn = false,
            MonitorCount = 2,
            UserIdleSeconds = 12,
            SessionLocked = true,
        };

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
}
