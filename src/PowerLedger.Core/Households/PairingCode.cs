using System.Security.Cryptography;
using System.Text;

namespace PowerLedger.Core.Households;

/// <summary>
/// The one-time code for adding a PC that isn't on the same network (households design §4): 16 Crockford base32
/// characters, 80 random bits, shown as <c>K7QM-2XHD-9PW4-R8TA</c>. The server sees only the meeting ID made from it; the
/// key made from it authenticates both sides' keys, which the server can't forge without the code.
/// </summary>
public static class PairingCode
{
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int Length = 16;

    public static string New() => Format(Encode(RandomNumberGenerator.GetBytes(10)));

    /// <summary>The code as typed, made canonical: upper case, no spaces or dashes, O read as 0 and I or L as 1; null when
    /// that isn't 16 characters of the alphabet.</summary>
    public static string? Normalize(string? typed) => Normalize(typed, Length);

    /// <summary>The meeting's ID: the first 16 bytes of SHA-256 of the canonical code, as hex.</summary>
    public static string MeetingId(string normalized) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(normalized))[..16]);

    public static byte[] Key(string normalized) => HouseholdCrypto.Hkdf(Encoding.ASCII.GetBytes(normalized), [], "powerledger code key");

    /// <summary>What vouches for one side's keys: HMAC-SHA256 under the code's key over the side ("adder" or "joiner") and
    /// its ephemeral, signing and key-agreement keys.</summary>
    public static byte[] Mac(byte[] key, string side, byte[] eph, byte[] sign, byte[] dh) =>
        HMACSHA256.HashData(key, (byte[])[.. Encoding.ASCII.GetBytes(side), .. eph, .. sign, .. dh]);

    /// <summary>Groups of four joined by dashes.</summary>
    public static string Format(string normalized) =>
        string.Join('-', Enumerable.Range(0, normalized.Length / 4).Select(group => normalized.Substring(group * 4, 4)));

    /// <summary>Base32 over the alphabet, 5 bits a character, most significant first.</summary>
    internal static string Encode(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder(bytes.Length * 8 / 5 + 1);
        int buffer = 0, bits = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                text.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) text.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return text.ToString();
    }

    internal static string? Normalize(string? typed, int length)
    {
        if (typed is null) return null;
        var text = new StringBuilder(length);
        foreach (var raw in typed)
        {
            if (raw is ' ' or '-') continue;
            var character = char.ToUpperInvariant(raw) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                var other => other,
            };
            if (!Alphabet.Contains(character)) return null;
            text.Append(character);
        }
        return text.Length == length ? text.ToString() : null;
    }
}

/// <summary>N2's recovery code (households design §7): 24 characters, 120 random bits, shown once, which with the account
/// unlocks the household key kept with it.</summary>
public static class RecoveryCode
{
    public const int Length = 24;

    public static string New() => PairingCode.Format(PairingCode.Encode(RandomNumberGenerator.GetBytes(15)));

    public static string? Normalize(string? typed) => PairingCode.Normalize(typed, Length);

    /// <summary>PBKDF2-SHA256; the code already carries 120 bits, so the iterations only slow a guess at a typing mistake.</summary>
    public static byte[] Key(string normalized) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.ASCII.GetBytes(normalized), Encoding.ASCII.GetBytes("powerledger recovery"), 100_000,
            HashAlgorithmName.SHA256, HouseholdCrypto.KeyLength);
}
