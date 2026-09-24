using System.Globalization;
using Shouldly;

namespace PowerLedger.App.Tests;

public class RangesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void Today_runs_from_midnight_to_now_and_its_chart_to_midnight()
    {
        var today = Ranges.Today(Now, Utc, English);
        today.From.ShouldBe(new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero));
        today.To.ShouldBe(Now);
        today.Through.ShouldBe(new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero));
        today.Bucket.ShouldBe(TimeSpan.FromMinutes(5));
        today.Capacity.ShouldBe(288);
        today.Title.ShouldBe("Today");
    }

    [Fact]
    public void The_last_seven_days_include_today()
    {
        var week = Ranges.LastDays(7, Now, Utc, English);
        week.From.ShouldBe(new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero));
        week.To.ShouldBe(Now);
        week.Through.ShouldBe(new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero));
        week.Bucket.ShouldBe(TimeSpan.FromHours(1));
        week.Title.ShouldBe("Last 7 days");
    }

    [Fact]
    public void This_month_and_last_month()
    {
        var month = Ranges.ThisMonth(Now, Utc, English);
        month.From.ShouldBe(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        month.To.ShouldBe(Now);
        month.Through.ShouldBe(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        month.Title.ShouldBe("September 2026");

        var january = new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);
        var last = Ranges.LastMonth(january, Utc, English);
        last.From.ShouldBe(new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero));
        last.To.ShouldBe(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        last.Through.ShouldBe(last.To);
        last.Title.ShouldBe("December 2025");
        last.Bucket.ShouldBe(TimeSpan.FromHours(6));
    }

    [Fact]
    public void A_custom_range_covers_whole_days_and_stops_at_now()
    {
        var custom = Ranges.Days(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), Now, Utc, English);
        custom.From.ShouldBe(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        custom.To.ShouldBe(Now);
        custom.Through.ShouldBe(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        custom.Title.ShouldBe("1 Sep to 30 Sep 2026");

        Ranges.Days(new DateOnly(2026, 9, 5), new DateOnly(2026, 9, 2), Now, Utc, English).Title.ShouldBe("2 Sep to 5 Sep 2026");
        Ranges.Days(new DateOnly(2025, 12, 28), new DateOnly(2026, 1, 3), Now, Utc, English).Title.ShouldBe("28 Dec 2025 to 3 Jan 2026");
        Ranges.Days(new DateOnly(2026, 9, 3), new DateOnly(2026, 9, 3), Now, Utc, English).Title.ShouldBe("Thu 3 Sep 2026");
    }

    [Fact]
    public void A_range_wholly_in_the_future_is_empty()
    {
        var future = Ranges.Days(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5), Now, Utc, English);
        future.To.ShouldBe(future.From);
    }

    [Theory]
    [InlineData(24, 5)]
    [InlineData(72, 15)]
    [InlineData(73, 60)]
    [InlineData(7 * 24, 60)]
    [InlineData(31 * 24, 360)]
    [InlineData(90 * 24, 1440)]
    public void The_bucket_suits_the_length(int hours, int minutes)
        => Ranges.BucketFor(TimeSpan.FromHours(hours)).ShouldBe(TimeSpan.FromMinutes(minutes));

    [Fact]
    public void Buckets_have_names_for_headings()
    {
        Ranges.BucketName(TimeSpan.FromMinutes(5)).ShouldBe("5-min");
        Ranges.BucketName(TimeSpan.FromHours(1)).ShouldBe("hourly");
        Ranges.BucketName(TimeSpan.FromHours(6)).ShouldBe("6-hour");
        Ranges.BucketName(TimeSpan.FromDays(1)).ShouldBe("daily");
        Ranges.BucketLength(TimeSpan.FromMinutes(15)).ShouldBe("15 min");
        Ranges.BucketLength(TimeSpan.FromHours(1)).ShouldBe("hour");
        Ranges.BucketLength(TimeSpan.FromHours(6)).ShouldBe("6 hours");
        Ranges.BucketLength(TimeSpan.FromDays(1)).ShouldBe("day");
    }

    [Fact]
    public void A_local_time_a_clock_change_skips_moves_to_the_first_valid_time()
    {
        var europe = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        Ranges.At(new DateTime(2026, 3, 29, 2, 30, 0), europe).ShouldBe(new DateTimeOffset(2026, 3, 29, 3, 0, 0, TimeSpan.FromHours(2)));
        Ranges.At(new DateTime(2026, 3, 29, 6, 0, 0), europe).ShouldBe(new DateTimeOffset(2026, 3, 29, 6, 0, 0, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void The_last_hour_is_sixty_whole_minutes_ending_with_the_one_under_way()
    {
        var hour = Ranges.LastHour(Now, Utc, English);
        hour.From.ShouldBe(new DateTimeOffset(2026, 9, 8, 13, 33, 0, TimeSpan.Zero));
        hour.To.ShouldBe(Now);
        hour.Through.ShouldBe(new DateTimeOffset(2026, 9, 8, 14, 33, 0, TimeSpan.Zero));
        hour.Bucket.ShouldBe(TimeSpan.FromMinutes(1));
        hour.Capacity.ShouldBe(60);
        hour.Title.ShouldBe("Last hour");
    }

    [Fact]
    public void The_last_hour_across_a_clock_change_is_still_sixty_minutes_long()
    {
        var europe = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var halfPastThree = new DateTimeOffset(2026, 3, 29, 3, 30, 0, TimeSpan.FromHours(2));   // 01:30 UTC, half an hour after 02:00 became 03:00
        var hour = Ranges.LastHour(halfPastThree, europe, English);
        hour.From.ShouldBe(new DateTimeOffset(2026, 3, 29, 1, 31, 0, TimeSpan.FromHours(1)));
        hour.From.Offset.ShouldBe(TimeSpan.FromHours(1));   // local time before the change
        (hour.Through - hour.From).ShouldBe(TimeSpan.FromMinutes(60));
    }

    [Fact]
    public void The_last_year_is_the_last_365_days_in_day_buckets()
    {
        var year = Ranges.LastYear(Now, Utc, English);
        year.From.ShouldBe(new DateTimeOffset(2025, 9, 9, 0, 0, 0, TimeSpan.Zero));
        year.To.ShouldBe(Now);
        year.Through.ShouldBe(new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero));
        year.Bucket.ShouldBe(TimeSpan.FromDays(1));
        year.Capacity.ShouldBe(365);
        year.Title.ShouldBe("Last 365 days");
    }

    [Fact]
    public void All_runs_from_the_first_rows_day_in_day_buckets_whatever_its_length()
    {
        var all = Ranges.All(new DateTimeOffset(2026, 9, 3, 9, 15, 0, TimeSpan.Zero), Now, Utc, English);
        all.From.ShouldBe(new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero));
        all.To.ShouldBe(Now);
        all.Through.ShouldBe(new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero));
        all.Bucket.ShouldBe(TimeSpan.FromDays(1));
        all.Capacity.ShouldBe(6);
        all.Title.ShouldBe("Since 3 Sep 2026");

        var none = Ranges.All(null, Now, Utc, English);   // no history yet: today alone
        none.From.ShouldBe(new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero));
        none.Capacity.ShouldBe(1);
        none.Title.ShouldBe("Since 8 Sep 2026");
    }

    /// <summary>Review round: a day-bucket range holds a bucket a day however long a clock change made its days; from
    /// 1 October to 1 November in London is 32 days and an hour.</summary>
    [Fact]
    public void A_day_bucket_range_across_a_clock_change_holds_one_bucket_a_day()
    {
        var london = TimeZoneInfo.TryFindSystemTimeZoneById("GMT Standard Time", out var windows) ? windows : TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

        var all = Ranges.All(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), now, london, English);

        (all.Through - all.From).ShouldBe(TimeSpan.FromDays(32) + TimeSpan.FromHours(1));
        all.Capacity.ShouldBe(32);
        Ranges.LastYear(now, london, English).Capacity.ShouldBe(365);
        Ranges.Today(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero), london, English).Capacity.ShouldBe(300);   // 25 hours of 5 minutes, as before
    }

    [Fact]
    public void A_minute_bucket_has_its_names()
    {
        Ranges.BucketName(TimeSpan.FromMinutes(1)).ShouldBe("1-min");
        Ranges.BucketLength(TimeSpan.FromMinutes(1)).ShouldBe("minute");
    }
}
