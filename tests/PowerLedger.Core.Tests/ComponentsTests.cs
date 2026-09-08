using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class ComponentsTests
{
    [Fact]
    public void Sum_adds_every_part()
    {
        var c = new Components(Cpu: 1, Gpu: 2, Display: 3, Ram: 4, Storage: 5, Board: 6, Extras: 7, Monitors: 8, PsuLoss: 9, Rest: 10);
        c.Sum.ShouldBe(55);
    }

    [Theory]
    [InlineData(0.0, true, 0)]
    [InlineData(0.05, true, 1)]
    [InlineData(0.45, true, 5)]
    [InlineData(0.55, true, 6)]
    [InlineData(1.0, true, 10)]
    [InlineData(null, true, 5)]
    [InlineData(double.NaN, true, 5)]
    [InlineData(0.9, false, -1)]
    public void Buckets_follow_brightness_in_ten_percent_steps_and_display_off_is_minus_one(double? brightness, bool displayOn, int expected)
    {
        CalibrationBuckets.For(brightness, displayOn).ShouldBe(expected);
    }
}
