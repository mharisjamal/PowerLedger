namespace PowerLedger.Updates;

/// <summary>A published release of PowerLedger as the App trusts it (spec §13): its version, its page on GitHub, and the
/// installer GitHub holds for it, with the size and SHA-256 GitHub lists. <paramref name="Signatures"/> is the release's
/// signatures file (Plan Q §4), trusted by the same rules, or null when it has none the service could use.</summary>
internal sealed record Release(
    Version Version, Uri Page, Uri Installer, string FileName, long Size, byte[] Sha256, ReleaseFile? Signatures = null)
{
    /// <summary>The version as people read it: "0.2.0".</summary>
    public string Name => Version.ToString(3);
}

/// <summary>A file attached to a release other than its installer, as GitHub lists it: where it is, its name, its size
/// and the SHA-256 GitHub computed.</summary>
internal sealed record ReleaseFile(Uri Address, string FileName, long Size, byte[] Sha256);

/// <summary>An update step failed; the message is a sentence for the user.</summary>
internal sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);
