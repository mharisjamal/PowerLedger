namespace PowerLedger.Service.Updates;

/// <summary>
/// The owner's release signing key (Plan Q §4): the public half of the ECDSA P-256 key scripts\new-release-key.ps1 made,
/// as base64 SubjectPublicKeyInfo. release.ps1 signs every installer with the private half; the service installs an update
/// itself only when the installer's signature verifies against this. Empty until the owner adds it, and while it is empty
/// the service never installs anything: the App's own click-to-install flow is the only way to update.
/// </summary>
internal static class ReleaseKey
{
    /// <summary>Base64 SubjectPublicKeyInfo of the release key; empty when none is set. release.ps1 reads this line.</summary>
    public const string PublicKey = "";

    /// <summary>Whether a key is built in, so the service may install updates itself.</summary>
    public static bool Present => PublicKey.Length > 0;
}
