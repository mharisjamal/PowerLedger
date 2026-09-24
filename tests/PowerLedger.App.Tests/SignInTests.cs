using System.Buffers.Text;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.App.Tests;

public class PkceTests
{
    [Fact]
    public void The_challenge_is_the_sha256_of_the_verifier_base64url()
    {
        var verifier = Pkce.Verifier();
        var expected = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        Pkce.Challenge(verifier).ShouldBe(expected);
    }

    [Fact]
    public void A_verifier_is_long_enough_for_rfc_7636_and_url_safe()
    {
        var verifier = Pkce.Verifier();
        verifier.Length.ShouldBeInRange(43, 128);
        verifier.ShouldAllBe(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');
    }

    [Fact]
    public void Two_verifiers_are_not_the_same()
        => Pkce.Verifier().ShouldNotBe(Pkce.Verifier());

    [Fact]
    public void The_nonce_hash_is_the_sha256_of_device_id_colon_salt()
    {
        var expected = Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes("abc123:the-salt")));
        Pkce.NonceHash("abc123", "the-salt").ShouldBe(expected);
    }

    [Fact]
    public void The_nonce_hash_changes_with_either_input()
    {
        var baseline = Pkce.NonceHash("device-a", "salt-1");
        Pkce.NonceHash("device-b", "salt-1").ShouldNotBe(baseline);
        Pkce.NonceHash("device-a", "salt-2").ShouldNotBe(baseline);
    }
}

public class SignInTests
{
    private readonly FakeLoopbackServer _server = new();
    private readonly FakeHttp _http = new();
    private readonly FakeTimeProvider _clock = new();
    private Uri? _opened;

    private SignIn Model() => new(() => _server, url => _opened = url, _http.Client(), _clock);

