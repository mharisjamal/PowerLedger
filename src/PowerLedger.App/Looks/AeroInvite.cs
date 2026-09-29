using System.Security.Cryptography;
using System.Text;

namespace PowerLedger.App;

/// <summary>Aero is invite only (0.10.3, the owner's decision): a PC turns it on with the invite code, checked here by
/// its SHA-256, so the code itself isn't in the source. Trimmed, in any case.</summary>
internal static class AeroInvite
{
    /// <summary>The SHA-256 of the upper-cased code, in lower-case hex.</summary>
    internal const string CodeHash = "f00c5fcf4f8b1285c608a74f3829b8bf739c233de5e13be3deb690331f981450";

    /// <summary>What a switch to Aero answers while it is locked.</summary>
    public const string Locked = "Aero is invite only. Enter your invite code in Settings, Look, to turn it on.";

    /// <summary>What a wrong code gets.</summary>
    public const string Invalid = "That code isn't valid.";

    public static bool IsValid(string? code)
    {
        var typed = code?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(typed)) return false;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(typed)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(CodeHash));
    }
}
