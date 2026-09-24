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

    /// <summary>A form that saves itself, as Settings' does. When <paramref name="answers"/> is given, what the service's
    /// answers leave for the UI thread waits there, as it does while a save is on its way, until the test runs it.</summary>
    private ServiceForm SavingItself(Queue<Action>? answers = null)
    {
        var threads = answers is null ? UiThreads.Inline : new UiThreads(answers.Enqueue, action => action());
        var form = new ServiceForm(_link, threads, English, savesItself: true);
        form.Load(ServiceSettings.Default);
        return form;
    }

    /// <summary>Runs what the service's answers left for the UI thread, and whatever that leaves in turn.</summary>
    private static void Answer(Queue<Action> answers)
    {
        while (answers.TryDequeue(out var next)) next();
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
        form.UpsLoad.ShouldBe(UpsLoad.NotSaid);
        form.ReadPowerSupply.ShouldBeTrue();
    }

    [Fact]
    public void Each_ups_and_power_supply_the_service_reads_is_listed_with_its_watts_and_how_they_were_found()
    {
        var form = SavingItself();

        form.ShowPowerDevices([Statuses.Ups, Statuses.PowerSupply]);

        form.Upses.ShouldBe(["UPS · APC Back-UPS ES 850G2 · 142 W (load of its rated watts)"]);
        form.PowerSupplies.ShouldBe(["Power supply · Corsair HX1000i · 312 W (DC output, all rails)"]);
        (form.HasUps, form.HasPowerSupply, form.AsksToReadPowerSupply).ShouldBe((true, true, true));
        _link.Writes.ShouldBeEmpty();                                 // the form filled itself; the user changed nothing
    }

    [Fact]
    public void A_device_read_no_watts_yet_or_named_by_nothing_is_listed_with_what_is_known_of_it()
    {
        var form = Form();

        form.ShowPowerDevices([Statuses.Ups with { Watts = null, How = "" }, Statuses.PowerSupply with { Name = "" }]);

        form.Upses.ShouldBe(["UPS · APC Back-UPS ES 850G2 · not read yet"]);
        form.PowerSupplies.ShouldBe(["Power supply · 312 W (DC output, all rails)"]);
    }

    [Fact]
    public void Nothing_detected_is_listed_as_nothing_and_asks_nothing()
    {
        var form = Form();
        form.ShowPowerDevices([Statuses.Ups, Statuses.PowerSupply]);

        form.ShowPowerDevices([]);

        form.Upses.ShouldBeEmpty();
        form.PowerSupplies.ShouldBeEmpty();
        (form.HasUps, form.HasPowerSupply, form.AsksToReadPowerSupply).ShouldBe((false, false, false));
    }

    [Fact]
    public void The_power_supply_tick_stays_while_reading_one_is_off_so_it_can_be_turned_on_again()
    {
        // Reading it off, the service leaves the power supply to its maker's program, and may stop naming it. The tick has to
        // stay, or it could never be ticked again.
        var form = SavingItself();
        form.ShowPowerDevices([Statuses.PowerSupply]);

        form.ReadPowerSupply = false;
        form.ShowPowerDevices([]);

        (form.HasPowerSupply, form.AsksToReadPowerSupply).ShouldBe((false, true));
        ((ServiceSettings)_link.Writes.Single()).Profile.ReadPowerSupply.ShouldBeFalse();
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
        form.UpsLoad = UpsLoad.ThisPcAndMonitors;
        form.ReadPowerSupply = false;

        (await form.SaveAsync()).ShouldBeTrue();

        var sent = (ServiceSettings)_link.Writes.Single();
        sent.Profile.ShouldBe(new MachineProfile
        {
            Chassis = ChassisKind.Desktop, PsuTier = PsuTier.Gold, RamSticks = 4, RamIsDdr5 = true, SsdCount = 2, HddCount = 1,
            FanCount = 5, DisplayDiagonalInches = 0, ExtrasWatts = 12.5, CpuTdpOverrideW = 125, GpuTdpOverrideW = null,
            UpsLoad = UpsLoad.ThisPcAndMonitors, ReadPowerSupply = false,
        });
        (sent.IdleThresholdSeconds, sent.SampleIntervalSeconds, sent.RawRetentionHours, sent.HistoryRetentionYears).ShouldBe((600, 2, 72, 5));
        form.Message.ShouldBe("Saved.");
    }

    [Theory]
    [InlineData(nameof(ServiceForm.RamIsDdr5), true)]
    [InlineData(nameof(ServiceForm.Chassis), ChassisKind.Desktop)]
    [InlineData(nameof(ServiceForm.PsuTier), PsuTier.Gold)]
    [InlineData(nameof(ServiceForm.SampleInterval), "3")]
    [InlineData(nameof(ServiceForm.UpsLoad), UpsLoad.ThisPc)]
    [InlineData(nameof(ServiceForm.ReadPowerSupply), false)]
    public void A_form_that_saves_itself_sends_a_tick_or_a_segmented_choice_at_once_and_once(string field, object chosen)
    {
        var form = SavingItself();

        typeof(ServiceForm).GetProperty(field)!.SetValue(form, chosen);

        _link.Writes.Single().ShouldBe(form.Read(out _));
        _link.Writes.Single().ShouldNotBe(ServiceSettings.Default);
        form.Message.ShouldBe("Saved.");
    }

    [Theory]
    [InlineData(nameof(ServiceForm.RamSticks), "4")]
    [InlineData(nameof(ServiceForm.SsdCount), "2")]
    [InlineData(nameof(ServiceForm.HddCount), "1")]
    [InlineData(nameof(ServiceForm.FanCount), "5")]
    [InlineData(nameof(ServiceForm.PanelInches), "14")]
    [InlineData(nameof(ServiceForm.ExtrasWatts), "12.5")]
    [InlineData(nameof(ServiceForm.CpuTdp), "125")]
    [InlineData(nameof(ServiceForm.GpuTdp), "80")]
    [InlineData(nameof(ServiceForm.IdleMinutes), "10")]
    [InlineData(nameof(ServiceForm.RawHours), "72")]
    [InlineData(nameof(ServiceForm.HistoryYears), "5")]
    public void A_typed_value_saves_once_its_box_gives_it_to_the_form(string field, string typed)
    {
        // The view gives a typed value to the form when its box loses focus or Enter is pressed, not at each keystroke.
        var form = SavingItself();

        typeof(ServiceForm).GetProperty(field)!.SetValue(form, typed);

        _link.Writes.Single().ShouldBe(form.Read(out _));
        _link.Writes.Single().ShouldNotBe(ServiceSettings.Default);
        form.Message.ShouldBe("Saved.");
    }

    [Fact]
    public void A_monitors_ticks_and_figure_save_at_once_and_once_each()
    {
        var form = SavingItself();
        form.ShowMonitors([Statuses.Dell, Statuses.Aoc]);
        var (dell, aoc) = (form.Monitors[0], form.Monitors[1]);

        aoc.Counted = false;
        dell.OwnPlug = false;
        aoc.Watts = "17";

        _link.Writes.Cast<ServiceSettings>().Select(sent => sent.Profile).ShouldBe(
        [
            MachineProfile.DefaultLaptop with { Monitors = [new MonitorChoice { Key = Statuses.Aoc.Key, Counted = false }] },
            MachineProfile.DefaultLaptop with
            {
                Monitors = [new MonitorChoice { Key = Statuses.Dell.Key, OwnPlug = false }, new MonitorChoice { Key = Statuses.Aoc.Key, Counted = false }],
            },
            MachineProfile.DefaultLaptop with
            {
                Monitors = [new MonitorChoice { Key = Statuses.Dell.Key, OwnPlug = false }, new MonitorChoice { Key = Statuses.Aoc.Key, Counted = false, Watts = 17 }],
            },
        ]);
    }

    [Theory]
    [InlineData(nameof(ServiceForm.FanCount), "two", "Type the fans as a whole number.")]
    [InlineData(nameof(ServiceForm.IdleMinutes), "45", "The idle threshold is between 1 and 30 minutes.")]
    [InlineData(nameof(ServiceForm.PanelInches), "80", "The panel size must be 0 for none, or between 7 and 50 inches.")]
    public void A_value_that_cannot_be_sent_is_said_stays_in_its_box_and_sends_nothing_until_it_is_put_right(string field, string typed, string problem)
    {
        var form = SavingItself();
        var property = typeof(ServiceForm).GetProperty(field)!;
        var loaded = property.GetValue(form);

        property.SetValue(form, typed);

        form.Message.ShouldBe(problem);
        property.GetValue(form).ShouldBe(typed);
        _link.Writes.ShouldBeEmpty();

        property.SetValue(form, loaded);
        _link.Writes.Single().ShouldBe(ServiceSettings.Default);
        form.Message.ShouldBe("Saved.");
    }

    [Fact]
    public void A_monitors_figure_that_cannot_be_sent_is_said_and_sends_nothing()
    {
        var form = SavingItself();
        form.ShowMonitors([Statuses.Dell, Statuses.Aoc]);

        form.Monitors[1].Watts = "lots";

        form.Message.ShouldBe("Type the watts for 24B1XH5 as a number.");
        form.Monitors[1].Watts.ShouldBe("lots");
        _link.Writes.ShouldBeEmpty();
    }

    [Fact]
    public void Loading_the_settings_and_what_the_service_says_of_the_monitors_since_send_nothing()
    {
        // The service's figure moves a box the user hasn't typed in, and its defaults and choices move boxes they haven't
        // ticked, but only what the user changes is sent.
        var form = SavingItself();
        form.ShowMonitors([Statuses.Dell, Statuses.Aoc]);
        form.ShowMonitors([Statuses.Dell with { OnWatts = 28.1 }, Statuses.Portable, Statuses.Aoc with { CountedByDefault = false, Counted = false }]);
        form.Monitors[0].Watts.ShouldBe("28.1");

        form.Load(ServiceSettings.Default with
        {
            Profile = MachineProfile.DefaultDesktop with { Monitors = [new MonitorChoice { Key = Statuses.Dell.Key, Counted = false, Watts = 30 }] },
            IdleThresholdSeconds = 600,
        });
        form.ShowMonitors([Statuses.Dell with { OnWatts = 30, Source = MonitorSource.Typed, Counted = false }]);

        _link.Writes.ShouldBeEmpty();
        form.Message.ShouldBeNull();
    }

    [Fact]
    public void A_save_that_reads_the_settings_again_sends_once()
    {
        // Reading them again puts the figure carried over in the box the user didn't type in, which isn't the user's change.
        var old = MachineProfile.DefaultLaptop with { ExternalMonitors = 1, IncludeMonitors = true, MonitorWatts = 30, Monitors = [] };
        var form = new ServiceForm(_link, UiThreads.Inline, English, savesItself: true);
        form.Load(ServiceSettings.Default with { Profile = old });
        form.ShowMonitors([Statuses.Dell, Statuses.Aoc]);
        var carried = old with
        {
            ExternalMonitors = 0, IncludeMonitors = false,
            Monitors = [.. new[] { Statuses.Dell.Key, Statuses.Aoc.Key }.Select(key => new MonitorChoice { Key = key, Watts = 30 })],
        };
        _link.Settings = ServiceSettings.Default with { Profile = carried };

        form.Monitors[1].Counted = false;

        ((ServiceSettings)_link.Writes.Single()).Profile.Monitors.ShouldBe(
        [
            new MonitorChoice { Key = Statuses.Dell.Key, Watts = 30 },
            new MonitorChoice { Key = Statuses.Aoc.Key, Counted = false, Watts = 30 },
        ]);
        form.Monitors[0].Watts.ShouldBe("30");
    }

    [Fact]
    public void Changes_made_while_a_save_is_on_its_way_are_sent_together_after_it_and_the_last_one_wins()
    {
        var answers = new Queue<Action>();
        var form = SavingItself(answers);

        form.FanCount = "2";
        form.FanCount = "3";
        form.RamIsDdr5 = true;
        form.FanCount = "4";

        ((ServiceSettings)_link.Writes.Single()).Profile.FanCount.ShouldBe(2);   // the rest wait for its answer
        form.Message.ShouldBe("Saving…");

        Answer(answers);

        _link.Writes.Cast<ServiceSettings>().Select(sent => (sent.Profile.FanCount, sent.Profile.RamIsDdr5)).ShouldBe([(2, false), (4, true)]);
        form.Message.ShouldBe("Saved.");
    }

    [Fact]
    public async Task A_form_that_doesnt_save_itself_sends_nothing_until_it_is_saved()
    {
        // The wizard's form, which saves when Next is pressed.
        var form = Form();
        form.ShowMonitors([Statuses.Dell]);

        form.FanCount = "4";
        form.RamIsDdr5 = true;
        form.Monitors.Single().Counted = false;

        _link.Writes.ShouldBeEmpty();
        (await form.SaveAsync()).ShouldBeTrue();
        _link.Writes.Count.ShouldBe(1);
    }

    [Fact]
    public void A_choice_saved_for_a_monitor_is_kept_once_the_monitor_is_unplugged_and_shown_when_it_is_plugged_in_again()
    {
        // Nothing fills the form again after a save, so the choices it sent are the ones it keeps for monitors not attached.
        var form = SavingItself();
        form.ShowMonitors([Statuses.Dell, Statuses.Aoc]);
        form.Monitors[1].Counted = false;
        form.ShowMonitors([Statuses.Dell]);

        form.FanCount = "2";

        _link.Writes.Count.ShouldBe(2);
        ((ServiceSettings)_link.Writes[1]).Profile.Monitors.ShouldBe([new MonitorChoice { Key = Statuses.Aoc.Key, Counted = false }]);
        form.ShowMonitors([Statuses.Dell, Statuses.Aoc]);
        form.Monitors[1].Counted.ShouldBeFalse();
    }

    [Fact]
    public void A_save_takes_the_settings_it_sent_as_the_services_so_the_next_save_has_nothing_to_read_again()
    {
        // The settings loaded still hold a monitor count from before monitors were detected, so the first save reads the
        // service's settings again before it sends. What it sends clears the count, and the service holds that from then on,
        // so the next save has nothing to catch up with, and doesn't wait on a read the service may not answer.
        var old = MachineProfile.DefaultLaptop with { ExternalMonitors = 2, Monitors = [] };
        _link.Settings = ServiceSettings.Default with { Profile = old };
        var form = new ServiceForm(_link, UiThreads.Inline, English, savesItself: true);
        form.Load(_link.Settings);
        form.ShowMonitors([Statuses.Dell]);

        form.IdleMinutes = "10";
        _link.Settings = null;
        form.FanCount = "2";

        _link.Writes.Cast<ServiceSettings>().Select(sent => (sent.Profile.ExternalMonitors, sent.IdleThresholdSeconds, sent.Profile.FanCount))
            .ShouldBe([(0, 600, 1), (0, 600, 2)]);
        form.Message.ShouldBe("Saved.");
    }

    [Fact]
    public void After_a_save_that_corrected_the_chassis_the_next_save_still_says_each_plug()
    {
        // The form isn't filled again after a save but takes what it sent as the service's settings, while the status is still
        // from the service's last reading, which took each plug for the chassis before. Weighed against those plugs alone, the
        // plugs said would say nothing, and the portable monitor shown with a plug of its own would be taken to run off the laptop.
        var form = new ServiceForm(_link, UiThreads.Inline, English, savesItself: true);
        form.Load(ServiceSettings.Default with { Profile = MachineProfile.DefaultDesktop });
        form.ShowMonitors([Statuses.Dell, Statuses.Portable with { OwnPlug = true, OwnPlugByDefault = true }]);

        form.Chassis = ChassisKind.Laptop;
        form.FanCount = "2";

        _link.Writes.Count.ShouldBe(2);
        ((ServiceSettings)_link.Writes[1]).Profile.Monitors.ShouldBe(
        [
            new MonitorChoice { Key = Statuses.Dell.Key, OwnPlug = true },
            new MonitorChoice { Key = Statuses.Portable.Key, OwnPlug = true },
        ]);
    }

    [Fact]
    public void A_figure_put_back_while_a_save_is_on_its_way_stays_in_its_box_and_is_sent_after_it()
    {
        var answers = new Queue<Action>();
        var form = SavingItself(answers);
        form.ShowMonitors([Statuses.Dell]);
        var dell = form.Monitors.Single();

        dell.Watts = "22";
        dell.Watts = "26.9";   // PowerLedger's own figure again, while the save of 22 is on its way
        Answer(answers);

        dell.Watts.ShouldBe("26.9");
        _link.Writes.Cast<ServiceSettings>().Select(sent => sent.Profile.Monitors.Count).ShouldBe([1, 0]);
        dell.Source.ShouldBe("measured for this model");
    }

    [Fact]
    public void Loading_the_settings_again_clears_what_was_said_of_the_last_save()
    {
        var form = SavingItself();
        form.FanCount = "two";
        form.Message.ShouldBe("Type the fans as a whole number.");

        form.Load(ServiceSettings.Default);

        (form.FanCount, form.Message).ShouldBe(("1", null));
        _link.Writes.ShouldBeEmpty();
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
    public async Task A_save_after_the_service_carried_the_old_monitor_settings_over_leaves_monitors_out_as_they_were()
    {
        // The old settings left monitors out, and Settings loaded them before the laptop was docked. At the first reading that
        // finds the Dell, the service carries them over: a monitor without a choice is left out, and the old count is cleared.
        // Settings lists the Dell left out, and a save of anything must not put back the settings it loaded, which would count
        // the Dell from then on, with nothing left to carry over.
        var old = MachineProfile.DefaultLaptop with { ExternalMonitors = 2, Monitors = [] };
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with { Profile = old });
        form.ShowMonitors([]);
        var carried = old with { ExternalMonitors = 0, CountMonitorsByDefault = false };
        _link.Settings = ServiceSettings.Default with { Profile = carried };
        form.ShowMonitors([Statuses.Dell with { CountedByDefault = false, Counted = false, WattsNow = 0 }]);
        form.IdleMinutes = "10";

        (await form.SaveAsync()).ShouldBeTrue();

        var sent = (ServiceSettings)_link.Writes.Single();
        sent.Profile.ShouldBe(carried);   // monitors left out unless chosen, the old count cleared, and no choice for the Dell
        sent.IdleThresholdSeconds.ShouldBe(600);
        form.Monitors.Single().Counted.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]   // docked after the form listed no monitor
    [InlineData(true)]    // listed at its own figure by the reading that carried the old settings over
    public async Task A_save_after_the_service_carried_the_old_monitor_settings_over_keeps_the_figure_typed_then_and_shows_it(bool listedBefore)
    {
        // The old settings counted monitors at a figure the user typed, which the service gives the Dell when it carries them over.
        var old = MachineProfile.DefaultLaptop with { ExternalMonitors = 1, IncludeMonitors = true, MonitorWatts = 30, Monitors = [] };
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with { Profile = old });
        form.ShowMonitors(listedBefore ? [Statuses.Dell] : []);
        var carried = old with { ExternalMonitors = 0, IncludeMonitors = false, Monitors = [new MonitorChoice { Key = Statuses.Dell.Key, Watts = 30 }] };
        _link.Settings = ServiceSettings.Default with { Profile = carried };
        form.ShowMonitors([Statuses.Dell with { OnWatts = 30, Source = MonitorSource.Typed, WattsNow = 30 }]);

        (await form.SaveAsync()).ShouldBeTrue();

        ((ServiceSettings)_link.Writes.Single()).Profile.ShouldBe(carried);
        (form.Monitors.Single().Watts, form.Monitors.Single().Source).ShouldBe(("30", "typed"));
    }

    [Fact]
    public async Task A_save_that_reads_the_settings_again_keeps_what_the_user_typed_and_ticked_and_takes_the_rest_from_the_service()
    {
        // The reading that carried the old settings over listed the Dell and the AOC at their own figures, and gave them and a
        // Samsung unplugged since the figure typed before. The user typed a figure for the Dell and left the AOC out: what they
        // changed stays, the AOC's figure, which they didn't touch, is the one carried over, and the Samsung, which the form
        // doesn't list, keeps its choice.
        const string samsung = "SAM0F9E-HNTW700123";
        var old = MachineProfile.DefaultLaptop with { ExternalMonitors = 3, IncludeMonitors = true, MonitorWatts = 30, Monitors = [] };
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with { Profile = old });
        form.ShowMonitors([Statuses.Dell, Statuses.Aoc]);
        var (dell, aoc) = (form.Monitors[0], form.Monitors[1]);
        dell.Watts = "22";
        aoc.Counted = false;
        _link.Settings = ServiceSettings.Default with
        {
            Profile = old with
            {
                ExternalMonitors = 0, IncludeMonitors = false,
                Monitors = [.. new[] { Statuses.Dell.Key, Statuses.Aoc.Key, samsung }.Select(key => new MonitorChoice { Key = key, Watts = 30 })],
            },
        };

        (await form.SaveAsync()).ShouldBeTrue();

        var profile = ((ServiceSettings)_link.Writes.Single()).Profile;
        profile.Monitors.ShouldBe(
        [
            new MonitorChoice { Key = Statuses.Dell.Key, Watts = 22 },
            new MonitorChoice { Key = Statuses.Aoc.Key, Counted = false, Watts = 30 },
            new MonitorChoice { Key = samsung, Watts = 30 },
        ]);
        (profile.CountMonitorsByDefault, profile.ExternalMonitors, profile.IncludeMonitors, profile.MonitorWatts).ShouldBe((true, 0, false, 30.0));
        (dell.Watts, aoc.Watts, aoc.Source, aoc.Counted).ShouldBe(("22", "30", "typed", false));
    }

    [Fact]
    public async Task A_save_that_reads_the_settings_again_leaves_out_a_monitor_the_user_didnt_tick_once_the_service_leaves_monitors_out()
    {
        // The reading that carried over old settings that left monitors out listed the monitors as counted, before it carried them
        // over. A box the user hasn't touched follows the service as it is now; one they ticked stays.
        var old = MachineProfile.DefaultLaptop with { ExternalMonitors = 2, Monitors = [] };
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with { Profile = old });
        form.ShowMonitors([Statuses.Dell, Statuses.Aoc]);
        var (dell, aoc) = (form.Monitors[0], form.Monitors[1]);
        aoc.Counted = false;
        aoc.Counted = true;   // and back again, which is the user's
        _link.Settings = ServiceSettings.Default with { Profile = old with { ExternalMonitors = 0, CountMonitorsByDefault = false } };

        (await form.SaveAsync()).ShouldBeTrue();

        var profile = ((ServiceSettings)_link.Writes.Single()).Profile;
        profile.CountMonitorsByDefault.ShouldBeFalse();
        profile.Monitors.ShouldBe([new MonitorChoice { Key = Statuses.Aoc.Key, Counted = true }]);
        (dell.Counted, aoc.Counted).ShouldBe((false, true));
    }

    [Fact]
    public async Task A_save_listing_no_monitor_after_the_service_carried_the_old_monitor_settings_over_keeps_what_it_carried_over()
    {
        // The Dell was docked and undocked again before Settings listed it, or the wizard, which reads the status once, shows. The
        // old settings loaded no longer go back as they came: they would drop the figure carried over until the service next
        // starts, and carry them over again then.
        var old = MachineProfile.DefaultLaptop with { ExternalMonitors = 1, IncludeMonitors = true, MonitorWatts = 30, Monitors = [] };
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with { Profile = old });
        form.ShowMonitors([]);
        var carried = old with { ExternalMonitors = 0, IncludeMonitors = false, Monitors = [new MonitorChoice { Key = Statuses.Dell.Key, Watts = 30 }] };
        _link.Settings = ServiceSettings.Default with { Profile = carried };

        (await form.SaveAsync()).ShouldBeTrue();

        ((ServiceSettings)_link.Writes.Single()).Profile.ShouldBe(carried);
    }

    [Theory]
    [InlineData(true, "The service didn't answer, so nothing was changed.")]
    [InlineData(false, "The service isn't running, so nothing was changed.")]
    public async Task A_save_that_cant_read_the_settings_again_sends_nothing(bool connected, string message)
    {
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with { Profile = MachineProfile.DefaultLaptop with { ExternalMonitors = 2, Monitors = [] } });
        form.ShowMonitors([Statuses.Dell with { CountedByDefault = false, Counted = false, WattsNow = 0 }]);
        _link.Settings = null;   // not sent in time
        _link.Connect(connected);

        (await form.SaveAsync()).ShouldBeFalse();

        form.Message.ShouldBe(message);
        _link.Writes.ShouldBeEmpty();
    }

    [Fact]
    public void Each_monitor_says_whether_its_brightness_was_read_and_what_it_draws_now()
    {
        var form = Form();

        form.ShowMonitors([Statuses.Dell, Statuses.Aoc, Statuses.Aoc with { Key = "AOC2402-2", Counted = false, WattsNow = 0 }]);

        form.Monitors.Select(m => (m.Brightness, m.Now)).ShouldBe(
        [
            ("brightness 60%, read from the monitor", "can't tell if it's on · 24.3 W now"),
            ("brightness unknown, assumed 75%", "can't tell if it's on · 13.4 W now"),
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
            ("brightness 60%, read from the monitor", "can't tell if it's on · 30.0 W now"),
            ("brightness unknown", "can't tell if it's on · 30.0 W now"),
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
        (dell.Brightness, dell.Now).ShouldBe(("brightness 80%, read from the monitor", "can't tell if it's on · 26.9 W now"));
        (aoc.Brightness, aoc.Now).ShouldBe(("brightness 50%, read from the monitor", "can't tell if it's on · 11.9 W now"));
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]   // settings from before monitors were detected left monitors out
    public async Task A_monitor_counted_where_the_service_wouldnt_count_it_or_left_out_where_it_would_saves_a_choice(bool countedByDefault)
    {
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with { Profile = MachineProfile.DefaultLaptop with { CountMonitorsByDefault = countedByDefault } });
        form.ShowMonitors(
        [
            Statuses.Dell with { CountedByDefault = countedByDefault, Counted = countedByDefault },
            Statuses.Aoc with { CountedByDefault = countedByDefault, Counted = countedByDefault },
        ]);
        form.Monitors[1].Counted = !countedByDefault;

        (await form.SaveAsync()).ShouldBeTrue();

        ((ServiceSettings)_link.Writes.Single()).Profile.Monitors.ShouldBe([new MonitorChoice { Key = Statuses.Aoc.Key, Counted = !countedByDefault }]);
    }

    [Fact]
    public void A_monitor_that_runs_off_the_pc_always_counts_and_what_was_ticked_for_it_comes_back_once_it_has_a_plug_of_its_own()
    {
        // Its draw is part of what the PC draws, so the service counts it whatever its choice says, and its box shows it
        // ticked; the view doesn't let it be unticked.
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with
        {
            Profile = MachineProfile.DefaultLaptop with { Monitors = [new MonitorChoice { Key = Statuses.Portable.Key, Counted = false }] },
        });
        form.ShowMonitors([Statuses.Portable, Statuses.Dell with { CountedByDefault = false, Counted = false }]);
        var (portable, dell) = (form.Monitors[0], form.Monitors[1]);
        var changed = new List<string?>();
        dell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        portable.Counted.ShouldBeTrue();
        dell.OwnPlug = false;   // it runs off the laptop after all
        dell.Counted.ShouldBeTrue();
        changed.ShouldContain(nameof(MonitorRow.Counted));
        dell.Counted = false;
        dell.Counted.ShouldBeTrue();

        dell.OwnPlug = true;
        portable.OwnPlug = true;
        (dell.Counted, portable.Counted).ShouldBe((false, false));   // as the service left the Dell, and as the choice left the portable one
    }

    [Fact]
    public void A_desktop_shows_the_plug_box_of_a_monitor_held_to_run_off_it_and_keeps_one_the_user_has_ticked()
    {
        // A desktop doesn't ask whether a monitor has a plug of its own, but shows the box of one held to run off the PC, so that
        // can be undone. A box the user ticks stays, or it would go from under the pointer and couldn't be unticked. A laptop
        // shows every box.
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with
        {
            Profile = MachineProfile.DefaultDesktop with { Monitors = [new MonitorChoice { Key = Statuses.Aoc.Key, OwnPlug = false }] },
        });
        form.ShowMonitors([Statuses.Dell, Statuses.Aoc with { OwnPlug = false }]);
        var (dell, aoc) = (form.Monitors[0], form.Monitors[1]);
        (dell.ShowsPlug, aoc.ShowsPlug).ShouldBe((false, true));
        var changed = new List<string?>();
        dell.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        aoc.OwnPlug = true;
        dell.OwnPlug = false;
        dell.OwnPlug = true;
        form.ShowMonitors([Statuses.Dell, Statuses.Aoc with { OwnPlug = false }]);

        (dell.ShowsPlug, aoc.ShowsPlug).ShouldBe((true, true));
        changed.ShouldContain(nameof(MonitorRow.ShowsPlug));
    }

    [Fact]
    public async Task Whether_a_monitor_that_runs_off_the_pc_counts_saves_nothing()
    {
        // Where the settings from before monitors left monitors out, a portable monitor still counts, and a choice that says
        // only that it doesn't goes. A monitor said to run off the laptop saves only that, and counts.
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with
        {
            Profile = MachineProfile.DefaultLaptop with
            {
                CountMonitorsByDefault = false, Monitors = [new MonitorChoice { Key = Statuses.Portable.Key, Counted = false }],
            },
        });
        form.ShowMonitors(
        [
            Statuses.Portable with { CountedByDefault = false },
            Statuses.Dell with { CountedByDefault = false, Counted = false },
            Statuses.Aoc with { CountedByDefault = false, Counted = false },
        ]);
        form.Monitors[1].OwnPlug = false;
        form.Monitors[2].Counted = true;
        form.Monitors[2].OwnPlug = false;

        (await form.SaveAsync()).ShouldBeTrue();

        ((ServiceSettings)_link.Writes.Single()).Profile.Monitors.ShouldBe(
        [
            new MonitorChoice { Key = Statuses.Dell.Key, OwnPlug = false },
            new MonitorChoice { Key = Statuses.Aoc.Key, OwnPlug = false },
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

    [Theory]
    [InlineData(ChassisKind.Desktop, ChassisKind.Laptop, true)]    // a portable monitor on a desktop has a plug of its own
    [InlineData(ChassisKind.Laptop, ChassisKind.Desktop, false)]   // and on a laptop runs off it
    public async Task Correcting_the_chassis_saves_the_plug_each_monitor_listed_shows(ChassisKind loaded, ChassisKind corrected, bool portableOwnPlug)
    {
        // The service took each plug shown for the chassis loaded. For the chassis corrected it would take the portable monitor's
        // plug otherwise, so what the user saw is said, for every monitor listed.
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with { Profile = loaded == ChassisKind.Laptop ? MachineProfile.DefaultLaptop : MachineProfile.DefaultDesktop });
        form.ShowMonitors([Statuses.Dell, Statuses.Portable with { OwnPlug = portableOwnPlug, OwnPlugByDefault = portableOwnPlug }]);
        form.Chassis = corrected;

        (await form.SaveAsync()).ShouldBeTrue();

        ((ServiceSettings)_link.Writes.Single()).Profile.Monitors.ShouldBe(
        [
            new MonitorChoice { Key = Statuses.Dell.Key, OwnPlug = true },
            new MonitorChoice { Key = Statuses.Portable.Key, OwnPlug = portableOwnPlug },
        ]);
    }

    [Fact]
    public async Task A_save_before_the_status_catches_up_with_a_corrected_chassis_keeps_each_plug_said_until_it_is_changed()
    {
        // Settings fills the form again once the service takes a save, but the service's status is from its last reading, which
        // took each plug for the chassis before. Saved again then, the plugs said must stay said: weighed against those plugs,
        // they would say nothing, and the portable monitor shown with a plug of its own would be taken to run off the laptop.
        MonitorStatus[] onDesktop = [Statuses.Dell, Statuses.Portable with { OwnPlug = true, OwnPlugByDefault = true }];
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with { Profile = MachineProfile.DefaultDesktop });
        form.ShowMonitors(onDesktop);
        form.Chassis = ChassisKind.Laptop;
        (await form.SaveAsync()).ShouldBeTrue();
        var corrected = (ServiceSettings)_link.Writes.Single();

        form.Load(corrected);
        form.ShowMonitors(onDesktop);
        form.FanCount = "2";
        (await form.SaveAsync()).ShouldBeTrue();
        ((ServiceSettings)_link.Writes[1]).Profile.Monitors.ShouldBe(corrected.Profile.Monitors);

        // Once the status catches up, a plug changed to the one the service would take goes back to its guess.
        form.ShowMonitors([Statuses.Dell, Statuses.Portable with { OwnPlug = true }]);
        form.Monitors[1].OwnPlug = false;
        (await form.SaveAsync()).ShouldBeTrue();
        ((ServiceSettings)_link.Writes[2]).Profile.Monitors.ShouldBe([new MonitorChoice { Key = Statuses.Dell.Key, OwnPlug = true }]);
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

        // Say the service has since taken settings saved elsewhere: a desktop's, where no monitor is taken to run off the PC,
        // carried over from before monitors were detected, when they were left out unless the user said otherwise.
        _link.Settings = ServiceSettings.Default with { Profile = MachineProfile.DefaultDesktop with { CountMonitorsByDefault = false } };
        form.ShowMonitors(
        [
            Statuses.Portable with { CountedByDefault = false, OwnPlugByDefault = true, OwnPlug = true },
            other with { CountedByDefault = false, Counted = false, OwnPlugByDefault = true, OwnPlug = true },
        ]);

        (ticked.Counted, ticked.OwnPlug).ShouldBe((true, true));
        (untouched.Counted, untouched.OwnPlug).ShouldBe((false, true));
        (await form.SaveAsync()).ShouldBeTrue();
        // The form still shows the laptop it loaded, and saves it, for which the service would take these portable monitors to
        // run off it, so each plug is said as it shows, with whether the monitor counts as it shows.
        ((ServiceSettings)_link.Writes.Single()).Profile.Monitors.ShouldBe(
        [
            new MonitorChoice { Key = Statuses.Portable.Key, Counted = true, OwnPlug = true },
            new MonitorChoice { Key = other.Key, Counted = false, OwnPlug = true },
        ]);
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
    [InlineData(23.8, 1920, 1080, "estimated from its size (correct it if you know better)")]
    [InlineData(0, 1920, 1080, "estimated (correct it if you know better)")]   // without a size, the median of all monitors
    [InlineData(23.8, 0, 0, "estimated (correct it if you know better)")]      // and without a resolution
    [InlineData(23.8, 1920, 0, "estimated (correct it if you know better)")]
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_form_the_service_never_filled_cannot_be_saved(bool savesItself)
    {
        var form = new ServiceForm(_link, UiThreads.Inline, English, savesItself);
        form.IsLoaded.ShouldBeFalse();

        form.FanCount = "2";
        if (!savesItself) (await form.SaveAsync()).ShouldBeFalse();

        form.Message.ShouldBe("The service hasn't sent its settings yet.");
        _link.Writes.ShouldBeEmpty();
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
