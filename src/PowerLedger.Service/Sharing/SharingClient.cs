using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using PowerLedger.Contracts;

namespace PowerLedger.Service.Sharing;

/// <summary>What the data server made of a request (data-sharing design §4). <see cref="Reason"/> is in words the App can
/// show after "Couldn't send:" or "Rejected by the server:", so it has no full stop of its own.</summary>
internal abstract record SendOutcome(string? Reason)
{
    /// <summary>200: taken.</summary>
    public sealed record Accepted() : SendOutcome((string?)null);

    /// <summary>400 or 413: the server won't ever take this day as it is.</summary>
    public sealed record Rejected(string Text) : SendOutcome(Text);

    /// <summary>410: the server has deleted this install.</summary>
    public sealed record Gone() : SendOutcome((string?)null);

    /// <summary>401, 403, 429, 5xx or anything else unexpected: try again later.</summary>
    public sealed record Refused(string Text) : SendOutcome(Text);

    /// <summary>No answer at all: no network, no server, or none in time.</summary>
    public sealed record Unreachable(string Text) : SendOutcome(Text);

    /// <summary>404: the server doesn't have this request, as an older one without <c>/v1/history</c>.</summary>
    public sealed record NotFound(string Text) : SendOutcome(Text);

    /// <summary>426 (Plan Q §3): this version is older than the server takes; <see cref="MinVersion"/> is the oldest it
    /// does, as the server said it, or null when it didn't.</summary>
    public sealed record UpdateRequired(string Text, string? MinVersion) : SendOutcome(Text);
}

/// <summary>The requests the service makes of the data server. Stopping the service cancels one with an
/// <see cref="OperationCanceledException"/>; everything else is an outcome.</summary>
internal interface ISharingClient
{
    /// <summary><c>POST /v1/report</c> with one day's report, already gzipped; the report names the install.</summary>
    Task<SendOutcome> SendReportAsync(byte[] gzipBody, string key, CancellationToken cancel = default);

    /// <summary><c>POST /v1/history</c> (Plan Q §2) with a chunk of the hourly totals already on the PC, already gzipped; the
    /// body names the install.</summary>
    Task<SendOutcome> SendHistoryAsync(byte[] gzipBody, string key, CancellationToken cancel = default);

    /// <summary><c>POST /v1/consent</c> with the switches as they are now.</summary>
    Task<SendOutcome> SendConsentAsync(string installId, string key, Consent consent, CancellationToken cancel = default);

    /// <summary><c>POST /v1/delete</c>: everything sent from this install is deleted from the server.</summary>
    Task<SendOutcome> DeleteAsync(string installId, string key, CancellationToken cancel = default);
}

