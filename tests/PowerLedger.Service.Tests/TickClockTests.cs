using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class TickClockTests
{
    private static readonly DateTimeOffset Start = Samples.T0;

    [Fact]
    public void The_first_tick_counts_one_interval_because_the_service_saw_nothing_before_it()
        => new TickClock(new FakeTimeProvider(Start)).Measure(Start, interval: 1).ShouldBe(1);

    [Fact]
    public void Later_ticks_measure_the_monotonic_time_since_the_last_one()
    {
        var clock = new FakeTimeProvider(Start);
        var ticks = new TickClock(clock);
        ticks.Commit(clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMilliseconds(1003));
        ticks.Measure(clock.GetUtcNow(), 1).ShouldBe(1.003, 1e-9);
    }

    [Fact]
    public void A_tick_that_was_never_committed_is_folded_into_the_next()
    {
        var clock = new FakeTimeProvider(Start);
        var ticks = new TickClock(clock);
        ticks.Commit(clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(1));
        ticks.Measure(clock.GetUtcNow(), 1).ShouldBe(1, 1e-9);
        clock.Advance(TimeSpan.FromSeconds(1));
        ticks.Measure(clock.GetUtcNow(), 1).ShouldBe(2, 1e-9);
    }

    [Fact]
    public void After_a_resume_the_wall_clock_measures_the_sleep_so_it_becomes_a_gap()
    {
        var clock = new SleepingClock(Start);
        var ticks = new TickClock(clock);
        ticks.Commit(clock.GetUtcNow());
        clock.Sleep(TimeSpan.FromHours(1));
        ticks.Measure(clock.GetUtcNow(), 1).ShouldBe(0, 1e-9);   // all a monotonic clock that stopped would say
        ticks.MarkResumed();
        ticks.Measure(clock.GetUtcNow(), 1).ShouldBe(3600, 1e-9);
        ticks.Commit(clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(1));
        ticks.Measure(clock.GetUtcNow(), 1).ShouldBe(1, 1e-9);   // back on the monotonic clock
    }

    [Fact]
    public void A_wall_clock_set_back_during_sleep_still_counts_one_interval()
    {
        var clock = new FakeTimeProvider(Start);
        var ticks = new TickClock(clock);
        ticks.Commit(Start);
        ticks.MarkResumed();
        ticks.Measure(Start.AddHours(-1), 1).ShouldBe(1);
    }

    /// <summary>A fake clock whose monotonic timestamp can stand still while the wall clock moves, as across a sleep.</summary>
    private sealed class SleepingClock(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private long _slept;

        public void Sleep(TimeSpan duration)
        {
            _slept += duration.Ticks;
            Advance(duration);
        }

        public override long GetTimestamp() => base.GetTimestamp() - _slept * TimestampFrequency / TimeSpan.TicksPerSecond;
    }
}
