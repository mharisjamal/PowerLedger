using PowerLedger.Core.Households;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class PairingCodeTests
{
    [Fact]
    public void A_new_code_is_four_groups_of_four_from_the_alphabet()
    {
        var code = PairingCode.New();
        code.ShouldMatch("^[0-9A-HJKMNP-TV-Z]{4}(-[0-9A-HJKMNP-TV-Z]{4}){3}$");
        PairingCode.New().ShouldNotBe(code);
    }

    [Theory]
    [InlineData("K7QM-2XHD-9PW4-R8TA", "K7QM2XHD9PW4R8TA")]
    [InlineData("k7qm 2xhd 9pw4 r8ta", "K7QM2XHD9PW4R8TA")]
    [InlineData("K7QM-2XHD-9PW4-R8TO", "K7QM2XHD9PW4R8T0")]
    [InlineData("I7QM-2XHD-9PW4-R8TL", "17QM2XHD9PW4R8T1")]
    public void A_typed_code_is_made_canonical(string typed, string canonical) =>
        PairingCode.Normalize(typed).ShouldBe(canonical);

    [Theory]
    [InlineData("K7QM-2XHD-9PW4")]
    [InlineData("K7QM-2XHD-9PW4-R8TA-X")]
    [InlineData("K7QM-2XHD-9PW4-R8TU")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_not_a_code(string? typed) => PairingCode.Normalize(typed).ShouldBeNull();

    [Fact]
    public void The_meeting_id_is_32_hex_characters_and_stable()
    {
        var id = PairingCode.MeetingId("K7QM2XHD9PW4R8TA");
        id.ShouldMatch("^[0-9a-f]{32}$");
        PairingCode.MeetingId("K7QM2XHD9PW4R8TA").ShouldBe(id);
        PairingCode.MeetingId("K7QM2XHD9PW4R8TB").ShouldNotBe(id);
    }

    [Fact]
    public void A_mac_names_its_side()
    {
        var key = PairingCode.Key("K7QM2XHD9PW4R8TA");
        byte[] eph = [1], sign = [2], dh = [3];
        PairingCode.Mac(key, "adder", eph, sign, dh).ShouldNotBe(PairingCode.Mac(key, "joiner", eph, sign, dh));
        PairingCode.Mac(key, "adder", eph, sign, dh).ShouldBe(PairingCode.Mac(key, "adder", eph, sign, dh));
    }

    [Fact]
    public void A_recovery_code_is_six_groups_of_four_and_its_key_is_stable()
    {
        var code = RecoveryCode.New();
        code.ShouldMatch("^[0-9A-HJKMNP-TV-Z]{4}(-[0-9A-HJKMNP-TV-Z]{4}){5}$");
        var normalized = RecoveryCode.Normalize(code).ShouldNotBeNull();
        RecoveryCode.Key(normalized).Length.ShouldBe(32);
        RecoveryCode.Key(normalized).ShouldBe(RecoveryCode.Key(normalized));
    }
}
