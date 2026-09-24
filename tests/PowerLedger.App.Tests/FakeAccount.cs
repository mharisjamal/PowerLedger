using System.Net.Http;

namespace PowerLedger.App.Tests;

/// <summary>A throwaway <see cref="SignInViewModel"/> for a test that needs a <see cref="HouseholdViewModel"/> but does
/// not exercise sign-in itself.</summary>
internal static class FakeAccount
{
    public static SignInViewModel Model(IServiceLink link) =>
        new(link, new FakeUiSettings(), new SignIn(() => new FakeLoopbackServer(), _ => { }, new HttpClient()), UiThreads.Inline, "", "");
}
