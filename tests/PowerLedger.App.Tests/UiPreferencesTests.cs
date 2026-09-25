using System.IO;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class UiPreferencesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"powerledger-ui-{Guid.NewGuid():N}");

    private string File => Path.Combine(_folder, "ui.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void With_no_file_the_defaults_apply()
    {
        var preferences = new UiPreferencesStore(File).Load();
        preferences.Theme.ShouldBe(ThemeChoice.System);
        preferences.Co2KgPerKwh.ShouldBe(0.40);
    }

    [Fact]
    public void Preferences_survive_a_save_and_a_load()
    {
        var store = new UiPreferencesStore(File);
        store.Save(new UiPreferences { Theme = ThemeChoice.Light, Co2KgPerKwh = 0.23 });
        store.Load().ShouldBe(new UiPreferences { Theme = ThemeChoice.Light, Co2KgPerKwh = 0.23 });
        System.IO.File.ReadAllText(File).ShouldContain("\"Light\"");
    }

    [Fact]
    public void A_damaged_file_gives_the_defaults()
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, "{ not json");
        new UiPreferencesStore(File).Load().ShouldBe(UiPreferences.Default);
    }

    [Fact]
    public void A_CO2_factor_out_of_range_falls_back_to_the_default()
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, """{ "Theme": "Dark", "Co2KgPerKwh": 55 }""");
        new UiPreferencesStore(File).Load().ShouldBe(new UiPreferences { Theme = ThemeChoice.Dark });
    }

    [Fact]
    public void A_file_without_a_CO2_factor_gets_the_default()
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, """{ "Theme": "Dark" }""");
        new UiPreferencesStore(File).Load().Co2KgPerKwh.ShouldBe(0.40);
    }

    [Fact]
    public void The_first_run_is_remembered()
    {
        var store = new UiPreferencesStore(File);
        store.Load().FirstRunDone.ShouldBeFalse();
        store.Save(UiPreferences.Default with { FirstRunDone = true });
        store.Load().FirstRunDone.ShouldBeTrue();
    }

    [Fact]
    public void A_file_from_when_updates_could_be_turned_off_still_loads()
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, """{ "Theme": "Dark", "FirstRunDone": true, "CheckForUpdates": false }""");
        var old = new UiPreferencesStore(File).Load();
        old.FirstRunDone.ShouldBeTrue();
        old.AnnouncedVersion.ShouldBeNull();
        old.LastVersion.ShouldBeNull();
    }

    [Fact]
    public void Reading_monitor_brightness_is_on_by_default_even_in_a_file_from_before_it_existed()
    {
        new UiPreferencesStore(File).Load().ReadMonitorBrightness.ShouldBeTrue();

        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, """{ "Theme": "Dark", "FirstRunDone": true, "CheckForUpdates": false }""");
        new UiPreferencesStore(File).Load().ReadMonitorBrightness.ShouldBeTrue();
    }

    [Fact]
    public void Reading_monitor_brightness_turned_off_survives_a_save_and_a_load()
    {
        var store = new UiPreferencesStore(File);
        store.Save(UiPreferences.Default with { ReadMonitorBrightness = false });
        store.Load().ReadMonitorBrightness.ShouldBeFalse();
    }

    /// <summary>Midnight is the default (the owner's decision, 2026-09-25): a new install opens in it, and a chosen Classic
    /// survives a save and a load.</summary>
    [Fact]
    public void The_look_is_midnight_until_chosen_and_a_chosen_classic_survives_a_save_and_a_load()
    {
        var store = new UiPreferencesStore(File);
        store.Load().Look.ShouldBe(Look.Midnight);
        UiPreferences.Default.Look.ShouldBe(Look.Midnight);

        store.Save(UiPreferences.Default with { Look = Look.Classic });

        store.Load().Look.ShouldBe(Look.Classic);
        System.IO.File.ReadAllText(File).ShouldContain("\"Classic\"");
    }

    /// <summary>Every PC updating from 0.7.x or earlier has a ui.json with no Look: it lands on Midnight.</summary>
    [Fact]
    public void A_file_from_before_the_look_existed_opens_in_midnight_and_keeps_the_rest()
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, """{ "Theme": "Light", "FirstRunDone": true, "Co2KgPerKwh": 0.23 }""");

        var read = new UiPreferencesStore(File).Load();

        read.Look.ShouldBe(Look.Midnight);
        read.LookIntroduced.ShouldBeFalse("so it is told of the new look once");
        read.Theme.ShouldBe(ThemeChoice.Light);
        read.FirstRunDone.ShouldBeTrue();
        read.Co2KgPerKwh.ShouldBe(0.23);
    }

    [Fact]
    public void A_look_this_version_does_not_know_reads_as_the_default_and_keeps_the_rest_of_the_file()
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, """{ "Theme": "Dark", "Look": "Neon", "FirstRunDone": true }""");

        var read = new UiPreferencesStore(File).Load();

        read.Look.ShouldBe(Look.Midnight);
        read.Theme.ShouldBe(ThemeChoice.Dark);
        read.FirstRunDone.ShouldBeTrue();
    }

    [Fact]
    public void That_the_new_look_was_introduced_survives_a_save_and_a_load()
    {
        var store = new UiPreferencesStore(File);
        store.Load().LookIntroduced.ShouldBeFalse();

        store.Save(UiPreferences.Default with { LookIntroduced = true });

        store.Load().LookIntroduced.ShouldBeTrue();
    }

    [Fact]
    public void The_update_bookkeeping_survives_a_save_and_a_load()
    {
        var store = new UiPreferencesStore(File);
        var saved = UiPreferences.Default with { AnnouncedVersion = "0.3.0", LastVersion = "0.2.0" };
        store.Save(saved);
        store.Load().ShouldBe(saved);
    }
}
