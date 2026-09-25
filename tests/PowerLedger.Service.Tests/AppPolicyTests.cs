using PowerLedger.Service.Updates;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>The oldest version the server takes (Plan Q §3): unknown until said, compared by X.Y.Z, a bad text ignored.</summary>
public class AppPolicyTests
{
    [Fact]
    public void Nothing_is_below_an_unknown_minimum()
    {
        var policy = new AppPolicy();
        policy.MinVersion.ShouldBeNull();
        policy.IsBelowMinimum("0.1.0").ShouldBeFalse();
    }

    [Fact]
    public void Versions_compare_by_their_numbers_and_the_commit_is_ignored()
    {
        var policy = new AppPolicy();
        policy.Set("0.9.0");
        policy.MinVersion.ShouldBe("0.9.0");
        policy.IsBelowMinimum("0.8.1").ShouldBeTrue();
        policy.IsBelowMinimum("0.8.10+abc1234").ShouldBeTrue();
        policy.IsBelowMinimum("0.9.0+abc1234").ShouldBeFalse();
        policy.IsBelowMinimum("0.10.0").ShouldBeFalse();
    }

    [Fact]
    public void A_bad_minimum_keeps_the_last_good_one_and_a_change_is_announced_once()
    {
        var policy = new AppPolicy();
        var changes = 0;
        policy.Changed += () => changes++;
        policy.Set("0.9.0");
        policy.Set("0.9.0");
        policy.Set("nine");
        policy.Set("0.9");
        policy.Set(null);
        policy.MinVersion.ShouldBe("0.9.0");
        changes.ShouldBe(1);
        policy.Set("0.9.1");
        changes.ShouldBe(2);
    }
}
