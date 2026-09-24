using System.Runtime.InteropServices;
using Shouldly;

namespace PowerLedger.App.Tests;

public class HostInfoTests
{
    [Theory]
    [InlineData(Architecture.Arm64, "arm64")]
    [InlineData(Architecture.X86, "x86")]
    [InlineData(Architecture.X64, "x64")]
    public void Arch_names_match_the_services_own(Architecture architecture, string expected)
        => HostInfo.ArchName(architecture).ShouldBe(expected);

    [Fact]
    public void A_normal_version_with_its_sha_passes_through_unchanged()
        => HostInfo.SanitizeAppVersion("0.7.0+abc1234").ShouldBe("0.7.0+abc1234");

    [Fact]
    public void Spaces_and_parentheses_are_stripped()
        => HostInfo.SanitizeAppVersion("0.7.0 (Debug build)").ShouldBe("0.7.0Debugbuild");

    [Fact]
    public void An_empty_version_becomes_a_single_zero()
        => HostInfo.SanitizeAppVersion("").ShouldBe("0");

    [Fact]
    public void A_version_starting_with_punctuation_has_it_dropped()
        => HostInfo.SanitizeAppVersion("+0.7.0").ShouldBe("0.7.0");

    [Fact]
    public void An_overlong_version_is_cut_to_64_characters()
    {
        var version = "1" + new string('2', 80);

        HostInfo.SanitizeAppVersion(version).Length.ShouldBe(64);
    }

    [Fact]
    public void Combine_with_no_product_name_is_null()
        => HostInfo.Combine(null, "26200").ShouldBeNull();

    [Fact]
    public void Combine_with_no_build_is_the_product_name_alone()
        => HostInfo.Combine("Windows 11 Home", null).ShouldBe("Windows 11 Home");

    [Fact]
    public void Combine_with_both_joins_them_with_a_space()
        => HostInfo.Combine("Windows 11 Home", "26200").ShouldBe("Windows 11 Home 26200");
}
