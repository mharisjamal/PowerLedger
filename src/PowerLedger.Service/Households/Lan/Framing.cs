using System.Buffers.Binary;
using System.Security.Cryptography;
using PowerLedger.Contracts;
using PowerLedger.Core.Households;

namespace PowerLedger.Service.Households.Lan;

/// <summary>A connection that carries whole frames (plan 0.6).</summary>
internal interface IFrameChannel : IAsyncDisposable
{
    Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancel = default);

    /// <summary>The next frame, or null when the other side closed the connection between frames.</summary>
    Task<byte[]?> ReceiveAsync(CancellationToken cancel = default);
}

/// <summary>LAN frames (plan 0.6): a 4-byte big-endian length, then that many bytes, at most 1 MB.</summary>
internal static class Framing
{
    public const int MaxFrame = 1024 * 1024;

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> frame, CancellationToken cancel = default)
    {
        if (frame.Length > MaxFrame) throw new InvalidDataException($"A frame of {frame.Length} bytes is over the 1 MB limit.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, frame.Length);
        await stream.WriteAsync(header, cancel).ConfigureAwait(false);
        await stream.WriteAsync(frame, cancel).ConfigureAwait(false);
        await stream.FlushAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>The next frame; null when the stream ends between frames. One cut short is an <see cref="EndOfStreamException"/>,
    /// and one said to be over 1 MB an <see cref="InvalidDataException"/>.</summary>
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancel = default)
    {
        var header = new byte[4];
        var read = await stream.ReadAtLeastAsync(header, 4, throwOnEndOfStream: false, cancel).ConfigureAwait(false);
        if (read == 0) return null;
        if (read < 4) throw new EndOfStreamException("The connection closed inside a frame's length.");
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 0 or > MaxFrame) throw new InvalidDataException($"A frame said to be {length} bytes is over the 1 MB limit.");
        var frame = new byte[length];
        await stream.ReadExactlyAsync(frame, cancel).ConfigureAwait(false);
        return frame;
    }
}

/// <summary>Frames over a stream, as a TCP connection is.</summary>
internal sealed class StreamFrameChannel(Stream stream) : IFrameChannel
{
    private readonly SemaphoreSlim _writing = new(1, 1);

    public async Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancel = default)
    {
        await _writing.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            await Framing.WriteAsync(stream, frame, cancel).ConfigureAwait(false);
        }
        finally
        {
            _writing.Release();
        }
    }

    public Task<byte[]?> ReceiveAsync(CancellationToken cancel = default) => Framing.ReadAsync(stream, cancel);

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync().ConfigureAwait(false);
        _writing.Dispose();
    }
}

/// <summary>
/// AES-256-GCM for the frames after the key exchange (plan 0.6): a key each way, and a nonce of 4 zero bytes then a count
/// from 0, big-endian, that each side keeps for itself. The nonce never goes on the wire, so a frame replayed, dropped or
/// out of order doesn't open, and the connection ends.
/// </summary>
internal sealed class FrameCipher(byte[] send, byte[] receive) : IDisposable
{
    private const int TagLength = 16;
    private readonly AesGcm _send = new(send, TagLength);
    private readonly AesGcm _receive = new(receive, TagLength);
    private ulong _sent;
    private ulong _received;

    /// <summary>The keys each way for the side that connected, the adder, and for the side that answered (plan 0.6).</summary>
    public static FrameCipher For(bool adder, byte[] shared, byte[] ephAdder, byte[] ephJoiner)
    {
        byte[] salt = [.. ephAdder, .. ephJoiner];
        var a2j = HouseholdCrypto.Hkdf(shared, salt, "powerledger lan a2j");
        var j2a = HouseholdCrypto.Hkdf(shared, salt, "powerledger lan j2a");
        return adder ? new FrameCipher(a2j, j2a) : new FrameCipher(j2a, a2j);
    }

    /// <summary>Ciphertext then tag.</summary>
    public byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        var sealedBytes = new byte[plaintext.Length + TagLength];
        _send.Encrypt(Nonce(_sent++), plaintext, sealedBytes.AsSpan(0, plaintext.Length), sealedBytes.AsSpan(plaintext.Length));
        return sealedBytes;
    }

    /// <summary>The next frame's plaintext; a <see cref="CryptographicException"/> for one that isn't the next.</summary>
    public byte[] Open(ReadOnlySpan<byte> sealedBytes)
    {
        if (sealedBytes.Length < TagLength) throw new CryptographicException("That frame is too short to be sealed.");
        var plaintext = new byte[sealedBytes.Length - TagLength];
        _receive.Decrypt(Nonce(_received), sealedBytes[..plaintext.Length], sealedBytes[plaintext.Length..], plaintext);
        _received++;
        return plaintext;
    }

    public void Dispose()
    {
        _send.Dispose();
        _receive.Dispose();
    }

    private static byte[] Nonce(ulong count)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), count);
        return nonce;
    }
}

