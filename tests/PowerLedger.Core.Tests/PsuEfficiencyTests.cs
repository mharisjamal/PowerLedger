using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

/// <summary>
/// A supply's efficiency at the load it is actually carrying. One flat number per tier is the 80 PLUS figure at half
/// load, which is where a supply is at its best; an idle desktop on a big supply is nowhere near it, and reading the
/// half-load figure there understates what the machine draws from the wall by a fifth or more.
/// </summary>
public class PsuEfficiencyTests
{
    [Fact]
    public void The_tiers_flat_number_is_its_efficiency_at_half_load()
    {
        // The flat numbers callers already use are the 80 PLUS half-load figures, so the curve goes through them and
        // nothing that asks for the tier alone changes.
        foreach (var tier in Enum.GetValues<PsuTier>())
        {
            PsuEfficiency.At(tier, 0.5).ShouldBe(PsuEfficiency.For(tier));
        }
    }

    [Theory]
    [InlineData(PsuTier.White, 0.82)]
    [InlineData(PsuTier.Bronze, 0.85)]
    [InlineData(PsuTier.Gold, 0.90)]
    [InlineData(PsuTier.Titanium, 0.94)]
    public void The_flat_numbers_the_existing_callers_ask_for_are_untouched(PsuTier tier, double expected)
        => PsuEfficiency.For(tier).ShouldBe(expected);

    [Fact]
    public void A_gold_supply_at_two_percent_of_its_rating_is_nowhere_near_its_half_load_figure()
    {
        // A 1000 W Gold unit carrying 20 W: measurements of real units put this between 60% and 75%, not 90%.
        PsuEfficiency.At(PsuTier.Gold, 0.02).ShouldBeInRange(0.60, 0.75);
    }

    [Theory]
    [InlineData(0.20, 0.86, 0.88)]      // 80 PLUS Gold is certified at 87% here
    [InlineData(0.50, 0.90, 0.90)]      // and 90% here
    [InlineData(1.00, 0.85, 0.88)]      // and 87% here
    public void A_gold_supply_meets_its_certified_points_within_a_point(double load, double low, double high)
        => PsuEfficiency.At(PsuTier.Gold, load).ShouldBeInRange(low, high);

    [Fact]
    public void Every_tier_is_at_its_best_at_half_load_and_falls_away_on_both_sides()
    {
        foreach (var tier in Enum.GetValues<PsuTier>())
        {
            var best = PsuEfficiency.At(tier, 0.5);
            double[] loads = [0, 0.005, 0.02, 0.05, 0.1, 0.2, 0.35, 0.5, 0.75, 1.0];
            var curve = loads.Select(load => PsuEfficiency.At(tier, load)).ToList();

            curve.ShouldAllBe(point => point > 0 && point <= best);
            for (var index = 1; index < curve.Count; index++)
            {
                // Up to half load it only ever improves, and past it only ever worsens.
                if (loads[index] <= 0.5) curve[index].ShouldBeGreaterThanOrEqualTo(curve[index - 1]);
                else curve[index].ShouldBeLessThanOrEqualTo(curve[index - 1]);
            }
        }
    }

    [Fact]
    public void A_load_that_makes_no_sense_falls_back_to_the_tiers_flat_number()
    {
        PsuEfficiency.At(PsuTier.Gold, double.NaN).ShouldBe(PsuEfficiency.For(PsuTier.Gold));
        PsuEfficiency.At(PsuTier.Gold, -1).ShouldBe(PsuEfficiency.At(PsuTier.Gold, 0));
        PsuEfficiency.At(PsuTier.Gold, 3).ShouldBe(PsuEfficiency.At(PsuTier.Gold, 1));

        // Nothing ever divides by zero, whatever a caller asks for.
        foreach (var tier in Enum.GetValues<PsuTier>()) PsuEfficiency.At(tier, 0).ShouldBeGreaterThan(0.1);
    }

    [Theory]
    [InlineData("Corsair RM1000i", 1000)]
    [InlineData("Corsair HX550i", 550)]
    [InlineData("Corsair HX1500i", 1500)]
    [InlineData("NZXT E650", 650)]
    [InlineData("Thermaltake Toughpower DPS G 750W", 750)]
    [InlineData("CORSAIR RM850x 2021", 850)]
    [InlineData("Corsair RM1000i 80 PLUS Gold", 1000)]
    public void The_rating_is_read_from_the_supplys_own_name(string name, int rated)
        => PsuEfficiency.RatedWatts(name).ShouldBe(rated);

    [Theory]
    [InlineData("Thermaltake Toughpower DPS G")]
    [InlineData("power supply")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Corsair 2026 edition")]                // a year is not a rating
    public void A_name_that_does_not_say_what_it_is_rated_for_says_nothing(string? name)
        => PsuEfficiency.RatedWatts(name).ShouldBeNull();

    [Fact]
    public void A_named_supply_is_read_on_its_curve_and_an_unnamed_one_on_the_flat_number()
    {
        // Twenty watts out of a thousand-watt Gold unit is two percent of its rating.
        PsuEfficiency.For(PsuTier.Gold, 20, "Corsair RM1000i").ShouldBe(PsuEfficiency.At(PsuTier.Gold, 0.02));
        PsuEfficiency.For(PsuTier.Gold, 500, "Corsair RM1000i").ShouldBe(PsuEfficiency.For(PsuTier.Gold));

        // Nothing is known about what this one is rated for, so the tier's flat number stands, as it always has.
        PsuEfficiency.For(PsuTier.Gold, 20, "Thermaltake Toughpower DPS G").ShouldBe(PsuEfficiency.For(PsuTier.Gold));
        PsuEfficiency.For(PsuTier.Gold, 20, null).ShouldBe(PsuEfficiency.For(PsuTier.Gold));
        PsuEfficiency.For(PsuTier.Gold, double.NaN, "Corsair RM1000i").ShouldBe(PsuEfficiency.For(PsuTier.Gold));
    }

    [Fact]
    public void An_idle_desktop_on_a_big_supply_draws_a_fifth_more_than_the_flat_number_says()
    {
        // The reason for all this: 20 W of DC out of a 1000 W Gold unit is about 30 W at the wall, not 22 W.
        const double output = 20;
        var flat = output / PsuEfficiency.For(PsuTier.Gold);
        var curved = output / PsuEfficiency.For(PsuTier.Gold, output, "Corsair RM1000i");

        flat.ShouldBeInRange(22, 23);
        curved.ShouldBeGreaterThan(flat * 1.2);
    }
}
