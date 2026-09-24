using System.Reflection;
using Shouldly;

namespace PowerLedger.App.Tests;

public class SignInClientsTests
{
    /// <summary>Security round, review: the repo is public, so the secret never sits in source; it comes from whatever
    /// metadata installer\build.ps1 attached to the built assembly.</summary>
    [Fact]
    public void The_secret_is_the_assemblys_own_googleclientsecret_metadata()
    {
        SignInClients.ReadGoogleSecret([new AssemblyMetadataAttribute("GoogleClientSecret", "abc123")]).ShouldBe("abc123");
    }

    [Fact]
    public void With_no_such_metadata_the_secret_is_empty()
    {
        SignInClients.ReadGoogleSecret([]).ShouldBe("");
        SignInClients.ReadGoogleSecret([new AssemblyMetadataAttribute("SomethingElse", "x")]).ShouldBe("");
    }

    [Fact]
    public void A_metadata_entry_with_no_value_reads_as_empty_not_null()
    {
        SignInClients.ReadGoogleSecret([new AssemblyMetadataAttribute("GoogleClientSecret", null)]).ShouldBe("");
    }

    /// <summary>An ordinary build (no -p:PowerLedgerGoogleClientSecret passed) leaves the csproj's own metadata value
    /// empty, so this always builds — Google sign-in just isn't offered.</summary>
    [Fact]
    public void In_this_build_the_secret_is_empty()
    {
        SignInClients.GoogleSecret.ShouldBe("");
    }
}
