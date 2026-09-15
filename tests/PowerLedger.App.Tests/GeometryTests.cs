using Shouldly;

namespace PowerLedger.App.Tests;

public class GeometryTests
{
    [Fact]
    public void A_value_sits_along_the_scale_and_is_clamped_to_its_ends()
    {
        Geometry.ScaleX(75, 150, 10, 590).ShouldBe(300);
        Geometry.ScaleX(-5, 150, 10, 590).ShouldBe(10);
        Geometry.ScaleX(400, 150, 10, 590).ShouldBe(590);
        Geometry.ScaleX(double.NaN, 150, 10, 590).ShouldBe(10);
    }

    [Theory]
    [InlineData(34.2, 7, 5)]
    [InlineData(68, 4, 20)]
    [InlineData(3, 3, 1)]
    [InlineData(900, 4, 500)]
    public void Steps_are_round(double span, int ticks, double expected) => Geometry.NiceStep(span, ticks).ShouldBe(expected);

    [Fact]
    public void The_day_chart_clears_its_tallest_slot_in_four_round_steps()
    {
        Geometry.ChartScale(68).ShouldBe((80.0, 20.0));
        Geometry.ChartScale(3).ShouldBe((20.0, 5.0));
    }

    [Fact]
    public void The_sparkline_range_leaves_room_and_carries_round_gridlines()
    {
        var (low, high, grid) = Geometry.SparkRange([30, 34, 38]);
        low.ShouldBe(28, 1e-9);
        high.ShouldBe(40, 1e-9);
        grid.ShouldBe(new double[] { 30, 35 });
    }

    [Fact]
    public void Spark_points_sit_by_their_age_with_the_newest_at_the_right()
    {
        var points = Geometry.SparkPoints([new(60, 10), new(30, 20), new(0, 30)], left: 0, right: 100, top: 0, bottom: 50, low: 10, high: 30);
        points.Select(p => p.X).ShouldBe(new double[] { 0, 50, 100 });
        points.Select(p => p.Y).ShouldBe(new double[] { 50, 25, 0 });
        Geometry.SparkPoints([new(3, 20), new(0, 20)], 0, 100, 0, 50, 10, 30).Select(p => p.X).ShouldBe(new double[] { 95, 100 });
    }

    [Fact]
    public void Stacked_layers_rise_from_rest_to_cpu_and_negative_rest_stays_at_zero()
    {
        ChartBucket[] buckets =
        [
            new(Cpu: 10, Gpu: 2, Display: 4, Rest: 6, OnSeconds: 300, AsleepSeconds: 0),
            new(Cpu: 20, Gpu: 0, Display: 4, Rest: -3, OnSeconds: 300, AsleepSeconds: 0),
        ];
        var tops = Geometry.StackTops(buckets);
        tops.RestTop.ShouldBe(new double[] { 6, 0 });
        tops.DisplayTop.ShouldBe(new double[] { 10, 4 });
        tops.GpuTop.ShouldBe(new double[] { 12, 4 });
        tops.CpuTop.ShouldBe(new double[] { 22, 24 });
    }

    [Fact]
    public void A_chart_scale_never_drops_under_its_floor()
    {
        Geometry.ChartScale(3, floor: 1).ShouldBe((3.0, 1.0));
        Geometry.ChartScale(0.3, floor: 0.1).Max.ShouldBe(0.3, 1e-9);
    }

    [Theory]
    [InlineData(30, 30.0, 1)]
    [InlineData(90, 10.0, 3)]
    [InlineData(0, 10.0, 1)]
    public void Labels_skip_slots_until_they_fit(int count, double slot, int every) => Geometry.LabelEvery(count, slot, 22).ShouldBe(every);
}
