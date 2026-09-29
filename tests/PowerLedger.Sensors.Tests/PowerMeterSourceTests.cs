using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class PowerMeterSourceTests
{
    private const string AcpiMeter = @"\\?\ACPI#ACPI000D#0#{e849804e-c719-43d8-ac88-96b894c191e2}";
    private const string BatteryMeter = @"\\?\ACPI#PNP0C0A#1#{e849804e-c719-43d8-ac88-96b894c191e2}";

    [Fact]
    public void An_acpi_power_meter_is_the_machine_s_input_power_in_watts()
    {
        var meters = new FakePowerMeters { Devices = [AcpiMeter] }.With("Power Meter (0)", 187_400);
        using var source = new PowerMeterSource(meters);

        var draft = Tick(source);

        source.Supported.ShouldBeTrue();
        draft.SystemMeterW.ShouldNotBeNull().ShouldBe(187.4, 1e-9);
        draft.SystemMeter.ShouldBe(SystemMeterKind.PowerMeter);
        draft.SystemMeterName.ShouldBe("ACPI power meter");
    }

    [Fact]
    public void A_battery_s_meter_is_never_taken_for_the_machine_s_as_the_owner_s_laptop_has_only_that_one()
    {
        // The owner's laptop: one instance, Power Meter (0), reading nought on mains, whose device is the battery (PNP0C0A).
        var meters = new FakePowerMeters { Devices = [BatteryMeter] }.With("Power Meter (0)", 0);
        using var source = new PowerMeterSource(meters);

        source.Supported.ShouldBeFalse();
        source.Unavailable.ShouldBe("this PC's only power meter is its battery's");
        meters.Reads.ShouldBe(0);
    }

    [Fact]
    public void A_meter_reading_nought_is_no_reading()
    {
        var meters = new FakePowerMeters { Devices = [AcpiMeter] }.With("Power Meter (0)", 0);
        using var source = new PowerMeterSource(meters);

        var draft = Tick(source);

        draft.SystemMeterW.ShouldBeNull();
        draft.SystemMeter.ShouldBe(SystemMeterKind.None);
    }

    [Fact]
    public void Beside_a_battery_s_meter_the_platform_s_is_read_by_its_place_in_windows_list()
    {
        var meters = new FakePowerMeters { Devices = [BatteryMeter, AcpiMeter] }
            .With("Power Meter (1)", 61_000)
            .With("Power Meter (0)", 9_000);                  // the battery's, which charging may move
        using var source = new PowerMeterSource(meters);

        Tick(source).SystemMeterW.ShouldNotBeNull().ShouldBe(61, 1e-9);
    }

    [Fact]
    public void With_several_platform_meters_the_largest_is_taken_so_a_meter_inside_another_is_never_added_twice()
    {
        var meters = new FakePowerMeters { Devices = [AcpiMeter, AcpiMeter] }
            .With("Power Meter (0)", 350_000)
            .With("Power Meter (1)", 120_000);
        using var source = new PowerMeterSource(meters);

        Tick(source).SystemMeterW.ShouldNotBeNull().ShouldBe(350, 1e-9);
    }

    [Fact]
    public void Meters_windows_can_t_tell_apart_are_not_used()
    {
        var unknown = new FakePowerMeters { Devices = null }.With("Power Meter (0)", 50_000);
        using (var source = new PowerMeterSource(unknown))
        {
            source.Supported.ShouldBeFalse();
            source.Unavailable.ShouldBe("Windows would not say what its power meters measure");
        }

        var mismatched = new FakePowerMeters { Devices = [BatteryMeter, AcpiMeter, AcpiMeter] }
            .With("Power Meter (0)", 50_000).With("Power Meter (1)", 40_000);
        using (var source = new PowerMeterSource(mismatched))
        {
            source.Supported.ShouldBeFalse();
            source.Unavailable.ShouldBe("Windows lists its power meters in a way PowerLedger can't match up");
        }
    }

    [Fact]
    public void A_machine_without_the_counters_says_so()
    {
        using var source = new PowerMeterSource(new FakePowerMeters { Devices = [] });

        source.Supported.ShouldBeFalse();
        source.Unavailable.ShouldBe("this PC has no power meter");
    }

    [Fact]
    public void A_counter_that_fails_costs_its_reading_and_nothing_else()
    {
        var meters = new FakePowerMeters { Devices = [AcpiMeter] }.With("Power Meter (0)", 90_000);
        using var source = new PowerMeterSource(meters);
        meters.Fail = true;

        Should.NotThrow(() => Tick(source)).SystemMeterW.ShouldBeNull();
    }

    [Fact]
    public void The_validator_drops_a_meter_reading_past_the_ceiling()
    {
        var validator = new SampleValidator();
        var draft = new SampleDraft { SystemMeterW = 90_000, SystemMeter = SystemMeterKind.PowerMeter };

        var sample = validator.Validate(draft.ToSample(DateTimeOffset.UnixEpoch, 1));

        sample.SystemMeterW.ShouldBeNull();
        sample.Suspect.ShouldBeTrue();
        validator.Validate(new SampleDraft { SystemMeterW = 240, SystemMeter = SystemMeterKind.Bmc }.ToSample(DateTimeOffset.UnixEpoch.AddHours(1), 1))
            .SystemMeterW.ShouldBe(240);
    }

    private static SampleDraft Tick(ISensorSource source)
    {
        var draft = new SampleDraft();
        source.Contribute(draft);
        return draft;
    }
}

internal sealed class FakePowerMeters : IPowerMeters
{
    private readonly List<(string Name, long Milliwatts)> _instances = [];

    public IReadOnlyList<string>? Devices { get; set; } = [];

    public bool Fail { get; set; }

    public int Reads { get; private set; }

    public FakePowerMeters With(string instance, long milliwatts)
    {
        _instances.Add((instance, milliwatts));
        return this;
    }

    public IReadOnlyList<string> Instances() => [.. _instances.Select(i => i.Name), "_Total"];

    public IReadOnlyList<string>? DeviceIds() => Devices;

    public long Milliwatts(string instance)
    {
        Reads++;
        if (Fail) throw new InvalidOperationException("the counter went away");
        return _instances.First(i => i.Name == instance).Milliwatts;
    }

    public void Dispose()
    {
    }
}
