using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class CalibrationLearnerTests
{
    // Small thresholds so tests stay fast; production defaults are 600 / 300 / 1800.
    private static readonly CalibrationOptions Fast = new(HalfLifeSamples: 10, MinBucketSamples: 5, MinTotalSamples: 5);

    private static void Feed(CalibrationLearner learner, int ticks, double batteryW, double brightness = 0.6, bool displayOn = true)
    {
        for (var i = 0; i < ticks; i++)
        {
            var s = TestData.Laptop(cpu: 10, gpu: 2, battery: batteryW, onBattery: true, brightness: brightness, displayOn: displayOn);
            learner.Observe(s, cpuW: 10, gpuW: 2, displayW: 4);
        }
    }

    [Fact]
    public void Nothing_is_reported_before_the_minimum_sample_counts()
    {
        var learner = new CalibrationLearner(new CalibrationOptions(HalfLifeSamples: 10, MinBucketSamples: 50, MinTotalSamples: 100));
        Feed(learner, 40, batteryW: 25);
        learner.GetBaseline(CalibrationBuckets.For(0.6, true)).ShouldBeNull();
    }

    [Fact]
    public void A_constant_observation_is_learned_exactly()
    {
        var learner = new CalibrationLearner(Fast);
        Feed(learner, 10, batteryW: 25);                       // 25 - 10 - 2 - 4 = 9 W of "rest"
        learner.GetBaseline(CalibrationBuckets.For(0.6, true)).ShouldBe(9.0, 0.0001);
    }

    [Fact]
    public void The_average_moves_with_the_configured_half_life()
    {
        var learner = new CalibrationLearner(Fast);
        Feed(learner, 1, batteryW: 26);                        // seeds bucket at 10 W
        Feed(learner, 30, batteryW: 36);                       // 20 W for three half-lives
        learner.GetBaseline(CalibrationBuckets.For(0.6, true)).ShouldBe(20 - 10 * 0.125, 0.05);
    }

    [Fact]
    public void Buckets_are_independent()
    {
        var learner = new CalibrationLearner(Fast);
        Feed(learner, 10, batteryW: 25, brightness: 0.3);
        learner.GetBaseline(CalibrationBuckets.For(0.3, true)).ShouldNotBeNull();
        learner.GetBaseline(CalibrationBuckets.For(0.9, true)).ShouldBeNull();
        learner.GetBaseline(CalibrationBuckets.DisplayOff).ShouldBeNull();
    }

    [Fact]
    public void Ticks_on_ac_or_marked_suspect_are_ignored()
    {
        var learner = new CalibrationLearner(Fast);
        learner.Observe(TestData.Laptop(battery: 25, onBattery: false), 10, 2, 4);
        learner.Observe(TestData.Laptop(battery: 25, onBattery: true, suspect: true), 10, 2, 4);
        learner.Observe(TestData.Laptop(battery: null, onBattery: true), 10, 2, 4);
        learner.Observe(TestData.Laptop(battery: -5, onBattery: true), 10, 2, 4);
        learner.Observe(TestData.Laptop(battery: 0, onBattery: true), 10, 2, 4);
        learner.TotalSamples.ShouldBe(0);
    }

    [Fact]
    public void Export_and_import_round_trip()
    {
        var learner = new CalibrationLearner(Fast);
        Feed(learner, 10, batteryW: 25, brightness: 0.6);
        Feed(learner, 10, batteryW: 21, displayOn: false);
        var state = learner.Export();
        state.Buckets.Count.ShouldBe(2);

        var restored = new CalibrationLearner(Fast);
        restored.Import(state);
        restored.TotalSamples.ShouldBe(20);
        restored.GetBaseline(CalibrationBuckets.For(0.6, true)).ShouldBe(9.0, 0.0001);
        restored.GetBaseline(CalibrationBuckets.DisplayOff).ShouldBe(21 - 10 - 2 - 4, 0.0001);
    }

    [Fact]
    public void Import_skips_corrupt_buckets()
    {
        var learner = new CalibrationLearner(Fast);
        learner.Import(new CalibrationState([
            new BucketState(6, 9.0, 10),
            new BucketState(7, double.NaN, 10),
            new BucketState(8, -1.0, 10),
            new BucketState(9, 5.0, 0),
        ]));
        learner.TotalSamples.ShouldBe(10);
        learner.GetBaseline(CalibrationBuckets.For(0.6, true)).ShouldBe(9.0, 0.0001);
        learner.GetBaseline(CalibrationBuckets.For(0.7, true)).ShouldBeNull();
    }

    [Fact]
    public void Reset_forgets_everything()
    {
        var learner = new CalibrationLearner(Fast);
        Feed(learner, 10, batteryW: 25);
        learner.Reset();
        learner.TotalSamples.ShouldBe(0);
        learner.GetBaseline(CalibrationBuckets.For(0.6, true)).ShouldBeNull();
    }

    [Fact]
    public void A_learned_baseline_turns_ac_readings_calibrated()
    {
        var learner = new CalibrationLearner(Fast);
        Feed(learner, 10, batteryW: 25);
        var model = new PowerModel(MachineProfile.DefaultLaptop, HardwareFacts.LaptopDefaults, new PowerModelOptions(), learner);
        var r = model.Evaluate(TestData.Laptop(cpu: 10, gpu: 2, brightness: 0.6));
        r.Quality.ShouldBe(Quality.Calibrated);
        r.Components.Rest.ShouldBe(9.0, 0.0001);
    }
}
