using PowerLedger.Core;
using Shouldly;
using static PowerLedger.App.Tests.InsightsSeries;

namespace PowerLedger.App.Tests;

/// <summary>Report finding 19: how much of a day's energy recent days had used by a time of day, from their hour rows.</summary>
public class DayShapeTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateOnly Today = new(2026, 9, 15);

    [Fact]
    public void An_evening_PC_has_used_none_of_its_day_by_noon()
    {
        var hours = Hours(Today.AddDays(-28), 29, Utc, local => local.Hour >= 18 ? 0.4 : 0);

        DayShape.ShareBy(hours, Today, TimeSpan.FromHours(12)).ShouldNotBeNull().ShouldBe(0, 1e-9);
        DayShape.ShareBy(hours, Today, new TimeSpan(20, 30, 0)).ShouldNotBeNull().ShouldBe(2.5 / 6, 1e-9);
        DayShape.ShareBy(hours, Today, TimeSpan.FromHours(24)).ShouldNotBeNull().ShouldBe(1, 1e-9);
    }

    [Fact]
    public void A_PC_used_evenly_has_used_the_day_s_share_gone()
        => DayShape.ShareBy(Hours(Today.AddDays(-28), 29, Utc, _ => 0.1), Today, new TimeSpan(14, 30, 0)).ShouldNotBeNull().ShouldBe(14.5 / 24, 1e-9);

    [Fact]
    public void Only_whole_days_before_today_and_within_four_weeks_count()
    {
        // Today and the days before the four weeks are used all morning; the four weeks only in the evening.
        var cut = Today.AddDays(-DayShape.Days);
        var hours = Hours(Today.AddDays(-56), 57, Utc, local =>
            DateOnly.FromDateTime(local) is var day && (day < cut || day == Today) ? (local.Hour < 12 ? 1 : 0) : (local.Hour >= 18 ? 0.4 : 0));

        DayShape.ShareBy(hours, Today, TimeSpan.FromHours(12)).ShouldNotBeNull().ShouldBe(0, 1e-9);
    }

    [Fact]
    public void With_nothing_used_there_is_no_shape()
    {
        DayShape.ShareBy(Hours(Today.AddDays(-28), 29, Utc, _ => 0), Today, TimeSpan.FromHours(12)).ShouldBeNull();
        DayShape.ShareBy([], Today, TimeSpan.FromHours(12)).ShouldBeNull();
    }

    /// <summary>The Dashboard hands over its hour series and the instant: the same shape, in the zone's local time.</summary>
    [Fact]
    public void A_series_reads_in_the_zone_s_local_time()
    {
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");
        var rows = Rows(Hours(Today.AddDays(-28), 29, plusTwo, local => local.Hour >= 18 ? 0.4 : 0));
        var now = new DateTimeOffset(2026, 9, 15, 18, 30, 0, TimeSpan.Zero);   // 20:30 there

        DayShape.ShareBy(rows, now, plusTwo).ShouldNotBeNull().ShouldBe(2.5 / 6, 1e-9);
    }
}
