using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

public class WizardViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 14, 30, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeLink _link = new();
    private readonly FakeMachineHistory _history = new();
    private readonly FakeUiSettings _ui = new();
    private readonly FakeTimeProvider _clock = new(Now);

    private WizardViewModel Model()
    {
        var model = new WizardViewModel(_link, _history, _ui, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, "EUR");
        model.Start();
        return model;
    }

    [Fact]
    public void It_starts_at_the_tariff_in_the_regions_currency_and_reads_the_machine()
    {
        _link.Connect(true);
        var model = Model();

        model.Step.ShouldBe(SetupStep.Tariff);
        model.IsFirst.ShouldBeTrue();
        model.Tariff.Currency.ShouldBe("EUR");
        model.Machine.IsLoaded.ShouldBeTrue();
        model.Detected.ShouldStartWith("Laptop · Core i7-1165G7");
        model.Readings.ShouldStartWith("This machine has a battery and a processor energy meter.");
    }

    [Fact]
    public async Task An_empty_tariff_is_later_and_a_typed_one_is_saved_first()
    {
        _link.Connect(true);
        var later = Model();
        await later.NextAsync();
        later.Step.ShouldBe(SetupStep.Machine);
        _link.Writes.ShouldBeEmpty();

        var typed = Model();
        typed.Tariff.Price = "0.30";
        await typed.NextAsync();
        typed.Step.ShouldBe(SetupStep.Machine);
        _link.Writes.Single().ShouldBeOfType<(decimal, string, DateTimeOffset?)>();
    }

    [Fact]
    public async Task A_refused_step_stays_put()
    {
        _link.Connect(true);
        _link.Answer = new WriteResult("The price per kWh must be zero or more.");
        var model = Model();
        model.Tariff.Price = "0.30";
        await model.NextAsync();

        model.Step.ShouldBe(SetupStep.Tariff);
        model.Tariff.Message.ShouldBe("The price per kWh must be zero or more.");
    }

    [Fact]
    public async Task The_machine_is_saved_before_the_readings()
    {
        _link.Connect(true);
        var model = Model();
        await model.NextAsync();
        model.Machine.Chassis = ChassisKind.Desktop;
        await model.NextAsync();

        model.Step.ShouldBe(SetupStep.Readings);
        model.IsLast.ShouldBeTrue();
        ((ServiceSettings)_link.Writes.Single()).Profile.Chassis.ShouldBe(ChassisKind.Desktop);
    }

    [Fact]
    public async Task Without_the_service_the_machine_step_says_so_and_moves_on()
    {
        var model = Model();
        await model.NextAsync();
        await model.NextAsync();

        model.Step.ShouldBe(SetupStep.Readings);
        model.Message.ShouldBe("The service isn't running, so the machine wasn't saved. Settings has it once the service starts.");
        model.Readings.ShouldStartWith("The service isn't running yet.");
    }

    [Fact]
    public async Task Back_goes_back_a_step()
    {
        _link.Connect(true);
        var model = Model();
        await model.NextAsync();
        await model.NextAsync();
        model.Back.Execute(null);
        model.Step.ShouldBe(SetupStep.Machine);
        model.Back.Execute(null);
        model.Step.ShouldBe(SetupStep.Tariff);
    }

    [Fact]
    public void Finishing_is_remembered()
    {
        var model = Model();
        var finished = 0;
        model.Finished += () => finished++;
        model.Finish.Execute(null);

        _ui.Current.FirstRunDone.ShouldBeTrue();
        finished.ShouldBe(1);
    }

    [Fact]
    public void A_service_that_comes_up_after_the_wizard_fills_the_machine()
    {
        var model = Model();
        model.Machine.IsLoaded.ShouldBeFalse();
        model.Machine.Chassis.ShouldBe(ChassisKind.Laptop);

        _link.Connect(true);
        model.Machine.IsLoaded.ShouldBeTrue();
        model.Readings.ShouldStartWith("This machine has a battery");
    }

    [Theory]
    [InlineData(ChassisKind.Laptop)]
    [InlineData(ChassisKind.Desktop)]
    public void A_machine_without_sensors_is_told_its_readings_are_estimated(ChassisKind chassis)
        => WizardViewModel.ReadingsFor(Statuses.Running(energyMeter: false, battery: false), chassis)
            .ShouldBe("This machine has no power sensors PowerLedger can read, so its readings are estimated from load and the machine profile.");

    [Fact]
    public void A_desktop_is_never_told_its_readings_are_measured_from_a_battery()
    {
        // The battery Windows shows on a desktop is a UPS it doesn't mark short-term: it powers more than this machine.
        WizardViewModel.ReadingsFor(Statuses.Running(energyMeter: true, battery: true), ChassisKind.Desktop)
            .ShouldBe("This machine reports its processor's energy, so the processor is measured; the rest is estimated from the machine profile. "
                      + "The battery Windows shows is taken for a UPS, which powers more than this machine, so it isn't used.");
        WizardViewModel.ReadingsFor(Statuses.Running(energyMeter: false, battery: true), ChassisKind.Desktop)
            .ShouldBe("This machine's readings are estimated from load and the machine profile. "
                      + "The battery Windows shows is taken for a UPS, which powers more than this machine, so it isn't used.");
    }

    [Fact]
    public void An_energy_meter_that_cannot_read_the_processor_package_is_not_called_a_processor_meter()
    {
        // Core, graphics and memory rails alone leave the processor modelled, so the service reports the meter unsupported.
        var packageless = new SourceStatus("energy-meter", false, "this machine's energy meter has no processor package rail", 0, null);
        SourceStatus Battery(bool fitted) => new("battery", fitted, fitted ? null : "no battery fitted", 0, null);

        WizardViewModel.ReadingsFor(Statuses.Running() with { Sources = [packageless, Battery(true)] }, ChassisKind.Laptop)
            .ShouldStartWith("This machine has a battery. On battery");
        WizardViewModel.ReadingsFor(Statuses.Running() with { Sources = [packageless, Battery(false)] }, ChassisKind.Laptop)
            .ShouldBe("This machine has no power sensors PowerLedger can read, so its readings are estimated from load and the machine profile.");
    }

    [Fact]
    public void A_desktop_without_a_battery_reads_like_any_machine_without_one()
        => WizardViewModel.ReadingsFor(Statuses.Running(energyMeter: true, battery: false), ChassisKind.Desktop)
            .ShouldBe(WizardViewModel.ReadingsFor(Statuses.Running(energyMeter: true, battery: false), ChassisKind.Laptop));

    [Fact]
    public void A_desktop_measured_by_a_power_supply_is_told_so_by_its_name()
    {
        var status = Statuses.Running() with
        {
            Last = Frames.At(Now, quality: Quality.Measured) with { Total = TotalSource.PowerSupply },
            PowerDevices = [Statuses.PowerSupply],
        };
        WizardViewModel.ReadingsFor(status, ChassisKind.Desktop)
            .ShouldBe("This machine reads its Corsair HX1000i over USB, so its readings are measured.");
    }

    [Fact]
    public void A_desktop_measured_by_a_ups_is_told_so_by_its_name()
    {
        var status = Statuses.Running() with
        {
            Last = Frames.At(Now, quality: Quality.Measured) with { Total = TotalSource.Ups },
            PowerDevices = [Statuses.Ups],
        };
        WizardViewModel.ReadingsFor(status, ChassisKind.Desktop)
            .ShouldBe("This machine reads its APC Back-UPS ES 850G2 over USB, so its readings are measured.");
    }

    [Fact]
    public void A_ups_giving_only_its_load_of_rated_va_is_told_estimated_not_measured()
    {
        var status = Statuses.Running() with
        {
            Last = Frames.At(Now, quality: Quality.Estimated) with { Total = TotalSource.Ups },
            PowerDevices = [Statuses.Ups],
        };
        WizardViewModel.ReadingsFor(status, ChassisKind.Desktop)
            .ShouldBe("This machine reads its APC Back-UPS ES 850G2 over USB. "
                      + "A UPS that only gives its load as a share of its rated VA is estimated, not measured.");
    }

    [Fact]
    public void A_measured_ups_or_power_supply_with_no_matching_device_listed_still_says_which_kind()
    {
        // An older service, or PowerDevices not (yet) naming the one giving the total: say the kind, not "working".
        var noDevices = Statuses.Running() with { Last = Frames.At(Now, quality: Quality.Measured) with { Total = TotalSource.Ups } };
        WizardViewModel.ReadingsFor(noDevices, ChassisKind.Desktop)
            .ShouldBe("This machine reads its UPS over USB, so its readings are measured.");

        var wrongKindListed = Statuses.Running() with
        {
            Last = Frames.At(Now, quality: Quality.Measured) with { Total = TotalSource.Ups },
            PowerDevices = [Statuses.PowerSupply],
        };
        WizardViewModel.ReadingsFor(wrongKindListed, ChassisKind.Desktop)
            .ShouldBe("This machine reads its UPS over USB, so its readings are measured.");
    }

    [Fact]
    public void A_laptop_keeps_its_own_wording_even_when_its_total_is_measured_from_a_ups()
    {
        // Plan L: a laptop off its own battery never takes a UPS or supply as its total, but one on the mains might: the
        // wizard still speaks only of the laptop's battery, unchanged, since that is what the user is asking about there.
        var status = Statuses.Running() with { Last = Frames.At(Now, quality: Quality.Measured) with { Total = TotalSource.Ups } };
        WizardViewModel.ReadingsFor(status, ChassisKind.Laptop)
            .ShouldBe("This machine has a battery and a processor energy meter. "
                      + "On battery its readings are measured; plugged in, they are calibrated once the model has learned from battery time, and estimated until then.");
    }

    [Fact]
    public async Task The_readings_follow_the_chassis_chosen_on_the_machine_step()
    {
        _link.Connect(true);
        var model = Model();
        model.Readings.ShouldStartWith("This machine has a battery and a processor energy meter.");
        await model.NextAsync();
        model.Machine.Chassis = ChassisKind.Desktop;
        await model.NextAsync();

        model.Step.ShouldBe(SetupStep.Readings);
        model.Readings.ShouldStartWith("This machine reports its processor's energy, so the processor is measured;");
        model.Readings.ShouldNotContain("On battery its readings are measured");
    }

    [Fact]
    public void A_desktop_profile_from_the_service_is_read_as_a_desktop()
    {
        _link.Settings = ServiceSettings.Default with { Profile = MachineProfile.DefaultDesktop };
        _link.Connect(true);
        var model = Model();

        model.Machine.Chassis.ShouldBe(ChassisKind.Desktop);
        model.Readings.ShouldNotContain("On battery its readings are measured");
    }

    [Theory]
    [InlineData(false)]   // this service lists none
    [InlineData(true)]    // a service from before monitors, which sends no list and no choices
    public async Task Without_external_monitors_the_machine_step_lists_none_and_saves_nothing_about_them(bool olderService)
    {
        _link.Status = Statuses.Running() with { Monitors = olderService ? null : [] };
        if (olderService) _link.Settings = ServiceSettings.Default with { Profile = MachineProfile.DefaultLaptop with { Monitors = null! } };
        _link.Connect(true);
        var model = Model();
        await model.NextAsync();

        model.Machine.HasMonitors.ShouldBeFalse();
        model.Machine.Monitors.ShouldBeEmpty();
        await model.NextAsync();

        model.Step.ShouldBe(SetupStep.Readings);
        ((ServiceSettings)_link.Writes.Single()).Profile.Monitors.ShouldBeEmpty();
    }

    [Fact]
    public async Task Each_monitor_is_listed_by_name_with_its_size_its_figure_and_where_the_figure_came_from()
    {
        _link.Status = Statuses.WithMonitors();
        _link.Connect(true);
        var model = Model();
        await model.NextAsync();

        model.Machine.HasMonitors.ShouldBeTrue();
        model.Machine.Monitors.Select(m => (m.Name, m.Size, m.Watts, m.Source, m.Counted)).ShouldBe(
        [
            ("DELL U2723QE", "27 in · 3840 × 2160", "26.9", "measured for this model", true),
            ("24B1XH5", "23.8 in · 1920 × 1080", "13.4", "estimated from its size (correct it if you know better)", true),
        ]);
    }

    [Fact]
    public async Task Saving_the_machine_saves_a_choice_for_a_monitor_unticked_and_none_for_one_left_counted()
    {
        _link.Status = Statuses.WithMonitors();
        _link.Connect(true);
        var model = Model();
        await model.NextAsync();
        model.Machine.Monitors[1].Counted = false;
        await model.NextAsync();

        model.Step.ShouldBe(SetupStep.Readings);
        ((ServiceSettings)_link.Writes.Single()).Profile.Monitors.ShouldBe([new MonitorChoice { Key = Statuses.Aoc.Key, Counted = false, Watts = null }]);
    }

    [Fact]
    public async Task A_figure_typed_for_an_estimated_monitor_is_saved_as_its_watts()
    {
        _link.Status = Statuses.WithMonitors();
        _link.Connect(true);
        var model = Model();
        await model.NextAsync();
        model.Machine.Monitors[1].Watts = "17.5";
        await model.NextAsync();

        ((ServiceSettings)_link.Writes.Single()).Profile.Monitors.ShouldBe([new MonitorChoice { Key = Statuses.Aoc.Key, Counted = true, Watts = 17.5 }]);
    }

    [Fact]
    public async Task Setup_run_again_shows_the_choices_already_made()
    {
        var typed = Statuses.Dell with { OnWatts = 30, Source = MonitorSource.Typed };
        _link.Status = Statuses.WithMonitors(typed, Statuses.Aoc);
        _link.Settings = ServiceSettings.Default with
        {
            Profile = MachineProfile.DefaultLaptop with
            {
                Monitors = [new MonitorChoice { Key = Statuses.Dell.Key, Counted = true, Watts = 30 }, new MonitorChoice { Key = Statuses.Aoc.Key, Counted = false }],
            },
        };
        _link.Connect(true);
        var model = Model();
        await model.NextAsync();

        model.Machine.Monitors.Select(m => (m.Watts, m.Source, m.Counted)).ShouldBe(
        [
            ("30", "typed", true),
            ("13.4", "estimated from its size (correct it if you know better)", false),
        ]);
        await model.NextAsync();
        ((ServiceSettings)_link.Writes.Single()).Profile.Monitors.ShouldBe(_link.Settings.Profile.Monitors);
    }
}
