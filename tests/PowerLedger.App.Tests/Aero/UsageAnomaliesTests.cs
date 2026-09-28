using Shouldly;
using static PowerLedger.App.Tests.InsightsSeries;

namespace PowerLedger.App.Tests;

/// <summary>Aero look design §4, Unusual use: each hour against the median and MAD of the same weekday and hour over 8
/// weeks, flagged only when far out, more than 30 Wh over, and the normal rests on 4 hours or more.</summary>
public class UsageAnomaliesTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateOnly Today = new(2026, 9, 15);
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 18, 30, 0, TimeSpan.Zero);

    /// <summary>A working pattern: busy weekday daytimes, quiet nights and weekends.</summary>
    private static double Pattern(DateTime local)
        => Weekend(local.DayOfWeek) ? 0.05 : local.Hour is >= 9 and < 18 ? 0.12 + 0.02 * Math.Sin(local.Hour) : 0.04;

    private static List<HourUse> Weeks(int weeks, Func<DateTime, double?> kwh, TimeZoneInfo? zone = null)
        => Hours(Today.AddDays(-7 * weeks + 1), 7 * weeks, zone ?? Utc, kwh);

    [Fact]
    public void A_flat_history_flags_nothing()
        => UsageAnomalies.Find(Weeks(8, _ => 0.05), Now, Utc).ShouldBeEmpty();

    [Fact]
    public void Seasonal_noise_raises_no_false_flags()
    {
        var random = new Random(11);
        var hours = Weeks(8, local => Math.Max(0, Pattern(local) * (1 + 0.12 * random.Gaussian()) * (1 + 0.1 * Math.Sin(local.DayOfYear / 9.0))));

        UsageAnomalies.Find(hours, Now, Utc).ShouldBeEmpty();
    }

    [Fact]
    public void An_hour_three_times_its_normal_is_flagged()
    {
        var spike = new DateTime(2026, 9, 14, 15, 0, 0);
        var random = new Random(12);
        var hours = Weeks(8, local => local == spike ? 3 * Pattern(local) : Pattern(local) * (1 + 0.05 * random.Gaussian()));

        var found = UsageAnomalies.Find(hours, Now, Utc).ShouldHaveSingleItem();

        found.Hour.ShouldBe(new DateTimeOffset(spike, TimeSpan.Zero));
        found.NormalKwh.ShouldBe(Pattern(spike), 0.01);
        found.Times.ShouldBe(3, 0.3);
        found.Kwh.ShouldBe(3 * Pattern(spike));
    }

    [Fact]
    public void An_hour_only_a_little_over_its_normal_is_not_flagged_however_steady_the_normal()
    {
        // 25 Wh over a steady 10 Wh: far out in deviations, but not worth a word.
        var small = new DateTime(2026, 9, 14, 3, 0, 0);
        var hours = Weeks(8, local => local == small ? 0.035 : 0.010);

        UsageAnomalies.Find(hours, Now, Utc).ShouldBeEmpty();
    }

    [Fact]
    public void A_normal_resting_on_fewer_than_four_hours_flags_nothing()
    {
        var spike = new DateTime(2026, 9, 14, 15, 0, 0);

        UsageAnomalies.Find(Weeks(4, local => local == spike ? 0.5 : 0.1), Now, Utc).ShouldBeEmpty();
        UsageAnomalies.Find(Weeks(5, local => local == spike ? 0.5 : 0.1), Now, Utc).ShouldHaveSingleItem();
    }

    [Fact]
    public void Missing_hours_neither_count_in_the_normal_nor_are_flagged()
    {
        var spike = new DateTime(2026, 9, 14, 15, 0, 0);
        // Only the last 5 weeks' Monday 15:00 hours were ever recorded... but two of the four before are lost.
        var lost = new[] { spike.AddDays(-7), spike.AddDays(-14) };

        UsageAnomalies.Find(Weeks(5, local => lost.Contains(local) ? null : local == spike ? 0.5 : 0.1), Now, Utc).ShouldBeEmpty();
    }

    [Fact]
    public void At_most_three_a_day_are_kept_the_largest_and_listed_newest_first()
    {
        var day = new DateOnly(2026, 9, 14);
        var excess = new Dictionary<int, double> { [9] = 0.5, [11] = 0.2, [13] = 0.9, [15] = 0.1, [17] = 0.7 };
        var hours = Weeks(8, local => DateOnly.FromDateTime(local) == day && excess.TryGetValue(local.Hour, out var extra) ? 0.1 + extra : 0.1);

        var found = UsageAnomalies.Find(hours, Now, Utc);

        found.Select(a => a.Hour.Hour).ShouldBe([17, 13, 9]);
    }

    [Fact]
    public void Only_the_last_week_is_checked_and_the_hour_under_way_is_never_judged()
    {
        var old = new DateTime(2026, 9, 8, 15, 0, 0);
        var current = new DateTime(2026, 9, 15, 18, 0, 0);

        UsageAnomalies.Find(Weeks(8, local => local == old || local == current ? 0.5 : 0.1), Now, Utc).ShouldBeEmpty();
    }

    [Fact]
    public void A_zero_normal_reads_as_infinitely_many_times()
    {
        var spike = new DateTime(2026, 9, 14, 3, 0, 0);

        UsageAnomalies.Find(Weeks(8, local => local == spike ? 0.2 : 0), Now, Utc).ShouldHaveSingleItem().Times.ShouldBe(double.PositiveInfinity);
    }

    /// <summary>The clocks went back on Sunday 25 October 2026 in London: that day has two 01:00 hours, and a spike on it is
    /// still found, at its own local time.</summary>
    [Fact]
    public void Clock_changes_are_handled_in_local_time()
    {
        var now = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        var spike = new DateTime(2026, 10, 25, 14, 0, 0);
        var hours = Hours(new DateOnly(2026, 8, 31), 57, London, local => local == spike ? 0.6 : 0.1);

        var found = UsageAnomalies.Find(hours, now, London).ShouldHaveSingleItem();

        found.Hour.ShouldBe(new DateTimeOffset(spike, TimeSpan.Zero));
        found.Hour.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void The_bell_takes_todays_newest_first()
    {
        var anomalies = new[]
        {
            new UsageAnomaly(new DateTimeOffset(2026, 9, 15, 14, 0, 0, TimeSpan.Zero), 0.5, 0.1, 5),
            new UsageAnomaly(new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero), 0.5, 0.1, 5),
            new UsageAnomaly(new DateTimeOffset(2026, 9, 14, 22, 0, 0, TimeSpan.Zero), 0.5, 0.1, 5),
        };

        UsageAnomalies.Today(anomalies, Now, Utc).Select(a => a.Hour.Hour).ShouldBe([14, 9]);
    }
}
