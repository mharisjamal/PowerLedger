using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Plan S D3: the geometry behind Aero's Dashboard charts, pure: Last minute's scale and time axis, Energy each
/// day's scale and days, and the 3D pie's slices.</summary>
public class AeroChartGeometryTests
{
    [Fact]
    public void Last_minutes_scale_holds_every_reading_and_the_average_on_round_steps()
    {
        var scale = LiveScale.For([31.2, 36.8, 40.9, 29.5], average: 35);

        scale.Lo.ShouldBeLessThanOrEqualTo(29.5);
        scale.Hi.ShouldBeGreaterThanOrEqualTo(40.9);
        scale.Step.ShouldBe(5);
        (scale.Lo % scale.Step).ShouldBe(0);
        scale.Grid.ShouldBe([30.0, 35.0, 40.0, 45.0]);
    }

    [Fact]
    public void A_flat_or_empty_minute_still_has_a_scale()
    {
        var flat = LiveScale.For([50, 50, 50], average: 50);
        flat.Hi.ShouldBeGreaterThan(flat.Lo);
        flat.Lo.ShouldBeLessThanOrEqualTo(50);
        flat.Hi.ShouldBeGreaterThanOrEqualTo(50);

        var empty = LiveScale.For([], average: double.NaN);
        empty.Lo.ShouldBe(0);
        empty.Hi.ShouldBeGreaterThan(0);
        LiveScale.For([0.4, 0.2], 0.3).Lo.ShouldBe(0, "never below nothing");
    }

    [Theory]
    [InlineData(0, 392)]
    [InlineData(60, 34)]
    [InlineData(30, 213)]
    [InlineData(90, 34)]
    public void A_readings_age_places_it_from_now_at_the_right_back_to_a_minute_at_the_left(double age, double x)
        => LiveScale.X(age).ShouldBe(x, 1e-9);

    [Fact]
    public void Watts_map_up_the_chart_from_its_floor()
    {
        var scale = new LiveScale(20, 70, 10, [30, 40, 50, 60]);
        scale.Y(20).ShouldBe(LiveScale.Y1);
        scale.Y(70).ShouldBe(LiveScale.Y0);
        scale.Y(45).ShouldBe((LiveScale.Y0 + LiveScale.Y1) / 2, 1e-9);
    }

    [Theory]
    [InlineData(2.02, 2.5)]
    [InlineData(1.84, 2)]
    [InlineData(0, 0.5)]
    [InlineData(7.3, 8)]
    [InlineData(13, 15)]
    [InlineData(0.31, 0.5)]
    public void Energy_each_days_ceiling_is_a_round_figure_over_the_busiest_day(double busiest, double max)
        => DailyScale.Ceiling(busiest).ShouldBe(max);

    [Theory]
    [InlineData(30, "1 5 10 15 20 25 30")]
    [InlineData(31, "1 5 10 15 20 25 31")]
    [InlineData(28, "1 5 10 15 20 28")]
    public void The_days_are_labelled_every_five_and_the_last(int days, string labels)
        => string.Join(' ', DailyScale.Labels(days)).ShouldBe(labels);

    [Fact]
    public void Days_spread_across_the_width_from_the_1st_to_the_months_last()
    {
        DailyScale.X(1, 30).ShouldBe(DailyScale.X0);
        DailyScale.X(30, 30).ShouldBe(DailyScale.X1);
        DailyScale.X(31, 31).ShouldBe(DailyScale.X1);
        DailyScale.Y(0, 2).ShouldBe(DailyScale.Y1);
        DailyScale.Y(2, 2).ShouldBe(DailyScale.Y0);
    }

    [Theory]
    [InlineData(1, "1st")]
    [InlineData(2, "2nd")]
    [InlineData(3, "3rd")]
    [InlineData(4, "4th")]
    [InlineData(11, "11th")]
    [InlineData(12, "12th")]
    [InlineData(13, "13th")]
    [InlineData(21, "21st")]
    [InlineData(22, "22nd")]
    [InlineData(23, "23rd")]
    public void The_tip_names_the_day_as_an_ordinal(int day, string name) => DailyScale.Ordinal(day).ShouldBe(name);

    [Fact]
    public void The_tip_gives_the_days_cost_or_without_one_its_energy()
    {
        var english = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        new DailyDay(new DateOnly(2026, 9, 17), 0.84, "$0.27").Tip(english).ShouldBe("$0.27 on the 17th");
        new DailyDay(new DateOnly(2026, 9, 2), 0.84, null).Tip(english).ShouldBe("0.840 kWh on the 2nd");
    }

    [Fact]
    public void The_pies_slices_go_round_from_the_top_in_the_parts_order()
    {
        var slices = PieSlices.From([0.46, 0.28, 0.17, 0.09]);

        slices.Count.ShouldBe(4);
        slices[0].A0.ShouldBe(-Math.PI / 2, 1e-9);
        slices[3].A1.ShouldBe(-Math.PI / 2 + 2 * Math.PI, 1e-9);
        slices[0].A1.ShouldBe(slices[1].A0, 1e-9);
        (slices[0].A1 - slices[0].A0).ShouldBe(0.46 * 2 * Math.PI, 1e-9);
        slices[0].Height.ShouldBeGreaterThan(slices[3].Height, "a bigger share stands taller");
        slices.Select(s => s.Index).ShouldBe([0, 1, 2, 3]);
    }

    [Fact]
    public void Shares_that_dont_add_up_are_scaled_and_empty_ones_have_no_slice()
    {
        var slices = PieSlices.From([0.2, 0, 0.2]);

        slices.Select(s => s.Index).ShouldBe([0, 2]);
        (slices[0].A1 - slices[0].A0).ShouldBe(Math.PI, 1e-9);
        PieSlices.From([0, 0, 0, 0]).ShouldBeEmpty();
        PieSlices.From([double.NaN, 1]).Select(s => s.Index).ShouldBe([1]);
    }

    /// <summary>The slices are painted back to front: the one furthest up the tilted disc first.</summary>
    [Fact]
    public void The_pie_paints_the_far_slices_first()
    {
        var order = PieSlices.PaintOrder(PieSlices.From([0.1, 0.4, 0.3, 0.2]));

        order.Select(s => s.Index).ShouldBe([0, 3, 1, 2], "the thin slice at the top is furthest back, the one low on the left nearest");
    }

    [Fact]
    public void Each_slice_rises_in_turn()
    {
        Enumerable.Range(0, 4).Select(i => PieSlices.Progress(0, i, 4)).ShouldAllBe(p => p == 0);
        Enumerable.Range(0, 4).Select(i => PieSlices.Progress(1, i, 4)).ShouldAllBe(p => p == 1);
        var early = PieSlices.Progress(0.2, 0, 4);
        early.ShouldBeGreaterThan(0, "the first starts at once");
        PieSlices.Progress(0.2, 3, 4).ShouldBe(0, "the last waits its three staggers");
        PieSlices.Progress(0.6, 0, 4).ShouldBeGreaterThan(PieSlices.Progress(0.6, 1, 4));
    }
}
