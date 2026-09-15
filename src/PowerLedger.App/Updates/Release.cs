namespace PowerLedger.App;

/// <summary>A published release of PowerLedger as the App trusts it (spec §13): its version, its page on GitHub, and the
/// installer GitHub holds for it, with the size and SHA-256 GitHub lists.</summary>
internal sealed record Release(Version Version, Uri Page, Uri Installer, string FileName, long Size, byte[] Sha256)
{
    /// <summary>The version as people read it: "0.2.0".</summary>
    public string Name => Version.ToString(3);
}

/// <summary>An update step failed; the message is a sentence for the user.</summary>
internal sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);
