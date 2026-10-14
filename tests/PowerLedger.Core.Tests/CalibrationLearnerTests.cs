using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class CalibrationLearnerTests
{
    // Small trust thresholds so tests stay fast; production defaults are 300 / 1800 seconds and a six-hour half-life.
    private static readonly CalibrationOptions Fast = new(MinBucketSeconds: 5, MinTotalSeconds: 5);

    private const double Gpu = 2, Display = 4;

    private static readonly int Bright60 = CalibrationBuckets.For(0.6, true);

    /// <summary>Feeds <paramref name="ticks"/> one-second battery ticks from <paramref name="start"/>, each with the CPU at
    /// <paramref name="cpuW"/> and the battery at what the CPU, the 2 W GPU, the 4 W display and <paramref name="restW"/>
    /// add up to; returns the time after the last.</summary>
    private static DateTimeOffset Feed(
        CalibrationLearner learner, DateTimeOffset start, int ticks, double cpuW, double restW,
        double brightness = 0.6, bool displayOn = true)
    {
        for (var i = 0; i < ticks; i++)
        {
            var s = TestData.Laptop(cpu: cpuW, gpu: Gpu, battery: cpuW + Gpu + Display + restW, onBattery: true,
                brightness: brightness, displayOn: displayOn, ts: start.AddSeconds(i));
            learner.Observe(s, cpuW, Gpu, Display);
        }
        return start.AddSeconds(ticks);
    }

    /// <summary>Feeds whole minutes in turn at each CPU level, the rest of the laptop drawing
    /// <paramref name="interceptW"/> plus <paramref name="perCpuWatt"/> of each CPU watt.</summary>
    private static DateTimeOffset Minutes(
        CalibrationLearner learner, DateTimeOffset start, int minutes, double[] cpuLevels, double interceptW, double perCpuWatt,
        double brightness = 0.6)
    {
        var at = start;
        for (var m = 0; m < minutes; m++)
        {
            var cpu = cpuLevels[m % cpuLevels.Length];
            at = Feed(learner, at, 60, cpu, interceptW + perCpuWatt * cpu, brightness);
        }
        return at;
    }

    private static RestOfSystem Rest(CalibrationLearner learner, int bucket) => ((IBaselineProvider)learner).GetRest(bucket).ShouldNotBeNull();

    [Fact]
    public void Nothing_is_reported_before_the_minimum_sample_counts()
    {
        var learner = new CalibrationLearner(new CalibrationOptions(MinBucketSeconds: 50, MinTotalSeconds: 100));
        Feed(learner, TestData.T0, 40, cpuW: 10, restW: 9);
        learner.GetBaseline(Bright60).ShouldBeNull();
        ((IBaselineProvider)learner).GetRest(Bright60).ShouldBeNull();
    }

    [Fact]
    public void Trust_counts_seconds_on_battery_so_five_second_ticks_take_no_longer_than_one_second_ticks()
    {
        // Nobody watching, the service reads every five seconds: ten minutes on battery is 120 ticks, and must be ten minutes.
        var learner = new CalibrationLearner(new CalibrationOptions(MinBucketSeconds: 300, MinTotalSeconds: 600));
        var at = TestData.T0;
        for (var i = 0; i < 119; i++, at = at.AddSeconds(5))
        {
            learner.Observe(TestData.Laptop(cpu: 10, gpu: Gpu, battery: 10 + Gpu + Display + 9, onBattery: true, delta: 5, ts: at), 10, Gpu, Display);
        }
        learner.TotalSeconds.ShouldBe(595);
        learner.GetBaseline(Bright60).ShouldBeNull();
        learner.Observe(TestData.Laptop(cpu: 10, gpu: Gpu, battery: 10 + Gpu + Display + 9, onBattery: true, delta: 5, ts: at), 10, Gpu, Display);
        learner.TotalSeconds.ShouldBe(600);
        learner.GetBaseline(Bright60).ShouldNotBeNull();

        // A tick after a long stall counts no more than the longest pause.
        learner.Observe(TestData.Laptop(cpu: 10, gpu: Gpu, battery: 21, onBattery: true, delta: 300, ts: at.AddSeconds(300)), 10, Gpu, Display);
        learner.TotalSeconds.ShouldBe(600 + (int)CalibrationLearner.LongestPauseSeconds);
    }

    [Fact]
    public void A_constant_observation_is_learned_exactly_at_its_cpu_with_the_prior_slope()
    {
        var learner = new CalibrationLearner(Fast);
        Feed(learner, TestData.T0, 10, cpuW: 10, restW: 9);
        var rest = Rest(learner, Bright60);
        rest.At(10).ShouldBe(9.0, 1e-9);                                   // what was seen, at the CPU it was seen at
        rest.PerCpuWatt.ShouldBe(PowerModel.LaptopRestPerCpuWatt, 1e-9);   // one CPU level says nothing of the slope
        learner.LearnedRestPerCpuWatt.ShouldBeNull();
        learner.Export().Buckets.Single().BaselineW.ShouldBe(9.0, 1e-9);
    }

    [Fact]
    public void The_rest_of_the_laptop_is_learned_as_a_level_plus_a_share_of_each_cpu_watt()
    {
        var learner = new CalibrationLearner(Fast);
        Minutes(learner, TestData.T0, 120, [5, 15], interceptW: 2, perCpuWatt: 0.7);
        var rest = Rest(learner, Bright60);
        // 120 minutes 5 W either side of the mean outweigh the prior's 60 W²·min fifty to one.
        rest.PerCpuWatt.ShouldBe(0.7, 0.01);
        rest.At(5).ShouldBe(2 + 0.7 * 5, 0.05);
        rest.At(15).ShouldBe(2 + 0.7 * 15, 0.05);
        learner.LearnedRestPerCpuWatt.ShouldNotBeNull().ShouldBe(0.7, 0.01);
    }

    [Fact]
    public void Ticks_are_averaged_to_minutes_so_a_battery_rate_that_lags_the_cpu_still_gives_the_slope()
    {
        // Within each minute the CPU flips 3 W either side of its level every second while the battery, which Windows
        // updates every 20 s or so, reads the minute's level throughout: a tick-by-tick fit would see no slope inside the
        // minutes, the minute averages see it whole.
        var learner = new CalibrationLearner(Fast);
        var at = TestData.T0;
        for (var m = 0; m < 120; m++)
        {
            var level = m % 2 == 0 ? 4.0 : 12.0;
            for (var t = 0; t < 60; t++)
            {
                var cpu = level + (t % 2 == 0 ? 3 : -3);
                var s = TestData.Laptop(cpu: cpu, gpu: Gpu, battery: Gpu + Display + 1 + 1.6 * level, onBattery: true, ts: at);
                learner.Observe(s, cpu, Gpu, Display);
                at = at.AddSeconds(1);
            }
        }
        Rest(learner, Bright60).PerCpuWatt.ShouldBe(0.6, 0.02);
    }

    [Theory]
    [InlineData(3.0, CalibrationLearner.MostRestPerCpuWatt)]
    [InlineData(-0.5, 0.0)]
    public void The_share_of_each_cpu_watt_stays_within_its_bounds(double perCpuWatt, double expected)
    {
        var learner = new CalibrationLearner(Fast);
        Minutes(learner, TestData.T0, 120, [5, 15], interceptW: 10, perCpuWatt: perCpuWatt);
        Rest(learner, Bright60).PerCpuWatt.ShouldBe(expected, 1e-9);
    }

    [Fact]
    public void Old_data_halves_its_weight_every_six_hours_on_battery()
    {
        var learner = new CalibrationLearner(Fast);
        var at = Minutes(learner, TestData.T0, 6 * 60, [8], interceptW: 0, perCpuWatt: 0.5);      // rest 4 W for 6 h
        Minutes(learner, at, 6 * 60, [8], interceptW: 4, perCpuWatt: 0.5);                       // then 8 W for 6 h
        // Continuous weights: the first six hours end with τ/2 of weight and are halved again, the second hold τ/2.
        Rest(learner, Bright60).At(8).ShouldBe((4 * 0.25 + 8 * 0.5) / 0.75, 0.05);
    }

    [Fact]
    public void Time_on_mains_ages_nothing()
    {
        var learner = new CalibrationLearner(Fast);
        var at = Minutes(learner, TestData.T0, 60, [8], interceptW: 0, perCpuWatt: 0.5);          // rest 4 W
        for (var i = 0; i < 6 * 3600; i += 60)
        {
            learner.Observe(TestData.Laptop(cpu: 8, battery: null, onBattery: false, ts: at.AddSeconds(i)), 8, Gpu, Display);
        }
        Minutes(learner, at.AddHours(6), 60, [8], interceptW: 4, perCpuWatt: 0.5);                // rest 8 W, same length
        // The first hour has aged by the second's hour on battery only; six hours of mains would have halved it again.
        var aged = Math.Pow(2, -1.0 / 6);
        Rest(learner, Bright60).At(8).ShouldBe((4 * aged + 8) / (aged + 1), 1e-6);
    }

    [Fact]
    public void Buckets_are_independent()
    {
        var learner = new CalibrationLearner(Fast);
        Feed(learner, TestData.T0, 10, cpuW: 10, restW: 9, brightness: 0.3);
        learner.GetBaseline(CalibrationBuckets.For(0.3, true)).ShouldNotBeNull();
        learner.GetBaseline(CalibrationBuckets.For(0.9, true)).ShouldBeNull();
        learner.GetBaseline(CalibrationBuckets.DisplayOff).ShouldBeNull();
    }

    [Fact]
    public void A_bucket_with_little_data_is_pulled_toward_the_line_through_the_others()
    {
        var learner = new CalibrationLearner(Fast);
        var at = TestData.T0;
        foreach (var brightness in new[] { 0.2, 0.4, 0.8 }) at = Minutes(learner, at, 60, [5, 15], 2, 0.5, brightness);
        Minutes(learner, at, 2, [5, 15], 12, 0.5, brightness: 0.6);                               // two odd minutes at 60 %
        // 2 minutes at 12 W against the line's 2 W or so, with the line's 10 minutes of weight: about (2 × 12 + 10 × 2) / 12,
        // a little over, as the odd minutes tilt the line a little themselves.
        Rest(learner, Bright60).InterceptW.ShouldBeInRange(3.6, 4.0);
        Rest(learner, CalibrationBuckets.For(0.4, true)).InterceptW.ShouldBe(2, 0.05);
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
        learner.TotalSeconds.ShouldBe(0);
        learner.Export().Buckets.ShouldBeEmpty();
    }

    [Fact]
    public void Export_and_import_round_trip()
    {
        var learner = new CalibrationLearner(Fast);
        var at = Minutes(learner, TestData.T0, 30, [5, 15], interceptW: 2, perCpuWatt: 0.7);
        Feed(learner, at.AddMinutes(5), 10, cpuW: 10, restW: 5, displayOn: false);
        var state = learner.Export();
        state.Buckets.Count.ShouldBe(2);

        var restored = new CalibrationLearner(Fast);
        restored.Import(state);
        restored.TotalSeconds.ShouldBe(30 * 60 + 10);
        Rest(restored, Bright60).InterceptW.ShouldBe(Rest(learner, Bright60).InterceptW, 1e-9);
        Rest(restored, Bright60).PerCpuWatt.ShouldBe(Rest(learner, Bright60).PerCpuWatt, 1e-9);
        Rest(restored, CalibrationBuckets.DisplayOff).At(10).ShouldBe(Rest(learner, CalibrationBuckets.DisplayOff).At(10), 1e-9);
        restored.LearnedRestPerCpuWatt.ShouldNotBeNull().ShouldBe(learner.LearnedRestPerCpuWatt.ShouldNotBeNull(), 1e-9);
    }

    [Fact]
    public void A_bucket_from_before_the_cpu_share_was_learned_keeps_its_level_at_a_typical_battery_cpu()
    {
        var learner = new CalibrationLearner(Fast);
        learner.Import(new CalibrationState([new BucketState(6, 3.4, 6000)]));
        var rest = Rest(learner, 6);
        rest.PerCpuWatt.ShouldBe(PowerModel.LaptopRestPerCpuWatt, 1e-9);
        rest.At(CalibrationLearner.MigratedCpuW).ShouldBe(3.4, 1e-9);
        learner.LearnedRestPerCpuWatt.ShouldBeNull();
        learner.TotalSeconds.ShouldBe(6000);

        // It weighs what the old ten-minute average held, about 14 minutes, so an hour of new data outweighs it.
        Minutes(learner, TestData.T0, 60, [5, 15], interceptW: 1, perCpuWatt: 0.5);
        Rest(learner, 6).At(10).ShouldBe(6.0, 0.25);
    }

    [Fact]
    public void Import_skips_corrupt_buckets()
    {
        var learner = new CalibrationLearner(Fast);
        learner.Import(new CalibrationState([
            new BucketState(6, 9.0, 10, CpuW: 10, Minutes: 1),
            new BucketState(7, double.NaN, 10),
            new BucketState(8, 1e9, 10),
            new BucketState(9, 5.0, 0),
            new BucketState(99, 5.0, 10),
            new BucketState(-5, 5.0, 10),
            new BucketState(1, 5.0, 10, CpuW: double.NaN, Minutes: 1),
            new BucketState(2, 5.0, 10, CpuW: 10, Minutes: 0),
            new BucketState(3, 5.0, 10, CpuW: 10, Minutes: 1, CpuSpread: -1),
            new BucketState(4, 5.0, 10, CpuW: 10, Minutes: 1, CpuDrawSpread: double.PositiveInfinity),
        ]));
        learner.TotalSeconds.ShouldBe(10);
        Rest(learner, Bright60).At(10).ShouldBe(9.0, 1e-9);
        foreach (var bucket in new[] { 1, 2, 3, 4, 7, 99, CalibrationBuckets.MaxBucket }) learner.GetBaseline(bucket).ShouldBeNull();
    }

    [Fact]
    public void Thresholds_are_inclusive_and_independent()
    {
        var learner = new CalibrationLearner(new CalibrationOptions(MinBucketSeconds: 5, MinTotalSeconds: 8));
        var at = Feed(learner, TestData.T0, 5, cpuW: 10, restW: 9, brightness: 0.6);
        learner.GetBaseline(Bright60).ShouldBeNull();                                            // bucket ready, machine not (5 < 8)
        Feed(learner, at, 3, cpuW: 10, restW: 9, brightness: 0.2);
        learner.GetBaseline(Bright60).ShouldNotBeNull();                                         // total now 8
        learner.GetBaseline(CalibrationBuckets.For(0.2, true)).ShouldBeNull();                   // bucket has 3 < 5
    }

    [Fact]
    public void Non_finite_parts_are_ignored()
    {
        var learner = new CalibrationLearner(Fast);
        learner.Observe(TestData.Laptop(battery: 25, onBattery: true), double.NaN, 2, 4);
        learner.Observe(TestData.Laptop(battery: 25, onBattery: true), 10, double.PositiveInfinity, 4);
        learner.TotalSeconds.ShouldBe(0);
    }

    [Fact]
    public void A_negative_rest_is_kept_raw_but_never_reported_below_zero()
    {
        var learner = new CalibrationLearner(Fast);
        Feed(learner, TestData.T0, 10, cpuW: 10, restW: -6);
        Rest(learner, Bright60).At(10).ShouldBe(0.0, 1e-9);
        learner.Export().Buckets.Single().BaselineW.ShouldBe(-6.0, 1e-9);
    }

    [Fact]
    public void A_pause_or_a_new_bucket_closes_the_minute_being_averaged()
    {
        // Two half minutes at different CPU levels, split by a pause, are two observations; run together they would be one
        // and would say nothing of the slope.
        var split = new CalibrationLearner(Fast);
        var at = Feed(split, TestData.T0, 30, cpuW: 5, restW: 1 + 0.5 * 5);
        Feed(split, at.AddSeconds(30), 30, cpuW: 15, restW: 1 + 0.5 * 15);
        split.LearnedRestPerCpuWatt.ShouldNotBeNull();

        var joined = new CalibrationLearner(Fast);
        at = Feed(joined, TestData.T0, 30, cpuW: 5, restW: 1 + 0.5 * 5);
        Feed(joined, at, 30, cpuW: 15, restW: 1 + 0.5 * 15);
        joined.LearnedRestPerCpuWatt.ShouldBeNull();

        var bucketed = new CalibrationLearner(Fast);
        at = Feed(bucketed, TestData.T0, 30, cpuW: 5, restW: 3.5, brightness: 0.6);
        Feed(bucketed, at, 30, cpuW: 15, restW: 8.5, brightness: 0.2);
        bucketed.Export().Buckets.Count.ShouldBe(2);
    }

    [Fact]
    public void Invalid_options_throw()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new CalibrationLearner(new CalibrationOptions(HalfLifeHours: 0)));
        Should.Throw<ArgumentOutOfRangeException>(() => new CalibrationLearner(new CalibrationOptions(HalfLifeHours: double.NaN)));
        Should.Throw<ArgumentOutOfRangeException>(() => new CalibrationLearner(new CalibrationOptions(MinBucketSeconds: -1)));
        Should.Throw<ArgumentOutOfRangeException>(() => new CalibrationLearner(new CalibrationOptions(MinTotalSeconds: 0)));
    }

    [Fact]
    public void Reset_forgets_everything()
    {
        var learner = new CalibrationLearner(Fast);
        Feed(learner, TestData.T0, 10, cpuW: 10, restW: 9);
        learner.Reset();
        learner.TotalSeconds.ShouldBe(0);
        learner.GetBaseline(Bright60).ShouldBeNull();
        learner.Export().Buckets.ShouldBeEmpty();
    }

    [Fact]
    public void A_learned_bucket_turns_ac_readings_calibrated_with_the_rest_scaling_with_the_cpu()
    {
        var learner = new CalibrationLearner(Fast);
        Minutes(learner, TestData.T0, 120, [5, 15], interceptW: 2, perCpuWatt: 0.7);
        var model = new PowerModel(MachineProfile.DefaultLaptop, HardwareFacts.LaptopDefaults, new PowerModelOptions(), learner);
        var r = model.Evaluate(TestData.Laptop(cpu: 20, gpu: 2, brightness: 0.6));
        r.Quality.ShouldBe(Quality.Calibrated);
        r.Components.Unattributed.ShouldBe(2 + 0.7 * 20, 0.25);
    }
}
