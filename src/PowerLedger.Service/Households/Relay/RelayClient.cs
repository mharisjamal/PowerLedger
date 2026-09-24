using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using PowerLedger.Core.Households;

namespace PowerLedger.Service.Households.Relay;

/// <summary>What the Worker answered: its status, 0 when it couldn't be reached, and the value or its error sentence.</summary>
internal sealed record RelayResult<T>(int Status, T? Value, string? Error)
{
    public bool Ok => Status is >= 200 and < 300;

    /// <summary>Worth trying again later: no answer, too many requests, a signature the server refused, which a wrong clock
    /// gives, or the server's own fault.</summary>
    public bool Transient => Status is 0 or 401 or 408 or 429 or >= 500;

    /// <summary>The problem in words the App can show after "Couldn't sync:", without a full stop.</summary>
    public string Problem => Status switch
    {
        0 => Error ?? "the server couldn't be reached",
        401 => $"the server didn't accept this PC's signature ({Error ?? "401"}); check the PC's clock",
        _ => Error ?? $"the server answered {Status}",
    };
}

/// <summary>A result without a value.</summary>
internal sealed record Done;

/// <summary>
/// The Worker's household routes over HTTPS (households design §5 to §8, plan 0.6): one <see cref="HttpClient"/> for the
/// service's life, at <see cref="Sharing.SharingEndpoint"/>'s address. Requests about a household are signed by this PC's
/// device key: <c>X-PL-Device</c>, <c>X-PL-Time</c> and <c>X-PL-Signature</c> over the method, the path with its query, the
/// time and the body's hash. Meeting slots aren't signed; what is in them vouches for itself.
/// </summary>
internal sealed class RelayClient : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private const int MaxReplyBytes = 8 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly TimeProvider _clock;

    /// <param name="handler">Null for the real network.</param>
    public RelayClient(Uri endpoint, TimeProvider clock, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _clock = clock;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) }, disposeHandler: handler is null)
        {
            BaseAddress = endpoint,
            Timeout = timeout ?? DefaultTimeout,
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PowerLedger", ServiceVersion.Short));
    }

    public void Dispose() => _http.Dispose();

    /// <summary><c>POST /v1/households</c>: makes the household with this PC its first member.</summary>
    public Task<RelayResult<Done>> CreateHouseholdAsync(DeviceKeys keys, string householdId, CancellationToken cancel) =>
        SendAsync<Done>(HttpMethod.Post, "v1/households", Json(new CreateHouseholdBody(householdId, Wire.Encode(keys.SignPublic), Wire.Encode(keys.DhPublic)),
            HouseholdJson.Default.CreateHouseholdBody), keys, null, cancel);

    /// <summary><c>POST /v1/households/{hid}/members</c>: a member adds another PC by its keys.</summary>
    public Task<RelayResult<Done>> AddMemberAsync(DeviceKeys keys, string householdId, byte[] sign, byte[] dh, CancellationToken cancel) =>
        SendAsync<Done>(HttpMethod.Post, $"v1/households/{householdId}/members",
            Json(new MemberKeysBody(Wire.Encode(sign), Wire.Encode(dh)), HouseholdJson.Default.MemberKeysBody), keys, null, cancel);

    /// <summary><c>DELETE /v1/households/{hid}/members/{device}</c>: a member removes another, or itself.</summary>
    public Task<RelayResult<Done>> RemoveMemberAsync(DeviceKeys keys, string householdId, string deviceId, CancellationToken cancel) =>
        SendAsync<Done>(HttpMethod.Delete, $"v1/households/{householdId}/members/{deviceId}", null, keys, null, cancel);

    /// <summary><c>POST /v1/households/{hid}/keys</c>: a new epoch's key, sealed to each member it is for.</summary>
    public Task<RelayResult<Done>> PostKeysAsync(DeviceKeys keys, string householdId, int epoch, IReadOnlyList<EnvelopeBody> envelopes, CancellationToken cancel) =>
        SendAsync<Done>(HttpMethod.Post, $"v1/households/{householdId}/keys",
            Json(new PostKeysBody(epoch, [.. envelopes]), HouseholdJson.Default.PostKeysBody), keys, null, cancel);

    /// <summary><c>GET /v1/households/{hid}/keys/{epoch}</c>: this PC's own envelope for that epoch, and who sealed it.</summary>
    public Task<RelayResult<KeyEnvelopeReply>> GetKeyAsync(DeviceKeys keys, string householdId, int epoch, CancellationToken cancel) =>
        SendAsync(HttpMethod.Get, $"v1/households/{householdId}/keys/{epoch}", null, keys, HouseholdJson.Default.KeyEnvelopeReply, cancel);

    /// <summary><c>GET /v1/households/{hid}/members</c>: every PC that is or was a member.</summary>
    public Task<RelayResult<List<ServerMember>>> MembersAsync(DeviceKeys keys, string householdId, CancellationToken cancel) =>
        SendAsync(HttpMethod.Get, $"v1/households/{householdId}/members", null, keys, HouseholdJson.Default.ListServerMember, cancel);

    /// <summary><c>POST /v1/households/{hid}/batches</c>: one sealed batch of this PC's rows.</summary>
    public Task<RelayResult<Done>> PostBatchAsync(DeviceKeys keys, string householdId, BatchPost batch, CancellationToken cancel) =>
        SendAsync<Done>(HttpMethod.Post, $"v1/households/{householdId}/batches", Json(batch, HouseholdJson.Default.BatchPost), keys, null, cancel);

    /// <summary><c>GET /v1/households/{hid}/batches?after=…&amp;limit=…</c>: the other members' batches after the cursor.</summary>
    public Task<RelayResult<BatchPage>> BatchesAsync(DeviceKeys keys, string householdId, long after, int limit, CancellationToken cancel) =>
        SendAsync(HttpMethod.Get, $"v1/households/{householdId}/batches?after={after}&limit={limit}", null, keys, HouseholdJson.Default.BatchPage, cancel);

    /// <summary><c>PUT /v1/meetings/{mid}/{slot}</c>: written once; 409 once written, 410 once the meeting has ended.</summary>
    public Task<RelayResult<Done>> PutSlotAsync(string meetingId, string slot, byte[] body, CancellationToken cancel) =>
        SendAsync<Done>(HttpMethod.Put, $"v1/meetings/{meetingId}/{slot}", body, null, null, cancel, "application/octet-stream");

    /// <summary><c>GET /v1/meetings/{mid}/{slot}</c>: the bytes put there; 404 while it is empty or once the meeting has ended.</summary>
    public Task<RelayResult<byte[]>> GetSlotAsync(string meetingId, string slot, CancellationToken cancel) =>
        SendRawAsync(HttpMethod.Get, $"v1/meetings/{meetingId}/{slot}", null, null, null, cancel);

    /// <summary>N2's <c>POST /v1/auth/signin</c>: the ID token and the nonce's salt, with this PC's keys, signed by them.</summary>
    public Task<RelayResult<SignInReply>> SignInAsync(DeviceKeys keys, string provider, string idToken, string salt, CancellationToken cancel) =>
        SendAsync(HttpMethod.Post, "v1/auth/signin",
            Json(new SignInBody(provider, idToken, salt, Wire.Encode(keys.SignPublic), Wire.Encode(keys.DhPublic)), HouseholdJson.Default.SignInBody),
            keys, HouseholdJson.Default.SignInReply, cancel);

    /// <summary>N2's <c>POST /v1/account/household</c>: links the account to this PC's household.</summary>
    public Task<RelayResult<Done>> LinkAsync(DeviceKeys keys, string session, string householdId, CancellationToken cancel) =>
        SendAsync<Done>(HttpMethod.Post, "v1/account/household", Json(new LinkBody(householdId), HouseholdJson.Default.LinkBody), keys, null, cancel,
            session: session);

    /// <summary>N2's <c>POST /v1/account/requests</c>: asks to join the account's household.</summary>
    public Task<RelayResult<Done>> AskToJoinAsync(DeviceKeys keys, string session, CancellationToken cancel) =>
        SendAsync<Done>(HttpMethod.Post, "v1/account/requests", [], keys, null, cancel, session: session);

    /// <summary>N2's <c>PUT /v1/account/recovery</c>: the household key sealed under the recovery code, and its verifier.</summary>
    public Task<RelayResult<Done>> PutRecoveryAsync(DeviceKeys keys, string session, RecoveryBody recovery, CancellationToken cancel) =>
        SendAsync<Done>(HttpMethod.Put, "v1/account/recovery", Json(recovery, HouseholdJson.Default.RecoveryBody), keys, null, cancel, session: session);

    /// <summary>N2's <c>GET /v1/account/recovery</c>.</summary>
    public Task<RelayResult<RecoveryReply>> GetRecoveryAsync(DeviceKeys keys, string session, CancellationToken cancel) =>
        SendAsync(HttpMethod.Get, "v1/account/recovery", null, keys, HouseholdJson.Default.RecoveryReply, cancel, session: session);

    /// <summary>N2's <c>POST /v1/account/recover</c>: this PC joins the account's household on a proof it opened the envelope.</summary>
    public Task<RelayResult<Done>> RecoverAsync(DeviceKeys keys, string session, byte[] proof, CancellationToken cancel) =>
        SendAsync<Done>(HttpMethod.Post, "v1/account/recover", Json(new RecoverBody(Wire.Encode(proof)), HouseholdJson.Default.RecoverBody), keys,
            null, cancel, session: session);

    /// <summary>N2's <c>GET /v1/households/{hid}/requests</c>: the PCs signed in as the account waiting to join.</summary>
    public Task<RelayResult<List<JoinRequestItem>>> RequestsAsync(DeviceKeys keys, string householdId, CancellationToken cancel) =>
        SendAsync(HttpMethod.Get, $"v1/households/{householdId}/requests", null, keys, HouseholdJson.Default.ListJoinRequestItem, cancel);

    /// <summary>N2's <c>POST /v1/households/{hid}/requests/{device}/approve</c>: the key sealed for the waiting PC.</summary>
    public Task<RelayResult<Done>> ApproveAsync(DeviceKeys keys, string householdId, string deviceId, int epoch, string envelope, CancellationToken cancel) =>
        SendAsync<Done>(HttpMethod.Post, $"v1/households/{householdId}/requests/{deviceId}/approve",
            Json(new ApproveBody(epoch, envelope), HouseholdJson.Default.ApproveBody), keys, null, cancel);

    /// <summary>N2's <c>POST /v1/auth/signout</c>.</summary>
    public Task<RelayResult<Done>> SignOutAsync(DeviceKeys keys, string session, CancellationToken cancel) =>
        SendAsync<Done>(HttpMethod.Post, "v1/auth/signout", [], keys, null, cancel, session: session);

    /// <summary>N2's <c>DELETE /v1/account</c>.</summary>
    public Task<RelayResult<Done>> DeleteAccountAsync(DeviceKeys keys, string session, CancellationToken cancel) =>
        SendAsync<Done>(HttpMethod.Delete, "v1/account", null, keys, null, cancel, session: session);

    /// <summary>A request and its JSON answer. With <paramref name="signer"/>, signed as plan 0.6 says; with
    /// <paramref name="session"/>, N2's session goes with it.</summary>
    public async Task<RelayResult<T>> SendAsync<T>(
        HttpMethod method, string path, byte[]? body, DeviceKeys? signer, JsonTypeInfo<T>? reply, CancellationToken cancel,
        string contentType = "application/json", string? session = null)
    {
        var raw = await SendRawAsync(method, path, body, signer, session, cancel, contentType).ConfigureAwait(false);
        if (!raw.Ok) return new RelayResult<T>(raw.Status, default, raw.Error);
        if (reply is null) return new RelayResult<T>(raw.Status, default, null);
        try
        {
            return JsonSerializer.Deserialize(raw.Value, reply) is { } value
                ? new RelayResult<T>(raw.Status, value, null)
                : new RelayResult<T>(502, default, "the server's answer was empty");
        }
        catch (JsonException)
        {
            return new RelayResult<T>(502, default, "the server's answer wasn't what was expected");
        }
    }

    public async Task<RelayResult<byte[]>> SendRawAsync(
        HttpMethod method, string path, byte[]? body, DeviceKeys? signer, string? session, CancellationToken cancel,
        string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }
        if (signer is not null) Sign(request, signer, body ?? []);
        if (session is not null) request.Headers.TryAddWithoutValidation("Authorization", "Session " + session);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            var bytes = await ReadAsync(response, cancel).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300) return new RelayResult<byte[]>(status, bytes, null);
            var error = HouseholdJson.Read(bytes, HouseholdJson.Default.ServerError)?.Error is { Length: > 0 } sentence
                ? sentence.Trim().TrimEnd('.')
                : null;
            return new RelayResult<byte[]>(status, null, error);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return new RelayResult<byte[]>(0, null, "the server didn't answer in time");
        }
        catch (HttpRequestException error)
        {
            return new RelayResult<byte[]>(0, null, $"the server couldn't be reached ({error.Message.Trim().TrimEnd('.')})");
        }
        catch (IOException error)
        {
            return new RelayResult<byte[]>(0, null, $"the connection to the server broke ({error.Message.Trim().TrimEnd('.')})");
        }
    }

    /// <summary>The three headers of a signed request (plan 0.6), over the path and query exactly as sent.</summary>
    private void Sign(HttpRequestMessage request, DeviceKeys signer, byte[] body)
    {
        var uri = new Uri(_http.BaseAddress!, request.RequestUri!);
        request.RequestUri = uri;
        var time = _clock.GetUtcNow().ToUnixTimeSeconds();
        var signature = HouseholdCrypto.SignData(signer.Sign, HouseholdCrypto.RequestToSign(request.Method.Method, uri.PathAndQuery, time, body));
        request.Headers.Add("X-PL-Device", signer.DeviceId);
        request.Headers.Add("X-PL-Time", time.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-PL-Signature", Wire.Encode(signature));
    }

    private static async Task<byte[]> ReadAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
        {
            if (copy.Length + read > MaxReplyBytes) throw new IOException("The server's answer was too large.");
            copy.Write(buffer, 0, read);
        }
        return copy.ToArray();
    }

    private static byte[] Json<T>(T value, JsonTypeInfo<T> type) => JsonSerializer.SerializeToUtf8Bytes(value, type);
}

