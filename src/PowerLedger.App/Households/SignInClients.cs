using System.Reflection;

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
    /// flow is PKCE. The repo is public, so this never sits in source: it comes from the built assembly's own
    /// "GoogleClientSecret" metadata (<see cref="AssemblyMetadataAttribute"/>), which installer\build.ps1 sets from the
    /// machine building it (security round, review). Empty in an ordinary build; read once and cached.</summary>
    public static string GoogleSecret { get; } = ReadGoogleSecret(typeof(SignInClients).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>());

    /// <summary>The lookup on its own, apart from the real assembly, so a test can hand it metadata directly rather than
    /// rebuilding anything.</summary>
    internal static string ReadGoogleSecret(IEnumerable<AssemblyMetadataAttribute> metadata)
        => metadata.FirstOrDefault(entry => entry.Key == "GoogleClientSecret")?.Value ?? "";
}
