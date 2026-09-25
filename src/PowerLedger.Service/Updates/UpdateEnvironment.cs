using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Win32;
using PowerLedger.Service.Sharing;
using PowerLedger.Updates;

namespace PowerLedger.Service.Updates;

/// <summary>Where installers come from: <see cref="UpdateDownloader"/> into the Updates folder.</summary>
internal interface IInstallerSource
{
    /// <summary>The installer's path once it is whole and its SHA-256 is GitHub's. Throws <see cref="UpdateException"/>.</summary>
    Task<string> DownloadAsync(Release release, CancellationToken cancel);

    /// <summary>A small release file's bytes, checked against GitHub's size and SHA-256. Throws <see cref="UpdateException"/>.</summary>
    Task<byte[]> FetchAsync(ReleaseFile file, CancellationToken cancel);

    /// <summary>Removes unfinished downloads and installers for <paramref name="running"/> or older.</summary>
    void Clean(Version running);
}

/// <summary>The data server's minimum version (Plan Q §3).</summary>
internal interface IAppPolicySource
{
    /// <summary>What <c>GET /v1/app-policy</c> says the minimum is; null when it can't be had.</summary>
    Task<string?> MinVersionAsync(CancellationToken cancel);
}

/// <summary>Everything the update worker reaches outside itself, so its rules are tested without Windows or the network.</summary>
/// <param name="Feed">The newest release.</param>
/// <param name="Installers">Downloads.</param>
/// <param name="Policy">The server's minimum version.</param>
/// <param name="Cost">Whether the connection is metered.</param>
/// <param name="Folder">The Updates folder, checked before every use.</param>
/// <param name="System">Sessions, the App and setup.</param>
/// <param name="PublicKey">The release key, base64 SubjectPublicKeyInfo; empty means the service never installs.</param>
/// <param name="Running">The service's own version: nothing but a newer one is installed.</param>
/// <param name="AppPath">The App's executable, relaunched after an update.</param>
internal sealed record UpdateEnvironment(
    IReleaseFeed Feed, IInstallerSource Installers, IAppPolicySource Policy, IConnectionCost Cost, UpdateFolder Folder, IUpdateSystem System,
    string PublicKey, Version Running, string AppPath)
{
    /// <summary>The installed service's: GitHub (or an administrator's test feed on this machine), the data server,
    /// %ProgramData%\PowerLedger\Updates, and the App beside the service's own folder.</summary>
    public static UpdateEnvironment ForService(ServicePaths paths, Uri dataServer, HttpClient http, Microsoft.Extensions.Logging.ILogger log)
    {
        var folder = UpdateFolder.ForService(paths.Updates);
        return new UpdateEnvironment(
            GitHubReleaseFeed.For(http, UpdateFeedSetting.Resolve()),
            new DownloadedInstallers(new UpdateDownloader(http, folder.Path)),
            new AppPolicyClient(http, dataServer, ServiceVersion.Short),
            new ConnectionCost(),
            folder,
            new WindowsUpdateSystem(log),
            ReleaseKey.PublicKey,
            Version.Parse(ServiceVersion.Short),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "PowerLedger.exe")));
    }
}

/// <summary><see cref="UpdateDownloader"/> as the update worker uses it.</summary>
internal sealed class DownloadedInstallers(UpdateDownloader downloader) : IInstallerSource
{
    public Task<string> DownloadAsync(Release release, CancellationToken cancel) => downloader.DownloadAsync(release, null, cancel);

    public Task<byte[]> FetchAsync(ReleaseFile file, CancellationToken cancel) => downloader.FetchAsync(file, cancel);

    public void Clean(Version running) => downloader.Clean(running);
}

/// <summary><c>GET /v1/app-policy</c> on the data server (Plan Q §3), saying which version asks.</summary>
internal sealed class AppPolicyClient(HttpClient http, Uri server, string version) : IAppPolicySource
{
    /// <summary>The header every service request carries.</summary>
    public const string VersionHeader = "X-PowerLedger-Version";

    private const int MaxAnswer = 4096;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<string?> MinVersionAsync(CancellationToken cancel)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server, "v1/app-policy"));
            request.Headers.Add(VersionHeader, version);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxAnswer) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(limit.Token).ConfigureAwait(false);
            return bytes.Length <= MaxAnswer ? Parse(bytes) : null;
        }
        catch (Exception error) when (error is HttpRequestException or IOException || (error is OperationCanceledException && !cancel.IsCancellationRequested))
        {
            return null;   // unreachable or slow: the last minimum heard stands, and the next check asks again
        }
    }

    /// <summary>The <c>minVersion</c> of <c>{ "minVersion": "X.Y.Z" }</c>; null for anything else.</summary>
    internal static string? Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            using var document = JsonDocument.ParseValue(ref reader);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("minVersion", out var min) && min.ValueKind == JsonValueKind.String
                ? min.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Where the service looks for releases: GitHub, or a stand-in on this machine for testing a service update (the Sandbox
/// end-to-end run), named by the REG_SZ value <c>UpdateFeed</c> under the service's <c>Parameters</c> key. Only
/// administrators can write there, and anything but a loopback address is ignored, as for <see cref="SharingEndpoint"/>.
/// </summary>
internal static class UpdateFeedSetting
{
    internal const string ValueName = "UpdateFeed";

    /// <param name="readOverride">Reads the registry value; null for the real registry.</param>
    /// <returns>The stand-in, or null for GitHub.</returns>
    public static Uri? Resolve(Func<string?>? readOverride = null)
    {
        try
        {
            return SharingEndpoint.OnThisMachine((readOverride ?? ReadRegistry)());
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    private static string? ReadRegistry()
    {
        using var key = Registry.LocalMachine.OpenSubKey(SharingEndpoint.ParametersKey);
        return key?.GetValue(ValueName) as string;
    }
}
