using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

/// <summary>
/// What each maker's power supply is asked and what its answer means. The byte sequences here are the ones the drivers
/// PowerLedger's protocol notes come from use, and the sample replies are from traffic captured off real devices.
/// </summary>
public class PsuProtocolTests
{
    [Fact]
    public void Linear11_reads_the_pmbus_example_and_the_replies_captured_from_a_supply()
    {
        // The PMBus specification's own example, 0xE367.
        PsuMath.Linear11(0x67, 0xE3).ShouldBe(54.4375);

        // An RM1000i answering 0xEE with 140 W, and one of its rails answering 0x96 with 124 W.
        PsuMath.Linear11(0x46, 0x08).ShouldBe(140);
        PsuMath.Linear11(0x3E, 0x08).ShouldBe(124);

        PsuMath.Linear11(0x00, 0x00).ShouldBe(0);
        PsuMath.Linear11(0xFF, 0x07).ShouldBe(-1);          // an exponent of 0 over a mantissa of -1
        PsuMath.Linear11(0x00, 0xFC).ShouldBe(-512);        // an exponent of -1 over a mantissa of -1024
    }

    [Fact]
    public void The_thermaltake_decode_reads_the_mantissa_unsigned_as_ttcontroller_does()
    {
        // 12 V with an exponent of -7 needs a mantissa of 1536, which a signed mantissa would make -512.
        PsuMath.Linear11Unsigned(0x00, 0xCE).ShouldBe(12);
        PsuMath.Linear11(0x00, 0xCE).ShouldBe(-4);

        // Below 1024 the two agree, because volts and amps are never negative.
        PsuMath.Linear11Unsigned(0x7C, 0xE0).ShouldBe(7.75);
        PsuMath.Linear11(0x7C, 0xE0).ShouldBe(7.75);
    }

    [Fact]
    public void Ulinear16_takes_its_exponent_from_the_vout_mode_byte()
    {
        PsuMath.ULinear16(0x00, 0xC0, voutMode: 0x14).ShouldBe(12);      // 49152 with an exponent of -12
        PsuMath.ULinear16(0x67, 0x03, voutMode: 0x1C).ShouldBe(54.4375); // the PMBus example again, as ULINEAR16

        // A supply that says it reports its output voltage some other way is not decoded at all.
        PsuMath.ULinear16(0x00, 0xC0, voutMode: 0x40).ShouldBeNull();
    }

    [Fact]
    public void A_corsair_supply_is_greeted_first_and_then_asked_for_its_total_and_its_name()
    {
        var device = FakePsus.Corsair(watts: 140);
        var draft = OneRead(device);

        draft.PsuOutputW.ShouldBe(140);
        draft.PsuName.ShouldBe("Corsair RM1000i");
        device.Written.Count.ShouldBe(3);
        device.Written[0].ShouldBe(Report(0xFE, 0x03, 0x00));    // the handshake the Linux driver sends before it reads
        device.Written[1].ShouldBe(Report(0x03, 0xEE, 0x00));    // total watts
        device.Written[2].ShouldBe(Report(0x03, 0x9A, 0x00));    // the product name
    }

    [Fact]
    public void A_corsair_supply_is_greeted_and_named_once_a_session_and_then_only_asked_its_total()
    {
        var device = FakePsus.Corsair(watts: 140);
        var now = TimeSpan.Zero;
        using var source = Source(device, () => now);

        source.Contribute(new SampleDraft());
        now += TimeSpan.FromSeconds(2);
        var draft = new SampleDraft();
        source.Contribute(draft);

        draft.PsuOutputW.ShouldBe(140);
        device.Written.Count.ShouldBe(4);
        device.Written[3].ShouldBe(Report(0x03, 0xEE, 0x00));
    }

    [Fact]
    public void A_corsair_name_that_already_says_corsair_is_not_said_twice_and_a_silent_one_keeps_its_model()
    {
        OneRead(FakePsus.Corsair(140, product: "CORSAIR HX1500i", productId: 0x1C1F)).PsuName.ShouldBe("Corsair HX1500i");
        OneRead(FakePsus.Corsair(140, product: "", productId: 0x1C1F)).PsuName.ShouldBe("Corsair HX1500i");

        // The supplies made since 2023 answer with a longer name than their id alone gives, and their word wins from
        // the very tick they say it.
        OneRead(FakePsus.Corsair(140, product: "CORSAIR HX1500i PSU", productId: 0x1C1F)).PsuName.ShouldBe("Corsair HX1500i PSU");
    }

