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
    public void The_first_run_is_remembered()
    {
        var store = new UiPreferencesStore(File);
        store.Load().FirstRunDone.ShouldBeFalse();
        store.Save(UiPreferences.Default with { FirstRunDone = true });
        store.Load().FirstRunDone.ShouldBeTrue();
    }
}
