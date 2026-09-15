using Shouldly;

namespace PowerLedger.App.Tests;

public class IdleAdviceTests
{
    private static SleepTimeouts Plugged(double sleepMinutes, double displayMinutes = 10)
        => new(TimeSpan.FromMinutes(sleepMinutes), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(displayMinutes), TimeSpan.FromMinutes(5));

    [Fact]
    public void Little_idle_energy_needs_no_advice()
    {
        IdleAdvice.For(idleKwh: 0.004, idleDisplayOnKwh: 0.004, energyKwh: 0.05, Plugged(30)).ShouldBe("Idle time used little energy in this range.");
        IdleAdvice.For(idleKwh: 0.02, idleDisplayOnKwh: 0.02, energyKwh: 1, Plugged(30)).ShouldBe("Idle time used little energy in this range.");
    }

    [Fact]
    public void A_plan_that_never_sleeps_is_named()
        => IdleAdvice.For(0.4, 0.3, 2.7, Plugged(0))
            .ShouldBe("Windows never sleeps here when plugged in. Sleeping after 30 minutes idle would cut most of it.");

    [Fact]
    public void A_long_timeout_is_quoted()
        => IdleAdvice.For(0.4, 0.3, 2.7, Plugged(180))
            .ShouldBe("Windows sleeps after 3 h idle when plugged in. Sleeping after 30 minutes would cut much of it.");

    [Fact]
    public void A_short_timeout_with_the_display_left_on_suggests_turning_it_off()
    {
        IdleAdvice.For(0.4, 0.3, 2.7, Plugged(20, displayMinutes: 15))
            .ShouldBe("Windows already sleeps after 20 min, but the display stays on for 15 min. Turning it off after 5 minutes would save a little more.");
        IdleAdvice.For(0.4, 0.3, 2.7, Plugged(20, displayMinutes: 0))
            .ShouldBe("Windows already sleeps after 20 min, but the display stays on while idle. Turning it off after 5 minutes would save a little more.");
    }

    [Fact]
    public void A_short_timeout_leaves_little_to_save()
        => IdleAdvice.For(0.4, 0.1, 2.7, Plugged(15))
            .ShouldBe("Windows already sleeps after 15 min idle when plugged in, so little more can be saved.");

    [Fact]
    public void An_unknown_timeout_gives_the_general_advice()
        => IdleAdvice.For(0.4, 0.3, 2.7, SleepTimeouts.Unknown)
            .ShouldBe("Letting Windows sleep sooner when the machine is idle would cut most of it.");

    [Theory]
    [InlineData(45, "45 min")]
    [InlineData(180, "3 h")]
    [InlineData(90, "1 h 30 min")]
    public void Spans_read_naturally(int minutes, string text) => IdleAdvice.Span(TimeSpan.FromMinutes(minutes)).ShouldBe(text);
}
