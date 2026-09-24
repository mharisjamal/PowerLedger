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

    private SignInViewModel Model(string microsoftClientId = "ms-client", string googleClientId = "google-client")
    {
        var signIn = new SignIn(() => _server, url => _opened = url, _http.Client());
        return new SignInViewModel(_link, _ui, signIn, UiThreads.Inline, microsoftClientId, googleClientId);
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

    [Fact]
    public async Task A_typed_recovery_code_is_sent_with_the_sign_in_and_cleared_once_taken()
    {
        _link.Status = Statuses.Running() with { Household = new HouseholdStatus(null, "device-xyz", "This-PC", ChassisKind.Desktop, true, [], null) };
        _link.Connect(true);
        var model = Model();
        model.Apply(_link.Status.Household);
        model.RecoveryCodeInput = "K7QM-2XHD-9PW4-R8TA-VMNP-3QWE";

        model.SignInWithMicrosoft.Execute(null);
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
