using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Aero look design §3: the Settings page's Glass section, like the iPhone's. Each choice sets
/// <see cref="SettingsViewModel.Glass"/> to a new record, which saves it and raises it; the section follows that one
/// channel back, whoever changed it. Reduce motion shows Windows' setting until the user changes it here.
/// </summary>
public class GlassSectionTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeUiSettings _ui = new();
    private bool _windowsReduces;

    private SettingsViewModel Settings() => new(
        new FakeLink(), new FakeMachineHistory(), _ui, UiThreads.Inline, new FakeTimeProvider(), TimeZoneInfo.Utc, English, "USD",
        windowsReducesMotion: () => _windowsReduces);

    [Fact]
    public void It_starts_from_the_saved_glass()
    {
        _ui.Current = _ui.Current with
        {
            Glass = GlassSettings.Default with { Style = GlassStyle.Dark, Frost = 0.3, Accent = GlassAccent.Rose, Backdrop = GlassBackdrop.Plain },
        };
        var glass = Settings().GlassSection;

        glass.Style.ShouldBe(GlassStyle.Dark);
        glass.Frost.ShouldBe(0.3);
        glass.Accent.ShouldBe(GlassAccent.Rose);
        glass.IsColour.ShouldBeFalse();
    }

    [Fact]
    public void Each_choice_saves_the_glass_as_a_whole_with_the_one_field_changed()
    {
        var settings = Settings();
        var glass = settings.GlassSection;

        glass.Style = GlassStyle.Clear;
        glass.TintStrength = 0.8;
        glass.Frost = 0.2;
        glass.EdgeLight = 0.9;
        glass.Accent = GlassAccent.Ice;
        glass.ReduceTransparency = true;
        glass.IncreaseContrast = true;
        glass.Parallax = false;

        settings.Glass.ShouldBe(GlassSettings.Default with
        {
            Style = GlassStyle.Clear, TintStrength = 0.8, Frost = 0.2, EdgeLight = 0.9, Accent = GlassAccent.Ice,
            ReduceTransparency = true, IncreaseContrast = true, Parallax = false,
        });
        _ui.Changes.Count(c => c.StartsWith("glass", StringComparison.Ordinal)).ShouldBe(8);
    }

    [Fact]
    public void The_same_choice_again_saves_nothing()
    {
        var glass = Settings().GlassSection;

        glass.Style = GlassStyle.Tinted;
        glass.Frost = GlassSettings.Default.Frost;

        _ui.Changes.ShouldBeEmpty();
    }

    /// <summary>A slider is saved as it lands, in range: the store clamps, and the section shows what was kept.</summary>
    [Fact]
    public void A_slider_out_of_range_shows_what_was_kept()
    {
        var glass = Settings().GlassSection;

        glass.Frost = 1.4;

        glass.Frost.ShouldBe(1);
        _ui.Current.Glass.Frost.ShouldBe(1);
    }

    [Fact]
    public void Colour_shows_the_picker_and_a_preset_chooses_colour_with_its_tint()
    {
        var settings = Settings();
        var glass = settings.GlassSection;
        GlassSection.Presets.Count.ShouldBe(8);
        GlassSection.Presets.Select(p => p.Hex).ShouldBeUnique();

        glass.ChoosePreset.Execute(GlassSection.Presets[3].Hex);

        glass.Style.ShouldBe(GlassStyle.Colour);
        glass.IsColour.ShouldBeTrue();
        settings.Glass.TintColor.ShouldBe(GlassSection.Presets[3].Hex);
        glass.TintColor.ShouldBe(GlassSection.Presets[3].Hex);
    }

    /// <summary>The wheel works in colours; the settings keep #RRGGBB, upper-case, and ignore any alpha.</summary>
    [Fact]
    public void The_wheels_colour_is_kept_as_hex()
    {
        var settings = Settings();
        var glass = settings.GlassSection;
        glass.Style = GlassStyle.Colour;

        glass.Tint = Color.FromArgb(0x80, 0x12, 0xAB, 0xCD);

        settings.Glass.TintColor.ShouldBe("#12ABCD");
        glass.Tint.ShouldBe(Color.FromRgb(0x12, 0xAB, 0xCD));
    }

    [Fact]
    public void Reduce_motion_shows_windows_setting_until_it_is_changed_here()
    {
        _windowsReduces = true;
        var settings = Settings();
        var glass = settings.GlassSection;

        glass.ReduceMotion.ShouldBeTrue("Windows reduces motion");
        glass.FollowsWindows.ShouldBeTrue();
        glass.ReduceMotionNote.ShouldBe("As Windows has it");

        glass.ReduceMotion = false;

        settings.Glass.ReduceMotion.ShouldBe(false);
        glass.FollowsWindows.ShouldBeFalse();
        glass.ReduceMotionNote.ShouldBe("Set here. Windows reduces motion.");

        glass.FollowWindows.Execute(null);

        settings.Glass.ReduceMotion.ShouldBeNull();
        glass.ReduceMotion.ShouldBeTrue();
    }

    /// <summary>The same as Windows' setting is still a choice made here: it stops following Windows.</summary>
    [Fact]
    public void Choosing_what_windows_has_still_stops_following_it()
    {
        var settings = Settings();

        settings.GlassSection.ReduceMotion = false;

        settings.Glass.ReduceMotion.ShouldBe(false);
    }

    [Fact]
    public void Under_reduced_motion_parallax_is_off_whatever_its_switch_says()
    {
        var glass = Settings().GlassSection;
        glass.ParallaxAvailable.ShouldBeTrue();

        glass.ReduceMotion = true;

        glass.ParallaxAvailable.ShouldBeFalse();
        glass.Parallax.ShouldBeTrue("the choice is kept for when motion is back");
    }

    [Fact]
    public void It_follows_the_glass_whoever_changed_it()
    {
        var settings = Settings();
        var glass = settings.GlassSection;
        var raised = new List<string?>();
        ((INotifyPropertyChanged)glass).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        settings.Glass = settings.Glass with { Style = GlassStyle.Dark, Accent = GlassAccent.Amber };

        glass.Style.ShouldBe(GlassStyle.Dark);
        glass.Accent.ShouldBe(GlassAccent.Amber);
        raised.ShouldContain(nameof(GlassSection.Style));
        raised.ShouldContain(nameof(GlassSection.Accent));
    }

    [Fact]
    public void Reset_puts_back_the_demos_glass()
    {
        var settings = Settings();
        settings.Glass = settings.Glass with { Style = GlassStyle.Colour, TintColor = "#123456", Frost = 0.1, ReduceMotion = true };

        settings.GlassSection.Reset.Execute(null);

        settings.Glass.ShouldBe(GlassSettings.Default);
    }

    [Fact]
    public void Each_accent_has_the_demos_swatch()
        => GlassSection.Accents.Select(a => (a.Accent, a.Hex)).ShouldBe(
        [
            (GlassAccent.Lime, "#D9F25A"), (GlassAccent.Ice, "#7FD4FF"), (GlassAccent.Indigo, "#9AA2FF"), (GlassAccent.Amber, "#FFC857"),
            (GlassAccent.Rose, "#FF8FB1"),
        ]);
}