    private static Dictionary<string, string> Query(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            result[Uri.UnescapeDataString(parts[0])] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
        }
        return result;
    }

    /// <summary>A JWT this test can decode: a real header and signature would need a real key, which the App never checks
    /// itself (the Worker verifies the signature; households design §7). Only the payload is real.</summary>
    private static string Jwt(object payload)
        => $"eyJhbGciOiJSUzI1NiJ9.{Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(payload))}.not-a-real-signature";

    [Fact]
    public void The_authorize_url_carries_pkce_the_state_and_a_nonce_bound_to_this_device()
    {
        var model = Model();
        _ = model.RunAsync(SignInProvider.Microsoft, "test-client", "device-abc", CancellationToken.None);

        var url = _opened.ShouldNotBeNull();
        url.GetLeftPart(UriPartial.Path).ShouldBe("https://login.microsoftonline.com/common/oauth2/v2.0/authorize");
        var q = Query(url);
        q["client_id"].ShouldBe("test-client");
        q["response_type"].ShouldBe("code");
        q["scope"].ShouldBe("openid email");
        q["redirect_uri"].ShouldBe($"http://localhost:{_server.Port}/");
        q["code_challenge_method"].ShouldBe("S256");
        q["state"].Length.ShouldBeGreaterThan(10);
        q["code_challenge"].Length.ShouldBeGreaterThan(10);
        q["nonce"].Length.ShouldBeGreaterThan(10);
    }

    [Fact]
    public void Microsoft_uses_its_own_authorize_endpoint()
    {
        var model = Model();
        _ = model.RunAsync(SignInProvider.Microsoft, "test-client", "device-abc", CancellationToken.None);
        _opened.ShouldNotBeNull().GetLeftPart(UriPartial.Path).ShouldBe("https://login.microsoftonline.com/common/oauth2/v2.0/authorize");
    }

    [Fact]
    public void Google_uses_its_own_authorize_endpoint()
    {
        var model = Model();
        _ = model.RunAsync(SignInProvider.Google, "test-client", "device-abc", CancellationToken.None);
        _opened.ShouldNotBeNull().GetLeftPart(UriPartial.Path).ShouldBe("https://accounts.google.com/o/oauth2/v2/auth");
    }

    [Fact]
    public async Task A_successful_sign_in_exchanges_the_code_with_pkce_and_returns_the_email()
    {
        var model = Model();
        var task = model.RunAsync(SignInProvider.Microsoft, "test-client", "device-abc", CancellationToken.None);
        var q = Query(_opened.ShouldNotBeNull());

        string? sentBody = null;
        _http.Answer = async (request, cancel) =>
        {
            sentBody = await request.Content!.ReadAsStringAsync(cancel);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new { id_token = Jwt(new { nonce = q["nonce"], email = "jo@example.com" }) })),
            };
        };
        _server.Redirect.SetResult($"?code=auth-code-1&state={q["state"]}");

        var result = await task;

        result.Ok.ShouldBeTrue();
        result.Email.ShouldBe("jo@example.com");
        result.IdToken.ShouldNotBeNull();
        result.Salt.ShouldNotBeNull();
        q["nonce"].ShouldBe(Pkce.NonceHash("device-abc", result.Salt!));

        var form = Query(new Uri("http://x/?" + sentBody));
        form["client_id"].ShouldBe("test-client");
        form["code"].ShouldBe("auth-code-1");
        form["grant_type"].ShouldBe("authorization_code");
        form["redirect_uri"].ShouldBe($"http://localhost:{_server.Port}/");
        Pkce.Challenge(form["code_verifier"]).ShouldBe(q["code_challenge"]);
    }

    [Fact]
    public async Task A_mismatched_state_is_refused_without_exchanging_the_code()
    {
        var model = Model();
        var task = model.RunAsync(SignInProvider.Microsoft, "test-client", "device-abc", CancellationToken.None);
        _ = Query(_opened.ShouldNotBeNull());

        _server.Redirect.SetResult("?code=auth-code-1&state=not-the-right-state");
        var result = await task;

        result.Ok.ShouldBeFalse();
        _http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_error_from_the_provider_is_reported_without_exchanging_the_code()
    {
        var model = Model();
        var task = model.RunAsync(SignInProvider.Google, "test-client", "device-abc", CancellationToken.None);
        var q = Query(_opened.ShouldNotBeNull());

        _server.Redirect.SetResult($"?error=access_denied&state={q["state"]}");
        var result = await task;

        result.Ok.ShouldBeFalse();
        result.Message.ShouldContain("access_denied");
        _http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_token_whose_nonce_does_not_match_is_rejected()
    {
        var model = Model();
        var task = model.RunAsync(SignInProvider.Microsoft, "test-client", "device-abc", CancellationToken.None);
        var q = Query(_opened.ShouldNotBeNull());

        _http.Reply(HttpStatusCode.OK, JsonSerializer.SerializeToUtf8Bytes(new { id_token = Jwt(new { nonce = "some-other-hash", email = "jo@example.com" }) }));
        _server.Redirect.SetResult($"?code=auth-code-1&state={q["state"]}");

        var result = await task;

        result.Ok.ShouldBeFalse();
        result.Email.ShouldBeNull();
    }

    [Fact]
    public async Task Signing_in_with_no_client_id_configured_fails_at_once_without_opening_a_browser()
    {
        var model = Model();

        var result = await model.RunAsync(SignInProvider.Microsoft, "", "device-abc", CancellationToken.None);

        result.Ok.ShouldBeFalse();
        _opened.ShouldBeNull();
    }

    /// <summary>Review finding A7: Google's installed-app clients call for a client secret in the token exchange, even
    /// under PKCE; a provider given none, Microsoft's public client, sends none.</summary>
    [Fact]
    public async Task A_client_secret_when_given_reaches_the_token_exchange()
    {
        var model = Model();
        var task = model.RunAsync(SignInProvider.Google, "test-client", "device-abc", clientSecret: "google-secret-xyz");
        var q = Query(_opened.ShouldNotBeNull());

        string? sentBody = null;
        _http.Answer = async (request, cancel) =>
        {
            sentBody = await request.Content!.ReadAsStringAsync(cancel);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new { id_token = Jwt(new { nonce = q["nonce"], email = "jo@example.com" }) })),
            };
        };
        _server.Redirect.SetResult($"?code=auth-code-1&state={q["state"]}");
        await task;

        Query(new Uri("http://x/?" + sentBody))["client_secret"].ShouldBe("google-secret-xyz");
    }

    [Fact]
    public async Task With_no_client_secret_none_is_sent()
    {
        var model = Model();
        var task = model.RunAsync(SignInProvider.Microsoft, "test-client", "device-abc");
        var q = Query(_opened.ShouldNotBeNull());

        string? sentBody = null;
        _http.Answer = async (request, cancel) =>
        {
            sentBody = await request.Content!.ReadAsStringAsync(cancel);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new { id_token = Jwt(new { nonce = q["nonce"], email = "jo@example.com" }) })),
            };
        };
        _server.Redirect.SetResult($"?code=auth-code-1&state={q["state"]}");
        await task;

        Query(new Uri("http://x/?" + sentBody)).ShouldNotContainKey("client_secret");
    }

    /// <summary>Review finding A7: the browser and the loopback wait don't hang forever.</summary>
    [Fact]
    public async Task Waiting_more_than_five_minutes_for_the_browser_times_out()
    {
        var model = Model();
        var task = model.RunAsync(SignInProvider.Microsoft, "test-client", "device-abc");
        _opened.ShouldNotBeNull();

        _clock.Advance(SignIn.Timeout);
        var result = await task;

        result.Ok.ShouldBeFalse();
        result.Message.ShouldBe("Timed out waiting for the browser.");
    }

    /// <summary>Review finding A7: TryGetProperty, not GetProperty, so a provider's answer missing this key is a clean
    /// failure rather than a thrown exception.</summary>
    [Fact]
    public async Task An_answer_with_no_id_token_is_a_clean_failure()
    {
        var model = Model();
        var task = model.RunAsync(SignInProvider.Microsoft, "test-client", "device-abc");
        var q = Query(_opened.ShouldNotBeNull());

        _http.Reply(HttpStatusCode.OK, JsonSerializer.SerializeToUtf8Bytes(new { access_token = "not-what-we-asked-for" }));
        _server.Redirect.SetResult($"?code=auth-code-1&state={q["state"]}");

        var result = await task;

        result.Ok.ShouldBeFalse();
        result.Message.ShouldBe("The sign-in server's answer had no ID token.");
    }
}
