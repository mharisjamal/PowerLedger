using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

/// <summary>Part B of "measure more": a machine's own input meter (a platform power meter or its BMC) and the NUT UPS
/// figures, as the model takes them.</summary>
public class PowerModelSystemMeterTests
{
    [Theory]
    [InlineData(SystemMeterKind.PowerMeter, TotalSource.PowerMeter)]
    [InlineData(SystemMeterKind.Bmc, TotalSource.Bmc)]
    public void A_machine_s_own_input_meter_is_its_measured_total_and_nothing_is_divided(SystemMeterKind kind, TotalSource expected)
    {
        var r = Desktop().Evaluate(WithMeter(DesktopTick(), 240, kind));

        (r.TotalSource, r.Quality).ShouldBe((expected, Quality.Measured));
        r.TotalW.ShouldBe(240, 1e-9);
        r.Components.PsuLoss.ShouldBe(0);
        r.Components.Unattributed.ShouldBe(240 - 192, 1e-9);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Fact]
    public void A_monitor_with_a_plug_of_its_own_is_added_to_the_meter_s_reading()
    {
        var r = Desktop(monitors: new FixedDraw(new(OwnPlug: 25, FromPc: 0))).Evaluate(WithMeter(DesktopTick(), 240));

        r.TotalW.ShouldBe(265, 1e-9);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-3.0)]
    [InlineData(double.NaN)]
    public void A_meter_that_gives_no_watts_leaves_the_total_to_the_model(double? watts)
    {
        var r = Desktop().Evaluate(WithMeter(DesktopTick(), watts));

        (r.TotalSource, r.Quality).ShouldBe((TotalSource.Model, Quality.Estimated));
        r.TotalW.ShouldBe(192 / 0.85, 1e-9);
    }

    [Fact]
    public void A_ups_said_to_power_this_pc_wins_over_the_meter_and_the_meter_wins_over_a_power_supply()
    {
        var all = WithMeter(DesktopTick(), 240) with { UpsOutputW = 250, UpsSource = UpsPowerSource.ActivePower, PsuWallW = 260 };

        Desktop(profile: MachineProfile.DefaultDesktop with { UpsLoad = UpsLoad.ThisPc }).Evaluate(all).TotalSource.ShouldBe(TotalSource.Ups);
        Desktop().Evaluate(all).TotalSource.ShouldBe(TotalSource.PowerMeter);
    }

    [Fact]
    public void A_laptop_on_its_battery_never_takes_a_meter_on_its_input()
    {
        var tick = WithMeter(TestData.Laptop(battery: 34.2, onBattery: true), 50);

        new PowerModel(MachineProfile.DefaultLaptop, HardwareFacts.LaptopDefaults, new PowerModelOptions(), new FixedBaseline(null))
            .Evaluate(tick).TotalSource.ShouldBe(TotalSource.Battery);
    }

    [Theory]
    [InlineData(UpsPowerSource.ApparentPower, Quality.Measured)]
    [InlineData(UpsPowerSource.ApparentPowerAssumedFactor, Quality.Estimated)]
    public void A_ups_s_apparent_power_is_measured_only_with_the_factor_the_ups_reports(UpsPowerSource source, Quality quality)
    {
        var tick = DesktopTick() with { UpsOutputW = 250, UpsSource = source };

        var r = Desktop(profile: MachineProfile.DefaultDesktop with { UpsLoad = UpsLoad.ThisPc }).Evaluate(tick);

        (r.TotalSource, r.Quality).ShouldBe((TotalSource.Ups, quality));
        r.TotalW.ShouldBe(250, 1e-9);
    }

    /// <summary>A desktop tick where 192 W go through its Bronze supply, as in <see cref="PowerModelTests"/>.</summary>
    private static Sample DesktopTick() => TestData.Laptop(cpu: 50, gpu: 120, brightness: null);

    private static Sample WithMeter(Sample s, double? watts, SystemMeterKind kind = SystemMeterKind.PowerMeter)
        => s with { SystemMeterW = watts, SystemMeter = kind, SystemMeterName = "Power Meter (0)" };

    private static PowerModel Desktop(MachineProfile? profile = null, IMonitorDraw? monitors = null)
        => new(profile ?? MachineProfile.DefaultDesktop, HardwareFacts.DesktopDefaults, new PowerModelOptions(), new FixedBaseline(null), monitors);

    private sealed class FixedDraw(MonitorWatts on) : IMonitorDraw
    {
        public MonitorWatts Watts(bool displayOn) => on;
    }
}
