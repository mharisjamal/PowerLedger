using System.Buffers.Text;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PowerLedger.App;

internal enum SignInProvider
{
    Microsoft,
    Google,
}

/// <summary>What a sign-in attempt came to (households design §7): the ID token and the nonce salt to send in a
/// SignInRequest, and the e-mail from the token for the App to show and keep in ui.json.</summary>
internal sealed record SignInResult(bool Ok, string Message, string? IdToken = null, string? Salt = null, string? Email = null)
{
    public static SignInResult Failed(string message) => new(false, message);
}

/// <summary>PKCE (RFC 7636) and the device-bound nonce (households design §7): base64url throughout, as WebCrypto and
/// the Worker both use.</summary>
internal static class Pkce
{
    public static string Verifier() => Random(32);

    public static string Challenge(string verifier) => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>A random value for a state or a nonce salt.</summary>
    public static string Random(int bytes = 16)
    {
        var buffer = new byte[bytes];
        RandomNumberGenerator.Fill(buffer);
        return Base64Url.EncodeToString(buffer);
    }

    /// <summary>The nonce bound to this PC: a PC in the middle that captured an authorization meant for another device
    /// cannot replay it here, since the ID token's own nonce claim must match this hash.</summary>
    public static string NonceHash(string deviceId, string salt) => Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes($"{deviceId}:{salt}")));
}

/// <summary>Starts on an OS-chosen free port on 127.0.0.1 and waits for the browser's one redirect (plan 0.9, RFC 8252
/// §7.3: a localhost prefix answers on every address, so 127.0.0.1 itself is the tighter, more precise target).</summary>
internal interface ILoopbackServer : IDisposable
{
    int Port { get; }

    /// <summary>The redirect's query string, "?code=...&amp;state=...", or "?error=...".</summary>
    Task<string> WaitForRedirectAsync(CancellationToken cancel);
}

/// <summary>The real loopback listener: an <see cref="HttpListener"/> on a port a <see cref="TcpListener"/> found free a
/// moment before, since <see cref="HttpListener"/> cannot ask Windows for one itself.</summary>
internal sealed class HttpLoopbackServer : ILoopbackServer
{
    private const string Page = "<html><body>You can close this window and return to PowerLedger.</body></html>";

    private readonly HttpListener _listener;

    public HttpLoopbackServer()
    {
        Port = FreePort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
    }

    public int Port { get; }

    public async Task<string> WaitForRedirectAsync(CancellationToken cancel)
    {
        using var registration = cancel.Register(_listener.Stop);
        HttpListenerContext context;
        try
        {
            context = await _listener.GetContextAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpListenerException or ObjectDisposedException)
        {
            cancel.ThrowIfCancellationRequested();
            throw;
        }
        var query = context.Request.Url?.Query ?? "";
        var body = Encoding.UTF8.GetBytes(Page);
        context.Response.ContentType = "text/html";
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body, CancellationToken.None).ConfigureAwait(false);
        context.Response.Close();
        return query;
    }

    public void Dispose() => _listener.Close();

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

/// <summary>
/// N2's browser sign-in (households design §7, Plan N task A6): an OIDC authorization-code flow with PKCE through the
/// system browser and a loopback redirect. The nonce sent to the provider is bound to this PC: a random salt is made
/// here, the nonce is the hash of this PC's device ID and the salt, and the salt — not the hash — is what a
/// SignInRequest carries, so the service and the Worker can redo the hash and refuse a token meant for another device.
/// The App checks the returned token's own nonce claim against that same hash before trusting it at all.
/// </summary>
internal sealed class SignIn(Func<ILoopbackServer> newServer, Action<Uri> openBrowser, HttpClient http, TimeProvider clock)
{
    /// <summary>Review finding A7: the browser and the loopback wait don't hang forever should the user walk away or the
    /// provider's page never come back.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    private static readonly Dictionary<SignInProvider, (Uri Authorize, Uri Token)> Endpoints = new()
    {
        [SignInProvider.Microsoft] = (
            new Uri("https://login.microsoftonline.com/common/oauth2/v2.0/authorize"),
            new Uri("https://login.microsoftonline.com/common/oauth2/v2.0/token")),
        [SignInProvider.Google] = (
            new Uri("https://accounts.google.com/o/oauth2/v2/auth"),
            new Uri("https://oauth2.googleapis.com/token")),
    };

