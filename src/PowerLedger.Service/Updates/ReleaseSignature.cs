using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using PowerLedger.Updates;

namespace PowerLedger.Service.Updates;

/// <summary>
/// Release signatures (Plan Q §4): release.ps1 signs each installer with the owner's ECDSA P-256 key, as a DER signature
/// over <see cref="Message"/>, and lists them in PowerLedger-X.Y.Z-signatures.json. The message names the version and the
/// file as well as the SHA-256, so a signature can't be carried over to another release or installer. The key is a
/// parameter, so tests bring their own pair; the service passes <see cref="ReleaseKey.PublicKey"/>.
/// </summary>
internal static class ReleaseSignature
{
    /// <summary>What is signed: the UTF-8 of "PowerLedger|&lt;version X.Y.Z&gt;|&lt;file name&gt;|&lt;SHA-256, lowercase
    /// hex&gt;".</summary>
    public static byte[] Message(string version, string fileName, ReadOnlySpan<byte> sha256)
        => Encoding.UTF8.GetBytes($"PowerLedger|{version}|{fileName}|{Convert.ToHexStringLower(sha256)}");

    /// <summary>Whether <paramref name="signature"/> is the key's signature of <paramref name="fileName"/>, with
    /// <paramref name="sha256"/>, as release <paramref name="version"/>'s. An empty or broken key, or a signature that
    /// isn't one, verifies nothing.</summary>
    public static bool Verifies(string publicKey, string version, string fileName, ReadOnlySpan<byte> sha256, ReadOnlySpan<byte> signature)
    {
        if (string.IsNullOrEmpty(publicKey)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            if (key.KeySize != 256) return false;
            return key.VerifyData(Message(version, fileName, sha256), signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception error) when (error is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>The signature the signatures file lists for <paramref name="fileName"/>; null when it lists none, or the
    /// file isn't a JSON object of base64 strings.</summary>
    public static byte[]? For(ReadOnlySpan<byte> signaturesJson, string fileName)
    {
        try
        {
            var reader = new Utf8JsonReader(signaturesJson);
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(fileName, out var value)
                || value.ValueKind != JsonValueKind.String)
                return null;
            return Convert.FromBase64String(value.GetString()!);
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// A downloaded installer, open against every writer and deleter from the moment it is checked until it is disposed, so
/// what setup runs is what was checked (Plan Q §4): its size, its SHA-256 (GitHub's), and the release key's signature over
/// that digest with the release's version and the installer's file name. A file someone already has open for writing
/// can't be held, and so isn't run.
/// </summary>
internal sealed class VerifiedInstaller : IDisposable
{
    private readonly FileStream _hold;

    private VerifiedInstaller(string path, FileStream hold)
    {
        Path = path;
        _hold = hold;
    }

    /// <summary>The installer's full path.</summary>
    public string Path { get; }

    /// <summary>The open handle, which stays open until this is disposed.</summary>
    public SafeFileHandle Handle => _hold.SafeFileHandle;

    /// <summary>Opens and checks the installer against <paramref name="release"/>'s size, SHA-256, version and file name.
    /// Throws <see cref="UpdateException"/> when it is gone, can't be held, or isn't the release's; the file is let go
    /// again in that case.</summary>
    public static VerifiedInstaller Open(string path, Release release, byte[] signature, string publicKey)
    {
        var (size, sha256) = (release.Size, release.Sha256);
        var name = System.IO.Path.GetFileName(path);
        FileStream hold;
        try
        {
            hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException($"{name} couldn't be held for checking, so it isn't run.", error);
        }
        try
        {
            if (hold.Length != size) throw new UpdateException($"{name} isn't the size GitHub lists, so it isn't run.");
            var digest = SHA256.HashData(hold);
            if (!digest.AsSpan().SequenceEqual(sha256)) throw new UpdateException($"{name} didn't match GitHub's checksum, so it isn't run.");
            if (!ReleaseSignature.Verifies(publicKey, release.Name, release.FileName, digest, signature)) throw new UpdateException($"{name} has no valid release signature, so it isn't run.");
            hold.Position = 0;
            return new VerifiedInstaller(path, hold);
        }
        catch
        {
            hold.Dispose();
            throw;
        }
    }

    public void Dispose() => _hold.Dispose();
}
