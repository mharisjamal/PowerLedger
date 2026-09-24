using System.Globalization;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.App.Tests;

public class ChartTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static Aggregate Bucket(DateTimeOffset start, double cpuWh, double restWh, double onSeconds, double gapSeconds = 0)
        => Aggregate.Empty(start) with { CpuWh = cpuWh, RestWh = restWh, EnergyWh = cpuWh + restWh, OnSeconds = onSeconds, GapSeconds = gapSeconds };

    [Fact]
    public void A_bucket_in_watts_is_its_energy_over_its_time_on()
    {
        var bucket = Charts.Buckets([Bucket(Now, cpuWh: 0.4, restWh: 0.6, onSeconds: 120, gapSeconds: 30)], ChartUnit.Watts).Single();
        bucket.Cpu.ShouldBe(12, 1e-9);
        bucket.Rest.ShouldBe(18, 1e-9);
        bucket.Total.ShouldBe(30, 1e-9);
        bucket.AsleepSeconds.ShouldBe(30);
        Charts.Buckets([Bucket(Now, 0.4, 0.6, onSeconds: 0)], ChartUnit.Watts).Single().Total.ShouldBe(0);
    }

    [Fact]
    public void A_bucket_in_watt_hours_is_its_energy()
    {
        var bucket = Charts.Buckets([Bucket(Now, cpuWh: 0.4, restWh: 0.6, onSeconds: 120)], ChartUnit.WattHours).Single();
        bucket.Cpu.ShouldBe(0.4, 1e-9);
        bucket.Total.ShouldBe(1, 1e-9);
    }

    [Fact]
    public void A_negative_rest_draws_as_zero()
        => Charts.Buckets([Bucket(Now, cpuWh: 0.5, restWh: -0.1, onSeconds: 60)], ChartUnit.Watts).Single().Total.ShouldBe(30, 1e-9);

    [Fact]
    public void A_day_is_marked_every_six_hours()
        => Charts.Ticks(Ranges.Today(Now, Utc, English), Utc, English)
            .ShouldBe(new AxisTick[] { new(0, "00:00"), new(72, "06:00"), new(144, "12:00"), new(216, "18:00") });

    [Fact]
    public void An_hour_is_marked_at_each_quarter_inside_it()
        => Charts.Ticks(Ranges.LastHour(Now, Utc, English), Utc, English)
            .ShouldBe(new AxisTick[] { new(12, "13:45"), new(27, "14:00"), new(42, "14:15"), new(57, "14:30") });

    [Fact]
    public void A_week_is_marked_at_each_midnight()
    {
        var ticks = Charts.Ticks(Ranges.LastDays(7, Now, Utc, English), Utc, English);
        ticks.Select(t => t.At).ShouldBe(new double[] { 0, 24, 48, 72, 96, 120, 144 });
        ticks[0].Label.ShouldBe("Wed 2");
        ticks[^1].Label.ShouldBe("Tue 8");
    }

    [Fact]
    public void Thirty_days_are_marked_weekly_and_a_long_range_at_month_starts()
    {
        var month = Charts.Ticks(Ranges.LastDays(30, Now, Utc, English), Utc, English);
        month.Select(t => t.At).ShouldBe(new double[] { 0, 28, 56, 84, 112 });
        month.Select(t => t.Label).ShouldBe(new[] { "10 Aug", "17 Aug", "24 Aug", "31 Aug", "7 Sep" });

        var year = Charts.Ticks(Ranges.Days(new DateOnly(2025, 11, 15), new DateOnly(2026, 9, 8), Now, Utc, English), Utc, English);
        year.Select(t => t.Label).Take(3).ShouldBe(new[] { "Dec 2025", "Jan 2026", "Feb" });
    }

    [Fact]
    public void Ticks_follow_the_clock_on_a_day_that_springs_forward()
    {
        var europe = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var day = Ranges.Today(new DateTimeOffset(2026, 3, 29, 12, 0, 0, TimeSpan.Zero), europe, English);
        day.Capacity.ShouldBe(276);                                              // a 23-hour day
        Charts.Ticks(day, europe, English).Select(t => t.At).ShouldBe(new double[] { 0, 60, 132, 204 });
    }

    [Fact]
    public void Todays_chart_spans_the_day_and_marks_now()
    {
        var range = Ranges.Today(Now, Utc, English);
        var chart = Charts.Build(range, [Bucket(range.From, 0.5, 0.5, 300)], ChartUnit.Watts, Utc, English);

        chart.Capacity.ShouldBe(288);
        chart.Bucket.ShouldBe(TimeSpan.FromMinutes(5));
        chart.NowAt.ShouldNotBeNull().ShouldBe(174.4, 1e-9);
        chart.Description.ShouldBe("Today: power by component in watts, stacked from the rest of the system up to the CPU. Peak 12 W.");
    }

    [Fact]
    public void A_past_range_has_no_now_and_watt_hours_name_their_bucket()
    {
        var chart = Charts.Build(Ranges.LastMonth(Now, Utc, English), [], ChartUnit.WattHours, Utc, English);
        chart.NowAt.ShouldBeNull();
        Charts.Build(Ranges.Days(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5), Now, Utc, English), [], ChartUnit.Watts, Utc, English)
            .NowAt.ShouldBeNull();                                           // a range still to come has no now in it either
        chart.Description.ShouldBe("August 2026: power by component in watt-hours per 6 hours, stacked from the rest of the system up to the CPU. No readings in this range.");
    }

    [Fact]
    public void Scale_labels_carry_the_decimals_their_step_needs()
    {
        Format.Scale(40, 20, English).ShouldBe("40");
        Format.Scale(2.5, 0.5, English).ShouldBe("2.5");
        Format.Scale(0.25, 0.05, English).ShouldBe("0.25");
    }
}
