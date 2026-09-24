using System.Buffers.Binary;
using System.Security.Cryptography;
using PowerLedger.Core.Households;
using PowerLedger.Service.Households.Lan;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>LAN frames (households design §3, plan 0.6): a 4-byte big-endian length and at most 1 MB, then AES-256-GCM under a
/// counter nonce once the keys are agreed.</summary>
public sealed class FramingTests
{
    [Fact]
    public async Task A_frame_is_its_length_big_endian_then_its_bytes_and_comes_back_whole()
    {
        using var stream = new MemoryStream();
        await Framing.WriteAsync(stream, "hello"u8.ToArray());
        await Framing.WriteAsync(stream, new byte[70_000]);

        stream.ToArray()[..9].ShouldBe(new byte[] { 0, 0, 0, 5, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' });
        stream.Position = 0;
        (await Framing.ReadAsync(stream)).ShouldBe("hello"u8.ToArray());
        (await Framing.ReadAsync(stream)).ShouldNotBeNull().Length.ShouldBe(70_000);
        (await Framing.ReadAsync(stream)).ShouldBeNull();                          // closed between frames
    }

    [Fact]
    public async Task A_frame_over_1_MB_or_cut_short_ends_the_connection()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, Framing.MaxFrame + 1);
        await Should.ThrowAsync<InvalidDataException>(() => Framing.ReadAsync(new MemoryStream(header)));
        await Should.ThrowAsync<InvalidDataException>(() => Framing.WriteAsync(new MemoryStream(), new byte[Framing.MaxFrame + 1]));
        await Should.ThrowAsync<EndOfStreamException>(() => Framing.ReadAsync(new MemoryStream([0, 0, 0, 9, 1, 2, 3])));
    }

    [Fact]
    public void Sealed_frames_open_in_order_under_the_other_directions_keys_and_use_counter_nonces()
    {
        var aToB = HouseholdCrypto.NewKey();
        var bToA = HouseholdCrypto.NewKey();
        using var a = new FrameCipher(send: aToB, receive: bToA);
        using var b = new FrameCipher(send: bToA, receive: aToB);

        var first = a.Seal("one"u8);
        var second = a.Seal("two"u8);

        first.Length.ShouldBe(3 + 16);                                             // no nonce on the wire: it is the counter
        b.Open(first).ShouldBe("one"u8.ToArray());
        b.Open(second).ShouldBe("two"u8.ToArray());
        a.Open(b.Seal("back"u8)).ShouldBe("back"u8.ToArray());

        using var gcm = new AesGcm(aToB, 16);
        var plain = new byte[3];
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), 1);
        gcm.Decrypt(nonce, second.AsSpan(0, 3), second.AsSpan(3), plain);
        plain.ShouldBe("two"u8.ToArray());
    }

    [Fact]
    public void A_frame_out_of_order_or_replayed_does_not_open()
    {
        var aToB = HouseholdCrypto.NewKey();
        using var a = new FrameCipher(send: aToB, receive: HouseholdCrypto.NewKey());
        using var b = new FrameCipher(send: HouseholdCrypto.NewKey(), receive: aToB);
        var first = a.Seal("one"u8);
        var second = a.Seal("two"u8);

        Should.Throw<CryptographicException>(() => b.Open(second));

        using var fresh = new FrameCipher(send: HouseholdCrypto.NewKey(), receive: aToB);
        fresh.Open(first);
        Should.Throw<CryptographicException>(() => fresh.Open(first));
    }
}