/// <summary>Messages on a frame channel: plain JSON until the keys are agreed, then each sealed with a <see cref="FrameCipher"/>.
/// Anything that isn't the message expected ends the exchange with a <see cref="LanException"/>.</summary>
internal sealed class LanConversation(IFrameChannel channel, TimeSpan step)
{
    private FrameCipher? _cipher;

    public void Secure(FrameCipher cipher) => _cipher = cipher;

    public Task SendAsync(LanMessage message, CancellationToken cancel)
    {
        var bytes = LanMessages.Write(message);
        return channel.SendAsync(_cipher is null ? bytes : _cipher.Seal(bytes), cancel);
    }

    /// <summary>The next message, which must be of <paramref name="type"/>, within <paramref name="within"/> or the usual step.</summary>
    public async Task<LanMessage> ReceiveAsync(string type, CancellationToken cancel, TimeSpan? within = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(within ?? step);
        byte[]? frame;
        try
        {
            frame = await channel.ReceiveAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new LanException(LanProblem.Timeout);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ObjectDisposedException)
        {
            throw new LanException(LanProblem.Broken, error);
        }
        if (frame is null) throw new LanException(LanProblem.Closed);
        byte[] plaintext;
        try
        {
            plaintext = _cipher is null ? frame : _cipher.Open(frame);
        }
        catch (CryptographicException error)
        {
            throw new LanException(LanProblem.Broken, error);
        }
        var message = LanMessages.Read(plaintext);
        if (message?.Type != type) throw new LanException(LanProblem.Broken);
        return message;
    }
}

internal enum LanProblem
{
    /// <summary>The other side sent nothing in time.</summary>
    Timeout,

    /// <summary>The other side closed the connection.</summary>
    Closed,

    /// <summary>The other side sent something that isn't what was due: a frame that doesn't open, or the wrong message.</summary>
    Broken,

    /// <summary>The other side isn't a PC this one may sync with.</summary>
    NotAMember,
}

internal sealed class LanException(LanProblem problem, Exception? inner = null) : Exception($"The exchange ended: {problem}.", inner)
{
    public LanProblem Problem { get; } = problem;
}

/// <summary>Every message on a LAN connection (plan 0.6), one type with the fields each kind uses.</summary>
internal sealed record LanMessage
{
    public string? Type { get; init; }

    public int? V { get; init; }

    public string? Purpose { get; init; }

    public string? Eph { get; init; }

    public string? Sign { get; init; }

    public string? Dh { get; init; }

    public string? Name { get; init; }

    public string? Kind { get; init; }

    public string? Instance { get; init; }

    public bool? Accept { get; init; }

    public string? Household { get; init; }

    public int? Epoch { get; init; }

    public string? Key { get; init; }

    public List<WireMember>? Members { get; init; }

    public string? Sig { get; init; }

    public Dictionary<string, long>? Latest { get; init; }

    public string? Device { get; init; }

    public List<WireRow>? Rows { get; init; }

    public string? Mac { get; init; }
}

internal static class LanMessages
{
    public const int Version = 1;

    public static byte[] Write(LanMessage message) => HouseholdJson.Bytes(message, HouseholdJson.Default.LanMessage);

    public static LanMessage? Read(ReadOnlySpan<byte> bytes) => HouseholdJson.Read(bytes, HouseholdJson.Default.LanMessage);

    public static LanMessage? Read(byte[]? bytes) => bytes is null ? null : Read(bytes.AsSpan());

    /// <summary>The first message each way (plan 0.6): the purpose, a fresh ephemeral key, this PC's keys, name and kind, and
    /// the instance name it is announced under.</summary>
    public static LanMessage Hello(string purpose, byte[] eph, DeviceKeys keys, string name, ChassisKind kind, string instance) => new()
    {
        Type = "hello",
        V = Version,
        Purpose = purpose,
        Eph = Wire.Encode(eph),
        Sign = Wire.Encode(keys.SignPublic),
        Dh = Wire.Encode(keys.DhPublic),
        Name = name,
        Kind = Wire.Kind(kind),
        Instance = instance,
    };
}

/// <summary>A hello as checked: P-256 keys, a name and a kind.</summary>
internal sealed record Hello(string Purpose, byte[] Eph, MemberInfo From, string? Instance)
{
    public const string Pair = "pair";
    public const string Sync = "sync";

    /// <summary>The hello in <paramref name="message"/>, or null when it isn't a good one.</summary>
    public static Hello? Of(LanMessage? message)
    {
        if (message is not { Type: "hello", V: LanMessages.Version, Purpose: Pair or Sync }) return null;
        if (Wire.PublicKey(message.Eph) is not { } eph || Wire.PublicKey(message.Sign) is not { } sign || Wire.PublicKey(message.Dh) is not { } dh)
        {
            return null;
        }
        if (Wire.Name(message.Name) is not { } name || Wire.Kind(message.Kind) is not { } kind) return null;
        var instance = message.Instance is { Length: > 0 and <= 64 } given ? given : null;
        return new Hello(message.Purpose!, eph, new MemberInfo(HouseholdCrypto.DeviceIdOf(sign), name, kind, sign, dh), instance);
    }
}