    [Fact]
    public void A_corsair_reply_to_another_command_is_passed_over_until_the_echo_matches()
    {
        var device = FakePsus.Corsair(watts: 140);
        // The supply answers the input voltage, which PowerLedger never asks for, before every answer it does give.
        device.Noise = _ => Report(0x03, 0x88, 0xF9, 0xCC);

        var draft = OneRead(device);

        draft.PsuOutputW.ShouldBe(140);
        draft.PsuName.ShouldBe("Corsair RM1000i");
    }

    [Fact]
    public void A_corsair_supply_that_was_never_greeted_says_nothing_a_reading_can_be_made_of()
    {
        // The reply to a read before the handshake carries 0xFE where the command should be, so the echo never matches.
        var device = FakePsus.Corsair(watts: 140);
        using var wire = new PsuWire(device.Connect(), 65, CorsairSession.Allows, static () => TimeSpan.Zero, static _ => { });
        wire.Begin(TimeSpan.MaxValue);

        var reply = wire.Ask([0x03, 0xEE, 0x00], report => report[1] == 0x03 && report[2] == 0xEE);

        reply.ShouldBeNull();
    }

    [Fact]
    public void An_nzxt_supply_is_asked_each_rail_with_a_paged_read_and_its_rail_watts_are_summed()
    {
        var device = FakePsus.Nzxt(
            watts: [92, 0, 12, 0.5, 0.25],
            volts: [12, 12, 12, 5, 3.25],
            amps: [7.75, 0, 1, 0.125, 0.0625]);
        var draft = OneRead(device);

        draft.PsuOutputW.ShouldBe(104.75);
        draft.PsuName.ShouldBe("NZXT E500");
        device.Written.Count.ShouldBe(20);
        for (byte rail = 0; rail < 5; rail++)
        {
            device.Written[(rail * 4) + 0].ShouldBe(Report(0xAD, 0x00, 0x03, 0x04, 0x60, 0x06, 0x02, rail, 0x20));
            device.Written[(rail * 4) + 1].ShouldBe(Report(0xAD, 0x00, 0x04, 0x04, 0x60, 0x06, 0x02, rail, 0x8B));
            device.Written[(rail * 4) + 2].ShouldBe(Report(0xAD, 0x00, 0x04, 0x04, 0x60, 0x06, 0x02, rail, 0x8C));
            device.Written[(rail * 4) + 3].ShouldBe(Report(0xAD, 0x00, 0x04, 0x04, 0x60, 0x06, 0x02, rail, 0x96));
        }
    }

    [Fact]
    public void An_nzxt_supply_is_asked_its_output_format_once_a_session()
    {
        var device = FakePsus.Nzxt(watts: [1, 1, 1, 1, 1], volts: [12, 12, 12, 5, 3.25], amps: [1, 1, 1, 1, 1]);
        var now = TimeSpan.Zero;
        using var source = Source(device, () => now);

        source.Contribute(new SampleDraft());
        now += TimeSpan.FromSeconds(2);
        source.Contribute(new SampleDraft());

        device.Written.Count.ShouldBe(35);                          // twenty the first time, fifteen the second
        device.Written.Skip(20).ShouldAllBe(report => report[9] != 0x20);
    }

    [Fact]
    public void An_nzxt_reply_that_is_not_an_answer_is_asked_for_again_up_to_three_times()
    {
        var device = FakePsus.Nzxt(
            watts: [1, 1, 1, 1, 1], volts: [12, 12, 12, 5, 3.25], amps: [1, 1, 1, 1, 1], busyReplies: 2);
        var draft = OneRead(device);

        // The first two commands are answered with the bridge's "busy" reply and asked again; the third answers.
        draft.PsuOutputW.ShouldBe(5);
        device.Written.Take(3).ShouldAllBe(report => report[9] == 0x20);
        device.Written.Count.ShouldBe(22);
    }

    [Fact]
    public void An_nzxt_rail_that_will_not_give_its_watts_falls_back_to_volts_times_amps()
    {
        var device = FakePsus.Nzxt(
            watts: [92, 0, null, 0.5, 0.25],
            volts: [12, 12, 12, 5, 3.25],
            amps: [7.75, 0, 1, 0.125, 0.0625]);
        var draft = OneRead(device);

        // The third rail answers no watts three times over, so its 12 V and 1 A stand in for them.
        draft.PsuOutputW.ShouldBe(104.75);
        device.Written.Count(report => report[8] == 2 && report[9] == 0x96).ShouldBe(3);
    }

