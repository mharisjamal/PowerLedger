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

    /// <summary>Midnight is the default since Aero became invite only (0.10.3): a new install opens in it, and a chosen
    /// Classic or Midnight survives a save and a load.</summary>
    [Theory]
    [InlineData("Classic")]
    [InlineData("Midnight")]
    public void The_look_is_midnight_until_chosen_and_a_chosen_look_survives_a_save_and_a_load(string chosen)
    {
        var store = new UiPreferencesStore(File);
        store.Load().Look.ShouldBe(Look.Midnight);
        UiPreferences.Default.Look.ShouldBe(Look.Midnight);
        UiPreferences.Default.AeroApproved.ShouldBeFalse();

        store.Save(UiPreferences.Default with { Look = Enum.Parse<Look>(chosen) });

        store.Load().Look.ShouldBe(Enum.Parse<Look>(chosen));
        System.IO.File.ReadAllText(File).ShouldContain($"\"{chosen}\"");
    }

    /// <summary>A PC updating from 0.7.x or earlier has a ui.json with no Look: it opens in Midnight and keeps the rest.</summary>
    [Fact]
    public void A_file_from_before_the_look_existed_opens_in_midnight_and_keeps_the_rest()
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, """{ "Theme": "Light", "FirstRunDone": true, "Co2KgPerKwh": 0.23 }""");

        var read = new UiPreferencesStore(File).Load();

        read.Look.ShouldBe(Look.Midnight);
        read.Theme.ShouldBe(ThemeChoice.Light);
        read.FirstRunDone.ShouldBeTrue();
        read.Co2KgPerKwh.ShouldBe(0.23);
    }

    /// <summary>0.10.3: every PC on Aero without the invite code goes to Midnight, on every load, without the new look's
    /// banner, and without Aero's intro; the rest of the file, the old Aero fields among it, still reads.</summary>
    [Theory]
    [InlineData("""{ "Theme": "Dark", "Look": "Aero", "LookIntroduced": false, "AeroIntroduced": true, "LookBeforeAero": "Classic", "AeroIntroSeen": true, "FirstRunDone": true }""")]
    [InlineData("""{ "Theme": "Dark", "Look": "Aero", "LookIntroduced": true, "AeroIntroduced": true, "AeroIntroSeen": false, "FirstRunDone": true }""")]
    [InlineData("""{ "Theme": "Dark", "Look": "Aero", "AeroApproved": false, "FirstRunDone": true }""")]
    [InlineData("""{ "Theme": "Dark", "Look": "Aero", "AeroApproved": "yes", "FirstRunDone": true }""")]
    [InlineData("""{ "Theme": "Dark", "Look": "aero", "AeroApproved": null, "FirstRunDone": true }""")]
    public void Aero_without_the_unlock_goes_to_midnight_on_load_without_a_banner(string json)
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, json);
        var store = new UiPreferencesStore(File);

        var read = store.Load();

        (read.Look, read.AeroApproved, read.LookIntroduced).ShouldBe((Look.Midnight, false, true));
        read.Theme.ShouldBe(ThemeChoice.Dark);
        read.FirstRunDone.ShouldBeTrue();
        store.Save(read with { Look = Look.Aero });   // a hand edit back to Aero is undone again at the next load
        store.Load().Look.ShouldBe(Look.Midnight);
    }

    /// <summary>0.10.7: 0.10.3's invite-code unlock, AeroUnlocked, is no longer read, so a PC on Aero by the old code
    /// opens in Midnight and asks again.</summary>
    [Fact]
    public void The_old_invite_unlock_is_ignored_and_aero_goes_to_midnight()
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, """{ "Look": "Aero", "AeroUnlocked": true, "LookIntroduced": true, "FirstRunDone": true }""");

        var read = new UiPreferencesStore(File).Load();

        (read.Look, read.AeroApproved, read.AeroRequestId).ShouldBe((Look.Midnight, false, (string?)null));
        read.FirstRunDone.ShouldBeTrue();
    }

    /// <summary>The request id is kept across a save and a load; anything that isn't one reads as none.</summary>
    [Theory]
    [InlineData("AERO-ABC12", "AERO-ABC12")]
    [InlineData("AERO-ABCDU", null)]
    [InlineData("aero-abc12", null)]
    [InlineData("", null)]
    public void The_aero_request_id_is_kept_when_it_is_one(string saved, string? expected)
    {
        Directory.CreateDirectory(_folder);
        var store = new UiPreferencesStore(File);
        store.Save(UiPreferences.Default with { AeroRequestId = saved });

        store.Load().AeroRequestId.ShouldBe(expected);
    }

    [Fact]
    public void Aero_with_the_unlock_stays_across_a_save_and_a_load()
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, """{ "Look": "Aero", "AeroApproved": true, "LookIntroduced": false, "FirstRunDone": true }""");
        var store = new UiPreferencesStore(File);

        var read = store.Load();

        (read.Look, read.AeroApproved, read.LookIntroduced).ShouldBe((Look.Aero, true, false));
        store.Save(read);
        System.IO.File.ReadAllText(File).ShouldContain("\"AeroApproved\": true");
        store.Load().ShouldBe(read);
    }

    /// <summary>Classic and Midnight are left as they are, locked or not, and nothing moves anyone to Aero any more.</summary>
    [Theory]
    [InlineData("Classic", true)]
    [InlineData("Midnight", true)]
    [InlineData("Midnight", false)]
    public void A_file_on_classic_or_midnight_keeps_its_look(string look, bool introduced)
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(
            File, $$"""{ "Theme": "Dark", "Look": "{{look}}", "LookIntroduced": {{(introduced ? "true" : "false")}}, "FirstRunDone": true }""");

        var read = new UiPreferencesStore(File).Load();

        read.Look.ShouldBe(Enum.Parse<Look>(look));
        read.LookIntroduced.ShouldBe(introduced);
        read.Theme.ShouldBe(ThemeChoice.Dark);
    }

    [Theory]
    [InlineData("Classic")]
    [InlineData("Midnight")]
    public void The_look_before_aero_survives_a_save_and_a_load(string look)
    {
        var store = new UiPreferencesStore(File);
        var saved = UiPreferences.Default with { LookBeforeAero = Enum.Parse<Look>(look) };

        store.Save(saved);

        store.Load().ShouldBe(saved);
    }

    [Theory]
    [InlineData("\"Neon\"")]
    [InlineData("2")]
    [InlineData("null")]
    public void A_look_this_version_does_not_know_reads_as_the_default_and_keeps_the_rest_of_the_file(string look)
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, $$"""{ "Theme": "Dark", "Look": {{look}}, "FirstRunDone": true, "AeroIntroduced": true }""");

        var read = new UiPreferencesStore(File).Load();

        read.Look.ShouldBe(Look.Midnight);
        read.Theme.ShouldBe(ThemeChoice.Dark);
        read.FirstRunDone.ShouldBeTrue();
    }

    [Fact]
    public void Glass_and_overlay_settings_are_the_designs_defaults_until_chosen()
    {
        var read = new UiPreferencesStore(File).Load();

        read.Glass.ShouldBe(GlassSettings.Default);
        read.Overlay.ShouldBe(OverlaySettings.Default);
        var glass = GlassSettings.Default;
        (glass.Style, glass.TintColor, glass.TintStrength, glass.Frost, glass.EdgeLight).ShouldBe((GlassStyle.Tinted, "#7466D8", 0.5, 0.6, 0.6));
        (glass.Accent, glass.Backdrop, glass.ReduceTransparency, glass.IncreaseContrast).ShouldBe((GlassAccent.Lime, GlassBackdrop.Desktop, false, false));
        (glass.ReduceMotion, glass.Parallax).ShouldBe(((bool?)null, true));
        var overlay = OverlaySettings.Default;
        (overlay.Enabled, overlay.Position, overlay.Left, overlay.Top, overlay.Opacity, overlay.Sparkline)
            .ShouldBe((false, OverlayPosition.TopRight, (double?)null, (double?)null, 1.0, true));
    }

    /// <summary>0.10.6, the owner's choice: every style shows what is really behind PowerLedger, so whatever backdrop an
    /// earlier version saved (Aero bloom, My desktop, Plain) moves to the live desktop as the file loads.</summary>
    [Theory]
    [InlineData("Desktop")]
    [InlineData("Wallpaper")]
    [InlineData("Plain")]
    [InlineData("Bloom")]
    [InlineData("Sideways")]
    public void Any_saved_backdrop_moves_to_the_live_desktop(string saved)
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, $$"""{ "Glass": { "Style": "Dark", "Backdrop": "{{saved}}" } }""");
        var glass = new UiPreferencesStore(File).Load().Glass;
        glass.Backdrop.ShouldBe(GlassBackdrop.Desktop);
        glass.Source.ShouldBe(GlassBackdrop.Desktop);
        glass.Style.ShouldBe(GlassStyle.Dark, "the rest of the glass is kept");
    }

    [Fact]
    public void Chosen_glass_and_overlay_settings_survive_a_save_and_a_load()
    {
        var store = new UiPreferencesStore(File);
        var saved = UiPreferences.Default with
        {
            Glass = new GlassSettings
            {
                Style = GlassStyle.Colour, TintColor = "#12AB9F", TintStrength = 0.8, Frost = 0.2, EdgeLight = 1, Accent = GlassAccent.Rose,
                ReduceTransparency = true, IncreaseContrast = true, ReduceMotion = false, Parallax = false,
            },
            Overlay = new OverlaySettings { Enabled = true, Position = OverlayPosition.Free, Left = -1200.5, Top = 40, Opacity = 0.7, Sparkline = false },
        };

        store.Save(saved);

        store.Load().ShouldBe(saved);
        var json = System.IO.File.ReadAllText(File);
        json.ShouldContain("\"Colour\"");
        json.ShouldContain("\"Desktop\"");
        json.ShouldContain("\"Free\"");
    }

    /// <summary>Every PC updating from 0.9.x has a ui.json with no glass or overlay: each takes its defaults, and the rest
    /// of the file is kept.</summary>
    [Fact]
    public void A_file_from_before_glass_settings_existed_takes_their_defaults_and_keeps_the_rest()
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, """{ "Theme": "Light", "Look": "Classic", "AeroIntroduced": true, "FirstRunDone": true }""");

        var read = new UiPreferencesStore(File).Load();

        read.Glass.ShouldBe(GlassSettings.Default);
        read.Overlay.ShouldBe(OverlaySettings.Default);
        (read.Theme, read.Look, read.FirstRunDone).ShouldBe((ThemeChoice.Light, Look.Classic, true));
    }

    /// <summary>A glass or overlay object missing some fields, or null, takes the defaults for what is missing.</summary>
    [Theory]
    [InlineData("""{ "Style": "Dark" }""", """{ "Enabled": true }""")]
    [InlineData("null", "null")]
    [InlineData("{}", "{}")]
    public void Missing_glass_and_overlay_fields_take_their_defaults(string glass, string overlay)
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, $$"""{ "Theme": "Dark", "AeroIntroduced": true, "Glass": {{glass}}, "Overlay": {{overlay}} }""");

        var read = new UiPreferencesStore(File).Load();

        read.Glass.ShouldBe(GlassSettings.Default with { Style = glass.Contains("Dark") ? GlassStyle.Dark : GlassStyle.Tinted });
        read.Overlay.ShouldBe(OverlaySettings.Default with { Enabled = overlay.Contains("true") });
        read.Theme.ShouldBe(ThemeChoice.Dark);
    }

    /// <summary>Names this version doesn't know, and numbers, read as each field's default rather than failing the file.</summary>
    [Theory]
    [InlineData("\"Frosted\"", "\"Violet\"", "\"Video\"", "\"Middle\"")]
    [InlineData("1", "3", "0", "2")]
    [InlineData("null", "null", "null", "null")]
    public void Glass_and_overlay_names_this_version_does_not_know_read_as_their_defaults(string style, string accent, string backdrop, string position)
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, $$"""
            { "Theme": "Dark", "AeroIntroduced": true,
              "Glass": { "Style": {{style}}, "Accent": {{accent}}, "Backdrop": {{backdrop}}, "Parallax": false },
              "Overlay": { "Position": {{position}}, "Enabled": true } }
            """);

        var read = new UiPreferencesStore(File).Load();

        read.Glass.ShouldBe(GlassSettings.Default with { Parallax = false });
        read.Overlay.ShouldBe(OverlaySettings.Default with { Enabled = true });
        read.Theme.ShouldBe(ThemeChoice.Dark);
    }

    [Theory]
    [InlineData("\"#7466d8\"", "#7466D8")]
    [InlineData("\"#12ab9F\"", "#12AB9F")]
    [InlineData("\"red\"", "#7466D8")]
    [InlineData("\"#12AB9\"", "#7466D8")]
    [InlineData("\"#12AB9FF0\"", "#7466D8")]
    [InlineData("\"12AB9F\"", "#7466D8")]
    [InlineData("\"#12AG9F\"", "#7466D8")]
    [InlineData("null", "#7466D8")]
    public void A_tint_colour_is_kept_as_hash_rrggbb_and_anything_else_reads_as_the_default(string written, string read)
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, $$"""{ "AeroIntroduced": true, "Glass": { "TintColor": {{written}} } }""");

        new UiPreferencesStore(File).Load().Glass.TintColor.ShouldBe(read);
    }

    /// <summary>Sliders out of their range are clamped to it, so a hand-edited 1.5 is full strength, not the default.</summary>
    [Theory]
    [InlineData(-0.5, 0.0, 0.55)]
    [InlineData(1.5, 1.0, 1.0)]
    [InlineData(0.3, 0.3, 0.55)]
    [InlineData(0.75, 0.75, 0.75)]
    public void Sliders_out_of_range_are_clamped(double written, double fraction, double opacity)
    {
        Directory.CreateDirectory(_folder);
        var number = written.ToString(System.Globalization.CultureInfo.InvariantCulture);
        System.IO.File.WriteAllText(File, $$"""
            { "AeroIntroduced": true,
              "Glass": { "TintStrength": {{number}}, "Frost": {{number}}, "EdgeLight": {{number}} },
              "Overlay": { "Opacity": {{number}} } }
            """);

        var read = new UiPreferencesStore(File).Load();

        (read.Glass.TintStrength, read.Glass.Frost, read.Glass.EdgeLight).ShouldBe((fraction, fraction, fraction));
        read.Overlay.Opacity.ShouldBe(opacity);
    }

    /// <summary>The Carbon insight reads Settings' one CO₂ factor (<see cref="UiPreferences.Co2KgPerKwh"/>); a glass
    /// carbon factor, which a build before that decision could have written, is ignored and the rest of the file kept.</summary>
    [Theory]
    [InlineData("233")]
    [InlineData("null")]
    [InlineData("\"by region\"")]
    public void A_glass_carbon_factor_from_an_earlier_build_is_ignored(string written)
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, $$"""{ "AeroIntroduced": true, "Co2KgPerKwh": 0.23, "Glass": { "Style": "Dark", "CarbonGramsPerKwh": {{written}} } }""");

        var read = new UiPreferencesStore(File).Load();

        read.Glass.ShouldBe(GlassSettings.Default with { Style = GlassStyle.Dark });
        read.Co2KgPerKwh.ShouldBe(0.23);
        typeof(GlassSettings).GetProperty("CarbonGramsPerKwh").ShouldBeNull();
    }

    [Fact]
    public void Glass_and_overlay_settings_out_of_range_in_memory_are_put_back_by_sanitising()
    {
        var wild = UiPreferences.Default with
        {
            Glass = new GlassSettings { Style = (GlassStyle)9, Accent = (GlassAccent)9, Backdrop = (GlassBackdrop)9, TintColor = "blue", Frost = double.NaN },
            Overlay = new OverlaySettings { Position = (OverlayPosition)9, Opacity = double.PositiveInfinity, Left = double.NaN, Top = double.NegativeInfinity },
        };

        var clean = wild.Sanitised();

        clean.Glass.ShouldBe(GlassSettings.Default);
        clean.Overlay.ShouldBe(OverlaySettings.Default);
        (UiPreferences.Default with { Glass = null!, Overlay = null! }).Sanitised().ShouldBe(UiPreferences.Default);
    }

    [Fact]
    public void The_energy_card_covers_everything_since_the_start_until_chosen_and_a_chosen_period_survives_a_save_and_a_load()
    {
        var store = new UiPreferencesStore(File);
        store.Load().EnergyPeriod.ShouldBe(EnergyPeriod.SinceStart);
        UiPreferences.Default.EnergyPeriod.ShouldBe(EnergyPeriod.SinceStart);

        store.Save(UiPreferences.Default with { EnergyPeriod = EnergyPeriod.ThisWeek });

        store.Load().EnergyPeriod.ShouldBe(EnergyPeriod.ThisWeek);
        System.IO.File.ReadAllText(File).ShouldContain("\"ThisWeek\"");
    }

    /// <summary>Every PC updating from 0.9.0 or earlier has a ui.json with no energy period: its card starts at the start.</summary>
    [Fact]
    public void A_file_from_before_the_energy_period_existed_covers_everything_since_the_start_and_keeps_the_rest()
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, """{ "Theme": "Light", "Look": "Classic", "AeroIntroduced": true, "FirstRunDone": true, "Co2KgPerKwh": 0.23 }""");

        var read = new UiPreferencesStore(File).Load();

        read.EnergyPeriod.ShouldBe(EnergyPeriod.SinceStart);
        read.Theme.ShouldBe(ThemeChoice.Light);
        read.Look.ShouldBe(Look.Classic);
        read.FirstRunDone.ShouldBeTrue();
        read.Co2KgPerKwh.ShouldBe(0.23);
    }

    [Theory]
    [InlineData("\"ThisYear\"")]
    [InlineData("7")]
    [InlineData("null")]
    public void An_energy_period_this_version_does_not_know_reads_as_since_the_start_and_keeps_the_rest_of_the_file(string period)
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, $$"""{ "Theme": "Dark", "EnergyPeriod": {{period}}, "FirstRunDone": true }""");

        var read = new UiPreferencesStore(File).Load();

        read.EnergyPeriod.ShouldBe(EnergyPeriod.SinceStart);
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

    /// <summary>Plan S intro: the Aero intro video autoplays once. A ui.json from before the field, or with anything but
    /// true for it (null, a string, a number), reads as not seen and keeps the rest of the file; seen survives a save and
    /// a load.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(""", "AeroIntroSeen": null""")]
    [InlineData(""", "AeroIntroSeen": "yes" """)]
    [InlineData(""", "AeroIntroSeen": 1""")]
    public void The_aero_intro_is_unseen_until_saved_seen_and_a_file_without_it_still_loads(string field)
    {
        UiPreferences.Default.AeroIntroSeen.ShouldBeFalse();
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, $$"""{ "Theme": "Dark", "FirstRunDone": true{{field}} }""");

        var read = new UiPreferencesStore(File).Load();

        read.AeroIntroSeen.ShouldBeFalse();
        read.Theme.ShouldBe(ThemeChoice.Dark);
        read.FirstRunDone.ShouldBeTrue();
        new UiPreferencesStore(File).Save(read with { AeroIntroSeen = true });
        new UiPreferencesStore(File).Load().AeroIntroSeen.ShouldBeTrue();
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
