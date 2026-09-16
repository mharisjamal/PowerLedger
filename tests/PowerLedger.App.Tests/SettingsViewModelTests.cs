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

        model.Calibration.ShouldBe("Not used on a desktop: its readings are always estimated.");
        _clock.Advance(SettingsViewModel.StatusEvery);
        model.Calibration.ShouldBe("Not used on a desktop: its readings are always estimated.");
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
            ("DELL U2723QE", "27 in · 3840 × 2160", "26.9", "measured for this model", "brightness 60%, read from the monitor", "24.3 W now", true),
            ("24B1XH5", "23.8 in · 1920 × 1080", "13.4", "estimated from its size — correct it if you know better", "brightness unknown, assumed 75%", "13.4 W now", true),
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
        (aoc.Watts, aoc.Brightness, aoc.Now).ShouldBe(("1", "brightness 40%, read from the monitor", "10.6 W now"));
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
