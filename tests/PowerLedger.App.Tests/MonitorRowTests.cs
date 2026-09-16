using System.Globalization;
using System.Text.RegularExpressions;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>What a monitor's row in the wizard and Settings says of the monitor now.</summary>
public class MonitorRowTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>A 27-inch 1440p monitor in the list, whose brightness was read, which Windows drives at 165 Hz: 0.006 W a
    /// megapixel a hertz above 60 adds 2.3 W to the 24.3 W it draws at 60 Hz.</summary>
    private static readonly MonitorStatus Fast = Statuses.Dell with
    {
        Key = "GSM5B08-105NTAB12345", Instance = @"DISPLAY\GSM5B08\5&2F5A1B&0&UID4356", Name = "LG ULTRAGEAR", Width = 2560, Height = 1440,
        RefreshHz = 165, RefreshWatts = 2.322432, WattsNow = 26.622432,
    };

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

    [Theory]
    [InlineData(165.0, "27 in · 2560 × 1440 · 165 Hz")]
    [InlineData(59.94, "27 in · 2560 × 1440 · 59.94 Hz")]
    [InlineData(143.856, "27 in · 2560 × 1440 · 143.86 Hz")]
    [InlineData(59.997, "27 in · 2560 × 1440 · 60 Hz")]
    [InlineData(null, "27 in · 2560 × 1440")]
    public void The_size_gives_the_refresh_rate_windows_drives_the_monitor_at_to_a_hundredth_of_a_hertz(double? refreshHz, string size)
    {
        // A rate Windows gives as a fraction keeps its hundredths, so 59.94 Hz, a mode of its own, isn't called 60 Hz.
        var row = new MonitorRow(Fast with { RefreshHz = refreshHz, RefreshWatts = 0 }, null, English);

        row.Size.ShouldBe(size);
    }

    [Theory]
    [InlineData(MonitorPowerState.On, "on · 26.6 W now, incl. 2.3 W for 165 Hz")]
    [InlineData(MonitorPowerState.Unknown, "can't tell if it's on · 26.6 W now, incl. 2.3 W for 165 Hz")]
    public void What_a_refresh_rate_above_60_hz_adds_is_said_with_what_the_monitor_draws_now(MonitorPowerState state, string now)
    {
        var row = new MonitorRow(Fast with { PowerState = state }, null, English);

        row.Now.ShouldBe(now);
    }

    [Fact]
    public void A_refresh_rate_that_adds_nothing_that_shows_says_nothing_of_it()
    {
        // Windows drives some 60 Hz modes a hair faster: 60.02 Hz adds under a thousandth of a watt, which shows as 0.0 W.
        var hair = new MonitorRow(Fast with { RefreshHz = 60.02, RefreshWatts = 0.000442, WattsNow = 24.300442 }, null, English);
        (hair.Size, hair.Now).ShouldBe(("27 in · 2560 × 1440 · 60.02 Hz", "can't tell if it's on · 24.3 W now"));

        // The service adds nothing for a monitor off or on standby, an OLED panel or a typed figure, whatever its rate.
        var off = new MonitorRow(Fast with { PowerState = MonitorPowerState.Off, RefreshWatts = 0, WattsNow = 0.2 }, null, English);
        (off.Size, off.Now).ShouldBe(("27 in · 2560 × 1440 · 165 Hz", "off · 0.2 W now"));
    }
}
