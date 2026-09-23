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
}

/// <summary>The three requests the service makes of the data server. Stopping the service cancels one with an
/// <see cref="OperationCanceledException"/>; everything else is an outcome.</summary>
internal interface ISharingClient
{
    /// <summary><c>POST /v1/report</c> with one day's report, already gzipped; the report names the install.</summary>
    Task<SendOutcome> SendReportAsync(byte[] gzipBody, string key, CancellationToken cancel = default);

    /// <summary><c>POST /v1/consent</c> with the switches as they are now.</summary>
    Task<SendOutcome> SendConsentAsync(string installId, string key, Consent consent, CancellationToken cancel = default);

    /// <summary><c>POST /v1/delete</c>: everything sent from this install is deleted from the server.</summary>
    Task<SendOutcome> DeleteAsync(string installId, string key, CancellationToken cancel = default);
}

/// <summary>
/// The data server over HTTPS, through one <see cref="HttpClient"/> for the service's life: user agent
/// <c>PowerLedger/X.Y.Z</c>, a 60-second timeout, and the install key as a bearer token. The service runs as LocalSystem,
/// so requests go direct or through the machine's own proxy settings, never a user's.
/// </summary>
internal sealed class SharingClient : ISharingClient, IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

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
    }

    /// <summary>JSON gzipped for sending, as the server stores it.</summary>
    public static byte[] Gzip(byte[] json)
    {
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(json);
        return packed.ToArray();
    }

    public Task<SendOutcome> SendReportAsync(byte[] gzipBody, string key, CancellationToken cancel = default)
    {
        var content = new ByteArrayContent(gzipBody);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        return PostAsync("v1/report", content, key, cancel);
    }

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
            var reason = await ReasonAsync(response, cancel).ConfigureAwait(false);
            return response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge
                ? new SendOutcome.Rejected(reason)
                : new SendOutcome.Refused(reason);
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

    /// <summary>The server's own sentence from its <c>{"error": …}</c> answer, or what the status says when there is none.</summary>
    private static async Task<string> ReasonAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        var fallback = $"the server answered {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            var buffer = new byte[MaxErrorBytes];
            var read = 0;
            while (read < buffer.Length && await stream.ReadAsync(buffer.AsMemory(read), cancel).ConfigureAwait(false) is > 0 and var count) read += count;
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            return SharingJson.Read(text, SharingJson.Default.ErrorBody)?.Error is { Length: > 0 } error ? Sentence(error) : fallback;
        }
        catch (Exception error) when (error is IOException or HttpRequestException)
        {
            return fallback;
        }
    }

    private static string Sentence(string text) => text.Trim().TrimEnd('.').Trim();

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");
}

/// <summary>The body of <c>POST /v1/consent</c>.</summary>
internal sealed record ConsentPost(string InstallId, ConsentDto Consent);

/// <summary>The body of <c>POST /v1/delete</c>.</summary>
internal sealed record DeletePost(string InstallId);

/// <summary>What the server says when it refuses: <c>{"error": "…"}</c>.</summary>
internal sealed record ErrorBody(string? Error);
