using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.App.Tests;

public class DaySlotsTests
{
    private static readonly DateTimeOffset Day = new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);

    private static Aggregate Minute(int minuteOfDay, double cpuWh = 0.2, double restWh = 0.3, double onSeconds = 60, double gapSeconds = 0)
        => Aggregate.Empty(Day.AddMinutes(minuteOfDay)) with
        {
            CpuWh = cpuWh, RestWh = restWh, EnergyWh = cpuWh + restWh, OnSeconds = onSeconds, GapSeconds = gapSeconds,
        };

    [Fact]
    public void A_slot_averages_its_minutes_over_the_time_the_machine_was_on()
    {
        var slots = DaySlots.Build([Minute(0), Minute(1)], Day, Day.AddMinutes(4));
        slots.Count.ShouldBe(1);
        slots[0].CpuW.ShouldBe(12, 1e-9);                          // 0.4 Wh over 120 s
        slots[0].RestW.ShouldBe(18, 1e-9);
        slots[0].TotalW.ShouldBe(30, 1e-9);
        slots[0].HasReadings.ShouldBeTrue();
    }

    [Fact]
    public void Slots_run_up_to_the_one_that_holds_now()
    {
        DaySlots.Build([], Day, Day.AddMinutes(60)).Count.ShouldBe(12);
        DaySlots.Build([], Day, Day.AddMinutes(61)).Count.ShouldBe(13);
        DaySlots.Build([], Day, Day).Count.ShouldBe(1);
    }

    [Fact]
    public void A_sleep_is_laid_backwards_over_the_slots_it_covered()
    {
        // Two hours asleep, closed by a tick in the minute that starts at 02:00, so the sleep ran from 00:01 to 02:01.
        var slots = DaySlots.Build([Minute(120, onSeconds: 30, gapSeconds: 7200)], Day, Day.AddMinutes(125));
        slots[0].AsleepSeconds.ShouldBe(240, 1e-9);
        slots.Skip(1).Take(23).ShouldAllBe(s => Math.Abs(s.AsleepSeconds - 300) < 1e-9);
        slots[24].AsleepSeconds.ShouldBe(60, 1e-9);
        slots[24].OnSeconds.ShouldBe(30);
    }

    [Fact]
    public void A_sleep_that_began_yesterday_stops_at_midnight()
    {
        var slots = DaySlots.Build([Minute(10, gapSeconds: 3600)], Day, Day.AddMinutes(15));
        slots.Sum(s => s.AsleepSeconds).ShouldBe(660, 1e-9);        // 00:00 to 00:11
    }

    [Fact]
    public void A_negative_rest_draws_as_zero()
    {
        var slot = DaySlots.Build([Minute(0, cpuWh: 0.5, restWh: -0.1)], Day, Day.AddMinutes(1))[0];
        slot.RestW.ShouldBe(-6, 1e-9);
        slot.TotalW.ShouldBe(30, 1e-9);
    }
}
