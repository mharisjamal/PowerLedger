using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class Co2Tests
{
    [Fact]
    public void The_default_is_the_worlds_grid_481_g_per_kwh()
    {
        Co2.WorldKgPerKwh.ShouldBe(0.481);
        Co2.DefaultKgPerKwh.ShouldBe(Co2.WorldKgPerKwh);
    }

    [Theory]
    [InlineData(0.40)]    // 0.10's default, which was never the world's figure
    [InlineData(0.481)]   // the default, untouched
    public void A_factor_left_at_a_default_takes_the_regions_grid(double current)
        => Co2.Seeded(current, suggestedKgPerKwh: 0.713).ShouldBe(0.713);

    [Theory]
    [InlineData(0.23)]
    [InlineData(0.48)]
    [InlineData(0)]
    public void A_factor_the_user_chose_is_kept(double current) => Co2.Seeded(current, suggestedKgPerKwh: 0.713).ShouldBe(current);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1)]
    [InlineData(double.PositiveInfinity)]
    public void A_suggestion_that_is_no_grid_changes_nothing(double suggested) => Co2.Seeded(0.40, suggested).ShouldBe(0.40);

    [Fact]
    public void Kilograms_are_the_energy_times_the_factor() => Co2.Kg(2.5, 0.4).ShouldBe(1.0, 1e-12);
}