/// <summary>The body of <c>POST /v1/households</c>.</summary>
internal sealed record CreateHouseholdBody(string Id, string Sign, string Dh);

/// <summary>A PC's keys, as <c>POST …/members</c> takes them.</summary>
internal sealed record MemberKeysBody(string Sign, string Dh);

/// <summary>One member's sealed key.</summary>
internal sealed record EnvelopeBody(string Device, string Body);

/// <summary>The body of <c>POST …/keys</c>.</summary>
internal sealed record PostKeysBody(int Epoch, List<EnvelopeBody> Envelopes);

/// <summary>What <c>GET …/keys/{epoch}</c> answers: this PC's envelope and the member that sealed it.</summary>
internal sealed record KeyEnvelopeReply(int Epoch, string From, string Body);

/// <summary>A member as the server lists it, times in unix milliseconds.</summary>
internal sealed record ServerMember(string Device, string Sign, string Dh, long Added, long? Removed);

/// <summary>A batch as posted (plan 0.6): <see cref="Seq"/> is this PC's own sequence number, in the sealed body's associated data.</summary>
internal sealed record BatchPost(string Device, int Epoch, long Seq, string Body);

/// <summary>One of the other members' batches, as posted.</summary>
internal sealed record BatchItem(long Seq, string Device, int Epoch, string Body);

