using Shouldly;

namespace PowerLedger.App.Tests;

public class ConnectionCostTests
{
    /// <summary>Windows' own flags: anything that isn't plainly unrestricted is data somebody pays for.</summary>
    [Theory]
    [InlineData(0x0u, false)]        // unknown
    [InlineData(0x1u, false)]        // unrestricted
    [InlineData(0x2u, true)]         // fixed allowance
    [InlineData(0x4u, true)]         // variable, paid by the byte
    [InlineData(0x10000u, true)]     // over the data limit
    [InlineData(0x20000u, false)]    // congested, but not charged for
    [InlineData(0x40000u, true)]     // roaming
    [InlineData(0x80000u, true)]     // approaching the data limit
    [InlineData(0x1u | 0x20000u, false)]
    [InlineData(0x2u | 0x20000u, true)]
    public void A_cost_says_whether_the_connection_is_metered(uint cost, bool metered)
        => ConnectionCost.IsMetered(cost).ShouldBe(metered);

    /// <summary>The real interface answers on this machine, whatever the answer is.</summary>
    [Fact]
    [Trait("Category", "Hardware")]
    public void Windows_gives_this_machine_a_cost()
    {
        var cost = new ConnectionCost();
        cost.Metered.ShouldBe(cost.Metered);   // twice: it must not throw, and must agree with itself
    }
}
