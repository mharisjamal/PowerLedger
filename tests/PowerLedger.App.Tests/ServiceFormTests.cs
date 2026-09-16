using System.Globalization;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

public class ServiceFormTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeLink _link = new();

    public ServiceFormTests() => _link.Connect(true);

    private ServiceForm Form()
    {
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default);
        return form;
    }

    [Fact]
    public void Loading_fills_every_field()
    {
        var form = Form();
        form.IsLoaded.ShouldBeTrue();
        form.Chassis.ShouldBe(ChassisKind.Laptop);
        form.RamSticks.ShouldBe("1");
        form.PanelInches.ShouldBe("15.6");
        form.CpuTdp.ShouldBe("");
        form.IdleMinutes.ShouldBe("5");
        form.SampleInterval.ShouldBe("1");
        form.RawHours.ShouldBe("48");
        form.HistoryYears.ShouldBe("2");
    }

    [Fact]
    public async Task What_is_typed_goes_to_the_service_whole()
    {
        var form = Form();
        form.Chassis = ChassisKind.Desktop;
        form.PsuTier = PsuTier.Gold;
        form.RamSticks = "4";
        form.RamIsDdr5 = true;
        form.SsdCount = "2";
        form.HddCount = "1";
        form.FanCount = "5";
        form.PanelInches = "0";
        form.ExtrasWatts = "12.5";
        form.CpuTdp = "125";
        form.GpuTdp = " ";
        form.IdleMinutes = "10";
        form.SampleInterval = "2";
        form.RawHours = "72";
        form.HistoryYears = "5";

        (await form.SaveAsync()).ShouldBeTrue();

        var sent = (ServiceSettings)_link.Writes.Single();
        sent.Profile.ShouldBe(new MachineProfile
        {
            Chassis = ChassisKind.Desktop, PsuTier = PsuTier.Gold, RamSticks = 4, RamIsDdr5 = true, SsdCount = 2, HddCount = 1,
            FanCount = 5, DisplayDiagonalInches = 0, ExtrasWatts = 12.5, CpuTdpOverrideW = 125, GpuTdpOverrideW = null,
        });
        (sent.IdleThresholdSeconds, sent.SampleIntervalSeconds, sent.RawRetentionHours, sent.HistoryRetentionYears).ShouldBe((600, 2, 72, 5));
        form.Message.ShouldBe("Saved.");
    }

    [Theory]
    [InlineData(false)]   // no status read yet
    [InlineData(true)]    // a status that lists none
    public void While_no_monitor_is_listed_the_old_monitor_count_choice_and_figure_go_back_as_the_service_sent_them(bool statusRead)
    {
        // They no longer reach the model and the form doesn't show them; the service carries them over to the monitors it
        // detects, once, so until the form lists a monitor it must neither change them nor put them back to their defaults.
        var old = MachineProfile.DefaultDesktop with { ExternalMonitors = 2, IncludeMonitors = true, MonitorWatts = 30, Monitors = [] };
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with { Profile = old });
        if (statusRead) form.ShowMonitors([]);

        var profile = form.Read(out _).ShouldNotBeNull().Profile;

        (profile.ExternalMonitors, profile.IncludeMonitors, profile.MonitorWatts).ShouldBe((2, true, 30.0));
    }

    [Fact]
    public void Once_a_monitor_is_listed_the_choices_shown_replace_the_old_monitor_count_and_choice()
    {
        // A monitor counted at its own figure saves no choice, so with the old count kept the service would find nothing
        // chosen at its next start and carry the count over again, on top of what the user saved. The old figure no longer
        // reaches the model, and goes back as the service sent it.
        var old = MachineProfile.DefaultDesktop with { ExternalMonitors = 2, IncludeMonitors = true, MonitorWatts = 30, Monitors = [] };
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with { Profile = old });
        form.ShowMonitors([Statuses.Dell]);

        var profile = form.Read(out _).ShouldNotBeNull().Profile;

        (profile.ExternalMonitors, profile.IncludeMonitors, profile.MonitorWatts).ShouldBe((0, false, 30.0));
    }

    [Fact]
    public void Each_monitor_says_whether_its_brightness_was_read_and_what_it_draws_now()
    {
        var form = Form();

        form.ShowMonitors([Statuses.Dell, Statuses.Aoc, Statuses.Aoc with { Key = "AOC2402-2", Counted = false, WattsNow = 0 }]);

        form.Monitors.Select(m => (m.Brightness, m.Now)).ShouldBe(
        [
            ("brightness 60%, read from the monitor", "24.3 W now"),
            ("brightness unknown, assumed 75%", "13.4 W now"),
            ("brightness unknown, assumed 75%", "not counted"),
        ]);
    }

    [Fact]
    public void A_monitor_at_a_typed_figure_shows_its_brightness_without_assuming_one()
    {
        // The service takes a typed figure as it is, so no brightness is assumed for it; one that was read is still shown.
        var form = Form();

        form.ShowMonitors(
        [
            Statuses.Dell with { OnWatts = 30, Source = MonitorSource.Typed, WattsNow = 30 },
            Statuses.Aoc with { OnWatts = 30, Source = MonitorSource.Typed, WattsNow = 30 },
        ]);

        form.Monitors.Select(m => (m.Brightness, m.Now)).ShouldBe(
        [
            ("brightness 60%, read from the monitor", "30.0 W now"),
            ("brightness unknown", "30.0 W now"),
        ]);
    }

    [Fact]
    public void A_refresh_keeps_what_the_user_typed_and_ticked_and_shows_what_the_service_says_now()
    {
        var form = Form();
        form.ShowMonitors([Statuses.Dell, Statuses.Aoc]);
        var (dell, aoc) = (form.Monitors[0], form.Monitors[1]);
        aoc.Watts = "17";
        dell.Counted = false;

        form.ShowMonitors([Statuses.Dell with { Brightness = 0.8, WattsNow = 26.9 }, Statuses.Aoc with { Brightness = 0.5, WattsNow = 11.9 }]);

        form.Monitors.ShouldBe([dell, aoc]);   // the same rows, so a box being typed in keeps its place
        (aoc.Watts, dell.Counted).ShouldBe(("17", false));
        (dell.Brightness, dell.Now).ShouldBe(("brightness 80%, read from the monitor", "26.9 W now"));
        (aoc.Brightness, aoc.Now).ShouldBe(("brightness 50%, read from the monitor", "11.9 W now"));
    }

    [Fact]
    public void A_refresh_follows_the_services_figure_in_a_box_the_user_hasnt_touched()
    {
        // Just after a save that cleared a typed figure the service may still report it, and the box is left blank.
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with { Profile = MachineProfile.DefaultLaptop with { Monitors = [new MonitorChoice { Key = Statuses.Dell.Key }] } });
        form.ShowMonitors([Statuses.Dell with { OnWatts = 30, Source = MonitorSource.Typed }]);
        var row = form.Monitors.Single();
        (row.Watts, row.Source).ShouldBe(("", ""));

        form.ShowMonitors([Statuses.Dell]);

        (row.Watts, row.Source).ShouldBe(("26.9", "measured for this model"));
    }

    [Fact]
    public async Task A_figure_powerledger_worked_out_before_the_service_reported_a_typed_one_still_isnt_saved_as_typed()
    {
        var form = Form();
        form.ShowMonitors([Statuses.Dell]);
        form.ShowMonitors([Statuses.Dell with { OnWatts = 30, Source = MonitorSource.Typed }]);   // a figure typed in the wizard since
        form.Monitors.Single().Watts = "26.9";

        (await form.SaveAsync()).ShouldBeTrue();

        ((ServiceSettings)_link.Writes.Single()).Profile.Monitors.ShouldBeEmpty();   // counted at its own figure, which needs no choice
    }

    [Fact]
    public void A_refresh_adds_a_monitor_plugged_in_and_drops_one_unplugged_in_the_services_order()
    {
        var form = Form();
        form.ShowMonitors([Statuses.Dell]);
        var dell = form.Monitors.Single();
        dell.Watts = "22";

        form.ShowMonitors([Statuses.Aoc, Statuses.Dell]);
        form.Monitors.Select(m => m.Key).ShouldBe([Statuses.Aoc.Key, Statuses.Dell.Key]);
        form.Monitors[1].ShouldBeSameAs(dell);
        dell.Watts.ShouldBe("22");

        form.ShowMonitors([Statuses.Aoc]);
        form.Monitors.Select(m => m.Key).ShouldBe([Statuses.Aoc.Key]);
        form.HasMonitors.ShouldBeTrue();

        form.ShowMonitors([]);
        form.HasMonitors.ShouldBeFalse();
    }

    [Theory]
    [InlineData("RamSticks", "two", "Type the memory sticks as a whole number.")]
    [InlineData("IdleMinutes", "45", "The idle threshold is between 1 and 30 minutes.")]
    [InlineData("RawHours", "12", "Second-by-second history must be kept between 24 and 168 hours.")]
    [InlineData("PanelInches", "80", "The panel size must be 0 for none, or between 7 and 50 inches.")]
    public async Task What_cannot_be_sent_is_said(string field, string typed, string message)
    {
        var form = Form();
        typeof(ServiceForm).GetProperty(field)!.SetValue(form, typed);
        (await form.SaveAsync()).ShouldBeFalse();
        form.Message.ShouldBe(message);
        _link.Writes.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_save_keeps_the_choices_for_monitors_not_attached_now()
    {
        var unplugged = new MonitorChoice { Key = "GSM5B09-104NTAB2C123", Counted = false, Watts = 41 };
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with
        {
            Profile = MachineProfile.DefaultLaptop with { Monitors = [new MonitorChoice { Key = Statuses.Dell.Key, Counted = false }, unplugged] },
        });
        (await form.SaveAsync()).ShouldBeTrue();   // no monitors shown at all

        form.ShowMonitors([Statuses.Dell]);
        form.Monitors.Single().Counted.ShouldBeFalse();
        form.Monitors.Single().Counted = true;
        (await form.SaveAsync()).ShouldBeTrue();

        var sent = _link.Writes.Cast<ServiceSettings>().ToList();
        sent.Count.ShouldBe(2);
        sent[0].Profile.Monitors.ShouldBe([new MonitorChoice { Key = Statuses.Dell.Key, Counted = false }, unplugged]);
        sent[1].Profile.Monitors.ShouldBe([unplugged]);   // counted again, the Dell needs no choice, and the one loaded for it goes
    }

    [Fact]
    public void A_monitor_without_a_choice_is_counted_and_plugged_in_as_the_service_would_take_it()
    {
        // Settings from before monitors were detected may have left monitors out, and a small monitor on a laptop is taken
        // for a portable one that runs off it.
        var form = Form();

        form.ShowMonitors([Statuses.Dell, Statuses.Aoc with { CountedByDefault = false, Counted = false }, Statuses.Portable]);

        form.Monitors.Select(m => (m.Counted, m.OwnPlug)).ShouldBe([(true, true), (false, true), (true, false)]);
    }

    [Fact]
    public void A_monitors_choice_wins_over_what_the_service_would_take()
    {
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with
        {
            Profile = MachineProfile.DefaultLaptop with
            {
                Monitors =
                [
                    new MonitorChoice { Key = Statuses.Dell.Key, Counted = true, OwnPlug = false },
                    new MonitorChoice { Key = Statuses.Portable.Key, Counted = false, OwnPlug = true },
                ],
            },
        });

        form.ShowMonitors([Statuses.Dell with { CountedByDefault = false, OwnPlug = false }, Statuses.Portable with { Counted = false, OwnPlug = true }]);

        form.Monitors.Select(m => (m.Counted, m.OwnPlug)).ShouldBe([(true, false), (false, true)]);
    }

    [Fact]
    public async Task A_monitor_counted_where_the_service_wouldnt_count_it_or_left_out_where_it_would_saves_a_choice()
    {
        var form = Form();
        form.ShowMonitors(
        [
            Statuses.Dell,
            Statuses.Aoc,
            Statuses.Dell with { Key = "DELA0B1-2", CountedByDefault = false, Counted = false },
            Statuses.Aoc with { Key = "AOC2402-2", CountedByDefault = false, Counted = false },
        ]);
        form.Monitors[1].Counted = false;
        form.Monitors[3].Counted = true;

        (await form.SaveAsync()).ShouldBeTrue();

        ((ServiceSettings)_link.Writes.Single()).Profile.Monitors.ShouldBe(
        [
            new MonitorChoice { Key = Statuses.Aoc.Key, Counted = false },
            new MonitorChoice { Key = "AOC2402-2", Counted = true },
        ]);
    }

    [Fact]
    public async Task A_plug_other_than_the_one_the_service_takes_saves_a_choice_that_says_so()
    {
        var form = Form();
        form.ShowMonitors([Statuses.Dell, Statuses.Portable, Statuses.Portable with { Key = "AUS1601-2" }]);
        form.Monitors[0].OwnPlug = false;   // it runs off the laptop after all
        form.Monitors[1].OwnPlug = true;    // a portable monitor with a plug of its own

        (await form.SaveAsync()).ShouldBeTrue();

        ((ServiceSettings)_link.Writes.Single()).Profile.Monitors.ShouldBe(
        [
            new MonitorChoice { Key = Statuses.Dell.Key, OwnPlug = false },
            new MonitorChoice { Key = Statuses.Portable.Key, OwnPlug = true },
        ]);
    }

    [Fact]
    public async Task A_refresh_keeps_what_the_user_ticked_and_a_box_they_havent_ticked_follows_what_the_service_would_take()
    {
        var form = Form();
        var other = Statuses.Portable with { Key = "AUS1601-2" };
        form.ShowMonitors([Statuses.Portable, other]);
        var (ticked, untouched) = (form.Monitors[0], form.Monitors[1]);
        ticked.OwnPlug = true;
        ticked.Counted = false;
        ticked.Counted = true;   // and back again, which is still the user's

        // Say the chassis was saved as a desktop elsewhere, and the service now leaves monitors out unless told.
        form.ShowMonitors(
        [
            Statuses.Portable with { CountedByDefault = false, OwnPlugByDefault = true, OwnPlug = true },
            other with { CountedByDefault = false, Counted = false, OwnPlugByDefault = true, OwnPlug = true },
        ]);

        (ticked.Counted, ticked.OwnPlug).ShouldBe((true, true));
        (untouched.Counted, untouched.OwnPlug).ShouldBe((false, true));
        (await form.SaveAsync()).ShouldBeTrue();
        // Each choice says only what differs from what the service would take now.
        ((ServiceSettings)_link.Writes.Single()).Profile.Monitors.ShouldBe([new MonitorChoice { Key = Statuses.Portable.Key, Counted = true }]);
    }

    [Fact]
    public async Task Where_monitors_are_left_out_unless_chosen_a_choice_to_count_one_not_attached_now_is_kept_and_one_to_leave_it_out_goes()
    {
        MonitorChoice[] loaded =
        [
            new MonitorChoice { Key = "LEN66F2-V906LMHT", Counted = false },   // no more than monitors are left out by default
            new MonitorChoice { Key = "SAM0F9E-HNTW700123" },
        ];
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with { Profile = MachineProfile.DefaultLaptop with { Monitors = loaded, CountMonitorsByDefault = false } });
        form.ShowMonitors([Statuses.Dell with { CountedByDefault = false, Counted = false }]);

        (await form.SaveAsync()).ShouldBeTrue();

        var profile = ((ServiceSettings)_link.Writes.Single()).Profile;
        profile.Monitors.ShouldBe([loaded[1]]);
        profile.CountMonitorsByDefault.ShouldBeFalse();   // as the service sent it
    }

    [Fact]
    public async Task A_monitors_choice_is_saved_when_it_is_left_out_or_has_a_figure_typed()
    {
        // A monitor with no choice counts by default, at PowerLedger's own figure, so a choice that says no more needs no entry.
        var form = Form();
        var third = Statuses.Aoc with { Key = "AOC2402-2" };
        form.ShowMonitors([Statuses.Dell, Statuses.Aoc, third]);
        form.Monitors[1].Counted = false;
        form.Monitors[2].Watts = "17";

        (await form.SaveAsync()).ShouldBeTrue();

        ((ServiceSettings)_link.Writes.Single()).Profile.Monitors.ShouldBe(
        [
            new MonitorChoice { Key = Statuses.Aoc.Key, Counted = false },
            new MonitorChoice { Key = third.Key, Watts = 17 },
        ]);
    }

    [Fact]
    public async Task Of_the_choices_for_monitors_not_attached_now_only_those_that_say_something_are_kept_in_the_order_loaded()
    {
        MonitorChoice[] loaded =
        [
            new MonitorChoice { Key = "LEN66F2-V906LMHT", Counted = false },
            new MonitorChoice { Key = "SAM0F9E-HNTW700123" },   // counted at its own figure, as carrying the old settings over can leave one
            new MonitorChoice { Key = "GSM5B09-104NTAB2C123", Watts = 41 },
            new MonitorChoice { Key = Statuses.Portable.Key, OwnPlug = true },
        ];
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with { Profile = MachineProfile.DefaultLaptop with { Monitors = loaded } });
        form.ShowMonitors([Statuses.Dell]);

        (await form.SaveAsync()).ShouldBeTrue();

        ((ServiceSettings)_link.Writes.Single()).Profile.Monitors.ShouldBe([loaded[0], loaded[2], loaded[3]]);
    }

    [Fact]
    public void A_save_puts_the_monitors_attached_first_and_drops_the_choices_for_those_unseen_longest_beyond_the_limit()
    {
        // Each save puts the monitors attached first, so the choices loaded run from the monitor seen most recently.
        MonitorChoice[] unplugged = [.. Enumerable.Range(0, 20).Select(i => new MonitorChoice { Key = $"GSM5B09-{i}", Counted = false })];
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with { Profile = MachineProfile.DefaultLaptop with { Monitors = unplugged } });
        form.ShowMonitors([Statuses.Dell, Statuses.Aoc]);
        form.Monitors[0].Counted = false;
        form.Monitors[1].Watts = "17";

        var settings = form.Read(out var problem);

        problem.ShouldBeNull();
        settings.ShouldNotBeNull().Profile.Monitors.ShouldBe(
        [
            new MonitorChoice { Key = Statuses.Dell.Key, Counted = false },
            new MonitorChoice { Key = Statuses.Aoc.Key, Watts = 17 },
            .. unplugged.Take(14),
        ]);
    }

    [Fact]
    public async Task Settings_from_a_service_that_keeps_no_monitor_choices_save_none()
    {
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with { Profile = MachineProfile.DefaultLaptop with { Monitors = null! } });
        form.ShowMonitors([]);

        (await form.SaveAsync()).ShouldBeTrue();

        ((ServiceSettings)_link.Writes.Single()).Profile.Monitors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("en-US", "26.9", null)]      // the figure shown, left as it was
    [InlineData("en-US", " 26.90 ", null)]   // the same figure, typed differently
    [InlineData("en-US", "", null)]          // blank: PowerLedger's own figure
    [InlineData("en-US", "27", 27.0)]
    [InlineData("en-US", "0", 0.0)]
    [InlineData("de-DE", "26,9", null)]
    [InlineData("de-DE", "26.9", null)]      // a point is read too, as in every number the form takes
    [InlineData("de-DE", "31,5", 31.5)]
    public async Task Only_a_figure_other_than_the_one_powerledger_worked_out_is_saved_as_typed(string culture, string typed, double? saved)
    {
        var form = new ServiceForm(_link, UiThreads.Inline, CultureInfo.GetCultureInfo(culture));
        form.Load(ServiceSettings.Default);
        form.ShowMonitors([Statuses.Dell with { OnWatts = 26.94 }]);
        form.Monitors.Single().Watts.ShouldBe(26.9.ToString(CultureInfo.GetCultureInfo(culture)));

        form.Monitors.Single().Watts = typed;
        (await form.SaveAsync()).ShouldBeTrue();

        // Without a figure of its own, the counted monitor needs no choice.
        ((ServiceSettings)_link.Writes.Single()).Profile.Monitors.Select(m => m.Watts).ShouldBe(saved is null ? [] : [saved]);
    }

    [Fact]
    public async Task A_typed_figure_stays_typed_until_it_is_cleared()
    {
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with
        {
            Profile = MachineProfile.DefaultLaptop with { Monitors = [new MonitorChoice { Key = Statuses.Dell.Key, Watts = 30 }] },
        });
        form.ShowMonitors([Statuses.Dell with { OnWatts = 30, Source = MonitorSource.Typed }]);
        var row = form.Monitors.Single();
        (row.Watts, row.Source).ShouldBe(("30", "typed"));

        (await form.SaveAsync()).ShouldBeTrue();
        row.Watts = "";
        (await form.SaveAsync()).ShouldBeTrue();

        var sent = _link.Writes.Cast<ServiceSettings>().ToList();
        sent[0].Profile.Monitors.ShouldBe([new MonitorChoice { Key = Statuses.Dell.Key, Watts = 30 }]);
        sent[1].Profile.Monitors.ShouldBeEmpty();   // cleared, it counts at PowerLedger's own figure, which needs no choice
    }

    [Fact]
    public void A_status_older_than_the_choices_doesnt_pass_a_typed_figure_off_as_powerledgers()
    {
        // Just after a save that cleared a typed figure, the service may still report it: the box stays blank, for
        // PowerLedger's figure, rather than filling with the old one, which a second save would type back in.
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with { Profile = MachineProfile.DefaultLaptop with { Monitors = [new MonitorChoice { Key = Statuses.Dell.Key }] } });

        form.ShowMonitors([Statuses.Dell with { OnWatts = 30, Source = MonitorSource.Typed }]);

        (form.Monitors.Single().Watts, form.Monitors.Single().Source).ShouldBe(("", ""));
    }

    [Theory]
    [InlineData("lots", "Type the watts for 24B1XH5 as a number.")]
    [InlineData("-3", "Type the watts for 24B1XH5 as a number.")]
    [InlineData("900", "A monitor must draw between 0 and 500 W.")]
    public async Task A_monitors_figure_that_cannot_be_sent_is_said(string typed, string message)
    {
        var form = Form();
        form.ShowMonitors([Statuses.Dell, Statuses.Aoc]);
        form.Monitors[1].Watts = typed;

        (await form.SaveAsync()).ShouldBeFalse();

        form.Message.ShouldBe(message);
        _link.Writes.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(23.8, 1920, 1080, "estimated from its size — correct it if you know better")]
    [InlineData(0, 1920, 1080, "estimated — correct it if you know better")]   // without a size, the median of all monitors
    [InlineData(23.8, 0, 0, "estimated — correct it if you know better")]      // and without a resolution
    [InlineData(23.8, 1920, 0, "estimated — correct it if you know better")]
    public void An_estimate_says_it_came_from_the_monitors_size_only_when_the_service_had_one(double inches, int width, int height, string source)
    {
        var form = Form();

        form.ShowMonitors([Statuses.Aoc with { Inches = inches, Width = width, Height = height }]);

        form.Monitors.Single().Source.ShouldBe(source);
    }

    [Fact]
    public void Monitors_are_listed_once_each_and_without_size_or_resolution_when_the_service_has_none()
    {
        var form = Form();
        var bare = new MonitorStatus { Key = "AOC2402-0", Instance = @"DISPLAY\AOC2402\1", Name = "", OnWatts = 14.3, Source = MonitorSource.Estimate };

        form.ShowMonitors([bare, bare with { Name = "AOC 2402" }, Statuses.Dell with { Width = 0, Height = 0 }]);

        form.Monitors.Select(m => (m.Name, m.Size)).ShouldBe([("External monitor", ""), ("DELL U2723QE", "27 in")]);
    }

    [Fact]
    public void Loading_the_settings_again_lists_the_monitors_afresh()
    {
        var form = Form();
        form.ShowMonitors([Statuses.Dell]);
        form.HasMonitors.ShouldBeTrue();

        form.Load(ServiceSettings.Default);

        form.HasMonitors.ShouldBeFalse();
        form.Monitors.ShouldBeEmpty();
    }

    [Fact]
    public void A_form_the_service_never_filled_cannot_be_saved()
    {
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.IsLoaded.ShouldBeFalse();
        form.Save.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void Choosing_desktop_shows_the_power_supply()
    {
        var form = Form();
        form.IsDesktop.ShouldBeFalse();
        form.Chassis = ChassisKind.Desktop;
        form.IsDesktop.ShouldBeTrue();
    }
}
