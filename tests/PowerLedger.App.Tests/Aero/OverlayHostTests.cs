using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Win32;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Aero look design §5: the overlay is Aero's only. It opens while the look is Aero and it is on, and closes when it is
/// turned off or the look leaves Aero, coming back with Aero if it is still on; the tray offers it only in Aero.
/// </summary>
public class OverlayHostTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeUiSettings _ui = new();
    private readonly List<FakeOverlay> _made = [];
    private readonly List<(bool Offered, bool On)> _offers = [];

    private SettingsViewModel Settings() => new(
        new FakeLink(), new FakeMachineHistory(), _ui, UiThreads.Inline, new FakeTimeProvider(), TimeZoneInfo.Utc, English, "USD");

    private OverlayHost Host(SettingsViewModel settings) => new(settings, () =>
    {
        var overlay = new FakeOverlay();
        _made.Add(overlay);
        return overlay;
    }, (offered, on) => _offers.Add((offered, on)));

    [Theory]
    [InlineData("Aero", true, true)]
    [InlineData("Aero", false, false)]
    [InlineData("Midnight", true, false)]
    [InlineData("Classic", true, false)]
    public void It_shows_only_in_aero_and_only_when_on(string look, bool enabled, bool shows)
        => OverlayHost.Shows(Enum.Parse<Look>(look), OverlaySettings.Default with { Enabled = enabled }).ShouldBe(shows);

    [Fact]
    public void Started_in_aero_with_it_on_it_opens_at_once()
    {
        _ui.Current = _ui.Current with { Look = Look.Aero, Overlay = OverlaySettings.Default with { Enabled = true } };

        using var host = Host(Settings());

        _made.Count.ShouldBe(1);
        _made[0].Shown.ShouldBeTrue();
        _offers[^1].ShouldBe((true, true));
    }

    [Fact]
    public void Turned_on_it_opens_and_turned_off_it_closes()
    {
        _ui.Current = _ui.Current with { Look = Look.Aero };
        var settings = Settings();
        using var host = Host(settings);
        _made.ShouldBeEmpty();
        _offers[^1].ShouldBe((true, false));

        settings.ToggleOverlay.Execute(null);
        _made.Count.ShouldBe(1);
        _made[0].Shown.ShouldBeTrue();
        _offers[^1].ShouldBe((true, true));

        settings.ToggleOverlay.Execute(null);
        _made[0].Closed.ShouldBeTrue();
        _offers[^1].ShouldBe((true, false));
    }

    [Fact]
    public void Leaving_aero_closes_it_and_coming_back_opens_it_again()
    {
        _ui.Current = _ui.Current with { Look = Look.Aero, Overlay = OverlaySettings.Default with { Enabled = true } };
        var settings = Settings();
        using var host = Host(settings);

        settings.Look = Look.Midnight;

        _made[0].Closed.ShouldBeTrue();
        _offers[^1].ShouldBe((false, true), "the tray has no overlay item outside Aero");
        settings.Overlay.Enabled.ShouldBeTrue("it is still on, for when Aero is back");

        settings.Look = Look.Aero;

        _made.Count.ShouldBe(2);
        _made[1].Shown.ShouldBeTrue();
        _offers[^1].ShouldBe((true, true));
    }

    [Fact]
    public void A_change_that_leaves_it_showing_moves_the_one_it_has()
    {
        _ui.Current = _ui.Current with { Look = Look.Aero, Overlay = OverlaySettings.Default with { Enabled = true } };
        var settings = Settings();
        using var host = Host(settings);

        settings.OverlaySection.Position = OverlayPosition.BottomLeft;

        _made.Count.ShouldBe(1);
        _made[0].Applied[^1].Position.ShouldBe(OverlayPosition.BottomLeft);
    }

    [Fact]
    public void Disposed_it_closes_the_overlay_and_stops_following_settings()
    {
        _ui.Current = _ui.Current with { Look = Look.Aero, Overlay = OverlaySettings.Default with { Enabled = true } };
        var settings = Settings();
        var host = Host(settings);

        host.Dispose();
        settings.ToggleOverlay.Execute(null);
        settings.ToggleOverlay.Execute(null);

        _made.Count.ShouldBe(1);
        _made[0].Closed.ShouldBeTrue();
    }

    /// <summary>The tray's own item: shown only while offered, ticked while on, and a click toggles.</summary>
    [Fact]
    [Trait("Category", "UI")]
    public void The_tray_item_shows_only_while_offered_and_toggles()
    {
        var key = $@"Software\PowerLedger.Tests.{Guid.NewGuid():N}";
        try
        {
            using var tray = new TrayIcon(() => { }, () => { }, new StartWithWindows(@"C:\PowerLedger.exe", key));
            var toggled = 0;
            tray.OverlayOffered.ShouldBeFalse("nothing is offered until the App says the look is Aero");

            tray.OfferOverlay(offered: true, on: false, () => toggled++);
            tray.OverlayOffered.ShouldBeTrue();
            tray.OverlayOn.ShouldBeFalse();
            tray.ClickOverlay();
            toggled.ShouldBe(1);

            tray.OfferOverlay(offered: true, on: true, () => toggled++);
            tray.OverlayOn.ShouldBeTrue();

            tray.OfferOverlay(offered: false, on: true, () => toggled++);
            tray.OverlayOffered.ShouldBeFalse();
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        }
    }

    private sealed class FakeOverlay : IOverlay
    {
        public bool Shown { get; private set; }

        public bool Closed { get; private set; }

        public List<OverlaySettings> Applied { get; } = [];

        public void Apply(OverlaySettings settings) => Applied.Add(settings);

        public void ShowOverlay() => Shown = true;

        public void CloseOverlay() => Closed = true;
    }
}
