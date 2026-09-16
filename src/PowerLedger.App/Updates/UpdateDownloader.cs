using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PowerLedger.App;

/// <summary>Brings a release's installer down and keeps it only when it is the file GitHub lists.</summary>
internal interface IUpdateDownloader
{
    /// <summary>The installer's path once it is whole and its SHA-256 is GitHub's; a checked copy already there is reused.
    /// <paramref name="progress"/> hears the fraction done. Throws <see cref="UpdateException"/>.</summary>
    Task<string> DownloadAsync(Release release, IProgress<double>? progress, CancellationToken cancel);

    /// <summary>Removes unfinished downloads, installers for <paramref name="running"/> or older, and setup logs older than
    /// it; the log of the update that brought it stays.</summary>
    void Clean(Version running);
}

/// <summary>
/// The quiet download (spec §13): into the Updates folder under a .partial name, hashed as it arrives, and renamed to the
/// installer's own name only when its size and SHA-256 are the release's. A minute without data abandons it, and a file
/// that doesn't match is deleted; the next check tries again.
/// </summary>
internal sealed partial class UpdateDownloader(HttpClient http, string folder, TimeSpan? stall = null) : IUpdateDownloader
{
    public static readonly TimeSpan Stall = TimeSpan.FromMinutes(1);

    public static string DefaultFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerLedger", "Updates");

    public async Task<string> DownloadAsync(Release release, IProgress<double>? progress, CancellationToken cancel)
    {
        var path = Path.Combine(folder, release.FileName);
        if (Matches(path, release.Size, release.Sha256)) return path;
        var partial = path + ".partial";
        var patience = stall ?? Stall;
        try
        {
            Directory.CreateDirectory(folder);
            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            quiet.CancelAfter(patience);
            using var response = await http.GetAsync(release.Installer, HttpCompletionOption.ResponseHeadersRead, quiet.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new UpdateException($"GitHub answered {(int)response.StatusCode} for {release.FileName}.");
            if (response.Content.Headers.ContentLength is { } length && length != release.Size) throw WrongSize(release);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(quiet.Token).ConfigureAwait(false))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await source.ReadAsync(buffer, quiet.Token).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > release.Size) throw WrongSize(release);
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), quiet.Token).ConfigureAwait(false);
                    quiet.CancelAfter(patience);   // a minute for each piece, however long the whole takes
                    progress?.Report((double)received / release.Size);
                }
            }
            if (received != release.Size) throw new UpdateException($"{release.FileName} arrived incomplete; PowerLedger tries again later.");
            if (!hash.GetHashAndReset().AsSpan().SequenceEqual(release.Sha256))
                throw new UpdateException($"{release.FileName} didn't match GitHub's checksum, so it was deleted.");
            File.Move(partial, path, overwrite: true);
            return path;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new UpdateException("The download stalled; PowerLedger tries again later.");
        }
        catch (Exception error) when (error is HttpRequestException or HttpIOException)
        {
            throw new UpdateException($"Couldn't download {release.FileName}; PowerLedger tries again later.", error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException("Couldn't save the update: " + error.Message, error);
        }
        finally
        {
            Delete(partial);   // nothing is there once the download has moved into place
        }
    }

    public void Clean(Version running)
    {
        string[] files;
        try
        {
            if (!Directory.Exists(folder)) return;
            files = Directory.GetFiles(folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var kept = Kept().Match(name);
            var version = kept.Success ? Version.Parse(kept.Groups[1].Value) : null;
            var old = kept.Groups[2].Value == "exe" ? version <= running : version < running;
            if (name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) || (version is not null && old)) Delete(file);
        }
    }

    /// <summary>Whether <paramref name="path"/> holds exactly this size and SHA-256.</summary>
    internal static bool Matches(string path, long size, byte[] sha256)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return file.Length == size && SHA256.HashData(file).AsSpan().SequenceEqual(sha256);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static UpdateException WrongSize(Release release) => new($"{release.FileName} isn't the size GitHub lists, so it was deleted.");

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // In use or refused: the next start tries again.
        }
    }

    [GeneratedRegex("^PowerLedger-([0-9]{1,5}\\.[0-9]{1,5}\\.[0-9]{1,5})-setup(?:-x64|-arm64)?\\.(exe|log)$")]
    private static partial Regex Kept();
}
