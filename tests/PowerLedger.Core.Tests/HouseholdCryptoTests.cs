using System.Security.Cryptography;
using System.Text;
using PowerLedger.Core.Households;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class HouseholdCryptoTests
{
    private static readonly byte[] Aad = Encoding.UTF8.GetBytes("aad");

    [Fact]
    public void Hkdf_gives_the_length_asked_and_differs_by_info()
    {
        byte[] ikm = [1, 2, 3];
        HouseholdCrypto.Hkdf(ikm, [], "a").Length.ShouldBe(32);
        HouseholdCrypto.Hkdf(ikm, [], "a", 4).Length.ShouldBe(4);
        HouseholdCrypto.Hkdf(ikm, [], "a").ShouldBe(HouseholdCrypto.Hkdf(ikm, [], "a"));
        HouseholdCrypto.Hkdf(ikm, [], "a").ShouldNotBe(HouseholdCrypto.Hkdf(ikm, [], "b"));
    }

    [Fact]
    public void Sealed_bytes_open_with_the_same_key_and_associated_data()
    {
        var key = HouseholdCrypto.NewKey();
        var sealedBytes = HouseholdCrypto.Seal(key, [9, 8, 7], Aad);
        sealedBytes.Length.ShouldBe(12 + 3 + 16);
        HouseholdCrypto.Open(key, sealedBytes, Aad).ShouldBe(new byte[] { 9, 8, 7 });
    }

    [Fact]
    public void Two_seals_of_the_same_bytes_differ()
    {
        var key = HouseholdCrypto.NewKey();
        HouseholdCrypto.Seal(key, [1], Aad).ShouldNotBe(HouseholdCrypto.Seal(key, [1], Aad));
    }

    [Fact]
    public void A_wrong_key_associated_data_or_byte_is_refused()
    {
        var key = HouseholdCrypto.NewKey();
        var sealedBytes = HouseholdCrypto.Seal(key, [1, 2, 3], Aad);
        Should.Throw<CryptographicException>(() => HouseholdCrypto.Open(HouseholdCrypto.NewKey(), sealedBytes, Aad));
        Should.Throw<CryptographicException>(() => HouseholdCrypto.Open(key, sealedBytes, Encoding.UTF8.GetBytes("other")));
        var tampered = (byte[])sealedBytes.Clone();
        tampered[14] ^= 1;
        Should.Throw<CryptographicException>(() => HouseholdCrypto.Open(key, tampered, Aad));
        Should.Throw<CryptographicException>(() => HouseholdCrypto.Open(key, new byte[20], Aad));
    }

    [Fact]
    public void A_signature_is_64_bytes_and_verifies_until_a_bit_changes()
    {
        using var keys = DeviceKeys.Create();
        var data = Encoding.UTF8.GetBytes("hello");
        var signature = HouseholdCrypto.SignData(keys.Sign, data);
        signature.Length.ShouldBe(64);
        HouseholdCrypto.Verify(keys.SignPublic, data, signature).ShouldBeTrue();
        signature[5] ^= 1;
        HouseholdCrypto.Verify(keys.SignPublic, data, signature).ShouldBeFalse();
        HouseholdCrypto.Verify([1, 2, 3], data, signature).ShouldBeFalse();
    }

    [Fact]
    public void Both_sides_agree_on_the_same_secret()
    {
        using var a = DeviceKeys.Create();
        using var b = DeviceKeys.Create();
        HouseholdCrypto.Agree(a.Dh, b.DhPublic).ShouldBe(HouseholdCrypto.Agree(b.Dh, a.DhPublic));
    }

    [Fact]
    public void The_approval_code_is_six_digits_and_changes_with_any_of_its_three_keys()
    {
        using var requester = DeviceKeys.Create();
        using var approver = DeviceKeys.Create();
        using var other = DeviceKeys.Create();
        var code = HouseholdCrypto.ApprovalCode(requester.SignPublic, requester.DhPublic, approver.DhPublic);

        code.ShouldMatch("^[0-9]{3} [0-9]{3}$");
        HouseholdCrypto.ApprovalCode(requester.SignPublic, requester.DhPublic, approver.DhPublic).ShouldBe(code);
        HouseholdCrypto.ApprovalCode(other.SignPublic, requester.DhPublic, approver.DhPublic).ShouldNotBe(code);
        HouseholdCrypto.ApprovalCode(requester.SignPublic, other.DhPublic, approver.DhPublic).ShouldNotBe(code);
        HouseholdCrypto.ApprovalCode(requester.SignPublic, requester.DhPublic, other.DhPublic).ShouldNotBe(code);
    }

    [Fact]
    public void A_nonce_is_32_fresh_bytes_and_its_commitment_is_a_fixed_hash_of_it()
    {
        var nonce = HouseholdCrypto.NewNonce();
        nonce.Length.ShouldBe(32);
        HouseholdCrypto.NewNonce().ShouldNotBe(nonce);
        HouseholdCrypto.Commitment(nonce).ShouldBe(SHA256.HashData([.. "powerledger commit"u8, .. nonce]));
        HouseholdCrypto.Commitment([.. nonce[..31], (byte)(nonce[31] ^ 1)]).ShouldNotBe(HouseholdCrypto.Commitment(nonce));
    }

    [Fact]
    public void The_code_takes_the_adders_revealed_nonce_so_the_side_that_answers_second_cant_steer_it()
    {
        byte[] shared = [.. Enumerable.Repeat((byte)7, 32)], transcript = [.. Enumerable.Repeat((byte)9, 32)];
        byte[] nonce = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];
        var code = HouseholdCrypto.ComparisonCode(shared, transcript, nonce);

        code.ShouldMatch("^[0-9]{3} [0-9]{3}$");
        code.ShouldBe(SixDigits(HouseholdCrypto.Hkdf(shared, [.. transcript, .. nonce], "powerledger comparison code", 4)));
        HouseholdCrypto.ComparisonCode(shared, transcript, [.. nonce[..31], 32]).ShouldNotBe(code);
    }

    [Fact]
    public void The_approval_code_binds_both_pcs_keys_and_both_nonces()
    {
        byte[][] parts = [.. Enumerable.Range(1, 6).Select(i => Enumerable.Repeat((byte)i, i <= 4 ? 91 : 32).ToArray())];
        var code = HouseholdCrypto.ApprovalCode(parts[0], parts[1], parts[2], parts[3], parts[4], parts[5]);

        code.ShouldMatch("^[0-9]{3} [0-9]{3}$");
        code.ShouldBe(SixDigits(SHA256.HashData([.. "powerledger approval code"u8, .. parts.SelectMany(part => part)])[..4]));
        for (var changed = 0; changed < parts.Length; changed++)
        {
            var other = parts.Select(part => (byte[])part.Clone()).ToArray();
            other[changed][0] ^= 0xff;
            HouseholdCrypto.ApprovalCode(other[0], other[1], other[2], other[3], other[4], other[5]).ShouldNotBe(code, $"part {changed}");
        }
    }

    private static string SixDigits(byte[] four)
    {
        var digits = (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(four) % 1_000_000).ToString("D6");
        return $"{digits[..3]} {digits[3..]}";
    }

    [Fact]
    public void A_batch_signature_covers_its_associated_data_and_then_its_sealed_bytes()
    {
        HouseholdCrypto.BatchToSign([1, 2], [3, 4, 5]).ShouldBe(new byte[] { 1, 2, 3, 4, 5 });
    }

    [Fact]
    public void A_key_wrapped_for_a_member_unwraps_only_there_and_only_for_its_context()
    {
        using var sender = DeviceKeys.Create();
        using var member = DeviceKeys.Create();
        var key = HouseholdCrypto.NewKey();
        var wrapped = HouseholdCrypto.WrapFor(sender.Dh, member.DhPublic, key, "household h1 epoch 2");

        HouseholdCrypto.UnwrapFrom(member.Dh, sender.DhPublic, wrapped, "household h1 epoch 2").ShouldBe(key);
        Should.Throw<CryptographicException>(() => HouseholdCrypto.UnwrapFrom(member.Dh, sender.DhPublic, wrapped, "household h1 epoch 3"));
        using var stranger = DeviceKeys.Create();
        Should.Throw<CryptographicException>(() => HouseholdCrypto.UnwrapFrom(stranger.Dh, sender.DhPublic, wrapped, "household h1 epoch 2"));
    }

    [Fact]
    public void A_device_id_is_32_hex_characters_and_survives_saving_the_keys()
    {
        using var keys = DeviceKeys.Create();
        keys.DeviceId.ShouldMatch("^[0-9a-f]{32}$");
        var (sign, dh) = keys.ExportPrivate();
        using var back = DeviceKeys.FromPrivate(sign, dh);
        back.DeviceId.ShouldBe(keys.DeviceId);
        back.DhPublic.ShouldBe(keys.DhPublic);
    }

    [Fact]
    public void What_a_request_signature_covers_is_one_field_a_line()
    {
        var text = Encoding.UTF8.GetString(HouseholdCrypto.RequestToSign("post", "/v1/households/h1/batches?x=1", 1_790_000_000, "{}"u8.ToArray()));
        text.ShouldBe("POST\n/v1/households/h1/batches?x=1\n1790000000\n44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a");
    }

    [Fact]
    public void A_household_tag_is_8_bytes_of_hex_and_differs_by_pc()
    {
        var key = HouseholdCrypto.NewKey();
        var tag = HouseholdCrypto.HouseholdTag(key, "pc-a");
        tag.ShouldMatch("^[0-9a-f]{16}$");
        HouseholdCrypto.HouseholdTag(key, "pc-a").ShouldBe(tag);
        HouseholdCrypto.HouseholdTag(key, "pc-b").ShouldNotBe(tag);
    }

    [Fact]
    public void A_batch_names_its_household_device_epoch_and_sequence()
    {
        Encoding.UTF8.GetString(HouseholdCrypto.BatchAad("h1", "d1", 3, 17)).ShouldBe("h1|d1|3|17");
    }
}
