using System.Globalization;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Plan O M1-4: the Dashboard's two converters, the width its cards go one under another below, and a KPI's
/// figure split so its unit can sit smaller beside the number.</summary>
public class MidnightConvertersTests
{
    [Theory]
    [InlineData(866.0, "866", true)]
    [InlineData(865.9, "866", false)]
    [InlineData(1200.0, "866", true)]
    public void At_least_holds_from_the_width_up(double width, string least, bool expected)
        => new AtLeast().Convert(width, typeof(bool), least, CultureInfo.InvariantCulture).ShouldBe(expected);

    [Fact]
    public void At_least_without_a_width_is_false()
        => new AtLeast().Convert("wide", typeof(bool), "866", CultureInfo.InvariantCulture).ShouldBe(false);

    [Theory]
    [InlineData("34.2 W", "34.2", "W")]
    [InlineData("0.284 kWh", "0.284", "kWh")]
    [InlineData("–", "–", "")]
    [InlineData("", "", "")]
    public void A_figure_splits_into_its_number_and_its_unit(string figure, string number, string unit)
    {
        var split = new FigurePart();
        split.Convert(figure, typeof(string), "Number", CultureInfo.InvariantCulture).ShouldBe(number);
        split.Convert(figure, typeof(string), "Unit", CultureInfo.InvariantCulture).ShouldBe(unit);
    }

    [Fact]
    public void A_figure_that_is_not_there_has_neither()
        => new FigurePart().Convert(null!, typeof(string), "Number", CultureInfo.InvariantCulture).ShouldBe("");
}
