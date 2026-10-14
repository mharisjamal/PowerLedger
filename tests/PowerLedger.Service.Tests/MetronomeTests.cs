using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class MetronomeTests
{
    [Fact]
    public void The_system_clock_gets_a_windows_waitable_timer_and_any_other_clock_a_timer_of_its_own()
    {
        using (var system = Metronome.For(TimeProvider.System)) system.ShouldBeOfType<WaitableTimerMetronome>();
        using var fake = Metronome.For(new FakeTimeProvider());
        fake.ShouldBeOfType<ClockMetronome>();
    }

    [Fact]
    public void A_waitable_timer_beats_once_a_period_and_beats_missed_meanwhile_come_as_one()
    {
        using var metronome = new WaitableTimerMetronome();
        metronome.Beat.WaitOne(0).ShouldBeFalse();
        var clock = Stopwatch.StartNew();
        metronome.Start(TimeSpan.FromMilliseconds(100));
        metronome.Beat.WaitOne(TimeSpan.FromSeconds(5)).ShouldBeTrue();
        clock.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(80);                // timer resolution may round the first a little

        Thread.Sleep(450);                                                         // four beats or so go by untaken
        metronome.Beat.WaitOne(0).ShouldBeTrue();
        metronome.Beat.WaitOne(0).ShouldBeFalse();                                 // only one stands
        metronome.Beat.WaitOne(TimeSpan.FromSeconds(5)).ShouldBeTrue();            // and it keeps beating
    }

    [Fact]
    public void Starting_a_waitable_timer_again_drops_a_beat_standing_and_stopping_it_ends_the_beats()
    {
        using var metronome = new WaitableTimerMetronome();
        metronome.Start(TimeSpan.FromMilliseconds(30));
        Thread.Sleep(200);
        metronome.Start(TimeSpan.FromSeconds(30));
        metronome.Beat.WaitOne(0).ShouldBeFalse();

        metronome.Start(TimeSpan.FromMilliseconds(30));
        Thread.Sleep(200);
        metronome.Stop();
        metronome.Beat.WaitOne(300).ShouldBeFalse();
    }

    [Fact]
    public void A_clock_metronome_beats_as_its_clock_moves_and_stops_when_told()
    {
        var clock = new FakeTimeProvider();
        using var metronome = new ClockMetronome(clock);
        metronome.Start(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(4));
        metronome.Beat.WaitOne(0).ShouldBeFalse();
        clock.Advance(TimeSpan.FromSeconds(11));                                   // three beats, taken as one
        metronome.Beat.WaitOne(0).ShouldBeTrue();
        metronome.Beat.WaitOne(0).ShouldBeFalse();

        clock.Advance(TimeSpan.FromSeconds(4));
        metronome.Start(TimeSpan.FromSeconds(2));                                  // a new period starts from now
        clock.Advance(TimeSpan.FromSeconds(1));
        metronome.Beat.WaitOne(0).ShouldBeFalse();
        clock.Advance(TimeSpan.FromSeconds(1));
        metronome.Beat.WaitOne(0).ShouldBeTrue();

        metronome.Stop();
        clock.Advance(TimeSpan.FromSeconds(10));
        metronome.Beat.WaitOne(0).ShouldBeFalse();
    }
}
