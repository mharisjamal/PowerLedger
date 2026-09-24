using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PowerLedger.Core.Households;

/// <summary>This PC's own keys (households design §1): P-256 ECDSA to sign, P-256 ECDH to agree keys. P-256 because both
/// .NET and WebCrypto, on the Worker, have it built in.</summary>
public sealed class DeviceKeys : IDisposable
{
    public ECDsa Sign { get; }
    public ECDiffieHellman Dh { get; }

    public DeviceKeys(ECDsa sign, ECDiffieHellman dh)
    {
        Sign = sign;
        Dh = dh;
    }

    public static DeviceKeys Create() =>
        new(ECDsa.Create(ECCurve.NamedCurves.nistP256), ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>The signing key's public half, as SubjectPublicKeyInfo DER.</summary>
    public byte[] SignPublic => Sign.ExportSubjectPublicKeyInfo();

    /// <summary>The key-agreement key's public half, as SubjectPublicKeyInfo DER.</summary>
    public byte[] DhPublic => Dh.ExportSubjectPublicKeyInfo();

    public string DeviceId => HouseholdCrypto.DeviceIdOf(SignPublic);

    /// <summary>The two private keys as PKCS#8, each to be kept DPAPI-protected on its own.</summary>
    public (byte[] Sign, byte[] Dh) ExportPrivate() => (Sign.ExportPkcs8PrivateKey(), Dh.ExportPkcs8PrivateKey());

    public static DeviceKeys FromPrivate(byte[] sign, byte[] dh)
    {
        var signKey = ECDsa.Create();
        signKey.ImportPkcs8PrivateKey(sign, out _);
        var dhKey = ECDiffieHellman.Create();
        dhKey.ImportPkcs8PrivateKey(dh, out _);
        return new(signKey, dhKey);
    }

    public void Dispose()
    {
        Sign.Dispose();
        Dh.Dispose();
    }
}

/// <summary>
/// The cryptography households rest on (households design §1 to §6): HKDF-SHA256, AES-256-GCM, ECDSA and ECDH over P-256,
/// and the few fixed formats both sides must agree on — what a signed request covers, a batch's associated data, the
/// comparison code and the household tag. The Worker's tests check the committed vectors against their own WebCrypto
/// code, so the two can't drift.
/// </summary>
public static class HouseholdCrypto
{
    public const int KeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;

    /// <summary>The first 16 bytes of SHA-256 of the signing key's SPKI, as lower-case hex.</summary>
    public static string DeviceIdOf(byte[] signSpki) => Convert.ToHexStringLower(SHA256.HashData(signSpki)[..16]);

    public static byte[] Hkdf(byte[] ikm, byte[] salt, string info, int length = KeyLength) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, length, salt, Encoding.UTF8.GetBytes(info));

    /// <summary>Nonce (12) ‖ ciphertext ‖ tag (16), AES-256-GCM under a random nonce.</summary>
    public static byte[] Seal(byte[] key, byte[] plaintext, byte[] aad)
    {
        var sealedBytes = new byte[NonceLength + plaintext.Length + TagLength];
        var nonce = sealedBytes.AsSpan(0, NonceLength);
        RandomNumberGenerator.Fill(nonce);
        using var gcm = new AesGcm(key, TagLength);
        gcm.Encrypt(nonce, plaintext, sealedBytes.AsSpan(NonceLength, plaintext.Length),
            sealedBytes.AsSpan(NonceLength + plaintext.Length, TagLength), aad);
        return sealedBytes;
    }

    /// <summary>The plaintext; a <see cref="CryptographicException"/> when the key, the associated data or any byte is wrong.</summary>
    public static byte[] Open(byte[] key, byte[] sealedBytes, byte[] aad)
    {
        if (sealedBytes.Length < NonceLength + TagLength) throw new CryptographicException("That is too short to be sealed.");
        var plaintext = new byte[sealedBytes.Length - NonceLength - TagLength];
        using var gcm = new AesGcm(key, TagLength);
        gcm.Decrypt(sealedBytes.AsSpan(0, NonceLength), sealedBytes.AsSpan(NonceLength, plaintext.Length),
            sealedBytes.AsSpan(NonceLength + plaintext.Length, TagLength), plaintext, aad);
        return plaintext;
    }

