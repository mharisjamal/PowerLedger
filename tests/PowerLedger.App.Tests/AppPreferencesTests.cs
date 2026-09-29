using System.IO;
using Microsoft.Win32;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class AppPreferencesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"powerledger-prefs-{Guid.NewGuid():N}");
    private readonly string _runKey = $@"Software\PowerLedger.Tests.{Guid.NewGuid():N}";
    private readonly List<ThemeChoice> _themes = [];
    private readonly List<double> _factors = [];
    private readonly List<Look> _looks = [];
    private string? _lookProblem;

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        Registry.CurrentUser.DeleteSubKeyTree(_runKey, throwOnMissingSubKey: false);
    }

    private UiPreferencesStore Store => new(Path.Combine(_folder, "ui.json"));

    private AppPreferences Preferences() => new(
        Store, UiPreferences.Default, _themes.Add, _factors.Add, new StartWithWindows(@"C:\Program Files\PowerLedger\PowerLedger.exe", _runKey),
        look =>
        {
            _looks.Add(look);
            return _lookProblem;
        });

    [Fact]
    public void A_theme_applies_at_once_and_is_saved()
    {
        var preferences = Preferences();
        preferences.Choose(ThemeChoice.Light).ShouldBeNull();

        _themes.ShouldBe(new[] { ThemeChoice.Light });
        preferences.Current.Theme.ShouldBe(ThemeChoice.Light);
        Store.Load().Theme.ShouldBe(ThemeChoice.Light);
    }

    [Fact]
    public void The_energy_cards_period_is_saved()
    {
        var preferences = Preferences();
        preferences.SetEnergyPeriod(EnergyPeriod.ThisMonth).ShouldBeNull();

        preferences.Current.EnergyPeriod.ShouldBe(EnergyPeriod.ThisMonth);
        Store.Load().EnergyPeriod.ShouldBe(EnergyPeriod.ThisMonth);
    }

    [Fact]
    public void A_co2_factor_reaches_the_screens_and_one_out_of_range_is_refused()
    {
        var preferences = Preferences();
        preferences.UseCo2(0.23).ShouldBeNull();
        _factors.ShouldBe(new[] { 0.23 });
        Store.Load().Co2KgPerKwh.ShouldBe(0.23);

        preferences.UseCo2(5).ShouldBe("A grid's intensity is between 0 and 2 kg of CO₂ per kWh.");
        preferences.UseCo2(double.NaN).ShouldNotBeNull();
        _factors.Count.ShouldBe(1);
        preferences.Current.Co2KgPerKwh.ShouldBe(0.23);
    }

    [Fact]
    public void Starting_with_windows_and_the_first_run_are_remembered()
    {
        var preferences = Preferences();
        preferences.StartWithWindows(true).ShouldBeNull();
        preferences.StartsWithWindows.ShouldBeTrue();
        preferences.StartWithWindows(false).ShouldBeNull();
        preferences.StartsWithWindows.ShouldBeFalse();

        preferences.FinishFirstRun().ShouldBeNull();
        Store.Load().FirstRunDone.ShouldBeTrue();
        Store.Load().FirstRunAt.ShouldNotBeNull();
    }

    [Fact]
    public void Finishing_the_first_run_twice_keeps_its_first_time()
    {
        var preferences = Preferences();
        preferences.FinishFirstRun();
        var first = preferences.Current.FirstRunAt;

        preferences.FinishFirstRun();

        preferences.Current.FirstRunAt.ShouldBe(first);
    }

    [Fact]
    public void An_existing_install_without_a_first_run_time_is_backfilled_once()
    {
        var preferences = new AppPreferences(
            Store, UiPreferences.Default with { FirstRunDone = true }, _themes.Add, _factors.Add,
            new StartWithWindows(@"C:\Program Files\PowerLedger\PowerLedger.exe", _runKey));

        preferences.EnsureFirstRunAt().ShouldBeNull();

        var stamped = preferences.Current.FirstRunAt.ShouldNotBeNull();
        preferences.EnsureFirstRunAt();
        preferences.Current.FirstRunAt.ShouldBe(stamped);   // not moved on a later call
    }

    [Fact]
    public void A_fresh_install_is_not_backfilled_before_it_has_finished_setup()
    {
        var preferences = Preferences();
        preferences.EnsureFirstRunAt();
        preferences.Current.FirstRunAt.ShouldBeNull();
    }

    [Fact]
    public void A_preference_that_cannot_be_saved_says_so_and_still_applies()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "ui.json.tmp"), "");
        using var blocker = File.Open(Path.Combine(_folder, "ui.json.tmp"), FileMode.Open, FileAccess.Read, FileShare.None);

        var preferences = Preferences();
        preferences.Choose(ThemeChoice.Dark).ShouldStartWith("Couldn't save your preferences:");
        _themes.ShouldBe(new[] { ThemeChoice.Dark });
    }

    [Fact]
    public void Until_the_first_run_is_done_starting_with_windows_is_turned_on()
    {
        var preferences = Preferences();
        preferences.StartsWithWindows.ShouldBeFalse();
        preferences.ApplyFirstRunDefaults();
        preferences.StartsWithWindows.ShouldBeTrue();
    }

    [Fact]
    public void After_the_first_run_starting_with_windows_is_left_as_the_user_set_it()
    {
        var preferences = new AppPreferences(
            Store, UiPreferences.Default with { FirstRunDone = true }, _themes.Add, _factors.Add,
            new StartWithWindows(@"C:\Program Files\PowerLedger\PowerLedger.exe", _runKey));
        preferences.ApplyFirstRunDefaults();
        preferences.StartsWithWindows.ShouldBeFalse();
    }

    [Fact]
    public void Reading_monitor_brightness_is_saved()
    {
        var preferences = Preferences();
        preferences.Current.ReadMonitorBrightness.ShouldBeTrue();

        preferences.ReadMonitorBrightness(false).ShouldBeNull();

        preferences.Current.ReadMonitorBrightness.ShouldBeFalse();
        Store.Load().ReadMonitorBrightness.ShouldBeFalse();
    }

    [Fact]
    public void A_look_switches_the_window_first_and_is_then_saved_and_the_new_look_needs_no_introducing_after()
    {
        var preferences = Preferences();
        preferences.Current.Look.ShouldBe(Look.Midnight, "the default");

        preferences.SetLook(Look.Classic).ShouldBeNull();

        _looks.ShouldBe(new[] { Look.Classic });
        preferences.Current.Look.ShouldBe(Look.Classic);
        preferences.Current.LookIntroduced.ShouldBeTrue("a user who has switched looks knows there are two");
        Store.Load().Look.ShouldBe(Look.Classic);
        Store.Load().LookIntroduced.ShouldBeTrue();
    }

    [Fact]
    public void A_look_whose_window_would_not_open_is_not_saved_and_the_reason_comes_back()
    {
        _lookProblem = "Couldn't open the Classic look: no window.";
        var preferences = Preferences();

        preferences.SetLook(Look.Classic).ShouldBe("Couldn't open the Classic look: no window.");

        preferences.Current.Look.ShouldBe(Look.Midnight);
        Store.Load().Look.ShouldBe(Look.Midnight);
    }

    /// <summary>0.10.3: Aero is invite only. Locked, choosing it switches nothing and saves nothing.</summary>
    [Fact]
    public void Aero_while_locked_is_refused_without_a_switch()
    {
        var preferences = Preferences();

        preferences.SetLook(Look.Aero).ShouldBe(AeroInvite.Locked);

        _looks.ShouldBeEmpty();
        preferences.Current.Look.ShouldBe(Look.Midnight);
        System.IO.File.Exists(Path.Combine(_folder, "ui.json")).ShouldBeFalse();
    }

    /// <summary>The code unlocks Aero for good, saved before the switch, and Aero opens as on a first open: its intro and
    /// banner due, Switch back going to the look left.</summary>
    [Fact]
    public void Unlocking_aero_saves_the_unlock_and_switches_to_aero_with_its_intro_due()
    {
        var preferences = Preferences();
        preferences.SetLook(Look.Classic).ShouldBeNull();
        preferences.SeeAeroIntro();

        preferences.UnlockAero().ShouldBeNull();

        _looks.ShouldBe([Look.Classic, Look.Aero]);
        var read = Store.Load();
        (read.Look, read.AeroUnlocked, read.LookIntroduced, read.AeroIntroSeen, read.LookBeforeAero)
            .ShouldBe((Look.Aero, true, false, false, (Look?)Look.Classic));
        preferences.Current.ShouldBe(read);
        preferences.SetLook(Look.Midnight).ShouldBeNull();
        preferences.SetLook(Look.Aero).ShouldBeNull("once unlocked, Aero is a normal choice");
    }

    [Fact]
    public void Aero_whose_window_would_not_open_stays_unlocked_and_unswitched()
    {
        var preferences = Preferences();
        _lookProblem = "Couldn't open the Aero look: no window.";

        preferences.UnlockAero().ShouldBe("Couldn't open the Aero look: no window.");

        var read = Store.Load();
        (read.Look, read.AeroUnlocked, read.LookIntroduced).ShouldBe((Look.Midnight, true, false));
    }

    [Fact]
    public void The_new_look_once_introduced_is_saved_as_such()
    {
        var preferences = Preferences();
        preferences.Current.LookIntroduced.ShouldBeFalse();

        preferences.IntroduceLook().ShouldBeNull();

        preferences.Current.LookIntroduced.ShouldBeTrue();
        Store.Load().LookIntroduced.ShouldBeTrue();
        _looks.ShouldBeEmpty("introducing it switches nothing");
    }

    [Fact]
    public void The_aero_intro_once_seen_is_saved_as_such_and_retires_nothing_else()
    {
        var preferences = Preferences();
        preferences.Current.AeroIntroSeen.ShouldBeFalse();

        preferences.SeeAeroIntro().ShouldBeNull();

        preferences.Current.AeroIntroSeen.ShouldBeTrue();
        Store.Load().AeroIntroSeen.ShouldBeTrue();
        preferences.Current.LookIntroduced.ShouldBeFalse("the banner still shows after the video");
    }

    /// <summary>Aero look design §3: the Glass section's choices are saved as a whole, put in range first; the look
    /// itself learns of them through Settings, which raises its Glass property.</summary>
    [Fact]
    public void Glass_settings_are_saved_in_range()
    {
        var preferences = Preferences();
        var chosen = GlassSettings.Default with { Style = GlassStyle.Colour, TintColor = "#12ab9f", TintStrength = 3, ReduceMotion = true };

        preferences.SetGlass(chosen).ShouldBeNull();

        var kept = GlassSettings.Default with { Style = GlassStyle.Colour, TintColor = "#12AB9F", TintStrength = 1, ReduceMotion = true };
        preferences.Current.Glass.ShouldBe(kept);
        Store.Load().Glass.ShouldBe(kept);
        _looks.ShouldBeEmpty();
    }

    [Fact]
    public void Overlay_settings_are_saved_in_range()
    {
        var preferences = Preferences();

        preferences.SetOverlay(new OverlaySettings { Enabled = true, Position = OverlayPosition.Free, Left = 10, Top = 20, Opacity = 0.1 }).ShouldBeNull();

        var kept = new OverlaySettings { Enabled = true, Position = OverlayPosition.Free, Left = 10, Top = 20, Opacity = 0.55 };
        preferences.Current.Overlay.ShouldBe(kept);
        Store.Load().Overlay.ShouldBe(kept);
    }

    [Fact]
    public void The_update_preferences_are_saved()
    {
        var preferences = Preferences();
        preferences.Announced("0.3.0").ShouldBeNull();
        preferences.Ran("0.2.0").ShouldBeNull();

        var saved = Store.Load();
        saved.AnnouncedVersion.ShouldBe("0.3.0");
        saved.LastVersion.ShouldBe("0.2.0");
    }
}
