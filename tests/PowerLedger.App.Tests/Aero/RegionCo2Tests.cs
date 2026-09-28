using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Aero look design §4: the App keeps one carbon setting, Settings' CO₂ per kWh. Aero's Settings offers the built-in
/// figure for Windows' region in one press, saved as any typed figure is, in kg to two decimals.
/// </summary>
public class RegionCo2Tests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeUiSettings _ui = new();

    private SettingsViewModel Settings(string? region) => new(
        new FakeLink(), new FakeMachineHistory(), _ui, UiThreads.Inline, new FakeTimeProvider(), TimeZoneInfo.Utc, English, "USD",
        windowsRegion: () => region);

    [Fact]
    public void It_saves_the_regions_figure_in_kg()
    {
        var settings = Settings("PL");

        settings.UseRegionCo2.Execute(null);

        settings.Co2.ShouldBe("0.65");
        _ui.Current.Co2KgPerKwh.ShouldBe(0.65);
        settings.Co2Message.ShouldBe("Saved Poland's figure, 651 g per kWh (Ember, 2023).");
    }

    [Fact]
    public void A_region_the_table_lacks_takes_the_world_average()
    {
        var settings = Settings(null);

        settings.UseRegionCo2.Execute(null);

        _ui.Current.Co2KgPerKwh.ShouldBe(0.48);
        settings.Co2Message.ShouldBe("Saved the world's figure, 481 g per kWh (Ember, 2023).");
    }

    [Fact]
    public void The_offer_names_the_region()
    {
        Settings("FR").RegionCo2Offer.ShouldBe("Use France's figure");
        Settings("ZZ").RegionCo2Offer.ShouldBe("Use the world average");
    }
}