    /// <summary>ECDSA P-256 over SHA-256, as IEEE P1363 (r ‖ s, 64 bytes), the form WebCrypto signs and verifies.</summary>
    public static byte[] SignData(ECDsa key, byte[] data) =>
        key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    /// <summary>False, not an exception, for a key that can't be read or a signature that doesn't match.</summary>
    public static bool Verify(byte[] signSpki, byte[] data, byte[] signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(signSpki, out _);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>The raw ECDH shared secret with another key (its SPKI).</summary>
    public static byte[] Agree(ECDiffieHellman mine, byte[] theirDhSpki)
    {
        using var theirs = ECDiffieHellman.Create();
        theirs.ImportSubjectPublicKeyInfo(theirDhSpki, out _);
        return mine.DeriveRawSecretAgreement(theirs.PublicKey);
    }

    /// <summary>The 6-digit comparison code both screens show (households design §3), as "482 913", from the shared
    /// secret and the hellos' transcript (<see cref="Transcript"/>). Every key in both hellos is bound: a PC in the middle
    /// that swaps an ephemeral or a device key gives each side a different code.</summary>
    public static string ComparisonCode(byte[] shared, byte[] transcript) =>
        SixDigits(Hkdf(shared, transcript, "powerledger comparison code", 4));

    /// <summary>What a pairing's code and frame keys are bound to: SHA-256 of the adder's hello frame then the joiner's, as
    /// the bytes that went over the wire.</summary>
    public static byte[] Transcript(byte[] adderHello, byte[] joinerHello) => SHA256.HashData([.. adderHello, .. joinerHello]);

    /// <summary>N2's approval code (households design §7): 6 digits over the waiting PC's signing and key-agreement keys and
    /// the approving PC's key-agreement key. Both PCs show it; a server that swapped any of the three keys makes them differ.</summary>
    public static string ApprovalCode(byte[] requesterSign, byte[] requesterDh, byte[] approverDh) =>
        SixDigits(SHA256.HashData([.. Encoding.UTF8.GetBytes("powerledger approval code"), .. requesterSign, .. requesterDh, .. approverDh])[..4]);

    /// <summary>What a batch's signature covers (households design §5): its associated data then the sealed bytes, so a
    /// batch can't be moved to another household, device, epoch or sequence, or changed.</summary>
    public static byte[] BatchToSign(byte[] aad, byte[] sealedBody) => [.. aad, .. sealedBody];

    private static string SixDigits(byte[] four)
    {
        var digits = (BinaryPrimitives.ReadUInt32BigEndian(four) % 1_000_000).ToString("D6");
        return $"{digits[..3]} {digits[3..]}";
    }

    /// <summary>A key sealed for one member (households design §6): ECDH with its key-agreement key, HKDF with the context,
    /// AES-GCM with the context as associated data. The context says what it is, "household hid epoch 4" say.</summary>
    public static byte[] WrapFor(ECDiffieHellman mine, byte[] theirDhSpki, byte[] key, string context) =>
        Seal(WrapKey(mine, theirDhSpki, context), key, Encoding.UTF8.GetBytes(context));

    public static byte[] UnwrapFrom(ECDiffieHellman mine, byte[] theirDhSpki, byte[] wrapped, string context) =>
        Open(WrapKey(mine, theirDhSpki, context), wrapped, Encoding.UTF8.GetBytes(context));

    private static byte[] WrapKey(ECDiffieHellman mine, byte[] theirDhSpki, string context) =>
        Hkdf(Agree(mine, theirDhSpki), [], "powerledger key wrap|" + context);

    /// <summary>What a signed request covers (households design §5): the method, the path with its query, the time in unix
    /// seconds and the lower-case hex SHA-256 of the body, one to a line.</summary>
    public static byte[] RequestToSign(string method, string pathAndQuery, long unixSeconds, byte[] body) =>
        Encoding.UTF8.GetBytes(
            $"{method.ToUpperInvariant()}\n{pathAndQuery}\n{unixSeconds}\n{Convert.ToHexStringLower(SHA256.HashData(body))}");

    /// <summary>A batch's associated data (households design §5): household, device, epoch and sequence.</summary>
    public static byte[] BatchAad(string householdId, string deviceId, int epoch, long sequence) =>
        Encoding.UTF8.GetBytes($"{householdId}|{deviceId}|{epoch}|{sequence}");

    /// <summary>The DNS-SD household tag (households design §3): HMAC-SHA256 of the instance ID under the household key,
    /// cut to 8 bytes, as hex. Members can work it out; strangers can't link PCs by it.</summary>
    public static string HouseholdTag(byte[] householdKey, string instanceId) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(householdKey, Encoding.UTF8.GetBytes(instanceId))[..8]);

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyLength);
}
