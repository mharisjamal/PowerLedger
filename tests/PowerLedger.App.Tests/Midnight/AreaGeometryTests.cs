using System.Globalization;
using System.Windows;
using Shouldly;

namespace PowerLedger.App.Tests;

public class AreaGeometryTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    [Fact]
    public void A_bucket_sits_at_its_centre_and_a_value_at_its_height()
    {
        AreaGeometry.X(0.5, 4, 40, 440).ShouldBe(90);
        AreaGeometry.X(3.5, 4, 40, 440).ShouldBe(390);
        AreaGeometry.Y(20, 80, 10, 210).ShouldBe(160);
        AreaGeometry.Y(120, 80, 10, 210).ShouldBe(10);
        AreaGeometry.Y(-3, 80, 10, 210).ShouldBe(210);
        AreaGeometry.Y(double.NaN, 80, 10, 210).ShouldBe(210);
        AreaGeometry.Y(5, 0, 10, 210).ShouldBe(210);
    }

    [Fact]
    public void The_bucket_under_the_pointer_is_the_slot_it_falls_in_held_within_the_buckets_there_are()
    {
        AreaGeometry.BucketAt(90, 4, 4, 40, 440).ShouldBe(0);
        AreaGeometry.BucketAt(141, 4, 4, 40, 440).ShouldBe(1);
        AreaGeometry.BucketAt(439, 4, 4, 40, 440).ShouldBe(3);
        AreaGeometry.BucketAt(-50, 4, 4, 40, 440).ShouldBe(0);
        AreaGeometry.BucketAt(900, 4, 4, 40, 440).ShouldBe(3);
        AreaGeometry.BucketAt(400, 2, 4, 40, 440).ShouldBe(1, "a range still filling holds the pointer to its last bucket");
        AreaGeometry.BucketAt(100, 0, 4, 40, 440).ShouldBeNull();
        AreaGeometry.BucketAt(100, 4, 4, 440, 40).ShouldBeNull();
    }

    [Fact]
    public void The_line_runs_through_the_bucket_centres_and_the_area_closes_along_the_baseline()
    {
        var line = AreaGeometry.Line([10, 20, 40], 4, 40, 40, 440, 10, 210);
        line.ShouldBe([new Point(90, 160), new Point(190, 110), new Point(290, 10)]);
        var area = AreaGeometry.Area(line, 210);
        area.Count.ShouldBe(5);
        area[3].ShouldBe(new Point(290, 210));
        area[4].ShouldBe(new Point(90, 210));
        AreaGeometry.Area([], 210).ShouldBeEmpty();
    }

    [Fact]
    public void The_y_axis_reads_zero_then_each_step_with_its_unit()
    {
        AreaGeometry.YLabels(80, 20, "W", English).Select(l => l.Label).ShouldBe(["0", "20 W", "40 W", "60 W", "80 W"]);
        AreaGeometry.YLabels(1, 0.25, "Wh", English).Select(l => l.Label).ShouldBe(["0", "0.25 Wh", "0.50 Wh", "0.75 Wh", "1.00 Wh"]);
        AreaGeometry.YLabels(0, 0, "W", English).ShouldBeEmpty();
    }

    [Fact]
    public void Sleep_runs_are_the_buckets_slept_through_for_most_of_their_length()
    {
        var five = TimeSpan.FromMinutes(5);
        ChartBucket Awake() => new(5, 1, 3, 4, 300, 0);
        ChartBucket Asleep(double seconds = 300) => new(0, 0, 0, 0, 0, seconds);
        var runs = AreaGeometry.AsleepRuns([Awake(), Asleep(), Asleep(), Awake(), Asleep(149), Asleep(150)], five);
        runs.ShouldBe([(1, 3), (5, 6)]);
        AreaGeometry.AsleepRuns([], five).ShouldBeEmpty();
    }

    [Fact]
    public void The_hover_label_gives_the_time_of_day_over_one_day_the_day_too_over_more_and_the_day_alone_for_day_buckets()
    {
        var zone = TimeZoneInfo.Utc;
        var midnight = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);   // a Tuesday
        // The time as the axis under it writes it (Charts: 24-hour "HH:mm"), so the two never disagree.
        AreaGeometry.HoverLabel(midnight, TimeSpan.FromMinutes(5), 144, 288, 92.4, ChartUnit.Watts, zone, English).ShouldBe("12:00 · 92 W");
        AreaGeometry.HoverLabel(midnight, TimeSpan.FromHours(1), 39, 168, 31, ChartUnit.Watts, zone, English).ShouldBe("Wed 9 Sep 15:00 · 31 W");
        AreaGeometry.HoverLabel(midnight, TimeSpan.FromDays(1), 2, 365, 640, ChartUnit.WattHours, zone, English).ShouldBe("Thu 10 Sep · 640 Wh");
        AreaGeometry.HoverLabel(null, TimeSpan.FromMinutes(5), 144, 288, 92.4, ChartUnit.Watts, zone, English).ShouldBe("92 W");
        AreaGeometry.HoverLabel(midnight, TimeSpan.FromDays(1), 2, 365, 640, ChartUnit.WattHours, zone, CultureInfo.GetCultureInfo("de-DE"))
            .ShouldBe("Do 10 Sept. · 640 Wh", "the day in the chart's own culture");
    }

    [Fact]
    public void The_hover_label_reads_the_bucket_in_the_users_zone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Plus two", TimeSpan.FromHours(2), "Plus two", "Plus two");
        var from = new DateTimeOffset(2026, 9, 7, 22, 0, 0, TimeSpan.Zero);   // local midnight in that zone
        AreaGeometry.HoverLabel(from, TimeSpan.FromMinutes(1), 30, 60, 40, ChartUnit.Watts, zone, English).ShouldBe("00:30 · 40 W");
    }
}
