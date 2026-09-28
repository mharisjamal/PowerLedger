using Shouldly;
using static PowerLedger.App.Tests.InsightsSeries;

namespace PowerLedger.App.Tests;

/// <summary>Aero look design §4, Habits: the 7 × 24 idle heatmap over 4 weeks, the worst 2-hour window, and the saving a
/// month from sleeping after 10 idle minutes.</summary>
public class IdleHabitsTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateOnly Today = new(2026, 9, 15);

    /// <summary>100 Wh idle for the whole hour at 22:00 and 23:00 every evening, and nothing idle otherwise.</summary>
    private static (double, double) Evenings(DateTime local) => local.Hour >= 22 ? (0.1, 3600) : (0, 0);

    private static List<HourUse> Days(int days, Func<DateTime, (double, double)> idle, TimeZoneInfo? zone = null, DateOnly? today = null)
        => Hours((today ?? Today).AddDays(-days), days + 1, zone ?? Utc, _ => 0.2, idle);

    [Fact]
    public void Idle_evenings_fill_their_cells_and_are_the_worst_window()
    {
        var habits = Habits.From(Days(28, Evenings), Today, 0.20m, "GBP").ShouldNotBeNull();

        for (var day = 0; day < 7; day++)
        {
            for (var hour = 0; hour < 24; hour++) habits.Heatmap[day, hour].ShouldBe(hour >= 22 ? 100 : 0, 1e-9);
        }
        (habits.WorstWindowStart, habits.WorstWindowHours).ShouldBe((22, 2));
    }

    [Fact]
    public void The_saving_keeps_the_first_ten_minutes_of_a_spell_awake()
    {
        var habits = Habits.From(Days(28, Evenings), Today, 0.20m, "GBP").ShouldNotBeNull();

        // Each evening: 22:00's 100 Wh less its first 10 minutes (83.3 Wh), and all of 23:00's, which carries on the spell.
        var perDay = 0.1 * 50 / 60 + 0.1;
        habits.SavingPerMonthKwh.ShouldBe(perDay * 365.25 / 12, 1e-9);
        habits.SavingPerMonthCost.ShouldBe(decimal.Round((decimal)(perDay * 365.25 / 12) * 0.20m, 2));
        habits.Currency.ShouldBe("GBP");
    }

    [Fact]
    public void A_short_idle_spell_saves_nothing()
    {
        var habits = Habits.From(Days(28, local => local.Hour == 12 ? (0.01, 540) : (0, 0)), Today, 1m, "USD").ShouldNotBeNull();

        habits.SavingPerMonthKwh.ShouldBe(0);
        habits.SavingPerMonthCost.ShouldBe(0m);
    }

    [Fact]
    public void The_worst_window_may_run_past_midnight()
    {
        var habits = Habits.From(Days(28, local => local.Hour is 23 or 0 ? (0.1, 3600) : (0, 0)), Today, 1m, "USD").ShouldNotBeNull();

        habits.WorstWindowStart.ShouldBe(23);
    }

    [Fact]
    public void Weekend_habits_land_on_their_own_days()
    {
        var habits = Habits.From(Days(28, local => local.DayOfWeek == DayOfWeek.Saturday && local.Hour == 10 ? (0.3, 3600) : (0, 0)), Today, 1m, "USD")
            .ShouldNotBeNull();

        habits.Heatmap[(int)DayOfWeek.Saturday, 10].ShouldBe(300, 1e-9);
        habits.Heatmap[(int)DayOfWeek.Sunday, 10].ShouldBe(0);
        habits.Heatmap[(int)DayOfWeek.Friday, 10].ShouldBe(0);
    }

    [Fact]
    public void Only_the_last_four_weeks_count()
    {
        var cut = Today.AddDays(-28);
        var habits = Habits.From(Days(56, local => DateOnly.FromDateTime(local) < cut ? (0.5, 3600) : (0, 0)), Today, 1m, "USD").ShouldNotBeNull();

        habits.SavingPerMonthKwh.ShouldBe(0);
        habits.Heatmap.Cast<double>().Sum().ShouldBe(0);
    }

    [Fact]
    public void Fewer_than_seven_whole_days_says_nothing_yet()
    {
        Habits.From(Days(6, Evenings), Today, 1m, "USD").ShouldBeNull();
        Habits.From(Days(7, Evenings), Today, 1m, "USD").ShouldNotBeNull();
    }

    [Fact]
    public void A_short_history_is_averaged_over_the_days_it_has()
    {
        var habits = Habits.From(Days(10, Evenings), Today, 1m, "USD").ShouldNotBeNull();

        habits.Heatmap[(int)DayOfWeek.Monday, 22].ShouldBe(100, 1e-9);
        habits.SavingPerMonthKwh.ShouldBe((0.1 * 50 / 60 + 0.1) * 365.25 / 12, 1e-9);
    }

    /// <summary>London's clocks went back on 25 October 2026: that Sunday has two 01:00 hours, and its idle cell holds both
    /// (it is averaged over the dates, not the hours).</summary>
    [Fact]
    public void A_clock_change_day_keeps_all_its_hours()
    {
        var habits = Habits.From(Days(28, local => local.Hour == 1 ? (0.1, 3600) : (0, 0), London, new DateOnly(2026, 10, 27)), new DateOnly(2026, 10, 27), 1m, "USD")
            .ShouldNotBeNull();

        // Four Sundays at 01:00: three of 100 Wh, and the one with two such hours, 200: 125 on average.
        habits.Heatmap[(int)DayOfWeek.Sunday, 1].ShouldBe(125, 1e-9);
        habits.Heatmap[(int)DayOfWeek.Monday, 1].ShouldBe(100, 1e-9);
    }

    [Fact]
    public void Without_a_tariff_the_cost_is_zero()
    {
        var habits = Habits.From(Days(28, Evenings), Today, 0m, null).ShouldNotBeNull();

        (habits.SavingPerMonthCost, habits.Currency).ShouldBe((0m, (string?)null));
        habits.SavingPerMonthKwh.ShouldBeGreaterThan(0);
    }
}
