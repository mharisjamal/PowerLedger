using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class AdapterEfficiencyTests
{
    [Theory]
    [InlineData(6.5, 0.80, 0.86)]     // a tenth of a 65 W adapter: what measured units make there
    [InlineData(9.75, 0.80, 0.86)]    // fifteen percent
    public void An_idle_laptops_adapter_makes_80_to_86_percent(double dc, double low, double high)
        => (dc / AdapterEfficiency.Wall(dc)).ShouldBeInRange(low, high);

    [Fact]
    public void Averaged_over_a_quarter_to_all_of_its_rating_it_meets_doe_level_vi()
    {
        double[] loads = [0.25, 0.5, 0.75, 1.0];
        loads.Average(load => load * 65 / AdapterEfficiency.Wall(load * 65)).ShouldBeGreaterThanOrEqualTo(0.88);
    }

    [Fact]
    public void With_nothing_to_deliver_it_draws_its_no_load_figure()
    {
        AdapterEfficiency.Wall(0).ShouldBe(AdapterEfficiency.NoLoadW);
        AdapterEfficiency.NoLoadW.ShouldBeInRange(0.1, 0.21);
    }

    [Fact]
    public void It_is_at_its_best_near_half_load_and_never_divides_by_nothing()
    {
        AdapterEfficiency.At(0.5).ShouldBe(0.90);
        AdapterEfficiency.At(0.1).ShouldBeLessThan(AdapterEfficiency.At(0.25));
        AdapterEfficiency.At(-1).ShouldBe(AdapterEfficiency.At(0));
        AdapterEfficiency.At(5).ShouldBe(AdapterEfficiency.At(1));
        AdapterEfficiency.At(double.NaN).ShouldBe(0.90);
        AdapterEfficiency.At(0).ShouldBeGreaterThan(0.5);
    }

    [Fact]
    public void A_bigger_adapter_is_further_down_its_curve_at_the_same_draw()
        => AdapterEfficiency.Wall(10, ratedW: 230).ShouldBeGreaterThan(AdapterEfficiency.Wall(10, ratedW: 65));
}