    [Fact]
    public void A_thermaltake_supply_is_asked_the_three_rails_volts_and_amps_and_they_are_multiplied()
    {
        var device = FakePsus.Dpsg(volts: [12, 5, 3.25], amps: [7.75, 1.5, 0.5]);
        var draft = OneRead(device);

        draft.PsuOutputW.ShouldBe(102.125);
        draft.PsuName.ShouldBe("Thermaltake Toughpower DPS G");
        device.Written.Count.ShouldBe(7);
        device.Written[0].ShouldBe(Report(0xFE, 0x31));          // the model query TTController sends before it reads
        device.Written[1].ShouldBe(Report(0x31, 0x34));
        device.Written[2].ShouldBe(Report(0x31, 0x37));
        device.Written[3].ShouldBe(Report(0x31, 0x35));
        device.Written[4].ShouldBe(Report(0x31, 0x38));
        device.Written[5].ShouldBe(Report(0x31, 0x36));
        device.Written[6].ShouldBe(Report(0x31, 0x39));
    }

    [Fact]
    public void Only_the_reports_the_rules_allow_are_ever_written()
    {
        var corsair = FakePsus.Corsair(140);
        var nzxt = FakePsus.Nzxt(watts: [1, 1, 1, 1, 1], volts: [12, 12, 12, 5, 3.25], amps: [1, 1, 1, 1, 1]);
        var thermaltake = FakePsus.Dpsg(volts: [12, 5, 3.25], amps: [1, 1, 1]);
        OneRead(corsair);
        OneRead(nzxt);
        OneRead(thermaltake);

        corsair.Written.ShouldAllBe(report => CorsairSession.Allows(report));
        nzxt.Written.ShouldAllBe(report => NzxtSession.Allows(report));
        thermaltake.Written.ShouldAllBe(report => DpsgSession.Allows(report));
    }

    [Fact]
    public void The_corsair_rules_turn_down_every_command_that_writes_to_the_supply()
    {
        CorsairSession.Allows(Report(0x03, 0x3B, 0x00)).ShouldBeFalse();       // reading the fan's duty cycle
        CorsairSession.Allows(Report(0x03, 0xF0, 0x00)).ShouldBeFalse();       // reading the fan control mode
        CorsairSession.Allows(Report(0x02, 0xF0, 0x01)).ShouldBeFalse();       // taking the fan off hardware control
        CorsairSession.Allows(Report(0x02, 0x00, 0x01)).ShouldBeFalse();       // choosing a rail, which writes a page
        CorsairSession.Allows(Report(0x02, 0xD8, 0x01)).ShouldBeFalse();       // changing the +12V protection mode
        CorsairSession.Allows(Report(0x03, 0x99, 0x00)).ShouldBeFalse();       // a read, but not one PowerLedger needs

        CorsairSession.Allows(Report(0xFE, 0x03, 0x00)).ShouldBeTrue();
        CorsairSession.Allows(Report(0x03, 0xEE, 0x00)).ShouldBeTrue();
        CorsairSession.Allows(Report(0x03, 0x9A, 0x00)).ShouldBeTrue();
    }

    [Fact]
    public void The_nzxt_rules_allow_the_paged_reads_of_five_rails_and_nothing_else()
    {
        NzxtSession.Allows(Report(0xAD, 0x00, 0x04, 0x04, 0x60, 0x06, 0x02, 0x04, 0x96)).ShouldBeTrue();
        NzxtSession.Allows(Report(0xAD, 0x00, 0x03, 0x04, 0x60, 0x06, 0x02, 0x00, 0x20)).ShouldBeTrue();

        NzxtSession.Allows(Report(0xAD, 0x00, 0x04, 0x04, 0x60, 0x06, 0x02, 0x05, 0x96)).ShouldBeFalse();  // a sixth rail
        NzxtSession.Allows(Report(0xAD, 0x00, 0x04, 0x04, 0x60, 0x06, 0x02, 0x00, 0x3B)).ShouldBeFalse();  // the fan
        NzxtSession.Allows(Report(0xAD, 0x00, 0x03, 0x01, 0x60, 0x00, 0x02, 0x00, 0x8B)).ShouldBeFalse();  // writing a page
        NzxtSession.Allows(Report(0xAD, 0x00, 0x02, 0x01, 0x60, 0xFC)).ShouldBeFalse();                    // the firmware version
    }

    [Fact]
    public void The_thermaltake_rules_allow_the_six_rail_registers_and_the_model_query_only()
    {
        DpsgSession.Allows(Report(0xFE, 0x31)).ShouldBeTrue();
        DpsgSession.Allows(Report(0x31, 0x34)).ShouldBeTrue();
        DpsgSession.Allows(Report(0x31, 0x39)).ShouldBeTrue();

        DpsgSession.Allows(Report(0x31, 0x3B)).ShouldBeFalse();                // the fan's speed
        DpsgSession.Allows(Report(0x30, 0x41, 0x02)).ShouldBeFalse();          // setting the fan mode
        DpsgSession.Allows(Report(0x30, 0x42, 0x00)).ShouldBeFalse();          // setting the lighting
        DpsgSession.Allows(Report(0x30, 0x43, 0x01)).ShouldBeFalse();          // saving a profile to the supply
    }

