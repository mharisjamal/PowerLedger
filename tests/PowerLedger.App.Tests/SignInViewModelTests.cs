using System.Net.Http;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

public class SignInViewModelTests
{
    private readonly FakeLink _link = new();
    private readonly FakeUiSettings _ui = new();
    private readonly FakeLoopbackServer _server = new();
    private readonly FakeHttp _http = new();
    private Uri? _opened;

    private SignInViewModel Model(string microsoftClientId = "ms-client", string googleClientId = "google-client", string googleClientSecret = "")
    {
        var signIn = new SignIn(() => _server, url => _opened = url, _http.Client(), TimeProvider.System);
        return new SignInViewModel(_link, _ui, signIn, UiThreads.Inline, microsoftClientId, googleClientId, googleClientSecret);
    }

    private static string Jwt(string nonce, string email)
        => $"eyJhbGciOiJSUzI1NiJ9.{System.Buffers.Text.Base64Url.EncodeToString(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { nonce, email }))}.sig";

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

    [Fact]
    public void With_no_client_id_the_button_says_sign_in_isnt_set_up_yet()
    {
        var model = Model(microsoftClientId: "", googleClientId: "");

        model.MicrosoftAvailable.ShouldBeFalse();
        model.MicrosoftButtonText.ShouldBe("Sign-in isn't set up yet");
        model.SignInWithMicrosoft.CanExecute(null).ShouldBeFalse();
        model.GoogleButtonText.ShouldBe("Sign-in isn't set up yet");
    }

    [Fact]
    public void With_a_client_id_the_button_invites_sign_in()
    {
        var model = Model();
        model.MicrosoftAvailable.ShouldBeTrue();
        model.MicrosoftButtonText.ShouldBe("Sign in with Microsoft");
        model.GoogleButtonText.ShouldBe("Sign in with Google");
    }

    [Fact]
    public async Task Signing_in_uses_this_pcs_device_id_for_the_nonce_and_remembers_the_email()
    {
        _link.Status = Statuses.Running() with { Household = new HouseholdStatus(null, "device-xyz", "This-PC", ChassisKind.Desktop, true, [], null) };
        _link.Connect(true);
        var model = Model();
        model.Apply(_link.Status.Household);

        model.SignInWithMicrosoft.Execute(null);
        var q = Query(_opened.ShouldNotBeNull());
        _http.Reply(System.Net.HttpStatusCode.OK, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { id_token = Jwt(q["nonce"], "jo@example.com") }));
        _server.Redirect.SetResult($"?code=abc&state={q["state"]}");

