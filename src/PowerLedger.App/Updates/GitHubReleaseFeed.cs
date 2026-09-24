using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PowerLedger.App;

/// <summary>Where the newest release comes from.</summary>
internal interface IReleaseFeed
{
    /// <summary>The newest published release, or null when nothing is published. Throws <see cref="UpdateException"/> when
    /// the answer can't be had or can't be trusted.</summary>
    Task<Release?> LatestAsync(CancellationToken cancel);
}

/// <summary>
/// PowerLedger's latest release on GitHub (spec §13); GitHub leaves out drafts and pre-releases. A release is trusted only
/// when its tag is vX.Y.Z and it carries an installer for this PC — the build for its architecture, or else
/// PowerLedger-X.Y.Z-setup.exe, which holds every build — under the repository's own download address, with a plausible
/// size and the SHA-256 GitHub computed for it. Anything else is refused rather than guessed at.
/// </summary>
internal sealed partial class GitHubReleaseFeed(HttpClient http, Uri latest, string downloads, TimeSpan? timeout = null) : IReleaseFeed
{
    public const string Repository = "mharisjamal/PowerLedger";
    public const string Releases = $"https://github.com/{Repository}/releases";
    public const string Downloads = $"{Releases}/download/";
    public static readonly Uri Latest = new($"https://api.github.com/repos/{Repository}/releases/latest");
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    internal const int MaxAnswer = 1 << 20;
    internal const long MaxInstaller = 1L << 30;

    /// <summary>GitHub, or a stand-in at <paramref name="feed"/> (the App's --update-feed) that answers at
    /// feed/releases/latest and serves installers under feed/download/.</summary>
    public static GitHubReleaseFeed For(HttpClient http, Uri? feed)
        => feed is null ? new(http, Latest, Downloads) : new(http, new Uri(feed, "releases/latest"), new Uri(feed, "download/").AbsoluteUri);

    /// <summary>One version's release page, for "What's new".</summary>
    public static Uri PageOf(Version version) => new($"{Releases}/tag/v{version.ToString(3)}");

    public async Task<Release?> LatestAsync(CancellationToken cancel)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout ?? Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, latest);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;   // nothing published yet
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new UpdateException("GitHub is limiting requests from this network; PowerLedger tries again later.");
            if (!response.IsSuccessStatusCode) throw new UpdateException($"GitHub answered {(int)response.StatusCode}.");
            return Parse(await ReadAsync(response.Content, limit.Token).ConfigureAwait(false), downloads);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new UpdateException("GitHub didn't answer in time.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            throw new UpdateException("Couldn't reach GitHub.", error);
        }
    }

    /// <summary>The release GitHub describes, or null for a draft or a pre-release; refuses one it can't trust.
    /// <paramref name="downloads"/> is where installers must be, and <paramref name="architecture"/> which build this PC
    /// wants.</summary>
    internal static Release? Parse(byte[] json, string downloads, Architecture? architecture = null)
    {
        GitHubRelease release;
        try
        {
            release = JsonSerializer.Deserialize(json, ReleaseJson.Default.GitHubRelease) ?? throw NotARelease(null);
        }
        catch (JsonException error)
        {
            throw NotARelease(error);
        }
        if (release.Draft || release.Prerelease) return null;
        var tag = Tag().Match(release.TagName ?? "");
        if (!tag.Success) throw new UpdateException($"The latest release's tag, {release.TagName}, isn't a version PowerLedger knows.");
        var version = new Version(Number(tag.Groups[1]), Number(tag.Groups[2]), Number(tag.Groups[3]));
        var wanted = InstallerNames(version, architecture ?? RuntimeInformation.OSArchitecture);
        var asset = wanted.Select(name => release.Assets?.FirstOrDefault(a => a.Name == name)).FirstOrDefault(found => found is not null)
            ?? throw new UpdateException($"Release {version.ToString(3)} has no installer for this PC.");
        var name = asset.Name!;
        if (!Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var installer)
            || !installer.AbsoluteUri.StartsWith(downloads, StringComparison.Ordinal))
            throw new UpdateException($"{name} isn't where PowerLedger's releases are kept.");
        if (asset.Size is <= 0 or > MaxInstaller) throw new UpdateException($"{name} is listed at an impossible size.");
        var digest = Digest().Match(asset.Digest ?? "");
        if (!digest.Success) throw new UpdateException($"GitHub lists no SHA-256 for {name}, so it can't be checked.");
        var page = Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var html) && html.AbsoluteUri.StartsWith(Releases + "/", StringComparison.Ordinal)
            ? html
            : PageOf(version);
        return new Release(version, page, installer, name, asset.Size, Convert.FromHexString(digest.Groups[1].Value));
    }

    /// <summary>The installers a release may carry, best first: the one built for this PC, then the one with every build.</summary>
    internal static IReadOnlyList<string> InstallerNames(Version version, Architecture architecture)
    {
        var number = version.ToString(3);
        var universal = $"PowerLedger-{number}-setup.exe";
        return architecture switch
        {
            Architecture.X64 => [$"PowerLedger-{number}-setup-x64.exe", universal],
            Architecture.Arm64 => [$"PowerLedger-{number}-setup-arm64.exe", universal],
            Architecture.X86 => [$"PowerLedger-{number}-setup-x86.exe", universal],
            _ => [universal],
        };
    }

    /// <summary>The answer's bytes, refusing more than a release could need.</summary>
    private static async Task<byte[]> ReadAsync(HttpContent content, CancellationToken cancel)
    {
        if (content.Headers.ContentLength > MaxAnswer) throw TooLarge();
        using var buffer = new MemoryStream();
        await using var stream = await content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancel).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxAnswer) throw TooLarge();
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static int Number(Group group) => int.Parse(group.ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);

    private static UpdateException NotARelease(Exception? inner) => new("GitHub's answer wasn't a release.", inner);

    private static UpdateException TooLarge() => new("GitHub's answer was too large to be a release.");

    [GeneratedRegex("^v([0-9]{1,5})\\.([0-9]{1,5})\\.([0-9]{1,5})$")]
    private static partial Regex Tag();

    [GeneratedRegex("^sha256:([0-9a-fA-F]{64})$")]
    private static partial Regex Digest();
}

/// <summary>The part of GitHub's release that the App reads.</summary>
internal sealed class GitHubRelease
{
    public string? TagName { get; init; }

    public string? HtmlUrl { get; init; }

    public bool Draft { get; init; }

    public bool Prerelease { get; init; }

    public List<GitHubAsset>? Assets { get; init; }
}

/// <summary>One file attached to a release.</summary>
internal sealed class GitHubAsset
{
    public string? Name { get; init; }

    public long Size { get; init; }

    public string? BrowserDownloadUrl { get; init; }

    public string? Digest { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class ReleaseJson : JsonSerializerContext;