/// <summary>
/// The data server over HTTPS, through one <see cref="HttpClient"/> for the service's life: user agent
/// <c>PowerLedger/X.Y.Z</c> and <see cref="VersionHeader"/> on every request, a 60-second timeout for the answer's headers and again for an error answer's body, and the
/// install key as a bearer token. The service runs as LocalSystem, so requests go direct or through the machine's own
/// proxy settings, never a user's.
/// </summary>
internal sealed class SharingClient : ISharingClient, IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The header every service request carries its version in, <c>X.Y.Z</c> (Plan Q §3), so the server can
    /// answer 426 to one older than it takes.</summary>
    public const string VersionHeader = "X-PowerLedger-Version";

    /// <summary>The most of an error answer that is read: the server's are one short sentence.</summary>
    private const int MaxErrorBytes = 4096;

    private readonly HttpClient _http;

    /// <param name="handler">Null for the real network.</param>
    public SharingClient(Uri endpoint, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) })
        {
            BaseAddress = endpoint,
            Timeout = timeout ?? DefaultTimeout,
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PowerLedger", ServiceVersion.Short));
        _http.DefaultRequestHeaders.Add(VersionHeader, ServiceVersion.Short);
    }

    /// <summary>JSON gzipped for sending, as the server stores it.</summary>
    public static byte[] Gzip(byte[] json)
    {
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(json);
        return packed.ToArray();
    }

    public Task<SendOutcome> SendReportAsync(byte[] gzipBody, string key, CancellationToken cancel = default) =>
        PostAsync("v1/report", Gzipped(gzipBody), key, cancel);

    public Task<SendOutcome> SendHistoryAsync(byte[] gzipBody, string key, CancellationToken cancel = default) =>
        PostAsync("v1/history", Gzipped(gzipBody), key, cancel);

    public Task<SendOutcome> SendConsentAsync(string installId, string key, Consent consent, CancellationToken cancel = default)
    {
        var body = new ConsentPost(installId, new ConsentDto(consent.Version, consent.Diagnostics, consent.Usage, consent.Power, consent.Share));
        return PostAsync("v1/consent", Json(SharingJson.Write(body, SharingJson.Default.ConsentPost)), key, cancel);
    }

    public Task<SendOutcome> DeleteAsync(string installId, string key, CancellationToken cancel = default) =>
        PostAsync("v1/delete", Json(SharingJson.Write(new DeletePost(installId), SharingJson.Default.DeletePost)), key, cancel);

    public void Dispose() => _http.Dispose();

    private async Task<SendOutcome> PostAsync(string path, HttpContent content, string key, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300) return new SendOutcome.Accepted();
            if (response.StatusCode == HttpStatusCode.Gone) return new SendOutcome.Gone();
            var (reason, minVersion) = await ReasonAsync(response, _http.Timeout, cancel).ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge => new SendOutcome.Rejected(reason),
                HttpStatusCode.NotFound => new SendOutcome.NotFound(reason),
                HttpStatusCode.UpgradeRequired => new SendOutcome.UpdateRequired(reason, minVersion),
                _ => new SendOutcome.Refused(reason),
            };
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return new SendOutcome.Unreachable("the server didn't answer in time");
        }
        catch (HttpRequestException error)
        {
            return new SendOutcome.Unreachable($"the server couldn't be reached ({Sentence(error.Message)})");
        }
    }

    /// <summary>The server's own sentence from its <c>{"error": …}</c> answer, or what the status says when there is none or
    /// it doesn't come within <paramref name="timeout"/>: the client's timeout stops once the headers are in. A 426's
    /// answer also says the oldest version the server takes, <c>minVersion</c>.</summary>
    private static async Task<(string Reason, string? MinVersion)> ReasonAsync(HttpResponseMessage response, TimeSpan timeout, CancellationToken cancel)
    {
        var fallback = ($"the server answered {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd(), (string?)null);
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        reading.CancelAfter(timeout);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(reading.Token).ConfigureAwait(false);
            var buffer = new byte[MaxErrorBytes];
            var read = 0;
            while (read < buffer.Length && await stream.ReadAsync(buffer.AsMemory(read), reading.Token).ConfigureAwait(false) is > 0 and var count)
            {
                read += count;
            }
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            var body = SharingJson.Read(text, SharingJson.Default.ErrorBody);
            return (body?.Error is { Length: > 0 } error ? Sentence(error) : fallback.Item1, body?.MinVersion);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return fallback;
        }
        catch (Exception error) when (error is IOException or HttpRequestException)
        {
            return fallback;
        }
    }

    private static string Sentence(string text) => text.Trim().TrimEnd('.').Trim();

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>A body already gzipped, sent as JSON with that encoding.</summary>
    private static ByteArrayContent Gzipped(byte[] gzipBody)
    {
        var content = new ByteArrayContent(gzipBody);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        return content;
    }
}

/// <summary>The body of <c>POST /v1/consent</c>.</summary>
internal sealed record ConsentPost(string InstallId, ConsentDto Consent);

/// <summary>The body of <c>POST /v1/delete</c>.</summary>
internal sealed record DeletePost(string InstallId);

/// <summary>What the server says when it refuses: <c>{"error": "…"}</c>, and with a 426 <c>"minVersion": "X.Y.Z"</c> too.</summary>
internal sealed record ErrorBody(string? Error, string? MinVersion = null);
