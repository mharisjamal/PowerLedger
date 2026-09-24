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
    public void Updates_are_on_by_default_even_in_a_file_from_before_they_existed()
    {
        new UiPreferencesStore(File).Load().CheckForUpdates.ShouldBeTrue();

        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, """{ "Theme": "Dark", "FirstRunDone": true }""");
        var old = new UiPreferencesStore(File).Load();
        old.CheckForUpdates.ShouldBeTrue();
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

    [Fact]
    public void The_look_is_classic_until_chosen_and_survives_a_save_and_a_load()
    {
        var store = new UiPreferencesStore(File);
        store.Load().Look.ShouldBe(Look.Classic);

        store.Save(UiPreferences.Default with { Look = Look.Midnight });

        store.Load().Look.ShouldBe(Look.Midnight);
        System.IO.File.ReadAllText(File).ShouldContain("\"Midnight\"");
    }

    [Fact]
    public void A_look_this_version_does_not_know_reads_as_classic_and_keeps_the_rest_of_the_file()
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, """{ "Theme": "Dark", "Look": "Neon", "FirstRunDone": true }""");

        var read = new UiPreferencesStore(File).Load();

        read.Look.ShouldBe(Look.Classic);
        read.Theme.ShouldBe(ThemeChoice.Dark);
        read.FirstRunDone.ShouldBeTrue();
    }

    [Fact]
    public void The_update_bookkeeping_survives_a_save_and_a_load()
    {
        var store = new UiPreferencesStore(File);
        var saved = UiPreferences.Default with { CheckForUpdates = false, AnnouncedVersion = "0.3.0", LastVersion = "0.2.0" };
        store.Save(saved);
        store.Load().ShouldBe(saved);
    }
}