        await WaitFor.True(() => _ui.Current.SignedInEmail is not null);
        _ui.Current.SignedInEmail.ShouldBe("jo@example.com");
        model.Email.ShouldBe("jo@example.com");
        _link.HouseholdRequests.Count.ShouldBe(1);
    }

    /// <summary>Plan 0.9: a typed recovery code warns before the browser opens at all; Continue is what actually runs
    /// the sign-in with it.</summary>
    [Fact]
    public async Task A_typed_recovery_code_is_sent_with_the_sign_in_and_cleared_once_taken()
    {
        _link.Status = Statuses.Running() with { Household = new HouseholdStatus(null, "device-xyz", "This-PC", ChassisKind.Desktop, true, [], null) };
        _link.Connect(true);
        var model = Model();
        model.Apply(_link.Status.Household);
        model.RecoveryCodeInput = "K7QM-2XHD-9PW4-R8TA-VMNP-3QWE";

        model.SignInWithMicrosoft.Execute(null);
        model.ConfirmingRecoverySignIn.ShouldBeTrue();
        _opened.ShouldBeNull();   // the browser doesn't open until Continue

        model.ContinueRecoverySignIn.Execute(null);
        model.ConfirmingRecoverySignIn.ShouldBeFalse();
        var q = Query(_opened.ShouldNotBeNull());
        _http.Reply(System.Net.HttpStatusCode.OK, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { id_token = Jwt(q["nonce"], "jo@example.com") }));
        _server.Redirect.SetResult($"?code=abc&state={q["state"]}");

        await WaitFor.True(() => _link.HouseholdRequests.Count > 0);
        var (kind, provider, _, _, recoveryCode) = ((string, string, string, string, string?))_link.HouseholdRequests.Single();
        kind.ShouldBe("signIn");
        provider.ShouldBe("microsoft");
        recoveryCode.ShouldBe("K7QM-2XHD-9PW4-R8TA-VMNP-3QWE");
        await WaitFor.True(() => model.RecoveryCodeInput == "");
    }

    [Fact]
    public void Cancelling_a_recovery_sign_in_warning_opens_no_browser()
    {
        _link.Connect(true);
        var model = Model();
        model.RecoveryCodeInput = "K7QM-2XHD-9PW4-R8TA-VMNP-3QWE";

        model.SignInWithMicrosoft.Execute(null);
        model.CancelRecoverySignIn.Execute(null);

        model.ConfirmingRecoverySignIn.ShouldBeFalse();
        _opened.ShouldBeNull();
        _link.HouseholdRequests.ShouldBeEmpty();
    }

    [Fact]
    public void With_no_recovery_code_signing_in_skips_the_warning()
    {
        _link.Connect(true);
        var model = Model();

        model.SignInWithMicrosoft.Execute(null);

        model.ConfirmingRecoverySignIn.ShouldBeFalse();
        _opened.ShouldNotBeNull();
    }

    /// <summary>Review finding A7: Google's client secret, configured on the view model, reaches the token exchange;
    /// Microsoft's flow needs none.</summary>
    [Fact]
    public async Task Signing_in_with_google_sends_the_configured_client_secret()
    {
        _link.Connect(true);
        var model = Model(googleClientSecret: "google-secret-xyz");

        model.SignInWithGoogle.Execute(null);
        var q = Query(_opened.ShouldNotBeNull());
        string? sentBody = null;
        _http.Answer = async (request, cancel) =>
        {
            sentBody = await request.Content!.ReadAsStringAsync(cancel);
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.ByteArrayContent(
                    System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { id_token = Jwt(q["nonce"], "jo@example.com") })),
            };
        };
        _server.Redirect.SetResult($"?code=abc&state={q["state"]}");

        await WaitFor.True(() => sentBody is not null);
        var form = Query(new Uri("http://x/?" + sentBody));
        form["client_secret"].ShouldBe("google-secret-xyz");
    }

    /// <summary>Review finding A7: something even <see cref="SignIn"/> itself didn't turn into a failed result must
    /// still leave the button usable again, rather than stuck Busy for good.</summary>
    [Fact]
    public async Task An_exception_from_the_service_still_leaves_busy_false()
    {
        _link.Status = Statuses.Running() with { Household = new HouseholdStatus(null, "device-xyz", "This-PC", ChassisKind.Desktop, true, [], null) };
        _link.Connect(true);
        _link.HouseholdThrows = new InvalidOperationException("boom");
        var model = Model();
        model.Apply(_link.Status.Household);

        model.SignInWithMicrosoft.Execute(null);
        var q = Query(_opened.ShouldNotBeNull());
        _http.Reply(System.Net.HttpStatusCode.OK, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { id_token = Jwt(q["nonce"], "jo@example.com") }));
        _server.Redirect.SetResult($"?code=abc&state={q["state"]}");

        await WaitFor.True(() => !model.Busy);
        model.Message.ShouldBe("Something went wrong signing in.");
    }

    [Fact]
    public void Signed_in_follows_the_services_status()
    {
        var model = Model();
        model.SignedIn.ShouldBeFalse();

        model.Apply(new HouseholdStatus("hh1", "device-xyz", "This-PC", ChassisKind.Desktop, true, [], null, SignedIn: true));

        model.SignedIn.ShouldBeTrue();
    }

    [Fact]
    public void Signing_out_clears_the_remembered_email()
    {
        _link.Connect(true);
        _ui.SetSignedInEmail("jo@example.com");
        var model = Model();

        model.SignOut.Execute(null);

        _link.HouseholdRequests.Single().ShouldBe("signOut");
        _ui.Current.SignedInEmail.ShouldBeNull();
    }

    [Fact]
    public void Deleting_the_account_asks_first()
    {
        _link.Connect(true);
        _ui.SetSignedInEmail("jo@example.com");
        var model = Model();

        model.DeleteAccount.Execute(null);
        model.ConfirmingDelete.ShouldBeTrue();
        _link.HouseholdRequests.ShouldBeEmpty();

        model.ConfirmDelete.Execute(null);

        _link.HouseholdRequests.Single().ShouldBe("deleteAccount");
        _ui.Current.SignedInEmail.ShouldBeNull();
        model.ConfirmingDelete.ShouldBeFalse();
    }

    [Fact]
    public void Cancelling_delete_sends_nothing()
    {
        _link.Connect(true);
        var model = Model();
        model.DeleteAccount.Execute(null);

        model.CancelDelete.Execute(null);

        model.ConfirmingDelete.ShouldBeFalse();
        _link.HouseholdRequests.ShouldBeEmpty();
    }
}
