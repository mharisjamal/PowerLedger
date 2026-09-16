using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class MonitorBoardTests
{
    /// <summary>Real listings from the shipped table: the Dell, and three 27-inch 1080p monitors for the estimate's median of
    /// 14.41 W on, 0.13 W asleep and 0.09 W off.</summary>
    internal static readonly MonitorCatalogue Catalogue = MonitorCatalogue.Parse(new StringReader("""
        brand,model_number,model_name,alternatives,inches,width,height,panel,on_w,sleep_w,off_w,max_nits,hdr,certified
        DELL,U2723QEt,U2723QE,U2723QX,27,3840,2160,IPS LCD,28.32,0.74,0.3,400,,2021-07-14
        Acer,CB272,CB272,CB27******,27,1920,1080,IPS LCD,14.41,0.11,0.09,227.6,,2024-08-15
        Acer,CB272,CB272_q,,27,1920,1080,IPS LCD,13.35,0.13,0.09,250,,2026-05-14
        Acer,CB273,CB273_v,CB273***,27,1920,1080,IPS LCD,17.07,0.35,0.29,250,,2025-04-22
        """));

    internal static readonly MonitorFacts Dell = new(
        @"DISPLAY\DELA0B1\5&2F5A1B&0&UID4353", "DELA0B1-7MKZG34", "DEL", "A0B1", "DELL U2723QE", 27, 3840, 2160);

    /// <summary>A monitor that gives no name or serial number, and that the list doesn't know.</summary>
    internal static readonly MonitorFacts Unnamed = new(
        @"DISPLAY\GSM5B08\7&1A2B&0&UID4354", @"DISPLAY\GSM5B08\7&1A2B&0&UID4354", "GSM", "5B08", "", 27, 1920, 1080);

    /// <summary>A 15.6-inch portable monitor, which the list doesn't know.</summary>
    internal static readonly MonitorFacts Portable = new(
        @"DISPLAY\AUS1B2C\7&3C4D&0&UID4355", "AUS1B2C-N7LMTF012345", "AUS", "1B2C", "ASUS MB16AC", 15.6, 1920, 1080);

    private readonly FakeTimeProvider _clock = new();
    private readonly MonitorBoard _board;

    public MonitorBoardTests() => _board = new MonitorBoard(Catalogue, _clock);

    /// <summary>A laptop's profile holding these choices, as the loop gives the board its settings.</summary>
    private static MachineProfile Laptop(params MonitorChoice[] choices) => MachineProfile.DefaultLaptop with { Monitors = choices };

    private static MachineProfile Desktop(params MonitorChoice[] choices) => MachineProfile.DefaultDesktop with { Monitors = choices };

    /// <summary>What the App read from a monitor about whether it is on.</summary>
    private static MonitorPowerReading Said(MonitorFacts monitor, MonitorPowerState state) => new() { Instance = monitor.Instance, State = state };

    /// <summary>What the App read about how Windows drives a monitor.</summary>
    private static MonitorDisplayReading Shown(MonitorFacts monitor, double refreshHz, bool hdr = false)
        => new() { Instance = monitor.Instance, RefreshHz = refreshHz, Hdr = hdr };

    /// <summary>What the Dell draws at a brightness, or at the one assumed for null: its 28.32 W were measured at 200 of its
    /// 400 cd/m², half way up its scale.</summary>
    internal static double DellAt(double? brightness) => MonitorPower.At(28.32, brightness, 0.5);

    /// <summary>What the unnamed monitor draws at a brightness, or at the one assumed for null: the 27-inch 1080p monitors'
    /// median 14.41 W, measured at 200 of their median 250 cd/m², 80% of the way up.</summary>
    internal static double UnnamedAt(double? brightness) => MonitorPower.At(14.41, brightness, 0.8);

    [Fact]
    public void With_no_monitors_the_list_is_empty_and_they_draw_nothing()
    {
        _board.Status(displayOn: true).ShouldBeEmpty();
        _board.Watts(displayOn: true).ShouldBe(new MonitorWatts(OwnPlug: 0, FromPc: 0));

        _board.Detected([]);
        _board.Status(displayOn: false).ShouldNotBeNull().ShouldBeEmpty();
        _board.Watts(displayOn: false).ShouldBe(new MonitorWatts(OwnPlug: 0, FromPc: 0));
    }

    [Fact]
    public void A_monitor_the_list_knows_counts_its_measured_figure_at_the_brightness_assumed()
    {
        _board.Detected([Dell]);

        var monitor = _board.Status(displayOn: true).ShouldHaveSingleItem();
        monitor.ShouldBe(new MonitorStatus
        {
            Key = "DELA0B1-7MKZG34",
            Instance = @"DISPLAY\DELA0B1\5&2F5A1B&0&UID4353",
            Name = "DELL U2723QE",
            Inches = 27,
            Width = 3840,
            Height = 2160,
            OnWatts = 28.32,
            SleepWatts = 0.74,
            OffWatts = 0.3,
            PowerState = MonitorPowerState.Unknown,
            RefreshHz = null,
            RefreshWatts = 0,
            Hdr = null,
            Source = MonitorSource.Model,
            Counted = true,
            CountedByDefault = true,
            OwnPlug = true,
            OwnPlugByDefault = true,
            Brightness = null,
            WattsNow = monitor.WattsNow,
        });
        monitor.WattsNow.ShouldBe(DellAt(null), 1e-9);
        monitor.WattsNow.ShouldBe(33.69, 0.005);
        _board.Watts(displayOn: true).OwnPlug.ShouldBe(DellAt(null), 1e-9);
        _board.Watts(displayOn: true).FromPc.ShouldBe(0);
    }

    [Fact]
    public void A_figure_worked_out_is_the_draw_where_200_nits_sits_on_the_monitor_s_brightness_scale()
    {
        // The Dell reaches 400 cd/m², so it draws its listed 28.32 W half way up. The 27-inch 1080p monitors reach 250 at the
        // median, so the unnamed monitor draws their 14.41 W at 80%. The portable monitor's figure comes from the formula,
        // with no luminance, so it draws that figure at 75%.
        _board.Detected([Dell, Unnamed, Portable]);
        _board.Report(
        [
            new MonitorBrightness { Instance = Dell.Instance, Brightness = 0.5 },
            new MonitorBrightness { Instance = Unnamed.Instance, Brightness = 0.8 },
            new MonitorBrightness { Instance = Portable.Instance, Brightness = 0.75 },
        ]);

        var status = _board.Status(displayOn: true);
        status[0].WattsNow.ShouldBe(28.32, 1e-9);
        status[1].WattsNow.ShouldBe(14.41, 1e-9);
        status[2].WattsNow.ShouldBe(MonitorEstimate.For(15.6, 1920, 1080, Catalogue).OnW, 1e-9);
    }

    [Fact]
    public void A_monitor_the_list_does_not_know_is_estimated_from_its_size_and_named_by_its_maker_and_product_code()
    {
        _board.Detected([Unnamed]);

        var monitor = _board.Status(displayOn: true).ShouldHaveSingleItem();
        monitor.Name.ShouldBe("GSM 5B08");
        monitor.Source.ShouldBe(MonitorSource.Estimate);
        monitor.OnWatts.ShouldBe(14.41);
        monitor.SleepWatts.ShouldBe(0.13);
        monitor.OffWatts.ShouldBe(0.09);
        monitor.Counted.ShouldBeTrue();
    }

    [Fact]
    public void A_monitor_the_user_does_not_count_draws_nothing_but_still_shows_its_figure()
    {
        _board.Detected([Dell, Unnamed]);
        _board.Choose(Laptop(new MonitorChoice { Key = Unnamed.Key, Counted = false }));

        var status = _board.Status(displayOn: true);
        status.Select(m => m.Counted).ShouldBe([true, false]);
        status.Select(m => m.CountedByDefault).ShouldBe([true, true]);
        status[1].OnWatts.ShouldBe(14.41);
        status[1].WattsNow.ShouldBe(0);
        _board.Watts(displayOn: true).OwnPlug.ShouldBe(DellAt(null), 1e-9);
        _board.Watts(displayOn: false).OwnPlug.ShouldBe(0.74, 1e-9);
    }

    [Fact]
    public void With_monitors_left_out_by_default_one_the_user_has_not_chosen_for_is_not_counted_and_a_choice_counts_it()
    {
        _board.Detected([Dell, Unnamed]);
        _board.Choose(Laptop(new MonitorChoice { Key = Unnamed.Key, Counted = true }) with { CountMonitorsByDefault = false });

        var status = _board.Status(displayOn: true);
        status.Select(m => (m.Counted, m.CountedByDefault)).ShouldBe([(false, false), (true, false)]);
        status[0].OnWatts.ShouldBe(28.32);
        status[0].WattsNow.ShouldBe(0);
        status[1].WattsNow.ShouldBe(UnnamedAt(null), 1e-9);
        _board.Watts(displayOn: true).OwnPlug.ShouldBe(UnnamedAt(null), 1e-9);
        _board.Watts(displayOn: false).OwnPlug.ShouldBe(0.13, 1e-9);

        // Counting by default again, the monitor with no choice counts.
        _board.Choose(Laptop(new MonitorChoice { Key = Unnamed.Key, Counted = true }));
        _board.Status(displayOn: true).Select(m => (m.Counted, m.CountedByDefault)).ShouldBe([(true, true), (true, true)]);
    }

    [Theory]
    [InlineData(ChassisKind.Laptop, 15.6, false)]
    [InlineData(ChassisKind.Laptop, 17.3, false)]
    [InlineData(ChassisKind.Laptop, 18.5, true)]
    [InlineData(ChassisKind.Laptop, 24, true)]
    [InlineData(ChassisKind.Laptop, 0, true)]
    [InlineData(ChassisKind.Desktop, 15.6, true)]
    public void Only_a_monitor_small_enough_to_be_a_portable_one_is_taken_to_run_off_a_laptop(ChassisKind chassis, double inches, bool ownPlug)
    {
        _board.Detected([Portable with { Inches = inches }]);
        _board.Choose(chassis == ChassisKind.Laptop ? Laptop() : Desktop());

        var monitor = _board.Status(displayOn: true).ShouldHaveSingleItem();
        monitor.OwnPlugByDefault.ShouldBe(ownPlug);
        monitor.OwnPlug.ShouldBe(ownPlug);
        monitor.Counted.ShouldBeTrue();
        monitor.WattsNow.ShouldBeGreaterThan(0);
        _board.Watts(displayOn: true).ShouldBe(ownPlug ? new MonitorWatts(OwnPlug: monitor.WattsNow, FromPc: 0) : new MonitorWatts(OwnPlug: 0, FromPc: monitor.WattsNow));
    }

    [Fact]
    public void On_a_laptop_a_portable_monitor_draws_from_it_beside_a_desk_monitor_with_a_plug_of_its_own()
    {
        _board.Detected([Dell, Portable]);
        _board.Choose(Laptop());

        var status = _board.Status(displayOn: true);
        var estimate = MonitorEstimate.For(15.6, 1920, 1080, Catalogue);
        status[1].ShouldBe(new MonitorStatus
        {
            Key = Portable.Key,
            Instance = Portable.Instance,
            Name = "ASUS MB16AC",
            Inches = 15.6,
            Width = 1920,
            Height = 1080,
            OnWatts = estimate.OnW,
            SleepWatts = estimate.SleepW,
            OffWatts = estimate.OffW,
            PowerState = MonitorPowerState.Unknown,
            RefreshHz = null,
            RefreshWatts = 0,
            Hdr = null,
            Source = MonitorSource.Estimate,
            Counted = true,
            CountedByDefault = true,
            OwnPlug = false,
            OwnPlugByDefault = false,
            Brightness = null,
            WattsNow = status[1].WattsNow,
        });
        status[0].OwnPlug.ShouldBeTrue();
        status[1].WattsNow.ShouldBe(estimate.OnW, 1e-9);

        var on = _board.Watts(displayOn: true);
        on.OwnPlug.ShouldBe(DellAt(null), 1e-9);
        on.FromPc.ShouldBe(estimate.OnW, 1e-9);
        var asleep = _board.Watts(displayOn: false);
        asleep.OwnPlug.ShouldBe(0.74, 1e-9);
        asleep.FromPc.ShouldBe(estimate.SleepW, 1e-9);
    }

    [Fact]
    public void What_the_user_says_about_a_monitor_s_plug_wins_over_the_guess()
    {
        _board.Detected([Portable]);

        // A portable monitor on a charger of its own.
        _board.Choose(Laptop(new MonitorChoice { Key = Portable.Key, OwnPlug = true }));
        var charged = _board.Status(displayOn: true).ShouldHaveSingleItem();
        (charged.OwnPlug, charged.OwnPlugByDefault).ShouldBe((true, false));
        _board.Watts(displayOn: true).ShouldBe(new MonitorWatts(OwnPlug: charged.WattsNow, FromPc: 0));

        // The same monitor running off a desktop's USB-C port.
        _board.Choose(Desktop(new MonitorChoice { Key = Portable.Key, OwnPlug = false }));
        var powered = _board.Status(displayOn: true).ShouldHaveSingleItem();
        (powered.OwnPlug, powered.OwnPlugByDefault).ShouldBe((false, true));
        _board.Watts(displayOn: true).ShouldBe(new MonitorWatts(OwnPlug: 0, FromPc: powered.WattsNow));
    }

    [Fact]
    public void A_monitor_running_off_the_pc_counts_even_when_the_user_chose_not_to_count_it()
    {
        // What it draws is inside what the laptop itself draws, so leaving it out would mean nothing.
        _board.Detected([Portable]);
        _board.Choose(Laptop(new MonitorChoice { Key = Portable.Key, Counted = false }));

        var monitor = _board.Status(displayOn: true).ShouldHaveSingleItem();
        (monitor.OwnPlug, monitor.Counted, monitor.CountedByDefault).ShouldBe((false, true, true));
        monitor.WattsNow.ShouldBe(MonitorEstimate.For(15.6, 1920, 1080, Catalogue).OnW, 1e-9);
        _board.Watts(displayOn: true).ShouldBe(new MonitorWatts(OwnPlug: 0, FromPc: monitor.WattsNow));
        _board.Watts(displayOn: false).ShouldBe(new MonitorWatts(OwnPlug: 0, FromPc: monitor.SleepWatts));

        // Said to have a plug of its own, the same monitor is left out as the user chose.
        _board.Choose(Laptop(new MonitorChoice { Key = Portable.Key, Counted = false, OwnPlug = true }));
        var charged = _board.Status(displayOn: true).ShouldHaveSingleItem();
        (charged.OwnPlug, charged.Counted, charged.WattsNow).ShouldBe((true, false, 0.0));
        _board.Watts(displayOn: true).ShouldBe(new MonitorWatts(OwnPlug: 0, FromPc: 0));
    }

    [Fact]
    public void With_monitors_left_out_by_default_a_portable_monitor_running_off_a_laptop_still_counts_and_a_desk_monitor_does_not()
    {
        _board.Detected([Portable, Unnamed with { Inches = 24 }]);
        _board.Choose(Laptop() with { CountMonitorsByDefault = false });

        var status = _board.Status(displayOn: true);
        status.Select(m => (m.OwnPlug, m.Counted, m.CountedByDefault)).ShouldBe([(false, true, false), (true, false, false)]);
        status[0].WattsNow.ShouldBeGreaterThan(0);
        status[1].OnWatts.ShouldBeGreaterThan(0);
        status[1].WattsNow.ShouldBe(0);
        _board.Watts(displayOn: true).ShouldBe(new MonitorWatts(OwnPlug: 0, FromPc: status[0].WattsNow));
        _board.Watts(displayOn: false).ShouldBe(new MonitorWatts(OwnPlug: 0, FromPc: status[0].SleepWatts));
    }

    [Fact]
    public void A_typed_figure_for_a_monitor_running_off_the_pc_is_taken_as_it_is_and_one_worked_out_is_scaled_by_brightness()
    {
        _board.Detected([Dell, Portable]);
        _board.Choose(Laptop(new MonitorChoice { Key = Portable.Key, Watts = 6 }, new MonitorChoice { Key = Dell.Key, OwnPlug = false }));
        _board.Report(
        [
            new MonitorBrightness { Instance = Dell.Instance, Brightness = 0.2 },
            new MonitorBrightness { Instance = Portable.Instance, Brightness = 0.2 },
        ]);

        var on = _board.Watts(displayOn: true);
        on.OwnPlug.ShouldBe(0);
        on.FromPc.ShouldBe(DellAt(0.2) + 6, 1e-9);
    }

    [Fact]
    public void A_typed_figure_wins_and_the_sleep_figure_stays_the_one_worked_out()
    {
        _board.Detected([Dell]);
        _board.Choose(Laptop(new MonitorChoice { Key = Dell.Key, Watts = 40 }));

        var monitor = _board.Status(displayOn: true).ShouldHaveSingleItem();
        monitor.Source.ShouldBe(MonitorSource.Typed);
        monitor.OnWatts.ShouldBe(40);
        monitor.SleepWatts.ShouldBe(0.74);
        _board.Watts(displayOn: true).OwnPlug.ShouldBe(40, 1e-9);

        // New choices replace the old ones whole.
        _board.Choose(Laptop());
        _board.Status(displayOn: true).ShouldHaveSingleItem().Source.ShouldBe(MonitorSource.Model);
    }

    [Fact]
    public void A_reported_brightness_scales_the_draw_until_it_goes_stale()
    {
        _board.Detected([Dell]);
        _board.Report([new MonitorBrightness { Instance = Dell.Instance, Brightness = 1 }]);

        var bright = _board.Status(displayOn: true).ShouldHaveSingleItem();
        bright.Brightness.ShouldBe(1);
        bright.OnWatts.ShouldBe(28.32);
        bright.WattsNow.ShouldBe(DellAt(1), 1e-9);
        _board.Watts(displayOn: true).OwnPlug.ShouldBe(DellAt(1), 1e-9);

        _clock.Advance(MonitorBoard.BrightnessStale);
        _board.Status(displayOn: true).ShouldHaveSingleItem().Brightness.ShouldBe(1);
        _clock.Advance(TimeSpan.FromSeconds(1));
        _board.Status(displayOn: true).ShouldHaveSingleItem().Brightness.ShouldBeNull();
        _board.Watts(displayOn: true).OwnPlug.ShouldBe(DellAt(null), 1e-9);
    }

    [Fact]
    public void A_typed_figure_is_taken_as_it_is_whatever_the_brightness()
    {
        _board.Detected([Dell]);
        _board.Choose(Laptop(new MonitorChoice { Key = Dell.Key, Watts = 30 }));
        _board.Report([new MonitorBrightness { Instance = Dell.Instance, Brightness = 0.2 }]);

        var monitor = _board.Status(displayOn: true).ShouldHaveSingleItem();
        monitor.Source.ShouldBe(MonitorSource.Typed);
        monitor.OnWatts.ShouldBe(30);
        monitor.Brightness.ShouldBe(0.2);   // still reported, for the user to see
        monitor.WattsNow.ShouldBe(30);
        _board.Watts(displayOn: true).OwnPlug.ShouldBe(30);
    }

    [Fact]
    public void With_the_display_off_a_monitor_with_a_typed_figure_draws_its_sleep_figure()
    {
        _board.Detected([Dell]);
        _board.Choose(Laptop(new MonitorChoice { Key = Dell.Key, Watts = 30 }));
        _board.Report([new MonitorBrightness { Instance = Dell.Instance, Brightness = 0.2 }]);

        _board.Status(displayOn: false).ShouldHaveSingleItem().WattsNow.ShouldBe(0.74);
        _board.Watts(displayOn: false).OwnPlug.ShouldBe(0.74, 1e-9);
    }

    [Fact]
    public void A_listed_figure_beside_a_typed_one_is_still_scaled_by_the_same_brightness()
    {
        _board.Detected([Dell, Unnamed]);
        _board.Choose(Laptop(new MonitorChoice { Key = Unnamed.Key, Watts = 30 }));
        _board.Report(
        [
            new MonitorBrightness { Instance = Dell.Instance, Brightness = 0.2 },
            new MonitorBrightness { Instance = Unnamed.Instance, Brightness = 0.2 },
        ]);

        var status = _board.Status(displayOn: true);
        status.Select(m => m.Source).ShouldBe([MonitorSource.Model, MonitorSource.Typed]);
        status[0].WattsNow.ShouldBe(DellAt(0.2), 1e-9);
        status[1].WattsNow.ShouldBe(30);
        _board.Watts(displayOn: true).OwnPlug.ShouldBe(DellAt(0.2) + 30, 1e-9);
    }

    [Fact]
    public void A_report_finds_its_monitor_by_instance_in_any_case_and_forgets_a_monitor_that_is_not_attached()
    {
        _board.Detected([Dell]);
        _board.Report(
        [
            new MonitorBrightness { Instance = Dell.Instance.ToLowerInvariant(), Brightness = 0.2 },
            new MonitorBrightness { Instance = Unnamed.Instance, Brightness = 0.9 },
        ]);

        _board.Status(displayOn: true).ShouldHaveSingleItem().Brightness.ShouldBe(0.2);
        _board.Detected([Dell, Unnamed]);
        _board.Status(displayOn: true)[1].Brightness.ShouldBeNull();
    }

    [Fact]
    public void With_the_display_off_a_counted_monitor_draws_its_sleep_figure_whatever_its_brightness()
    {
        _board.Detected([Dell, Unnamed]);
        _board.Report([new MonitorBrightness { Instance = Dell.Instance, Brightness = 1 }]);

        _board.Status(displayOn: false).Select(m => m.WattsNow).ShouldBe([0.74, 0.13]);
        _board.Watts(displayOn: false).OwnPlug.ShouldBe(0.87, 1e-9);
    }

    [Fact]
    public void A_monitor_that_says_it_is_off_draws_its_off_figure_and_one_in_standby_its_sleep_figure_whatever_the_displays_do()
    {
        _board.Detected([Dell]);
        _board.Report([new MonitorBrightness { Instance = Dell.Instance, Brightness = 1 }], [Said(Dell, MonitorPowerState.Off)]);

        var off = _board.Status(displayOn: true).ShouldHaveSingleItem();
        (off.PowerState, off.OffWatts, off.WattsNow).ShouldBe((MonitorPowerState.Off, 0.3, 0.3));
        _board.Watts(displayOn: true).ShouldBe(new MonitorWatts(OwnPlug: 0.3, FromPc: 0));
        _board.Watts(displayOn: false).ShouldBe(new MonitorWatts(OwnPlug: 0.3, FromPc: 0));

        _board.Report([], [Said(Dell, MonitorPowerState.Standby)]);
        var standby = _board.Status(displayOn: true).ShouldHaveSingleItem();
        (standby.PowerState, standby.WattsNow).ShouldBe((MonitorPowerState.Standby, 0.74));
        _board.Watts(displayOn: true).ShouldBe(new MonitorWatts(OwnPlug: 0.74, FromPc: 0));
        _board.Watts(displayOn: false).ShouldBe(new MonitorWatts(OwnPlug: 0.74, FromPc: 0));

        // On, it draws as a monitor that hasn't said: its figure at its brightness while the displays are on, and its sleep
        // figure while they are off.
        _board.Report([], [Said(Dell, MonitorPowerState.On)]);
        var on = _board.Status(displayOn: true).ShouldHaveSingleItem();
        on.PowerState.ShouldBe(MonitorPowerState.On);
        on.WattsNow.ShouldBe(DellAt(1), 1e-9);
        _board.Watts(displayOn: true).OwnPlug.ShouldBe(DellAt(1), 1e-9);
        _board.Watts(displayOn: false).ShouldBe(new MonitorWatts(OwnPlug: 0.74, FromPc: 0));
    }

    [Fact]
    public void A_power_state_goes_stale_after_three_minutes_and_the_monitor_then_draws_as_one_that_has_not_said()
    {
        _board.Detected([Dell]);
        _board.Report([new MonitorBrightness { Instance = Dell.Instance, Brightness = 1 }], [Said(Dell, MonitorPowerState.Off)]);

        MonitorBoard.PowerStale.ShouldBe(TimeSpan.FromMinutes(3));
        _clock.Advance(MonitorBoard.PowerStale);
        _board.Status(displayOn: true).ShouldHaveSingleItem().PowerState.ShouldBe(MonitorPowerState.Off);
        _board.Watts(displayOn: true).OwnPlug.ShouldBe(0.3);

        _clock.Advance(TimeSpan.FromSeconds(1));
        var stale = _board.Status(displayOn: true).ShouldHaveSingleItem();
        stale.PowerState.ShouldBe(MonitorPowerState.Unknown);
        stale.Brightness.ShouldBe(1);   // a brightness stays fresh for longer
        stale.WattsNow.ShouldBe(DellAt(1), 1e-9);
        _board.Watts(displayOn: false).OwnPlug.ShouldBe(0.74);

        // A new report counts again, after a detection has dropped the stale one.
        _board.Detected([Dell]);
        _board.Report([], [Said(Dell, MonitorPowerState.Off)]);
        _board.Watts(displayOn: true).OwnPlug.ShouldBe(0.3);
    }

    [Fact]
    public void While_a_reading_says_the_displays_are_off_nothing_the_app_reported_goes_stale()
    {
        _board.Detected([Dell]);
        _board.Report(
            [new MonitorBrightness { Instance = Dell.Instance, Brightness = 0.2 }], [Said(Dell, MonitorPowerState.Off)], [Shown(Dell, 144, hdr: true)]);
        _board.Watts(displayOn: false).OwnPlug.ShouldBe(0.3);

        // The App reads nothing while the displays are off, so a night later the monitor last known off still counts so, and
        // a detection drops none of it.
        _clock.Advance(TimeSpan.FromHours(8));
        _board.Detected([Dell]);
        var asleep = _board.Status(displayOn: false).ShouldHaveSingleItem();
        (asleep.PowerState, asleep.WattsNow).ShouldBe((MonitorPowerState.Off, 0.3));
        asleep.Brightness.ShouldBe(0.2);
        asleep.RefreshHz.ShouldBe(144);
        asleep.Hdr.ShouldBe(true);
        _board.Watts(displayOn: false).OwnPlug.ShouldBe(0.3);

        // With the displays on again, only the time they are on counts.
        _board.Watts(displayOn: true).OwnPlug.ShouldBe(0.3);
        _clock.Advance(MonitorBoard.PowerStale);
        _board.Status(displayOn: true).ShouldHaveSingleItem().PowerState.ShouldBe(MonitorPowerState.Off);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var stale = _board.Status(displayOn: true).ShouldHaveSingleItem();
        (stale.PowerState, stale.RefreshHz, stale.Hdr).ShouldBe((MonitorPowerState.Unknown, (double?)null, (bool?)null));
        stale.Brightness.ShouldBe(0.2);
    }

    [Fact]
    public void What_had_gone_stale_before_the_displays_went_off_stays_unknown_while_they_are_off()
    {
        _board.Detected([Dell]);
        _board.Report([], [Said(Dell, MonitorPowerState.Off)]);
        _clock.Advance(MonitorBoard.PowerStale + TimeSpan.FromSeconds(1));
        _board.Watts(displayOn: false).OwnPlug.ShouldBe(0.74);

        _clock.Advance(TimeSpan.FromHours(1));
        _board.Status(displayOn: false).ShouldHaveSingleItem().PowerState.ShouldBe(MonitorPowerState.Unknown);
        _board.Watts(displayOn: false).OwnPlug.ShouldBe(0.74);
    }

    [Fact]
    public void A_monitor_with_a_typed_figure_that_says_it_is_off_or_in_standby_draws_its_off_or_sleep_figure()
    {
        _board.Detected([Dell]);
        _board.Choose(Laptop(new MonitorChoice { Key = Dell.Key, Watts = 30 }));

        _board.Report([], [Said(Dell, MonitorPowerState.Off)]);
        var off = _board.Status(displayOn: true).ShouldHaveSingleItem();
        (off.Source, off.OnWatts, off.OffWatts, off.WattsNow).ShouldBe((MonitorSource.Typed, 30.0, 0.3, 0.3));
        _board.Watts(displayOn: true).OwnPlug.ShouldBe(0.3);

        _board.Report([], [Said(Dell, MonitorPowerState.Standby)]);
        _board.Watts(displayOn: true).OwnPlug.ShouldBe(0.74);

        _board.Report([], [Said(Dell, MonitorPowerState.On)]);
        _board.Watts(displayOn: true).OwnPlug.ShouldBe(30);
    }

    [Theory]
    [InlineData(MonitorPowerState.On)]
    [InlineData(MonitorPowerState.Standby)]
    [InlineData(MonitorPowerState.Off)]
    public void A_monitor_the_user_does_not_count_draws_nothing_whatever_it_says_and_still_shows_what_it_said(MonitorPowerState state)
    {
        _board.Detected([Dell]);
        _board.Choose(Laptop(new MonitorChoice { Key = Dell.Key, Counted = false }));
        _board.Report([], [Said(Dell, state)]);

        var monitor = _board.Status(displayOn: true).ShouldHaveSingleItem();
        (monitor.Counted, monitor.PowerState, monitor.OffWatts, monitor.WattsNow).ShouldBe((false, state, 0.3, 0.0));
        _board.Watts(displayOn: true).ShouldBe(new MonitorWatts(OwnPlug: 0, FromPc: 0));
        _board.Watts(displayOn: false).ShouldBe(new MonitorWatts(OwnPlug: 0, FromPc: 0));
    }

    [Fact]
    public void Monitors_that_say_they_are_off_or_in_standby_keep_the_split_between_their_own_plugs_and_the_pc()
    {
        _board.Detected([Dell, Portable]);
        _board.Choose(Laptop());
        var estimate = MonitorEstimate.For(15.6, 1920, 1080, Catalogue);

        _board.Report([], [Said(Dell, MonitorPowerState.Off), Said(Portable, MonitorPowerState.Standby)]);
        _board.Watts(displayOn: true).ShouldBe(new MonitorWatts(OwnPlug: 0.3, FromPc: estimate.SleepW));

        _board.Report([], [Said(Dell, MonitorPowerState.Standby), Said(Portable, MonitorPowerState.Off)]);
        _board.Watts(displayOn: true).ShouldBe(new MonitorWatts(OwnPlug: 0.74, FromPc: estimate.OffW));
        _board.Watts(displayOn: false).ShouldBe(new MonitorWatts(OwnPlug: 0.74, FromPc: estimate.OffW));
        _board.Status(displayOn: true).Select(m => (m.OwnPlug, m.PowerState, m.OffWatts))
            .ShouldBe([(true, MonitorPowerState.Standby, 0.3), (false, MonitorPowerState.Off, estimate.OffW)]);
    }

    [Fact]
    public void A_power_state_finds_its_monitor_by_instance_in_any_case_and_one_for_a_monitor_that_is_not_attached_is_forgotten()
    {
        _board.Detected([Dell]);
        _board.Report([],
        [
            new MonitorPowerReading { Instance = Dell.Instance.ToLowerInvariant(), State = MonitorPowerState.Standby },
            Said(Unnamed, MonitorPowerState.Off),
        ]);

        _board.Status(displayOn: true).ShouldHaveSingleItem().PowerState.ShouldBe(MonitorPowerState.Standby);
        _board.Detected([Dell, Unnamed]);
        var unnamed = _board.Status(displayOn: true)[1];
        unnamed.PowerState.ShouldBe(MonitorPowerState.Unknown);
        unnamed.WattsNow.ShouldBe(UnnamedAt(null), 1e-9);
    }

    [Fact]
    public void A_report_without_power_states_leaves_them_as_they_were_and_a_reading_that_is_no_state_is_ignored()
    {
        _board.Detected([Dell]);
        _board.Report([], [Said(Dell, MonitorPowerState.Off)]);

        _board.Report([new MonitorBrightness { Instance = Dell.Instance, Brightness = 0.5 }]);
        _board.Report([], [Said(Dell, MonitorPowerState.Unknown), Said(Dell, (MonitorPowerState)7)]);

        var monitor = _board.Status(displayOn: true).ShouldHaveSingleItem();
        monitor.Brightness.ShouldBe(0.5);
        monitor.PowerState.ShouldBe(MonitorPowerState.Off);
        monitor.WattsNow.ShouldBe(0.3);
    }

    [Fact]
    public void A_monitor_missing_from_one_detection_keeps_a_power_state_that_is_still_fresh()
    {
        _board.Detected([Dell]);
        _board.Report([], [Said(Dell, MonitorPowerState.Off)]);
        _board.Detected([]);
        _board.Detected([Dell]);

        _board.Status(displayOn: true).ShouldHaveSingleItem().PowerState.ShouldBe(MonitorPowerState.Off);
    }

    [Fact]
    public void The_refresh_rate_and_hdr_state_the_app_reads_are_shown_until_they_go_stale_and_hdr_changes_no_watts()
    {
        _board.Detected([Dell, Unnamed]);
        _board.Report([], displays: [Shown(Dell, 144, hdr: true), Shown(Unnamed, 60)]);

        var status = _board.Status(displayOn: true);
        status[0].RefreshHz.ShouldBe(144);
        status[0].Hdr.ShouldBe(true);
        status[1].RefreshHz.ShouldBe(60);
        status[1].Hdr.ShouldBe(false);
        var withHdr = _board.Watts(displayOn: true);
        _board.Report([], displays: [Shown(Dell, 144, hdr: false)]);
        _board.Watts(displayOn: true).ShouldBe(withHdr);

        MonitorBoard.DisplayStale.ShouldBe(TimeSpan.FromMinutes(3));
        _clock.Advance(MonitorBoard.DisplayStale);
        _board.Status(displayOn: true).Select(m => m.RefreshHz).ShouldBe([144, 60]);

        _clock.Advance(TimeSpan.FromSeconds(1));
        var stale = _board.Status(displayOn: true);
        stale.Select(m => m.RefreshHz).ShouldBe([null, null]);
        stale.Select(m => m.Hdr).ShouldBe([null, null]);
    }

    [Fact]
    public void A_display_reading_finds_its_monitor_by_instance_in_any_case_and_one_for_a_monitor_that_is_not_attached_is_forgotten()
    {
        _board.Detected([Dell]);
        _board.Report([], displays:
        [
            new MonitorDisplayReading { Instance = Dell.Instance.ToLowerInvariant(), RefreshHz = 120, Hdr = true },
            Shown(Unnamed, 165),
        ]);

        _board.Status(displayOn: true).ShouldHaveSingleItem().RefreshHz.ShouldBe(120);
        _board.Detected([Dell, Unnamed]);
        var unnamed = _board.Status(displayOn: true)[1];
        unnamed.RefreshHz.ShouldBeNull();
        unnamed.Hdr.ShouldBeNull();
    }

    [Fact]
    public void A_report_without_display_readings_leaves_them_as_they_were_and_a_refresh_rate_that_is_no_rate_is_ignored()
    {
        _board.Detected([Dell]);
        _board.Report([], displays: [Shown(Dell, 144, hdr: true)]);

        _board.Report([new MonitorBrightness { Instance = Dell.Instance, Brightness = 0.5 }], [Said(Dell, MonitorPowerState.On)]);
        _board.Report([], displays: [Shown(Dell, double.NaN), Shown(Dell, double.PositiveInfinity), Shown(Dell, 0), Shown(Dell, -60)]);

        var monitor = _board.Status(displayOn: true).ShouldHaveSingleItem();
        monitor.RefreshHz.ShouldBe(144);
        monitor.Hdr.ShouldBe(true);
    }

    [Fact]
    public void An_lcd_monitor_driven_above_60_hz_adds_the_refresh_term_while_it_is_on_or_unknown_with_the_displays_on()
    {
        // A 27-inch 1440p monitor the list doesn't know, at 165 Hz: 0.006 W for each of its 3.6864 megapixels and 105 Hz.
        var fast = Unnamed with { Width = 2560, Height = 1440 };
        _board.Detected([fast]);
        _board.Report([], displays: [Shown(fast, 165)]);

        var unknown = _board.Status(displayOn: true).ShouldHaveSingleItem();
        (unknown.PowerState, unknown.RefreshHz).ShouldBe((MonitorPowerState.Unknown, (double?)165));
        unknown.RefreshWatts.ShouldBe(2.322432, 1e-9);
        unknown.WattsNow.ShouldBe(unknown.OnWatts + 2.322432, 1e-9);
        _board.Watts(displayOn: true).OwnPlug.ShouldBe(unknown.WattsNow, 1e-9);

        _board.Report([], [Said(fast, MonitorPowerState.On)]);
        _board.Status(displayOn: true).ShouldHaveSingleItem().RefreshWatts.ShouldBe(2.322432, 1e-9);

        // Nothing is added with the displays off, in standby, switched off, or left out.
        var asleep = _board.Status(displayOn: false).ShouldHaveSingleItem();
        (asleep.RefreshWatts, asleep.WattsNow).ShouldBe((0.0, asleep.SleepWatts));
        _board.Report([], [Said(fast, MonitorPowerState.Standby)]);
        var standby = _board.Status(displayOn: true).ShouldHaveSingleItem();
        (standby.RefreshWatts, standby.WattsNow).ShouldBe((0.0, standby.SleepWatts));
        _board.Report([], [Said(fast, MonitorPowerState.Off)]);
        var off = _board.Status(displayOn: true).ShouldHaveSingleItem();
        (off.RefreshWatts, off.WattsNow).ShouldBe((0.0, off.OffWatts));
        _board.Report([], [Said(fast, MonitorPowerState.On)]);
        _board.Choose(Laptop(new MonitorChoice { Key = fast.Key, Counted = false }));
        var leftOut = _board.Status(displayOn: true).ShouldHaveSingleItem();
        (leftOut.RefreshHz, leftOut.RefreshWatts, leftOut.WattsNow).ShouldBe(((double?)165, 0.0, 0.0));

        // Nor once the refresh rate has gone stale.
        _board.Choose(Laptop());
        _clock.Advance(MonitorBoard.DisplayStale + TimeSpan.FromSeconds(1));
        _board.Report([], [Said(fast, MonitorPowerState.On)]);
        var stale = _board.Status(displayOn: true).ShouldHaveSingleItem();
        (stale.RefreshHz, stale.RefreshWatts).ShouldBe((null, 0.0));
    }

    [Fact]
    public void An_oled_monitor_adds_no_refresh_term()
    {
        // MSI's 26.7-inch 1440p OLED from the shipped table, at 240 Hz. Its 21.3 W were measured at its 200 cd/m², its
        // brightest.
        var board = new MonitorBoard(MonitorCatalogue.Parse(new StringReader("""
            brand,model_number,model_name,alternatives,inches,width,height,panel,on_w,sleep_w,off_w,max_nits,hdr,certified
            MSI,PRO MAX 271QPX14G,PRO MAX 271QPX14G,,26.7,2560,1440,OLED,21.3,0.36,0.23,200,,2026-04-24
            """)), _clock);
        var oled = new MonitorFacts(@"DISPLAY\MSI3CB1\7&2B3C&0&UID4356", "MSI3CB1-00001", "MSI", "3CB1", "MSI 271QPX14G", 26.7, 2560, 1440);
        board.Detected([oled]);
        board.Report([], displays: [Shown(oled, 240)]);

        var monitor = board.Status(displayOn: true).ShouldHaveSingleItem();
        (monitor.Source, monitor.RefreshHz, monitor.RefreshWatts).ShouldBe((MonitorSource.Model, (double?)240, 0.0));
        monitor.WattsNow.ShouldBe(MonitorPower.At(21.3, null, 1), 1e-9);
    }

    [Fact]
    public void A_typed_figure_or_an_unknown_resolution_or_refresh_rate_adds_no_refresh_term()
    {
        var fast = Unnamed with { Width = 2560, Height = 1440 };
        _board.Detected([Dell, fast]);
        _board.Choose(Laptop(new MonitorChoice { Key = Dell.Key, Watts = 30 }));
        _board.Report([], displays: [Shown(Dell, 144)]);

        var status = _board.Status(displayOn: true);
        (status[0].RefreshHz, status[0].RefreshWatts, status[0].WattsNow).ShouldBe(((double?)144, 0.0, 30.0));
        (status[1].RefreshHz, status[1].RefreshWatts).ShouldBe((null, 0.0));

        // A monitor that doesn't give its resolution.
        var unsized = Unnamed with { Width = 0, Height = 0 };
        _board.Detected([unsized]);
        _board.Report([], displays: [Shown(unsized, 165)]);
        var monitor = _board.Status(displayOn: true).ShouldHaveSingleItem();
        (monitor.RefreshHz, monitor.RefreshWatts).ShouldBe(((double?)165, 0.0));
    }

    [Fact]
    public void A_monitor_missing_from_one_detection_keeps_a_display_reading_that_is_still_fresh()
    {
        _board.Detected([Dell]);
        _board.Report([], displays: [Shown(Dell, 144, hdr: true)]);
        _board.Detected([]);
        _board.Detected([Dell]);

        _board.Status(displayOn: true).ShouldHaveSingleItem().RefreshHz.ShouldBe(144);
    }

    [Fact]
    public void An_unplugged_monitor_disappears()
    {
        _board.Detected([Dell, Unnamed]);
        _board.Detected([Dell]);

        _board.Status(displayOn: true).ShouldHaveSingleItem().Key.ShouldBe(Dell.Key);
        _board.Watts(displayOn: true).OwnPlug.ShouldBe(DellAt(null), 1e-9);
    }

    [Fact]
    public void What_a_sensor_set_finds_once_it_is_retired_is_ignored_and_its_replacement_is_not()
    {
        using var abandoned = new CancellationTokenSource();
        using var replacement = new CancellationTokenSource();
        _board.Detected([Dell, Unnamed], abandoned.Token);
        abandoned.Cancel();

        _board.Detected([], abandoned.Token);
        _board.Status(displayOn: true).Select(m => m.Key).ShouldBe([Dell.Key, Unnamed.Key]);

        _board.Detected([Dell], replacement.Token);
        _board.Status(displayOn: true).ShouldHaveSingleItem().Key.ShouldBe(Dell.Key);
        _board.Watts(displayOn: true).Total.ShouldBe(DellAt(null), 1e-9);
    }

    [Fact]
    public void A_monitor_keeps_its_figure_when_the_native_modes_fail_to_answer_once_between_two_good_reads()
    {
        // The monitor the list doesn't know, whose estimate goes by its resolution, as WMI's four classes give it to the
        // inventory, which remembers from one read to the next as the service's does.
        static ushort[] Codes(string text) => [.. text.Select(c => (ushort)c)];
        HashSet<string> sharedKeys = [];
        var resolutions = new Dictionary<string, (int Width, int Height)>();
        IReadOnlyList<MonitorFacts> Read(bool modesAnswer) => MonitorInventory.From(
            [(Unnamed.Instance + "_0", Codes("GSM"), Codes("5B08"), Codes(""), Codes(""))],
            new Dictionary<string, uint> { [Unnamed.Instance] = 5 },
            [(Unnamed.Instance, true, 60, 34)],
            modesAnswer ? new Dictionary<string, (int Width, int Height)> { [Unnamed.Instance] = (1920, 1080) } : null,
            sharedKeys,
            resolutions).ShouldNotBeNull();

        _board.Detected(Read(modesAnswer: true));
        var figure = _board.Status(displayOn: true).ShouldHaveSingleItem().OnWatts;
        figure.ShouldBe(14.41);

        _board.Detected(Read(modesAnswer: false));
        _board.Status(displayOn: true).ShouldHaveSingleItem().OnWatts.ShouldBe(figure);
        _board.Detected(Read(modesAnswer: true));
        _board.Status(displayOn: true).ShouldHaveSingleItem().OnWatts.ShouldBe(figure);
    }

    [Fact]
    public void A_monitor_that_changes_is_worked_out_again_and_keeps_its_brightness()
    {
        _board.Detected([Unnamed]);
        _board.Report([new MonitorBrightness { Instance = Unnamed.Instance, Brightness = 0.5 }]);

        // Now at 4K, where the list has too few 27-inch monitors for a median, so the formula gives its figure.
        _board.Detected([Unnamed with { Width = 3840, Height = 2160 }]);

        var monitor = _board.Status(displayOn: true).ShouldHaveSingleItem();
        monitor.OnWatts.ShouldBe(MonitorEstimate.For(27, 3840, 2160, Catalogue).OnW);
        monitor.OnWatts.ShouldNotBe(14.41);
        monitor.Brightness.ShouldBe(0.5);
    }

    [Fact]
    public void A_monitor_missing_from_one_detection_keeps_a_brightness_that_is_still_fresh()
    {
        _board.Detected([Dell]);
        _board.Report([new MonitorBrightness { Instance = Dell.Instance, Brightness = 0.3 }]);
        _board.Detected([]);
        _board.Detected([Dell]);

        _board.Status(displayOn: true).ShouldHaveSingleItem().Brightness.ShouldBe(0.3);
    }

    [Fact]
    public async Task Detection_reports_choices_and_readers_may_all_come_from_different_threads_at_once()
    {
        _board.Detected([Dell]);
        var until = Environment.TickCount64 + 200;
        Task Repeat(Action action) => Task.Run(() =>
        {
            while (Environment.TickCount64 < until) action();
        });

        await Task.WhenAll(
            Repeat(() =>
            {
                _board.Detected([Dell, Unnamed]);
                _board.Detected([Dell]);
            }),
            Repeat(() => _board.Report(
                [new MonitorBrightness { Instance = Unnamed.Instance, Brightness = 0.5 }],
                [Said(Dell, MonitorPowerState.Standby), Said(Unnamed, MonitorPowerState.Off)],
                [Shown(Dell, 144, hdr: true), Shown(Unnamed, 60)])),
            Repeat(() =>
            {
                _board.Choose(Laptop(new MonitorChoice { Key = Dell.Key, Watts = 30 }));
                _board.Choose(Desktop(new MonitorChoice { Key = Dell.Key, OwnPlug = false }));
            }),
            Repeat(() => _board.Watts(displayOn: true).Total.ShouldBeGreaterThan(0)),
            Repeat(() => _board.Status(displayOn: false).ShouldNotBeEmpty()));
    }
}
