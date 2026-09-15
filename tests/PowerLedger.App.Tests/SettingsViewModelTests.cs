using System.Globalization;
using Microsoft.Extensions.Time.Testing;
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
    public void Run_setup_again_asks_the_shell()
    {
        var model = Model();
        var asked = 0;
        model.SetupRequested += () => asked++;
        model.RunSetup.Execute(null);
        asked.ShouldBe(1);
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
