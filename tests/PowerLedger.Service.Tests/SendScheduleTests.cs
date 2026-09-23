using PowerLedger.Service.Sharing;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>When a day's upload goes (data-sharing design §4): at the install's minute each night, at the first chance after
/// it, after a back-off, or when asked, and only when a complete day waits.</summary>
public class SendScheduleTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("PL+2", TimeSpan.FromHours(2), "PL+2", "PL+2");

    /// <summary>01:30 local on the 24th: minute 90.</summary>
    private static readonly DateTimeOffset SendTime = new(2026, 9, 23, 23, 30, 0, TimeSpan.Zero);

    private static bool Due(DateTimeOffset now, long? lastRun = null, Backoff? backoff = null, bool waits = true, bool asked = false) =>
        SendSchedule.Due(now, Zone, sendMinute: 90, lastRun, backoff, waits, asked);

    [Fact]
    public void Tonights_minute_is_due_once_and_not_before()
    {
        Due(SendTime.AddMinutes(-1)).ShouldBeFalse();
        Due(SendTime).ShouldBeTrue();
        Due(SendTime.AddHours(7)).ShouldBeTrue();                                    // a missed night, caught up later
        Due(SendTime.AddMinutes(5), lastRun: SendTime.ToUnixTimeMilliseconds()).ShouldBeFalse();
        Due(SendTime.AddMinutes(-1), lastRun: SendTime.AddHours(-24).ToUnixTimeMilliseconds()).ShouldBeFalse();
        Due(SendTime.AddDays(1), lastRun: SendTime.ToUnixTimeMilliseconds()).ShouldBeTrue();  // the next night
    }

    [Fact]
    public void Nothing_is_due_without_a_complete_day_waiting()
    {
        Due(SendTime, waits: false).ShouldBeFalse();
        Due(SendTime, waits: false, asked: true).ShouldBeFalse();
    }

    [Fact]
    public void A_back_off_is_due_once_it_has_passed_and_send_now_whenever_a_day_waits()
    {
        var ran = SendTime.ToUnixTimeMilliseconds();
        var backoff = new Backoff(1, SendTime.AddHours(1).ToUnixTimeMilliseconds());
        Due(SendTime.AddMinutes(59), ran, backoff).ShouldBeFalse();
        Due(SendTime.AddHours(1), ran, backoff).ShouldBeTrue();
        Due(SendTime.AddMinutes(1), ran, backoff, asked: true).ShouldBeTrue();
        Due(SendTime.AddHours(-3), asked: true).ShouldBeTrue();
    }

    [Fact]
    public void Failed_runs_wait_one_two_four_eight_sixteen_then_twenty_four_hours()
    {
        var steps = new List<double>();
        Backoff? backoff = null;
        for (var run = 0; run < 8; run++)
        {
            backoff = SendSchedule.After(backoff, SendTime);
            steps.Add(TimeSpan.FromMilliseconds(backoff.NextMs - SendTime.ToUnixTimeMilliseconds()).TotalHours);
        }
        steps.ShouldBe(new double[] { 1, 2, 4, 8, 16, 24, 24, 24 });
        backoff!.Failures.ShouldBe(8);
    }

    [Fact]
    public void A_send_minute_the_clocks_skip_goes_at_the_first_time_after_it()
    {
        // 29 March 2026 in London: 01:00 GMT becomes 02:00 BST, so 01:30 never happens and 02:00 BST, 01:00 UTC, is next.
        var london = MinuteBuilderTests.London;
        SendSchedule.SendTime(new DateOnly(2026, 3, 29), 90, london).ShouldBe(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero));
        SendSchedule.SendTime(new DateOnly(2026, 9, 24), 90, london).ShouldBe(new DateTimeOffset(2026, 9, 24, 0, 30, 0, TimeSpan.Zero));
    }
}
