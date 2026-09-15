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
}
