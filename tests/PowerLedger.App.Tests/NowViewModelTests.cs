using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

public class NowViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 7, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeLink _link = new();
    private readonly FakeHistory _history = new();
    private int _starts;

    private NowViewModel Model()
        => new(_link, _history, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, co2KgPerKwh: 0.38, startService: () => _starts++);

    [Fact]
    public void A_reading_fills_the_live_panel()
    {
        var model = Model();
        _link.Push(Frames.At(Now, totalW: 34.2));

        model.Live.Watts.ShouldBe(34.2);
        model.Live.Eyebrow.ShouldBe("Live · battery discharge · 14:32:07");
        model.Live.Quality.ShouldBe(Quality.Measured);
        model.Live.QualityNote.ShouldBe("Windows battery report · 1 s samples");
        model.Live.Spark.ShouldBe(new[] { new SparkSample(0, 34.2) });
        model.Live.Budget.Select(r => r.Name).ShouldBe(new[] { "CPU package", "GPU", "Display", "Rest of system" });
        model.Live.Budget[0].Watts.ShouldBe("14.6 W");
        model.Live.Budget[0].Percent.ShouldBe("43%");
        model.Live.BudgetTotalW.ShouldBe(34.2, 1e-9);
        model.TrayTooltip.ShouldStartWith("34.2 W · Measured");
    }

    [Theory]
    [InlineData(Quality.Measured, 0, "Windows battery report · 1 s samples")]
    [InlineData(Quality.Calibrated, 0, "Model with a baseline learned on battery · ±10%")]
    [InlineData(Quality.Estimated, 0, "Model from the sensors and the machine profile · ±20%")]
    [InlineData(Quality.Measured, 37.7, "Windows battery report, with the monitors' own figures · 1 s samples")]
    [InlineData(Quality.Calibrated, 37.7, "Model with a baseline learned on battery, ±10%, plus the monitors' own figures")]
    [InlineData(Quality.Estimated, 37.7, "Model from the sensors and the machine profile, ±20%, plus the monitors' own figures")]
    public void The_note_says_where_the_reading_came_from_and_that_monitors_in_it_came_from_their_own_figures(Quality quality, double monitors, string note)
    {
        // The model's margin covers only what it models. A measured reading isn't the battery's report plus the monitors: a
        // monitor running off the laptop is already in the report, and its own figure only splits it off.
        var model = Model();

        _link.Push(Frames.At(Now, totalW: 34.2 + monitors, quality: quality, monitors: monitors));

        model.Live.QualityNote.ShouldBe(note);
    }

    [Fact]
    public void Budget_rows_say_what_each_part_is_and_how_it_was_known()
    {
        var model = Model();
        _history.Snapshot = Snapshots.Typical(Now);
        model.RefreshHistory();
        _link.Push(Frames.At(Now));

        model.Live.Budget[0].Detail.ShouldBe("Core i7-1165G7 · 30% load · measured");
        model.Live.Budget[1].Detail.ShouldBe("GeForce MX330 · 31% load · modelled");
        model.Live.Budget[2].Detail.ShouldBe("15.3 in · brightness 60%");
        model.Live.Budget[3].Detail.ShouldBe("RAM, SSD, board, radios · measured remainder");
    }

    [Fact]
    public void The_display_row_adds_the_monitors_the_service_counts()
    {
        _link.Status = Statuses.WithMonitors(Statuses.Dell, Statuses.Aoc, Statuses.Aoc with { Key = "AOC2402-2", Counted = false });
        _history.Snapshot = Snapshots.Typical(Now);
        var model = Model();
        model.Start();
        _link.Connect(true);
        _link.Push(Frames.At(Now));

        model.Live.Budget[2].Detail.ShouldBe("15.3 in · brightness 60% · plus 2 monitors, estimated");

        _link.Status = Statuses.WithMonitors(Statuses.Dell);
        _clock.Advance(NowViewModel.StatusEvery);
        model.Live.Budget[2].Detail.ShouldBe("15.3 in · brightness 60% · plus 1 monitor");

        _link.Status = Statuses.Running();
        _clock.Advance(NowViewModel.StatusEvery);
        model.Live.Budget[2].Detail.ShouldBe("15.3 in · brightness 60%");
    }

    [Fact]
    public void The_display_row_says_how_the_monitors_counted_were_figured_by_the_least_sure_of_them()
    {
        var unread = Statuses.Dell with { Key = "DELA0B1-2", Brightness = null };
        var typed = Statuses.Aoc with { Key = "AOC2402-2", OnWatts = 17, Source = MonitorSource.Typed, WattsNow = 17 };
        _history.Snapshot = Snapshots.Typical(Now);
        var model = Model();
        model.Start();
        _link.Connect(true);
        _link.Push(Frames.At(Now));

        string With(params MonitorStatus[] monitors)
        {
            _link.Status = Statuses.WithMonitors(monitors);
            _clock.Advance(NowViewModel.StatusEvery);
            return model.Live.Budget[2].Detail;
        }

        With(unread, Statuses.Aoc).ShouldBe("15.3 in · brightness 60% · plus 2 monitors, estimated");
        With(Statuses.Dell, unread).ShouldBe("15.3 in · brightness 60% · plus 2 monitors, brightness assumed");
        With(Statuses.Dell, typed).ShouldBe("15.3 in · brightness 60% · plus 2 monitors");   // a typed figure takes no brightness
        With(unread, Statuses.Aoc with { Counted = false }).ShouldBe("15.3 in · brightness 60% · plus 1 monitor, brightness assumed");
    }

    [Fact]
    public void The_display_row_says_how_many_of_the_monitors_it_adds_are_off_or_on_standby()
    {
        // A monitor that hasn't said, or said too long ago, counts at its figure on, as one that said it was on does, so the
        // row says nothing of either.
        var on = Statuses.Dell with { PowerState = MonitorPowerState.On };
        var unknown = Statuses.Dell with { Key = "DELA0B1-2" };
        var off = Statuses.Dell with { Key = "DELA0B1-3", PowerState = MonitorPowerState.Off, WattsNow = 0.2 };
        var standby = Statuses.Dell with { Key = "DELA0B1-4", PowerState = MonitorPowerState.Standby, WattsNow = 0.3 };
        _history.Snapshot = Snapshots.Typical(Now);
        var model = Model();
        model.Start();
        _link.Connect(true);
        _link.Push(Frames.At(Now));

        string With(params MonitorStatus[] monitors)
        {
            _link.Status = Statuses.WithMonitors(monitors);
            _clock.Advance(NowViewModel.StatusEvery);
            return model.Live.Budget[2].Detail;
        }

        With(on, unknown).ShouldBe("15.3 in · brightness 60% · plus 2 monitors");
        With(on, off).ShouldBe("15.3 in · brightness 60% · plus 2 monitors, 1 off");
        With(standby, unknown).ShouldBe("15.3 in · brightness 60% · plus 2 monitors, 1 on standby");
        With(off, standby, on).ShouldBe("15.3 in · brightness 60% · plus 3 monitors, 1 off, 1 on standby");
        With(off, off with { Key = "DELA0B1-5" }, unknown).ShouldBe("15.3 in · brightness 60% · plus 3 monitors, 2 off");
        With(off, standby).ShouldBe("15.3 in · brightness 60% · plus 2 monitors, 1 off, 1 on standby");
        With(on, off with { Counted = false, WattsNow = 0 }).ShouldBe("15.3 in · brightness 60% · plus 1 monitor");   // only those it adds

        // When every monitor it adds says the same, the row says so without counting them again.
        With(off).ShouldBe("15.3 in · brightness 60% · plus 1 monitor, off");
        With(off, off with { Key = "DELA0B1-5" }).ShouldBe("15.3 in · brightness 60% · plus 2 monitors, both off");
        With(standby).ShouldBe("15.3 in · brightness 60% · plus 1 monitor, on standby");
        With(standby, standby with { Key = "DELA0B1-5" }, standby with { Key = "DELA0B1-6" })
            .ShouldBe("15.3 in · brightness 60% · plus 3 monitors, all on standby");
    }

    [Fact]
    public void A_monitor_off_or_on_standby_still_says_how_it_was_figured_but_assumes_no_brightness()
    {
        // Off, a monitor counts at its off figure and, on standby, at its sleep figure. Those are listed for its model or
        // estimated as its figure on is, so an estimated one still makes the row say so, but no brightness scales them.
        var unread = Statuses.Dell with { Key = "DELA0B1-2", Brightness = null };
        var typed = Statuses.Aoc with { Key = "AOC2402-2", OnWatts = 17, Source = MonitorSource.Typed, WattsNow = 17 };
        _history.Snapshot = Snapshots.Typical(Now);
        var model = Model();
        model.Start();
        _link.Connect(true);
        _link.Push(Frames.At(Now));

        string With(params MonitorStatus[] monitors)
        {
            _link.Status = Statuses.WithMonitors(monitors);
            _clock.Advance(NowViewModel.StatusEvery);
            return model.Live.Budget[2].Detail;
        }

        With(Statuses.Dell, Statuses.Aoc with { PowerState = MonitorPowerState.Off, WattsNow = 0.2 })
            .ShouldBe("15.3 in · brightness 60% · plus 2 monitors, 1 off, estimated");
        With(Statuses.Dell, Statuses.Aoc with { PowerState = MonitorPowerState.Standby, WattsNow = 0.2 })
            .ShouldBe("15.3 in · brightness 60% · plus 2 monitors, 1 on standby, estimated");
        With(Statuses.Dell, unread with { PowerState = MonitorPowerState.Off, WattsNow = 0.2 })
            .ShouldBe("15.3 in · brightness 60% · plus 2 monitors, 1 off");
        With(Statuses.Dell, unread with { PowerState = MonitorPowerState.Standby, WattsNow = 0.3 })
            .ShouldBe("15.3 in · brightness 60% · plus 2 monitors, 1 on standby");
        With(Statuses.Dell with { PowerState = MonitorPowerState.Off, WattsNow = 0.2 }, unread with { PowerState = MonitorPowerState.On })
            .ShouldBe("15.3 in · brightness 60% · plus 2 monitors, 1 off, brightness assumed");
        With(Statuses.Dell, typed with { PowerState = MonitorPowerState.Off, WattsNow = 0.2 })
            .ShouldBe("15.3 in · brightness 60% · plus 2 monitors, 1 off");   // a typed monitor adds nothing, as it does on
    }

    [Fact]
    public async Task A_display_band_with_no_panel_to_speak_of_says_what_it_counts()
    {
        _link.Status = Statuses.WithMonitors();
        _history.Snapshot = Snapshots.Typical(Now) with { Machine = new MachineNames("AMD Ryzen 7 7700X", null, 0) };
        var model = Model();
        model.RefreshHistory();
        _link.Connect(true);
        _link.Push(Frames.At(Now) with { Brightness = null });
        model.Live.Budget[2].Detail.ShouldBe("2 monitors, estimated");

        _link.Status = Statuses.WithMonitors(Statuses.Dell with { PowerState = MonitorPowerState.Off, WattsNow = 0.2 }, Statuses.Aoc);
        await model.PollAsync();
        model.Live.Budget[2].Detail.ShouldBe("2 monitors, 1 off, estimated");

        _link.Status = Statuses.Running();
        await model.PollAsync();
        model.Live.Budget[2].Detail.ShouldBe("built-in panel");

        _link.Push(Frames.At(Now, display: 0) with { Brightness = null });
        model.Live.Budget[2].Detail.ShouldBe("nothing counted");
    }

    [Fact]
    public void A_card_windows_has_switched_off_says_so()
    {
        var model = Model();
        _link.Push(Frames.At(Now, gpu: 0, gpuMeasured: true, gpuLoad: 0));
        model.Live.Budget[1].Detail.ShouldBe("switched off");
    }

    [Fact]
    public void History_fills_today_s_ledger_the_way_the_user_reads_it()
    {
        var model = Model();
        _history.Snapshot = Snapshots.Typical(Now);
        model.RefreshHistory();

        model.Today.Date.ShouldBe("Tue 8 Sep");
        model.Today.Energy.ShouldBe("0.284");
        model.Today.Cost.ShouldBe("$0.05");
        model.Today.Tariff.ShouldBe("at $0.17 / kWh");
        model.Today.Average.ShouldBe("41");
        model.Today.Peak.ShouldBe("68");
        model.Today.PeakAt.ShouldBe("at 14:00");
        model.Today.On.ShouldBe("7h 05m");
        model.Today.Idle.ShouldBe("45m");
        model.Today.IdleWasted.ShouldBe("11 Wh wasted");
        model.Today.Asleep.ShouldBe("7h 30m");
        model.Today.Co2.ShouldBe("0.11");
        model.Today.Co2Factor.ShouldBe("0.38 kg / kWh grid");
        model.IsCollecting.ShouldBeFalse();
    }

    [Fact]
    public void History_fills_the_month_s_ledger()
    {
        var model = Model();
        _history.Snapshot = Snapshots.Typical(Now);
        model.RefreshHistory();

        model.Month.Name.ShouldBe("September");
        model.Month.Summary.ShouldBe("8 days · 62% measured");
        model.Month.Energy.ShouldBe("2.74");
        model.Month.Cost.ShouldBe("$0.47");
        model.Month.Projected.ShouldBe("$1.85");
        model.Month.ProjectedEnergy.ShouldBe("10.8 kWh");
        model.Month.DailyAverage.ShouldBe("0.360 kWh");
        model.Month.IdleWaste.ShouldBe("0.210 kWh");
        model.Month.NextReport.ShouldBe("1 Oct");
    }

    [Fact]
    public void The_meter_marks_today_s_average_and_the_higher_of_history_s_peak_and_the_live_one()
    {
        var model = Model();
        _history.Snapshot = Snapshots.Typical(Now);
        model.RefreshHistory();
        _link.Push(Frames.At(Now, totalW: 90));

        model.Live.PeakW.ShouldBe(90);
        model.Live.AverageW.ShouldBe(41);
        model.Live.Meter.Max.ShouldBe(100);
    }

    [Fact]
    public void Until_today_has_a_folded_minute_the_ledgers_are_collecting()
    {
        var model = Model();
        model.IsCollecting.ShouldBeTrue();
        var empty = Snapshots.Typical(Now);
        _history.Snapshot = empty with { Today = empty.Today with { OnHours = 0 } };
        model.RefreshHistory();
        model.IsCollecting.ShouldBeTrue();
    }

    [Fact]
    public void Without_the_service_the_screen_says_so_after_the_grace_period_and_offers_to_start_it()
    {
        var model = Model();
        model.Start();
        model.Connection.ShouldBe(Connection.Connecting);
        _clock.Advance(NowViewModel.ConnectGrace);
        model.IsServiceDown.ShouldBeTrue();
        model.Status.State.ShouldBe("Service not running");

        model.StartService.Execute(null);
        _starts.ShouldBe(1);

        _link.Connect(true);
        model.IsServiceDown.ShouldBeFalse();
        model.Status.State.ShouldBe("Service running");
        model.Status.Service.ShouldBe("Service 0.1.0");
        model.Status.Sampling.ShouldBe("Sampling 1 s");
        model.Status.Calibration.ShouldBe("Calibrating · 15m of 30m on battery");
        model.Status.Database.ShouldBe("Database 31 MB");
    }

    [Fact]
    public void With_no_energy_meter_and_no_battery_the_screen_says_so_honestly()
    {
        _link.Status = Statuses.Running(energyMeter: false, battery: false);
        var model = Model();
        _link.Connect(true);
        model.IsSensorless.ShouldBeTrue();
    }

    [Theory]
    [InlineData(ChassisKind.Laptop, false)]
    [InlineData(ChassisKind.Desktop, true)]
    public void A_battery_is_a_power_sensor_only_on_a_laptop(ChassisKind chassis, bool sensorless)
    {
        // On a desktop the battery Windows shows is a UPS, which powers more than the machine and measures nothing.
        _link.Status = Statuses.Running(energyMeter: false, battery: true);
        _link.Settings = ServiceSettings.Default with { Profile = MachineProfile.DefaultLaptop with { Chassis = chassis } };
        var model = Model();
        _link.Connect(true);
        model.IsSensorless.ShouldBe(sensorless);
    }

    [Fact]
    public void A_desktop_s_status_never_speaks_of_calibrating_on_battery()
    {
        _link.Settings = ServiceSettings.Default with { Profile = MachineProfile.DefaultDesktop };
        var model = Model();
        _link.Connect(true);
        model.Status.Calibration.ShouldBe("Desktop · always estimated");
    }

    [Fact]
    public void A_chassis_corrected_after_connecting_reaches_the_screen_with_the_next_status()
    {
        var model = Model();
        model.Start();
        _link.Connect(true);
        model.Status.Calibration.ShouldBe("Calibrating · 15m of 30m on battery");

        _link.Settings = ServiceSettings.Default with { Profile = MachineProfile.DefaultDesktop };
        _clock.Advance(NowViewModel.StatusEvery);
        model.Status.Calibration.ShouldBe("Desktop · always estimated");
    }

    [Fact]
    public void Losing_the_service_clears_the_live_panel()
    {
        var model = Model();
        _link.Connect(true);
        _link.Push(Frames.At(Now));
        _link.Connect(false);
        model.Live.ShouldBe(LivePanel.Waiting);
        model.IsServiceDown.ShouldBeTrue();
    }

    [Theory]
    [InlineData("11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", "Core i7-1165G7")]
    [InlineData("AMD Ryzen 7 5800U with Radeon Graphics", "Ryzen 7 5800U")]
    [InlineData("NVIDIA GeForce MX330", "GeForce MX330")]
    [InlineData("Intel(R) Core(TM) Ultra 7 155H", "Core Ultra 7 155H")]
    public void Names_lose_the_trademarks_the_maker_and_the_clock_speed(string full, string expected)
        => NowViewModel.ShortName(full).ShouldBe(expected);

    [Fact]
    public void Todays_chart_spans_the_day_and_its_legend_gives_each_bands_energy()
    {
        var model = Model();
        _history.Snapshot = Snapshots.Typical(Now);
        model.RefreshHistory();

        model.Chart.Capacity.ShouldBe(288);
        model.Chart.NowAt.ShouldNotBeNull().ShouldBe(174.42, 0.01);           // 14:32:07 in five-minute buckets
        model.Legend.ShouldBe(new ChartLegend("120 Wh", "30 Wh", "28 Wh", "106 Wh"));
    }

    [Fact]
    public void A_new_co2_factor_redraws_todays_ledger()
    {
        var model = Model();
        _history.Snapshot = Snapshots.Typical(Now);
        model.RefreshHistory();
        model.Today.Co2.ShouldBe("0.11");                                     // 0.284 kWh at 0.38

        model.Co2KgPerKwh = 0.8;
        model.Today.Co2.ShouldBe("0.23");
        model.Today.Co2Factor.ShouldBe("0.80 kg / kWh grid");
    }
}
