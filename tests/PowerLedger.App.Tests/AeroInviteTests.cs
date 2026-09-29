using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Aero is invite only (0.10.3): the code is checked by its hash, trimmed and in any case.</summary>
public class AeroInviteTests
{
    [Theory]
    [InlineData("BCZ859")]
    [InlineData("bcz859 ")]
    [InlineData("  Bcz859\t")]
    public void The_invite_code_is_valid_trimmed_in_any_case(string code) => AeroInvite.IsValid(code).ShouldBeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("BCZ858")]
    [InlineData("BCZ8590")]
    [InlineData("B CZ859")]
    public void Any_other_code_is_not(string? code) => AeroInvite.IsValid(code).ShouldBeFalse();

    [Fact]
    public void The_source_keeps_only_the_hash() => AeroInvite.CodeHash.ShouldNotContain("BCZ859", Case.Insensitive);

    private readonly FakeUiSettings _ui = new() { Current = UiPreferences.Default with { LookIntroduced = true, AeroIntroSeen = true, AeroUnlocked = false } };

    private SettingsViewModel Settings() => new(
        new FakeLink(), new FakeMachineHistory(), _ui, UiThreads.Inline, new FakeTimeProvider(), TimeZoneInfo.Utc, CultureInfo.GetCultureInfo("en-US"), "USD");

    /// <summary>Settings, Look: Aero shows as locked, and picking it asks for the code rather than switching.</summary>
    [Fact]
    public void Picking_aero_while_locked_asks_for_the_code_and_switches_nothing()
    {
        var settings = Settings();
        settings.AeroLocked.ShouldBeTrue();
        settings.EnteringAeroCode.ShouldBeFalse();

        settings.Look = Look.Aero;

        settings.Look.ShouldBe(Look.Midnight);
        settings.EnteringAeroCode.ShouldBeTrue();
        _ui.Changes.ShouldBeEmpty();
    }

    [Fact]
    public void A_wrong_code_says_so_and_switches_nothing()
    {
        var settings = Settings();
        settings.Look = Look.Aero;
        settings.AeroCode = "ABC123";

        settings.UnlockAero.Execute(null);

        settings.AeroCodeMessage.ShouldBe("That code isn't valid.");
        (settings.Look, settings.AeroLocked, settings.EnteringAeroCode).ShouldBe((Look.Midnight, true, true));
        _ui.Changes.ShouldBeEmpty();
    }

    /// <summary>The code unlocks Aero and switches to it, with its intro and banner due as on a first Aero open; after
    /// that Aero is a normal choice.</summary>
    [Fact]
    public void The_code_unlocks_aero_and_switches_to_it_with_its_intro_due()
    {
        var settings = Settings();
        settings.Look = Look.Aero;
        settings.AeroCode = " bcz859 ";

        settings.UnlockAero.Execute(null);

        _ui.Changes.ShouldBe(["aero unlocked", "look Aero"]);
        (settings.Look, settings.AeroLocked, settings.EnteringAeroCode, settings.AeroCodeMessage).ShouldBe((Look.Aero, false, false, (string?)null));
        (settings.LookIntroduced, settings.AeroIntroSeen).ShouldBe((false, false));
        settings.Look = Look.Classic;
        settings.Look = Look.Aero;
        settings.Look.ShouldBe(Look.Aero, "once unlocked, Aero is a normal choice");
    }
}
