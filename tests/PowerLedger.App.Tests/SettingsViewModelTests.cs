using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.App.Tests;

public class SettingsViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 14, 30, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeLink _link = new();
    private readonly FakeMachineHistory _history = new();
    private readonly FakeUiSettings _ui = new();
    private readonly FakeTimeProvider _clock = new(Now);

    private SettingsViewModel Model() => new(_link, _history, _ui, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, "USD");

    /// <summary>Runs what the service's answers left for the UI thread, and whatever that leaves in turn.</summary>
    private static void Answer(Queue<Action> answers)
    {
        while (answers.TryDequeue(out var next)) next();
    }

    [Fact]
    public void Showing_it_reads_the_service_the_tariffs_and_the_detection()
    {
        _link.Connect(true);
        var model = Model();
        model.Show();

        model.Service.IsLoaded.ShouldBeTrue();
        model.TariffHistory.ShouldBe(new[] { "From 1 Aug 2026 · $0.17 / kWh" });
        model.Detected.ShouldBe("Laptop · Core i7-1165G7 · GeForce MX330 · 2 × DDR4 · 1 SSD · 15.3 in panel · 1 display");
        model.Notice.ShouldBeNull();
        model.Calibration.ShouldBe("Learning on battery: 15m of 30m needed.");
        model.Sources.Select(s => s.Name).ShouldBe(new[] { "Processor energy meter", "Battery" });
        model.Sources[0].State.ShouldBe("working");
        model.ServiceState.ShouldBe("Service 0.1.0 · 10 readings since 1 Jan 1970 00:00");
        model.Database.ShouldBe("31 MB");
    }

    [Fact]
    public void A_desktop_is_told_calibration_is_not_used_rather_than_learning_on_battery()
    {
        _link.Settings = ServiceSettings.Default with { Profile = MachineProfile.DefaultDesktop };
        _link.Connect(true);
        var model = Model();
        model.Show();

        model.Calibration.ShouldBe("Not used on a desktop: it has no battery, so its readings are estimated.");
        _clock.Advance(SettingsViewModel.StatusEvery);
        model.Calibration.ShouldBe("Not used on a desktop: it has no battery, so its readings are estimated.");
    }

    [Fact]
    public void A_desktop_whose_total_is_measured_from_a_ups_or_power_supply_is_not_told_its_readings_are_estimated()
    {
        _link.Settings = ServiceSettings.Default with { Profile = MachineProfile.DefaultDesktop };
        _link.Status = Statuses.Running() with { Last = Frames.At(Now, quality: Quality.Measured) with { Total = TotalSource.PowerSupply } };
        _link.Connect(true);
        var model = Model();
        model.Show();

        model.Calibration.ShouldBe("Not used on a desktop: a UPS or power supply measures its readings.");

        // A UPS that only gives its load as a share of its rated VA is stored as Estimated, so it still reads so here.
        _link.Status = Statuses.Running() with { Last = Frames.At(Now, quality: Quality.Estimated) with { Total = TotalSource.Ups } };
        _clock.Advance(SettingsViewModel.StatusEvery);
        model.Calibration.ShouldBe("Not used on a desktop: it has no battery, so its readings are estimated.");
    }

    [Fact]
    public void About_names_the_arc_gpu_ups_and_power_supply_sources_and_says_when_the_ups_has_found_nothing()
    {
        _link.Status = Statuses.Running() with
        {
            Sources =
            [
                new SourceStatus("arc-gpu", false, "no Intel Arc discrete GPU", 0, null),
                new SourceStatus("ups", true, "no UPS found on USB", 0, null),
                new SourceStatus("power-supply", true, null, 0, null),
            ],
        };
        _link.Connect(true);
        var model = Model();
        model.Show();

        model.Sources.ShouldBe(
        [
            new SourceLine("Intel Arc graphics", "not on this machine", "no Intel Arc discrete GPU"),
            new SourceLine("UPS", "note", "no UPS found on USB"),
            new SourceLine("Power supply", "working", ""),
        ]);
    }

    [Fact]
    public void Without_the_service_the_apps_own_preferences_still_work()
    {
        var model = Model();
        model.Show();

        model.Notice.ShouldBe("The service isn't running. Its settings appear when it starts; the App's own preferences below work now.");
        model.Service.IsLoaded.ShouldBeFalse();
        model.Theme = ThemeChoice.Dark;
        _ui.Changes.ShouldBe(new[] { "theme Dark" });
        model.Theme.ShouldBe(ThemeChoice.Dark);
    }

    [Fact]
    public void A_service_that_comes_up_while_settings_shows_fills_it()
    {
        var model = Model();
        model.Show();
        model.Notice.ShouldNotBeNull();

        _link.Connect(true);
        model.Service.IsLoaded.ShouldBeTrue();
        model.Notice.ShouldBeNull();
    }

    [Fact]
    public void A_co2_factor_is_read_in_the_users_culture_and_applied()
    {
        var model = Model();
        model.Co2.ShouldBe("0.40");

        model.Co2 = "0.23";
        model.SaveCo2.Execute(null);
        _ui.Changes.ShouldBe(new[] { "co2 0.23" });
        model.Co2Message.ShouldBe("Saved.");

        model.Co2 = "x";
        model.SaveCo2.Execute(null);
        model.Co2Message.ShouldBe("Type the kilograms of CO₂ per kWh as a number, like 0.40.");

        model.Co2 = "3";
        model.SaveCo2.Execute(null);
        model.Co2Message.ShouldBe("refused");
    }

    [Fact]
    public void Starting_with_windows_applies_when_ticked()
    {
        var model = Model();
        model.StartWithWindows.ShouldBeTrue();
        model.StartWithWindows = false;
        _ui.Changes.ShouldBe(new[] { "autostart False" });
        model.StartWithWindows.ShouldBeFalse();
    }

    [Fact]
    public void Reading_brightness_from_monitors_applies_when_ticked()
    {
        var model = Model();
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        model.ReadMonitorBrightness.ShouldBeTrue();

        model.ReadMonitorBrightness = false;

        _ui.Changes.ShouldBe(new[] { "monitor brightness False" });
        model.ReadMonitorBrightness.ShouldBeFalse();
        changed.ShouldContain(nameof(SettingsViewModel.ReadMonitorBrightness));
    }

    [Fact]
    public async Task A_calibration_reset_asks_first()
    {
        _link.Connect(true);
        var model = Model();
        model.Show();

        model.ResetCalibration.Execute(null);
        model.ConfirmingReset.ShouldBeTrue();
        _link.Writes.ShouldBeEmpty();
        model.CancelReset.Execute(null);
        model.ConfirmingReset.ShouldBeFalse();

        model.ResetCalibration.Execute(null);
        await model.ConfirmResetAsync();
        _link.Writes.ShouldBe(new object[] { "reset" });
        model.ConfirmingReset.ShouldBeFalse();
        model.CalibrationMessage.ShouldBe("Calibration reset. The model learns again the next time the machine runs on battery.");
    }

    [Fact]
    public async Task A_saved_tariff_shows_in_the_history()
    {
        _link.Connect(true);
        var model = Model();
        model.Show();

        _history.TariffList!.Add(new Tariff(Now, 0.2m, "USD"));
        model.Tariff.Price = "0.20";
        await model.Tariff.SaveAsync();

        model.TariffHistory.First().ShouldBe("From 15 Sep 2026 · $0.20 / kWh");
    }

    [Fact]
    public async Task A_save_finishing_after_the_screen_hid_starts_no_reading()
    {
        _link.Connect(true);
        var model = Model();
        model.Show();
        model.Hide();
        var before = _link.StatusReads;

        (await model.Service.SaveAsync()).ShouldBeTrue();
        _clock.Advance(SettingsViewModel.StatusEvery * 3);
        _link.StatusReads.ShouldBe(before);
    }

    [Fact]
    public void A_monitor_ticked_saves_itself_and_is_still_ticked_after_leaving_settings_and_coming_back()
    {
        // Leaving Settings fills it again from the service, which once dropped a tick that waited for a Save button further down.
        _link.Settings = ServiceSettings.Default with
        {
            Profile = MachineProfile.DefaultDesktop with { Monitors = [new MonitorChoice { Key = Statuses.Aoc.Key, Counted = false }] },
        };
        _link.Status = Statuses.WithMonitors(Statuses.Dell, Statuses.Aoc with { Counted = false, WattsNow = 0 });
        _link.Connect(true);
        var model = Model();
        model.Show();
        model.Service.Monitors[1].Counted.ShouldBeFalse();

        model.Service.Monitors[1].Counted = true;
        model.Service.Message.ShouldBe("Saved.");
        model.Hide();
        _link.Settings = (ServiceSettings)_link.Writes.Single();   // what the service holds now
        model.Show();

        model.Service.Monitors[1].Counted.ShouldBeTrue();
        _link.Writes.Count.ShouldBe(1);
    }

    [Fact]
    public void A_save_fills_no_box_again_so_what_is_changed_while_it_is_on_its_way_stays_and_is_sent_after_it()
    {
        var answers = new Queue<Action>();
        _link.Status = Statuses.WithMonitors();
        _link.Connect(true);
        var model = new SettingsViewModel(_link, _history, _ui, new UiThreads(answers.Enqueue, action => action()), _clock, TimeZoneInfo.Utc, English, "USD");
        model.Show();
        Answer(answers);
        var form = model.Service;
        var (dell, aoc) = (form.Monitors[0], form.Monitors[1]);

        form.FanCount = "2";   // sent, and its answer is on its way
        form.SsdCount = "3";
        aoc.Watts = "17";
        Answer(answers);

        (form.FanCount, form.SsdCount, aoc.Watts, aoc.Source).ShouldBe(("2", "3", "17", "typed"));
        form.Monitors.ShouldBe([dell, aoc]);
        var sent = _link.Writes.Cast<ServiceSettings>().ToList();
        sent.Count.ShouldBe(2);
        (sent[1].Profile.FanCount, sent[1].Profile.SsdCount).ShouldBe((2, 3));
        sent[1].Profile.Monitors.ShouldBe([new MonitorChoice { Key = Statuses.Aoc.Key, Watts = 17 }]);
    }

    [Fact]
    public void The_calibration_line_follows_the_sample_interval_and_the_chassis_saved()
    {
        _link.Connect(true);
        var model = Model();
        model.Show();

        model.Service.SampleInterval = "2";
        model.Calibration.ShouldBe("Learning on battery: 30m of 1h 00m needed.");

        model.Service.Chassis = ChassisKind.Desktop;
        model.Calibration.ShouldBe("Not used on a desktop: it has no battery, so its readings are estimated.");
    }

    [Fact]
    public void Filling_settings_reading_the_status_every_ten_seconds_and_the_service_coming_up_send_nothing()
    {
        _link.Status = Statuses.WithMonitors();
        var model = Model();
        model.Show();
        _link.Connect(true);   // the service comes up while Settings shows, and fills it
        _link.Status = Statuses.WithMonitors(Statuses.Dell with { OnWatts = 28.1 }, Statuses.Aoc with { CountedByDefault = false, Counted = false });
        _clock.Advance(SettingsViewModel.StatusEvery);
        model.Service.Monitors[0].Watts.ShouldBe("28.1");
        model.Hide();
        model.Show();

        _link.Writes.ShouldBeEmpty();
        model.Service.IsLoaded.ShouldBeTrue();
    }

    [Fact]
    public void Run_setup_again_asks_the_shell()
    {
        var model = Model();
        var asked = 0;
        model.SetupRequested += () => asked++;
        model.RunSetup.Execute(null);
        asked.ShouldBe(1);
    }

    [Fact]
    public void Showing_it_lists_each_monitor_with_its_figure_its_source_and_its_brightness()
    {
        _link.Status = Statuses.WithMonitors();
        _link.Connect(true);
        var model = Model();
        model.Show();

        model.Service.HasMonitors.ShouldBeTrue();
        model.Service.Monitors.Select(m => (m.Name, m.Size, m.Watts, m.Source, m.Brightness, m.Now, m.Counted)).ShouldBe(
        [
            ("DELL U2723QE", "27 in · 3840 × 2160", "26.9", "measured for this model", "brightness 60%, read from the monitor", "can't tell if it's on · 24.3 W now", true),
            ("24B1XH5", "23.8 in · 1920 × 1080", "13.4", "estimated from its size — correct it if you know better", "brightness unknown, assumed 75%", "can't tell if it's on · 13.4 W now", true),
        ]);
    }

    [Fact]
    public void The_monitors_refresh_with_the_status_without_losing_a_figure_being_typed()
    {
        _link.Status = Statuses.WithMonitors();
        _link.Connect(true);
        var model = Model();
        model.Show();
        var aoc = model.Service.Monitors[1];
        aoc.Watts = "1";   // on the way to 18

        _link.Status = Statuses.WithMonitors(Statuses.Dell, Statuses.Aoc with { Brightness = 0.4, WattsNow = 10.6 });
        _clock.Advance(SettingsViewModel.StatusEvery);

        model.Service.Monitors[1].ShouldBeSameAs(aoc);
        (aoc.Watts, aoc.Brightness, aoc.Now).ShouldBe(("1", "brightness 40%, read from the monitor", "can't tell if it's on · 10.6 W now"));
    }

    [Fact]
    public void A_monitor_plugged_in_while_settings_shows_appears_within_ten_seconds()
    {
        _link.Status = Statuses.WithMonitors(Statuses.Dell);
        _link.Connect(true);
        var model = Model();
        model.Show();

        _link.Status = Statuses.WithMonitors();
        _clock.Advance(SettingsViewModel.StatusEvery);

        model.Service.Monitors.Select(m => m.Name).ShouldBe(["DELL U2723QE", "24B1XH5"]);
    }

    [Fact]
    public void Showing_it_lists_the_ups_and_the_power_supply_the_service_reads_and_what_is_said_of_them_saves_itself()
    {
        _link.Status = Statuses.WithPowerDevices();
        _link.Connect(true);
        var model = Model();
        model.Show();

        model.Service.Upses.ShouldBe(["UPS · APC Back-UPS ES 850G2 · 142 W (load of its rated watts)"]);
        model.Service.PowerSupplies.ShouldBe(["Power supply · Corsair HX1000i · 312 W (DC output, all rails)"]);
        (model.Service.UpsLoad, model.Service.ReadPowerSupply).ShouldBe((UpsLoad.NotSaid, true));
        _link.Writes.ShouldBeEmpty();

        model.Service.UpsLoad = UpsLoad.ThisPcAndMonitors;
        model.Service.ReadPowerSupply = false;

        var sent = _link.Writes.Cast<ServiceSettings>().ToList();
        sent.Count.ShouldBe(2);
        sent[0].Profile.UpsLoad.ShouldBe(UpsLoad.ThisPcAndMonitors);
        (sent[1].Profile.UpsLoad, sent[1].Profile.ReadPowerSupply).ShouldBe((UpsLoad.ThisPcAndMonitors, false));
        model.Service.Message.ShouldBe("Saved.");
    }

    [Fact]
    public void A_ups_plugged_in_while_settings_shows_appears_within_ten_seconds_and_a_service_from_before_them_lists_none()
    {
        _link.Connect(true);
        var model = Model();
        model.Show();
        (model.Service.HasUps, model.Service.HasPowerSupply).ShouldBe((false, false));

        _link.Status = Statuses.WithPowerDevices(Statuses.Ups);
        _clock.Advance(SettingsViewModel.StatusEvery);

        model.Service.Upses.ShouldHaveSingleItem().ShouldBe("UPS · APC Back-UPS ES 850G2 · 142 W (load of its rated watts)");
        model.Service.HasPowerSupply.ShouldBeFalse();
        _link.Writes.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_service_from_before_monitors_lists_none_and_its_settings_still_save()
    {
        _link.Status = Statuses.Running() with { Monitors = null };
        _link.Settings = ServiceSettings.Default with { Profile = MachineProfile.DefaultLaptop with { Monitors = null! } };
        _link.Connect(true);
        var model = Model();
        model.Show();

        model.Service.HasMonitors.ShouldBeFalse();
        (await model.Service.SaveAsync()).ShouldBeTrue();
        ((ServiceSettings)_link.Writes.Single()).Profile.Monitors.ShouldBeEmpty();
    }

    [Fact]
    public void The_status_is_read_again_every_ten_seconds_while_shown()
    {
        _link.Connect(true);
        var model = Model();
        model.Show();

        _link.Status = Statuses.Running() with { Ticks = 99 };
        _clock.Advance(SettingsViewModel.StatusEvery);
        model.ServiceState.ShouldContain("99 readings");

        model.Hide();
        _link.Status = Statuses.Running() with { Ticks = 5 };
        _clock.Advance(SettingsViewModel.StatusEvery * 2);
        model.ServiceState.ShouldContain("99 readings");
    }
}