    [Fact]
    public void A_command_the_rules_turn_down_never_reaches_the_device()
    {
        var device = FakePsus.Corsair(140);
        using var wire = new PsuWire(device.Connect(), 65, CorsairSession.Allows, static () => TimeSpan.Zero, static _ => { });
        wire.Begin(TimeSpan.MaxValue);

        wire.Ask([0x03, 0x3B, 0x00]).ShouldBeNull();
        wire.Ask([0x02, 0x00, 0x01]).ShouldBeNull();

        device.Written.ShouldBeEmpty();
    }

    [Fact]
    public void A_supply_that_answers_something_impossible_is_read_as_nothing()
    {
        // Two and a half kilowatts out of a supply: nonsense, whoever sent it.
        var device = FakePsus.Dpsg(volts: [12, 5, 3.25], amps: [120, 120, 120]);

        OneRead(device).PsuOutputW.ShouldBeNull();
    }

    [Fact]
    public void Every_supply_in_the_table_has_its_own_ids_and_a_name_that_says_who_made_it()
    {
        PsuModels.All.Select(model => (model.VendorId, model.ProductId)).Distinct().Count().ShouldBe(PsuModels.All.Count);
        PsuModels.All.ShouldAllBe(model => model.Name.Length > 0);
        PsuModels.Known(0x1B1C, 0x1C0D).ShouldBeTrue();
        PsuModels.Known(0x1B1C, 0x1C0E).ShouldBeFalse();
        PsuModels.Find(0x7793, 0x2500).ShouldNotBeNull().Name.ShouldBe("NZXT E850");
        PsuModels.Find(0x264A, 0x2329).ShouldNotBeNull().Family.ShouldBe(PsuFamily.Thermaltake);
    }

    [Fact]
    public void A_makers_own_program_is_known_by_the_name_windows_lists_its_process_under()
    {
        PsuPrograms.Holding(PsuFamily.Corsair, ["explorer", "iCUE"]).ShouldBe("iCUE");
        PsuPrograms.Holding(PsuFamily.Corsair, ["iCUEDevicePluginHost"]).ShouldBe("iCUE");
        PsuPrograms.Holding(PsuFamily.Corsair, ["CorsairLink4.Service"]).ShouldBe("Corsair Link");
        PsuPrograms.Holding(PsuFamily.Nzxt, ["NZXT CAM"]).ShouldBe("NZXT CAM");
        PsuPrograms.Holding(PsuFamily.Nzxt, ["cam_helper"]).ShouldBe("NZXT CAM");
        PsuPrograms.Holding(PsuFamily.Nzxt, ["CAM"]).ShouldBe("NZXT CAM");
        PsuPrograms.Holding(PsuFamily.Thermaltake, ["TT RGB Plus"]).ShouldBe("TT RGB Plus");
        PsuPrograms.Holding(PsuFamily.Thermaltake, ["TTRGBPlusGUI"]).ShouldBe("TT RGB Plus");
        PsuPrograms.Holding(PsuFamily.Thermaltake, ["Thermaltake DPS POWER"]).ShouldBe("the DPS G App");
        PsuPrograms.Holding(PsuFamily.Thermaltake, ["DPSApp"]).ShouldBe("DPSApp");

        // Another maker's program, and a program whose name only starts like one, are nothing to do with this supply.
        PsuPrograms.Holding(PsuFamily.Corsair, ["NZXT CAM", "TT RGB Plus"]).ShouldBeNull();
        PsuPrograms.Holding(PsuFamily.Nzxt, ["Camtasia", "explorer"]).ShouldBeNull();
    }

    /// <summary>One report as it goes on the wire: the report ID Windows puts first, the command, and zero padding.</summary>
    internal static byte[] Report(params byte[] command)
    {
        var report = new byte[65];
        command.CopyTo(report, 1);
        return report;
    }

    private static PsuSource Source(FakeHidDevice device, Func<TimeSpan> clock)
        => new(new FakeHidPort(device), static () => true, static () => [], clock);

    private static SampleDraft OneRead(FakeHidDevice device)
    {
        using var source = Source(device, static () => TimeSpan.Zero);
        var draft = new SampleDraft();
        source.Contribute(draft);
        return draft;
    }
}
