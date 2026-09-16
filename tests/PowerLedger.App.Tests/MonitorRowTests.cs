using System.Globalization;
using System.Text.RegularExpressions;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>What a monitor's row in the wizard and Settings says of the monitor now.</summary>
public class MonitorRowTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    [Theory]
    [InlineData(MonitorPowerState.On, true, 24.3, "on · 24.3 W now")]
    [InlineData(MonitorPowerState.Standby, true, 0.3, "standby · 0.3 W now")]
    [InlineData(MonitorPowerState.Off, true, 0.2, "off · 0.2 W now")]
    [InlineData(MonitorPowerState.Unknown, true, 24.3, "can't tell if it's on · 24.3 W now")]
    [InlineData(MonitorPowerState.On, false, 0, "on · not counted")]
    [InlineData(MonitorPowerState.Standby, false, 0, "standby · not counted")]
    [InlineData(MonitorPowerState.Off, false, 0, "off · not counted")]
    [InlineData(MonitorPowerState.Unknown, false, 0, "not counted")]
    public void A_monitor_says_whether_it_is_on_as_it_said_or_that_powerledger_cant_tell_and_what_it_draws_now(
        MonitorPowerState state, bool counted, double watts, string now)
    {
        // Where PowerLedger can't tell, the Count tick decides, so saying so matters only for a monitor that counts.
        var row = new MonitorRow(Statuses.Dell with { PowerState = state, Counted = counted, WattsNow = watts }, null, English);

        row.Now.ShouldBe(now);
    }

    [Theory]
    [InlineData(MonitorSource.Model, MonitorPowerState.On, "brightness unknown, assumed 75%")]
    [InlineData(MonitorSource.Model, MonitorPowerState.Unknown, "brightness unknown, assumed 75%")]
    [InlineData(MonitorSource.Model, MonitorPowerState.Standby, "brightness unknown")]
    [InlineData(MonitorSource.Model, MonitorPowerState.Off, "brightness unknown")]
    [InlineData(MonitorSource.Estimate, MonitorPowerState.On, "brightness unknown, assumed 75%")]
    [InlineData(MonitorSource.Estimate, MonitorPowerState.Unknown, "brightness unknown, assumed 75%")]
    [InlineData(MonitorSource.Estimate, MonitorPowerState.Standby, "brightness unknown")]
    [InlineData(MonitorSource.Estimate, MonitorPowerState.Off, "brightness unknown")]
    [InlineData(MonitorSource.Typed, MonitorPowerState.On, "brightness unknown")]
    [InlineData(MonitorSource.Typed, MonitorPowerState.Off, "brightness unknown")]
    public void A_monitor_off_or_on_standby_assumes_no_brightness_since_none_scales_what_it_draws(MonitorSource source, MonitorPowerState state, string brightness)
    {
        // Off, a monitor counts at its off figure and, on standby, at its sleep figure, and a typed figure is taken as it is.
        // No brightness scales any of them.
        var row = new MonitorRow(Statuses.Dell with { Source = source, PowerState = state, Brightness = null }, null, English);

        row.Brightness.ShouldBe(brightness);
    }

    [Theory]
    [InlineData(MonitorPowerState.On)]
    [InlineData(MonitorPowerState.Standby)]
    [InlineData(MonitorPowerState.Off)]
    [InlineData(MonitorPowerState.Unknown)]
    public void A_brightness_read_shows_in_every_state_and_the_row_says_once_what_was_read_from_the_monitor(MonitorPowerState state)
    {
        // The state is read from the monitor as the brightness is, and Settings shows the two texts together.
        var row = new MonitorRow(Statuses.Dell with { PowerState = state }, null, English);

        row.Brightness.ShouldBe("brightness 60%, read from the monitor");
        Regex.Count($"{row.Now}\n{row.Brightness}", "read from the monitor").ShouldBe(1);
    }

    [Fact]
    public void A_monitor_switched_off_says_so_with_the_next_status_and_one_that_stops_saying_says_it_cant_tell()
    {
        var row = new MonitorRow(Statuses.Dell with { PowerState = MonitorPowerState.On }, null, English);
        (row.Now, row.Brightness).ShouldBe(("on · 24.3 W now", "brightness 60%, read from the monitor"));

        row.Refresh(Statuses.Dell with { PowerState = MonitorPowerState.Off, Brightness = null, OffWatts = 0.2, WattsNow = 0.2 });
        (row.Now, row.Brightness).ShouldBe(("off · 0.2 W now", "brightness unknown"));

        // Many monitors stop answering once switched off at their own button, and after three minutes the service can't tell.
        row.Refresh(Statuses.Dell with { Brightness = null, OffWatts = 0.2, WattsNow = 26.9 });
        (row.Now, row.Brightness).ShouldBe(("can't tell if it's on · 26.9 W now", "brightness unknown, assumed 75%"));
    }
}
