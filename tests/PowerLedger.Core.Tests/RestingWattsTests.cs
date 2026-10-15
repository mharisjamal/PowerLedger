using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class RestingWattsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A minute of one-second ticks from <paramref name="minute"/> minutes after T0, at rest or not.</summary>
    private static void Minute(RestingWatts rest, int minute, double watts, bool atRest = true, int restSeconds = 60)
    {
        for (var s = 0; s < 60; s++) rest.Observe(T0.AddMinutes(minute).AddSeconds(s), watts, 1, atRest && s < restSeconds);
    }

    [Fact]
    public void Nothing_learned_is_nought_and_a_busy_tick_teaches_nothing()
    {
        var rest = new RestingWatts();
        rest.Watts.ShouldBe(0);
        rest.Observe(T0, 15, 1, atRest: false);
        rest.Watts.ShouldBe(0);
    }

    [Fact]
    public void Before_a_whole_quiet_minute_the_lowest_tick_at_rest_stands_in()
    {
        var rest = new RestingWatts();
        rest.Observe(T0, 4, 1, atRest: true);
        rest.Observe(T0.AddSeconds(1), 3, 1, atRest: true);
        rest.Observe(T0.AddSeconds(2), 1, 1, atRest: false);       // busy, however low
        rest.Observe(T0.AddSeconds(3), double.NaN, 1, atRest: true);
        rest.Watts.ShouldBe(3);
    }

    [Fact]
    public void A_quiet_minute_counts_once_it_ends_and_only_with_half_its_time_at_rest()
    {
        var rest = new RestingWatts();
        Minute(rest, 0, 6, restSeconds: 20);                        // a third at rest: not a quiet minute
        Minute(rest, 1, 7);
        rest.Watts.ShouldBe(6);                                     // the lowest tick so far; minute 1 has not ended
        rest.Observe(T0.AddMinutes(2), 9, 1, atRest: false);
        rest.Watts.ShouldBe(7, 1e-12);                              // the one quiet minute
    }

    [Fact]
    public void The_estimate_is_the_tenth_percentile_of_the_last_sixty_quiet_minutes()
    {
        var rest = new RestingWatts();
        for (var m = 0; m < 60; m++) Minute(rest, m, 5);
        for (var m = 60; m < 65; m++) Minute(rest, m, 2);
        rest.Observe(T0.AddMinutes(65), 9, 1, atRest: false);
        rest.Watts.ShouldBe(5, 1e-12);                              // five low minutes of sixty: under the tenth percentile

        Minute(rest, 65, 2);
        rest.Observe(T0.AddMinutes(66), 9, 1, atRest: false);
        rest.Watts.ShouldBe(2, 1e-12);                              // the sixth is the tenth percentile

        for (var m = 66; m < 126; m++) Minute(rest, m, 7);          // a whole hour of quiet minutes at 7 W replaces them
        rest.Observe(T0.AddMinutes(126), 9, 1, atRest: false);
        rest.Watts.ShouldBe(7, 1e-12);
    }

    [Fact]
    public void A_minute_counts_its_average_by_time_so_long_ticks_weigh_more()
    {
        var rest = new RestingWatts();
        rest.Observe(T0.AddSeconds(5), 2, 5, atRest: true);
        rest.Observe(T0.AddSeconds(50), 8, 45, atRest: true);
        rest.Observe(T0.AddMinutes(1), 1, 1, atRest: false);
        rest.Watts.ShouldBe((2 * 5 + 8 * 45) / 50.0, 1e-12);
    }
}
