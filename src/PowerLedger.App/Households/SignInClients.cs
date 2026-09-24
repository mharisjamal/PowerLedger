namespace PowerLedger.App;

/// <summary>
/// Public client IDs for native-app OIDC (households design §7, Plan N task L6): a public client is not a secret, since
/// the flow is authorization code with PKCE. Filled in by the lead once the owner has made an Azure app registration (a
/// public client with a http://localhost redirect) and a Google OAuth client of the Desktop kind. Empty until then; the
/// sign-in buttons say sign-in isn't set up yet while they are.
/// </summary>
internal static class SignInClients
{
    public const string Microsoft = "";

    public const string Google = "";

    /// <summary>Google's installed-app clients call for one in the token exchange (review finding A7), even though the
    /// flow is PKCE; empty, like the client IDs above, until the lead fills it in at L6.</summary>
    public const string GoogleSecret = "";
}
