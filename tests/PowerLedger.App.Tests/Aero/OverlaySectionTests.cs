using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Aero look design §5: the Overlay section in Settings and the top bar's button turn the overlay on and off and choose
/// its corner, opacity and sparkline, each through <see cref="SettingsViewModel.Overlay"/>, the one channel the overlay
/// follows.
/// </summary>
public class OverlaySectionTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeUiSettings _ui = new();

    private SettingsViewModel Settings() => new(
        new FakeLink(), new FakeMachineHistory(), _ui, UiThreads.Inline, new FakeTimeProvider(), TimeZoneInfo.Utc, English, "USD");

    [Fact]
    public void Each_choice_saves_the_overlay_with_the_one_field_changed()
    {
        var settings = Settings();
        var overlay = settings.OverlaySection;

        overlay.Enabled = true;
        overlay.Position = OverlayPosition.BottomLeft;
        overlay.Opacity = 0.7;
        overlay.Sparkline = false;

        settings.Overlay.ShouldBe(OverlaySettings.Default with { Enabled = true, Position = OverlayPosition.BottomLeft, Opacity = 0.7, Sparkline = false });
        _ui.Changes.ShouldBe(["overlay on TopRight", "overlay on BottomLeft", "overlay on BottomLeft", "overlay on BottomLeft"]);
    }

    [Fact]
    public void The_opacity_is_kept_in_its_range()
    {
        var overlay = Settings().OverlaySection;

        overlay.Opacity = 0.2;

        overlay.Opacity.ShouldBe(OverlaySettings.MinOpacity);
    }

    /// <summary>The top bar's button (agent D binds <see cref="SettingsViewModel.ToggleOverlay"/>).</summary>
    [Fact]
    public void The_toggle_turns_it_on_and_off_again_keeping_its_place()
    {
        _ui.Current = _ui.Current with { Overlay = OverlaySettings.Default with { Position = OverlayPosition.Free, Left = 300, Top = 200 } };
        var settings = Settings();

        settings.ToggleOverlay.Execute(null);
        settings.OverlaySection.Enabled.ShouldBeTrue();
        settings.ToggleOverlay.Execute(null);

        settings.Overlay.ShouldBe(OverlaySettings.Default with { Position = OverlayPosition.Free, Left = 300, Top = 200 });
        _ui.Changes.ShouldBe(["overlay on Free", "overlay off Free"]);
    }

    [Fact]
    public void It_follows_the_overlay_whoever_changed_it()
    {
        var settings = Settings();
        var raised = new List<string?>();
        settings.OverlaySection.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        settings.Overlay = settings.Overlay with { Enabled = true, Sparkline = false };

        settings.OverlaySection.Enabled.ShouldBeTrue();
        settings.OverlaySection.Sparkline.ShouldBeFalse();
        raised.ShouldContain(nameof(OverlaySection.Enabled));
        raised.ShouldContain(nameof(OverlaySection.Sparkline));
    }
}