    /// <summary>Runs the whole flow. <paramref name="clientId"/> is <see cref="SignInClients"/>'s for the provider; empty
    /// before Plan N task L6 fills it in, which fails at once without opening anything. <paramref name="clientSecret"/>
    /// is Google's only (review finding A7: <see cref="SignInClients.GoogleSecret"/>) — Google's installed-app clients
    /// call for one in the token exchange even though the flow is PKCE; Microsoft's public client needs none.</summary>
    public async Task<SignInResult> RunAsync(
        SignInProvider provider, string clientId, string deviceId, CancellationToken cancel = default, string? clientSecret = null)
    {
        if (string.IsNullOrEmpty(clientId)) return SignInResult.Failed("Sign-in isn't set up yet.");

        var (authorize, token) = Endpoints[provider];
        var verifier = Pkce.Verifier();
        var challenge = Pkce.Challenge(verifier);
        var state = Pkce.Random();
        var salt = Pkce.Random();
        var nonce = Pkce.NonceHash(deviceId, salt);

        using var server = newServer();
        var redirectUri = $"http://127.0.0.1:{server.Port}/";
        var url = new UriBuilder(authorize)
        {
            Query = Encode(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["response_type"] = "code",
                ["redirect_uri"] = redirectUri,
                ["scope"] = "openid email",
                ["state"] = state,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["nonce"] = nonce,
            }),
        }.Uri;
        openBrowser(url);

        string query;
        try
        {
            using var timeout = new CancellationTokenSource(Timeout, clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, timeout.Token);
            query = await server.WaitForRedirectAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return SignInResult.Failed("Timed out waiting for the browser.");
        }

        var answer = ParseQuery(query);
        if (answer.TryGetValue("error", out var refusal)) return SignInResult.Failed($"The provider refused: {refusal}.");
        if (!answer.TryGetValue("state", out var gotState) || gotState != state) return SignInResult.Failed("Couldn't verify this sign-in.");
        if (!answer.TryGetValue("code", out var code)) return SignInResult.Failed("The browser didn't return a code.");

        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
            ["grant_type"] = "authorization_code",
        };
        if (!string.IsNullOrEmpty(clientSecret)) form["client_secret"] = clientSecret;
        using var request = new HttpRequestMessage(HttpMethod.Post, token) { Content = new FormUrlEncodedContent(form) };
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancel).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            return SignInResult.Failed("Couldn't reach the sign-in server: " + error.Message);
        }
        if (!response.IsSuccessStatusCode) return SignInResult.Failed("The sign-in server refused the code.");

        string idToken;
        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false));
            // Review finding A7: TryGetProperty, not GetProperty, since a provider's answer missing this key must be a
            // clean failure here, never a thrown KeyNotFoundException.
            if (!body.RootElement.TryGetProperty("id_token", out var idTokenElement)) return SignInResult.Failed("The sign-in server's answer had no ID token.");
            idToken = idTokenElement.GetString() ?? "";
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return SignInResult.Failed("The sign-in server's answer couldn't be read.");
        }

        if (DecodeJwtPayload(idToken) is not { } payload
            || !payload.TryGetProperty("nonce", out var nonceClaim) || nonceClaim.GetString() != nonce)
            return SignInResult.Failed("Couldn't verify this sign-in.");
        var email = payload.TryGetProperty("email", out var emailClaim) && emailClaim.ValueKind == JsonValueKind.String ? emailClaim.GetString() : null;

        return new SignInResult(true, "Signed in.", idToken, salt, email);
    }

    private static string Encode(Dictionary<string, string> parameters)
        => string.Join('&', parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            result[Uri.UnescapeDataString(parts[0])] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
        }
        return result;
    }

    /// <summary>The payload of a JWT (header.payload.signature): the App reads its claims but never checks its
    /// signature, which is the Worker's job (households design §7).</summary>
    private static JsonElement? DecodeJwtPayload(string idToken)
    {
        var parts = idToken.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            return JsonDocument.Parse(DecodeBase64Url(parts[1])).RootElement;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            return null;
        }
    }

    private static byte[] DecodeBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(padded);
    }
}