/// <summary>A page of batches: <see cref="Next"/> is the cursor to send next time, <see cref="More"/> that another page waits.</summary>
internal sealed record BatchPage(List<BatchItem> Items, long Next, bool More);

/// <summary>A batch's sealed JSON (plan 0.6): the PC it is from, its name and kind with it, and its rows.</summary>
internal sealed record BatchPlain(int V, WireMember Device, List<WireRow> Rows);

/// <summary>N2: the body of <c>POST /v1/auth/signin</c>; <see cref="Nonce"/> is the salt the App made the ID token's nonce from.</summary>
internal sealed record SignInBody(string Provider, string IdToken, string Nonce, string Sign, string Dh);

/// <summary>N2: what sign-in answers: this PC's session, the household the account is linked to, and whether it has a
/// recovery envelope.</summary>
internal sealed record SignInReply(string Session, string? HouseholdId, bool HasRecovery);

internal sealed record LinkBody(string HouseholdId);

/// <summary>N2: the recovery envelope as put: the household key sealed under the recovery code's key, the verifier the proof
/// of recovering is checked with, and the key's epoch.</summary>
internal sealed record RecoveryBody(string Body, string Verifier, int Epoch);

internal sealed record RecoveryReply(string? HouseholdId, int? Epoch, string Body);

internal sealed record RecoverBody(string Proof);

/// <summary>N2: a PC signed in as the account waiting to join.</summary>
internal sealed record JoinRequestItem(string Device, string Sign, string Dh, long Created);

internal sealed record ApproveBody(int Epoch, string Body);

/// <summary>What the server says when it refuses: <c>{"error": "…"}</c>.</summary>
internal sealed record ServerError(string? Error);
