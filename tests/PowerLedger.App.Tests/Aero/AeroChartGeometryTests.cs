using PowerLedger.App.Aero;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Plan S D3 and 0.10.9: the geometry behind Aero's Dashboard charts, pure: Last minute's scale and time axis,
/// Energy each day's bars and their tips, and the ring's arcs.</summary>
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
    [InlineData(0, 400)]
    [InlineData(60, 0)]
    [InlineData(30, 200)]
    [InlineData(90, 0)]
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

    /// <summary>The mockup's busiest bar stands at 90 % of the well's room, every other in proportion, an empty one a sliver.</summary>
    [Theory]
    [InlineData(2.0, 2.0, 0.9)]
    [InlineData(1.0, 2.0, 0.45)]
    [InlineData(0.0, 2.0, BarScale.Least)]
    [InlineData(0.0, 0.0, BarScale.Least)]
    [InlineData(double.NaN, 2.0, BarScale.Least)]
    public void A_bar_stands_in_proportion_to_the_busiest(double kwh, double busiest, double share)
        => BarScale.Share(kwh, busiest).ShouldBe(share, 1e-9);

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
    public void The_tip_names_the_day_as_an_ordinal(int day, string name) => EnergyBars.Ordinal(day).ShouldBe(name);

    [Fact]
    public void The_tip_names_the_period_and_its_figure()
    {
        var english = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        EnergyBars.Tip(HistorySpan.Day, new DateOnly(2026, 9, 17), "$0.27", english).ShouldBe("$0.27 on the 17th");
        EnergyBars.Tip(HistorySpan.Week, new DateOnly(2026, 9, 7), "1.84 kWh", english).ShouldBe("1.84 kWh in the week from the 7th");
        EnergyBars.Tip(HistorySpan.Month, new DateOnly(2026, 9, 1), "$5.73", english).ShouldBe("$5.73 in September");
        EnergyBars.Label(HistorySpan.Month, new DateOnly(2026, 9, 1), english).ShouldBe("Sep");
        EnergyBars.Label(HistorySpan.Day, new DateOnly(2026, 9, 17), english).ShouldBe("17");
    }

    [Fact]
    public void The_rings_arcs_go_round_from_the_top_in_the_parts_order()
    {
        var arcs = RingArcs.From([(Part.Cpu, 0.46), (Part.Gpu, 0.28), (Part.Display, 0.17), (Part.Rest, 0.09)]);

        arcs.Count.ShouldBe(4);
        arcs[0].From.ShouldBe(0);
        arcs[0].To.ShouldBe(0.46, 1e-9);
        arcs[1].From.ShouldBe(arcs[0].To, 1e-9);
        arcs[3].To.ShouldBe(1, 1e-9);
        arcs.Select(a => a.Part).ShouldBe([Part.Cpu, Part.Gpu, Part.Display, Part.Rest]);
    }

    [Fact]
    public void Shares_that_dont_add_up_are_scaled_and_empty_ones_have_no_arc()
    {
        var arcs = RingArcs.From([(Part.Cpu, 0.2), (Part.Gpu, 0), (Part.Display, 0.2)]);

        arcs.Select(a => a.Part).ShouldBe([Part.Cpu, Part.Display]);
        arcs[0].To.ShouldBe(0.5, 1e-9);
        RingArcs.From([(Part.Cpu, 0), (Part.Gpu, 0)]).ShouldBeEmpty();
        RingArcs.From([(Part.Cpu, double.NaN), (Part.Gpu, 1)]).Select(a => a.Part).ShouldBe([Part.Gpu]);
    }
}
