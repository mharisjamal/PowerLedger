using PowerLedger.Contracts;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class UpsSourceTests
{
    private readonly FakeHid _hid = new();
    private TimeSpan _now;

    [Fact]
    public void The_output_s_own_active_power_is_the_most_exact_source_and_wins()
    {
        var ups = Attach();
        var output = ups.Holder(Pdc.Output);
        ups.Field(Pdc.ActivePower, 137, report: 4, holder: output, units: Pdc.Watt, unitsExp: 7);
        ups.Field(Pdc.PercentLoad, 27, report: 5, holder: output);
        ups.Field(Pdc.ConfigActivePower, 520, report: 6, holder: output, units: Pdc.Watt, unitsExp: 7);

        using var source = Source();
        var draft = Tick(source);

        draft.UpsOutputW.ShouldBe(137);
        draft.UpsSource.ShouldBe(UpsPowerSource.ActivePower);
        draft.UpsName.ShouldBe("CPS CP1500PFCLCD");
        source.Unavailable.ShouldBeNull();

        // The load and the rating were never asked for, so a round costs the bus one control transfer.
        ups.Reads.ShouldBe([(byte)4]);
    }

    [Fact]
    public void A_ups_that_reports_no_active_power_takes_its_load_of_its_rated_watts()
    {
        var ups = Attach();
        var output = ups.Holder(Pdc.Output);
        ups.Field(Pdc.PercentLoad, 27, report: 5, holder: output);
        ups.Field(Pdc.ConfigActivePower, 520, report: 6, holder: output, units: Pdc.Watt, unitsExp: 7);

        using var source = Source();
        var draft = Tick(source);

        draft.UpsOutputW.ShouldNotBeNull().ShouldBe(140.4, 1e-9);
        draft.UpsSource.ShouldBe(UpsPowerSource.LoadOfRatedWatts);
    }

    [Fact]
    public void A_ups_that_rates_itself_only_in_volt_amperes_falls_to_the_assumed_power_factor()
    {
        var ups = Attach();
        var output = ups.Holder(Pdc.Output);
        ups.Field(Pdc.PercentLoad, 27, report: 5, holder: output);
        ups.Field(Pdc.ConfigApparentPower, 850, report: 6, holder: output, units: Pdc.Watt, unitsExp: 7);

        using var source = Source();
        var draft = Tick(source);

        draft.UpsOutputW.ShouldNotBeNull().ShouldBe(183.6, 1e-9);                 // 27% of 850 VA at 0.8
        draft.UpsSource.ShouldBe(UpsPowerSource.LoadOfRatedVoltAmps);
    }

    [Fact]
    public void The_unit_s_exponent_is_applied_to_what_the_report_carries()
    {
        // A device that reports tenths of a watt declares an exponent of 6, one below the 7 that means whole watts.
        var ups = Attach();
        var output = ups.Holder(Pdc.Output);
        ups.Field(Pdc.ActivePower, 1374, report: 4, holder: output, units: Pdc.Watt, unitsExp: 6);

        using var source = Source();

        Tick(source).UpsOutputW.ShouldNotBeNull().ShouldBe(137.4, 1e-9);
    }

    [Fact]
    public void A_load_of_nothing_is_a_reading_of_no_watts_rather_than_no_reading()
    {
        // Load comes in whole percents, so a PC drawing less than one percent of the rating reads as zero.
        var ups = Attach();
        var output = ups.Holder(Pdc.Output);
        ups.Field(Pdc.PercentLoad, 0, report: 5, holder: output);
        ups.Field(Pdc.ConfigActivePower, 520, report: 6, holder: output, units: Pdc.Watt, unitsExp: 7);

        using var source = Source();
        var draft = Tick(source);

        draft.UpsOutputW.ShouldBe(0);
        draft.UpsSource.ShouldBe(UpsPowerSource.LoadOfRatedWatts);
    }

    [Fact]
    public void A_ups_that_reports_a_load_but_no_rating_gives_no_watts_and_is_still_named()
    {
        var ups = Attach();
        ups.Field(Pdc.PercentLoad, 27, report: 5, holder: ups.Holder(Pdc.Output));

        using var source = Source();
        var draft = Tick(source);

        draft.UpsOutputW.ShouldBeNull();
        draft.UpsSource.ShouldBe(UpsPowerSource.None);
        draft.UpsName.ShouldBe("CPS CP1500PFCLCD");
        source.Unavailable.ShouldNotBeNull().ShouldContain("reports neither its output power nor its load and rating");
    }

    [Fact]
    public void A_rating_without_a_load_gives_no_watts()
    {
        var ups = Attach();
        ups.Field(Pdc.ConfigActivePower, 520, report: 6, holder: ups.Holder(Pdc.Output), units: Pdc.Watt, unitsExp: 7);

        using var source = Source();

        Tick(source).UpsOutputW.ShouldBeNull();
    }

    [Fact]
    public void Devices_that_are_not_a_ups_are_closed_unread()
    {
        var keyboard = new FakeCollection("keyboard", usagePage: 0x01, usage: 0x06);
        var supply = new FakeCollection("psu", usage: 0x05);    // a power supply on the same page, not a UPS
        _hid.Attached.Add(keyboard);
        _hid.Attached.Add(supply);

        using var source = Source();
        var draft = Tick(source);

        draft.UpsOutputW.ShouldBeNull();
        draft.UpsName.ShouldBeNull();
        source.Unavailable.ShouldBe("no UPS found on USB");
        source.Supported.ShouldBeTrue();
        keyboard.Reads.ShouldBeEmpty();
        keyboard.Closes.ShouldBe(1);
        supply.Closes.ShouldBe(1);
    }

    [Fact]
    public void A_collection_windows_will_not_open_is_passed_over()
    {
        Attach();
        _hid.Refused.Add("ups");

        using var source = Source();

        Tick(source).UpsName.ShouldBeNull();
        source.Unavailable.ShouldBe("no UPS found on USB");
    }

    [Fact]
    public void The_ups_is_asked_at_most_every_five_seconds_and_the_ticks_between_carry_the_answer()
    {
        var ups = ActivePowerUps();
        using var source = Source();

        Tick(source).UpsOutputW.ShouldBe(137);
        ups.Reads.Count.ShouldBe(1);

        for (var second = 1; second < 5; second++)
        {
            _now = TimeSpan.FromSeconds(second);
            Tick(source).UpsOutputW.ShouldBe(137);
        }
        ups.Reads.Count.ShouldBe(1);

        _now = TimeSpan.FromSeconds(5);
        Tick(source).UpsOutputW.ShouldBe(137);
        ups.Reads.Count.ShouldBe(2);

        // One look was enough: the UPS is open, so the HID devices are not gone through again.
        _hid.Looks.ShouldBe(1);
    }

    [Fact]
    public void An_answer_older_than_fifteen_seconds_is_no_answer_and_then_the_ups_is_given_up()
    {
        var ups = ActivePowerUps();
        using var source = Source();
        Tick(source).UpsOutputW.ShouldBe(137);

        // A UPS may miss an answer while Windows' own driver is talking to it; the last one still stands.
        ups.Gone = true;
        _now = TimeSpan.FromSeconds(10);
        Tick(source).UpsOutputW.ShouldBe(137);
        _now = TimeSpan.FromSeconds(15);
        Tick(source).UpsOutputW.ShouldBe(137);

        // Past fifteen seconds the answer says nothing about the tick, though the UPS is still there to be named.
        _now = TimeSpan.FromSeconds(16);
        var stale = Tick(source);
        stale.UpsOutputW.ShouldBeNull();
        stale.UpsSource.ShouldBe(UpsPowerSource.None);
        stale.UpsName.ShouldBe("CPS CP1500PFCLCD");

        _now = TimeSpan.FromSeconds(20);
        Tick(source).UpsName.ShouldBeNull();
        source.Unavailable.ShouldBe("CPS CP1500PFCLCD stopped answering");
    }

    [Fact]
    public void A_ups_plugged_in_later_is_found_when_the_devices_are_gone_through_again()
    {
        using var source = Source();
        Tick(source).UpsName.ShouldBeNull();
        source.Unavailable.ShouldBe("no UPS found on USB");

        ActivePowerUps();
        _now = TimeSpan.FromSeconds(30);
        Tick(source).UpsName.ShouldBeNull();                    // not yet: the devices are gone through once a minute
        _hid.Looks.ShouldBe(1);

        _now = TimeSpan.FromSeconds(60);
        Tick(source).UpsOutputW.ShouldBe(137);
        _hid.Looks.ShouldBe(2);
        source.Unavailable.ShouldBeNull();
    }

    [Fact]
    public void Where_there_is_more_than_one_ups_the_first_is_read_and_the_status_says_so()
    {
        var first = ActivePowerUps("ups-a", "usb-a");
        var second = ActivePowerUps("ups-b", "usb-b");

        using var source = Source();
        var draft = Tick(source);

        draft.UpsOutputW.ShouldBe(137);
        draft.UpsName.ShouldBe("CPS CP1500PFCLCD");
        source.Unavailable.ShouldBe("2 UPSes are attached; only the first, CPS CP1500PFCLCD, is read");
        first.Reads.ShouldNotBeEmpty();
        second.Reads.ShouldBeEmpty();
        second.Closes.ShouldBe(1);
    }

    [Fact]
    public void The_several_collections_of_one_ups_are_one_ups_and_are_read_together()
    {
        // A device that publishes both a UPS and a power summary collection gets an interface for each.
        var main = new FakeCollection("ups&col01", device: "usb-1") { Manufacturer = "EATON", Product = "Eaton 5E" };
        main.Field(Pdc.PercentLoad, 27, report: 5, holder: main.Holder(Pdc.Output));
        var summary = new FakeCollection("ups&col02", usage: Pdc.PowerSummary, device: "usb-1");
        summary.Field(Pdc.ConfigActivePower, 520, report: 6, units: Pdc.Watt, unitsExp: 7);
        _hid.Attached.Add(main);
        _hid.Attached.Add(summary);

        using var source = Source();
        var draft = Tick(source);

        draft.UpsOutputW.ShouldNotBeNull().ShouldBe(140.4, 1e-9);
        draft.UpsName.ShouldBe("Eaton 5E");                     // the product already names the maker
        source.Unavailable.ShouldBeNull();
        summary.Closes.ShouldBe(0);
    }

    [Fact]
    public void A_report_that_holds_several_usages_is_read_once_a_round()
    {
        var ups = Attach();
        var output = ups.Holder(Pdc.Output);
        ups.Field(Pdc.PercentLoad, 27, report: 3, holder: output);
        ups.Field(Pdc.ConfigActivePower, 520, report: 3, holder: output, units: Pdc.Watt, unitsExp: 7);

        using var source = Source();

        Tick(source).UpsOutputW.ShouldNotBeNull().ShouldBe(140.4, 1e-9);
        ups.Reads.ShouldBe([(byte)3]);
    }

    [Fact]
    public void The_rating_is_asked_for_once_and_kept_because_it_never_changes()
    {
        var ups = Attach();
        var output = ups.Holder(Pdc.Output);
        ups.Field(Pdc.PercentLoad, 27, report: 5, holder: output);
        ups.Field(Pdc.ConfigActivePower, 520, report: 6, holder: output, units: Pdc.Watt, unitsExp: 7);

        using var source = Source();
        Tick(source);
        _now = TimeSpan.FromSeconds(5);
        Tick(source);
        _now = TimeSpan.FromSeconds(10);
        Tick(source).UpsOutputW.ShouldNotBeNull().ShouldBe(140.4, 1e-9);

        ups.Reads.ShouldBe([(byte)5, (byte)6, (byte)5, (byte)5]);
    }

    [Fact]
    public void Power_meant_for_one_outlet_or_for_the_mains_is_not_the_output_s_power()
    {
        var ups = Attach();
        var output = ups.Holder(Pdc.Output);
        var outlet = ups.Holder(Pdc.Outlet, parent: output);
        var input = ups.Holder(Pdc.Input);
        ups.Field(Pdc.ActivePower, 40, report: 2, holder: outlet, units: Pdc.Watt, unitsExp: 7);
        ups.Field(Pdc.ActivePower, 300, report: 3, holder: input, units: Pdc.Watt, unitsExp: 7);
        ups.Field(Pdc.PercentLoad, 27, report: 5, holder: output);
        ups.Field(Pdc.ConfigActivePower, 520, report: 6, holder: output, units: Pdc.Watt, unitsExp: 7);

        using var source = Source();
        var draft = Tick(source);

        draft.UpsOutputW.ShouldNotBeNull().ShouldBe(140.4, 1e-9);
        draft.UpsSource.ShouldBe(UpsPowerSource.LoadOfRatedWatts);
    }

    [Fact]
    public void Only_the_output_flow_s_rating_counts()
    {
        // MGE keeps the mains input's ratings in one Flow collection and the output's in another; NUT reads the
        // fourth, "UPS.Flow.[4].ConfigApparentPower", which Windows tells apart by its collection type.
        var ups = Attach();
        var mains = ups.Holder(Pdc.Flow, type: 0x81);
        var outputFlow = ups.Holder(Pdc.Flow, type: 0x84);
        var summary = ups.Holder(Pdc.PowerSummary);
        ups.Field(Pdc.PercentLoad, 27, report: 5, holder: summary);
        ups.Field(Pdc.ConfigApparentPower, 2200, report: 6, holder: mains, units: Pdc.Watt, unitsExp: 7);
        ups.Field(Pdc.ConfigApparentPower, 850, report: 7, holder: outputFlow, units: Pdc.Watt, unitsExp: 7);

        using var source = Source();

        Tick(source).UpsOutputW.ShouldNotBeNull().ShouldBe(183.6, 1e-9);          // 27% of the output flow's 850 VA at 0.8
    }

    [Fact]
    public void A_usage_spread_over_several_fields_is_left_alone()
    {
        // Windows reads single values only, so a usage with a report count above one is a value array and is skipped.
        var ups = Attach();
        var output = ups.Holder(Pdc.Output);
        ups.Field(Pdc.ActivePower, 137, report: 4, holder: output, units: Pdc.Watt, unitsExp: 7, count: 3);
        ups.Field(Pdc.PercentLoad, 27, report: 5, holder: output);
        ups.Field(Pdc.ConfigActivePower, 520, report: 6, holder: output, units: Pdc.Watt, unitsExp: 7);

        using var source = Source();
        var draft = Tick(source);

        draft.UpsOutputW.ShouldNotBeNull().ShouldBe(140.4, 1e-9);
        draft.UpsSource.ShouldBe(UpsPowerSource.LoadOfRatedWatts);
    }

    [Fact]
    public void Trouble_listing_the_devices_never_leaves_the_tick()
    {
        _hid.ListThrows = new InvalidOperationException("SetupAPI said no");
        using var source = Source();

        var draft = Should.NotThrow(() => Tick(source));

        draft.UpsOutputW.ShouldBeNull();
        draft.UpsName.ShouldBeNull();
        source.Unavailable.ShouldNotBeNull().ShouldContain("SetupAPI said no");
    }

    [Fact]
    public void One_device_that_throws_while_it_is_opened_does_not_hide_the_ups_behind_it()
    {
        var awkward = new FakeCollection("awkward") { OpenThrows = new UnauthorizedAccessException("no") };
        _hid.Attached.Add(awkward);
        var ups = ActivePowerUps();

        using var source = Source();

        Should.NotThrow(() => Tick(source)).UpsOutputW.ShouldBe(137);
        ups.Reads.ShouldNotBeEmpty();
    }

    [Fact]
    public void A_ups_that_throws_while_it_is_read_is_let_go_of_and_the_tick_stands()
    {
        var ups = ActivePowerUps();
        ups.ReadThrows = new IOException("the device is not ready");
        using var source = Source();

        var draft = Should.NotThrow(() => Tick(source));

        draft.UpsOutputW.ShouldBeNull();
        source.Unavailable.ShouldNotBeNull().ShouldContain("the device is not ready");
        ups.Closes.ShouldBe(1);
    }

    [Fact]
    public void The_source_is_named_for_the_status_screen_and_is_never_skipped_for_good()
    {
        using var source = Source();

        source.Name.ShouldBe("ups");
        source.Supported.ShouldBeTrue();
    }

    [Fact]
    public void The_ups_is_named_from_what_the_device_calls_itself()
    {
        var apc = ActivePowerUps();
        apc.Manufacturer = "American Power Conversion";
        apc.Product = "Back-UPS ES 850G2 FW:931.a10.D USB FW:a10";

        using var source = Source();

        // APC keeps its firmware versions in the product string, as NUT's subdriver also has to cut off.
        Tick(source).UpsName.ShouldBe("American Power Conversion Back-UPS ES 850G2");
    }

    [Fact]
    public void A_ups_that_says_nothing_about_itself_is_still_named()
    {
        var ups = ActivePowerUps();
        ups.Manufacturer = null;
        ups.Product = null;

        using var source = Source();

        Tick(source).UpsName.ShouldBe("UPS");
    }

    [Fact]
    public void Disposing_the_source_closes_the_ups()
    {
        var ups = ActivePowerUps();
        var source = Source();
        Tick(source);

        source.Dispose();

        ups.Closes.ShouldBe(1);
        Should.NotThrow(source.Dispose);
    }

    private UpsSource Source() => new(_hid, () => _now);

    private SampleDraft Tick(UpsSource source)
    {
        var draft = new SampleDraft();
        source.Contribute(draft);
        return draft;
    }

    /// <summary>A UPS attached over USB, named as a CyberPower unit names itself.</summary>
    private FakeCollection Attach(string path = "ups", string? device = null)
    {
        var ups = new FakeCollection(path, device: device) { Manufacturer = "CPS", Product = "CP1500PFCLCD" };
        _hid.Attached.Add(ups);
        return ups;
    }

    /// <summary>A UPS that reports its output's own active power, 137 W.</summary>
    private FakeCollection ActivePowerUps(string path = "ups", string? device = null)
    {
        var ups = Attach(path, device);
        ups.Field(Pdc.ActivePower, 137, report: 4, holder: ups.Holder(Pdc.Output), units: Pdc.Watt, unitsExp: 7);
        return ups;
    }
}
