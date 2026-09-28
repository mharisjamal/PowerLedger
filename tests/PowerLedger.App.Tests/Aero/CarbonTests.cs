using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Aero look design §4, Carbon: kWh times Settings' one CO₂ factor, and where that factor came from in words;
/// the region table only names and suggests.</summary>
public class CarbonTests
{
    [Fact]
    public void Carbon_is_energy_times_the_factor()
    {
        var carbon = Carbon.Estimate(monthKwh: 30, sinceStartKwh: 400, kgPerKwh: 0.236, region: "GB");

        carbon.MonthKg.ShouldBe(7.08, 1e-9);
        carbon.SinceStartKg.ShouldBe(94.4, 1e-9);
        carbon.GramsPerKwh.ShouldBe(236, 1e-9);
    }

    [Fact]
    public void The_default_factor_is_called_the_default()
        => Carbon.Estimate(1, 1, Co2.DefaultKgPerKwh, "GB").Source.ShouldBe("The default in Settings, a world average");

    [Theory]
    [InlineData(0.236, "GB", "The United Kingdom's grid in 2023, from Ember")]
    [InlineData(0.24, "GB", "The United Kingdom's grid in 2023, from Ember")]
    [InlineData(0.05, "FR", "France's grid in 2023, from Ember")]
    [InlineData(0.36, "de", "Germany's grid in 2023, from Ember")]
    [InlineData(0.48, "ZZ", "The world's grids in 2023, from Ember")]
    [InlineData(0.48, null, "The world's grids in 2023, from Ember")]
    [InlineData(0.3, "GB", "Your own figure, set in Settings")]
    [InlineData(0.05, "GB", "Your own figure, set in Settings")]
    public void A_factor_matching_the_regions_table_is_credited_to_its_source(double kgPerKwh, string? region, string source)
        => Carbon.Estimate(1, 1, kgPerKwh, region).Source.ShouldBe(source);

    [Fact]
    public void The_table_holds_about_forty_countries_each_with_its_source_and_year()
    {
        GridFactors.All.Count.ShouldBeInRange(40, 60);
        GridFactors.All.Select(f => f.Region).ShouldBeUnique();
        GridFactors.All.ShouldAllBe(f => f.Region.Length == 2 && f.Region == f.Region.ToUpperInvariant());
        GridFactors.All.ShouldAllBe(f => f.Grams > 0 && f.Grams < UiPreferences.MaxCo2KgPerKwh * 1000);
        GridFactors.All.ShouldAllBe(f => f.Source.Length > 0 && f.Year >= 2020 && f.Country.Length > 0);
    }

    [Theory]
    [InlineData("GB", 236)]
    [InlineData("us", 393)]
    [InlineData("IN", 713)]
    [InlineData("NO", 30)]
    public void A_region_is_looked_up_by_its_code_in_any_case(string region, double grams)
        => GridFactors.For(region).ShouldNotBeNull().Grams.ShouldBe(grams);

    [Theory]
    [InlineData("ZZ")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("419")]
    public void A_region_the_table_does_not_hold_suggests_the_worlds_figure(string? region)
    {
        GridFactors.For(region).ShouldBeNull();
        GridFactors.Suggested(region).ShouldBe(GridFactors.World);
    }

    [Fact]
    public void Windows_region_is_a_two_letter_code_or_nothing()
        => GridFactors.WindowsRegion().ShouldSatisfyAllConditions(r => (r is null || (r.Length == 2 && r.All(char.IsAsciiLetterUpper))).ShouldBeTrue());
}
