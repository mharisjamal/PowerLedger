using System.Text;
using System.Text.Json.Nodes;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class PipeProtocolTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

    internal static ReadingFrame Frame(double totalW = 20) => new(
        At, 1, totalW, Quality.Measured,
        new ComponentWatts(Cpu: 6, Gpu: 1, Display: 3, Ram: 0, Storage: 0, Board: 0, Extras: 0, Monitors: 2, PsuLoss: 0, Unattributed: 8),
        CpuMeasured: true, GpuMeasured: false,
        OnBattery: true, DisplayOn: true, UserIdle: false, SessionLocked: false, CpuLoad: 0.3, GpuLoad: null, Brightness: 0.5, Suspect: false);

    public static TheoryData<PipeMessage> SimpleMessages => new()
    {
        new SubscribeRequest(1),
        new GetStatusRequest(2),
        new GetSettingsRequest(3),
        new SetTariffRequest(4, 0.31m, "EUR", At),
        new SetTariffRequest(5, 12.5m, "JPY", null),
        new ResetCalibrationRequest(6),
        new ReportActivityRequest(7, 42.5),
        new OkReply(8),
        new ErrorReply(9, "no"),
        new ErrorReply(null, "unreadable"),
        Frame(),
    };

    [Theory]
    [MemberData(nameof(SimpleMessages))]
    public void Every_simple_message_survives_the_wire(PipeMessage message)
    {
        var line = PipeProtocol.Serialize(message);
        line[^1].ShouldBe((byte)'\n');
        PipeProtocol.Deserialize(Trim(line)).ShouldBe(message);
    }

    private static MonitorStatus Monitor() => new()
    {
        Key = "DELA0B1-4C4A3833", Instance = @"DISPLAY\DELA0B1\5&2F5A1B&0&UID4353", Name = "DELL U2723QE", Inches = 27, Width = 3840,
        Height = 2160, OnWatts = 28.3, SleepWatts = 0.3, Source = MonitorSource.Model, Counted = true, Brightness = 0.6, WattsNow = 30.5,
    };

    private static ServiceStatus Status() => new(
        "0.1.0", At, 42,
        [new SourceStatus("battery", true, null, 0, null), new SourceStatus("nvidia-gpu", false, "no NVIDIA driver", 0, null)],
        3, 1, new CalibrationStatus(900, 1800, 2, 12), "64596c2b0f03e58f", 123456, null, null, Frame());

    [Fact]
    public void Status_and_settings_replies_survive_the_wire()
    {
        var status = Status();
        var line = PipeProtocol.Serialize(new StatusReply(10, status));
        var back = PipeProtocol.Deserialize(Trim(line)).ShouldBeOfType<StatusReply>();
        back.Id.ShouldBe(10);
        back.Status.Sources.Count.ShouldBe(2);
        back.Status.Sources[1].Unavailable.ShouldBe("no NVIDIA driver");
        back.Status.Calibration.ShouldBe(status.Calibration);
        back.Status.Last.ShouldBe(status.Last);
        PipeProtocol.Serialize(back).ShouldBe(line);

        var settings = ServiceSettings.Default with { SampleIntervalSeconds = 2, Profile = MachineProfile.DefaultDesktop with { PsuTier = PsuTier.Gold } };
        PipeProtocol.Deserialize(Trim(PipeProtocol.Serialize(new SettingsReply(11, settings))))
            .ShouldBeOfType<SettingsReply>().Settings.ShouldBe(settings);
    }

    [Fact]
    public void A_status_carries_each_monitor_with_its_source_as_a_number()
    {
        var status = Status() with
        {
            Monitors =
            [
                Monitor(),
                Monitor() with
                {
                    Key = @"DISPLAY\GSM5B08\7&1A2B&0&UID4354", Instance = @"DISPLAY\GSM5B08\7&1A2B&0&UID4354", Name = "GSM 5B08",
                    Source = MonitorSource.Estimate, Counted = false, Brightness = null, WattsNow = 0,
                },
            ],
        };
        var line = PipeProtocol.Serialize(new StatusReply(12, status));
        Encoding.UTF8.GetString(line).ShouldContain("\"source\":1");
        var back = PipeProtocol.Deserialize(Trim(line)).ShouldBeOfType<StatusReply>().Status;
        back.Monitors.ShouldNotBeNull().ShouldBe(status.Monitors!);
        PipeProtocol.Serialize(new StatusReply(12, back)).ShouldBe(line);
    }

    [Fact]
    public void A_status_from_a_service_that_sends_no_monitors_reads_as_null()
    {
        var older = JsonNode.Parse(Trim(PipeProtocol.Serialize(new StatusReply(13, Status()))))!;
        older["status"]!.AsObject().Remove("monitors").ShouldBeTrue();
        var back = PipeProtocol.Deserialize(Encoding.UTF8.GetBytes(older.ToJsonString())).ShouldBeOfType<StatusReply>().Status;
        back.Monitors.ShouldBeNull();
        back.Version.ShouldBe("0.1.0");
    }

    [Fact]
    public void A_brightness_report_survives_the_wire_under_its_own_kind()
    {
        var report = new ReportBrightnessRequest(14,
        [
            new MonitorBrightness { Instance = @"DISPLAY\DELA0B1\5&2F5A1B&0&UID4353", Brightness = 0.6 },
            new MonitorBrightness { Instance = @"DISPLAY\GSM5B08\7&1A2B&0&UID4354", Brightness = 0 },
        ],
        [
            new MonitorPowerReading { Instance = @"DISPLAY\DELA0B1\5&2F5A1B&0&UID4353", State = MonitorPowerState.On },
            new MonitorPowerReading { Instance = @"DISPLAY\GSM5B08\7&1A2B&0&UID4354", State = MonitorPowerState.Off },
        ],
        [
            new MonitorDisplayReading { Instance = @"DISPLAY\DELA0B1\5&2F5A1B&0&UID4353", RefreshHz = 165, Hdr = true },
            new MonitorDisplayReading { Instance = @"DISPLAY\GSM5B08\7&1A2B&0&UID4354", RefreshHz = 59.94, Hdr = false },
        ]);
        Text(report).ShouldStartWith("""{"type":"reportBrightness","monitors":[{"instance":""");
        Text(report).ShouldContain("\"brightness\":0.6},{\"instance\":");
        Text(report).ShouldContain("\"power\":[{\"instance\":");
        Text(report).ShouldContain("\"state\":1},{\"instance\":");
        Text(report).ShouldContain("\"displays\":[{\"instance\":");
        Text(report).ShouldContain("\"refreshHz\":165,\"hdr\":true},{\"instance\":");
        var back = PipeProtocol.Deserialize(Trim(PipeProtocol.Serialize(report))).ShouldBeOfType<ReportBrightnessRequest>();
        back.Id.ShouldBe(14);
        back.Monitors.ShouldBe(report.Monitors);
        back.Power.ShouldNotBeNull().ShouldBe(report.Power!);
        back.Displays.ShouldNotBeNull().ShouldBe(report.Displays!);
    }

    [Fact]
    public void A_brightness_report_from_an_app_that_reads_no_power_states_or_displays_arrives_without_them()
    {
        var older = PipeProtocol.Deserialize(Encoding.UTF8.GetBytes(
                """{"type":"reportBrightness","monitors":[{"instance":"DISPLAY\\DELA0B1\\5&2F5A1B&0&UID4353","brightness":0.6}],"id":15}"""))
            .ShouldBeOfType<ReportBrightnessRequest>();
        older.Id.ShouldBe(15);
        older.Monitors.ShouldBe([Reading(brightness: 0.6)]);
        older.Power.ShouldBeNull();
        older.Displays.ShouldBeNull();
        older.Validate().ShouldBeNull();

        // An App that reads power states but not displays.
        var powerOnly = PipeProtocol.Deserialize(Encoding.UTF8.GetBytes(
                """{"type":"reportBrightness","monitors":[],"power":[{"instance":"DISPLAY\\DELA0B1\\5&2F5A1B&0&UID4353","state":3}],"id":16}"""))
            .ShouldBeOfType<ReportBrightnessRequest>();
        powerOnly.Power.ShouldNotBeNull().ShouldBe([Said(MonitorPowerState.Off)]);
        powerOnly.Displays.ShouldBeNull();
        powerOnly.Validate().ShouldBeNull();

        var sent = PipeProtocol.Deserialize(Trim(PipeProtocol.Serialize(new ReportBrightnessRequest(17, [Reading()])))).ShouldBeOfType<ReportBrightnessRequest>();
        sent.Power.ShouldBeNull();
        sent.Displays.ShouldBeNull();
    }

    [Fact]
    public void A_brightness_report_within_its_limits_is_accepted()
    {
        Report().Validate().ShouldBeNull();
        Report(Reading(brightness: 0), Reading(new string('I', 260), brightness: 1)).Validate().ShouldBeNull();
        Report([.. Enumerable.Range(0, 16).Select(i => Reading($"I{i}"))]).Validate().ShouldBeNull();
    }

    [Fact]
    public void A_brightness_report_outside_its_limits_is_refused_rather_than_crashing()
    {
        Report([.. Enumerable.Range(0, 17).Select(i => Reading($"I{i}"))]).Validate().ShouldBe("At most 16 monitors can report a brightness.");
        Report(Reading("")).Validate().ShouldNotBeNull();
        Report(Reading(new string('I', 261))).Validate().ShouldNotBeNull();
        foreach (var brightness in new[] { -0.01, 1.01, double.NaN, double.PositiveInfinity })
            Report(Reading(brightness: brightness)).Validate().ShouldNotBeNull();
        new ReportBrightnessRequest(17, null!).Validate().ShouldNotBeNull();
        Report([null!]).Validate().ShouldNotBeNull();
        Report(Reading(null!)).Validate().ShouldNotBeNull();
    }

    [Fact]
    public void A_report_s_power_states_within_their_limits_are_accepted_and_outside_them_refused()
    {
        Report([], Said(MonitorPowerState.On), Said(MonitorPowerState.Standby), Said(MonitorPowerState.Off)).Validate().ShouldBeNull();
        Report([], Said(instance: new string('I', 260))).Validate().ShouldBeNull();
        Report([], [.. Enumerable.Range(0, 16).Select(i => Said(instance: $"I{i}"))]).Validate().ShouldBeNull();

        Report([], [.. Enumerable.Range(0, 17).Select(i => Said(instance: $"I{i}"))]).Validate().ShouldBe("At most 16 monitors can report a power state.");
        foreach (var state in new[] { MonitorPowerState.Unknown, (MonitorPowerState)4, (MonitorPowerState)(-1) })
            Report([], Said(state)).Validate().ShouldBe("A monitor's power state must be on, standby or off.");
        Report([], Said(instance: "")).Validate().ShouldNotBeNull();
        Report([], Said(instance: new string('I', 261))).Validate().ShouldNotBeNull();
        Report([], Said(instance: null!)).Validate().ShouldNotBeNull();
        Report([], [null!]).Validate().ShouldNotBeNull();
    }

    [Fact]
    public void A_report_s_display_readings_within_their_limits_are_accepted_and_outside_them_refused()
    {
        Driven(Shown(1), Shown(59.94, hdr: true), Shown(1000), Shown(instance: new string('I', 260))).Validate().ShouldBeNull();
        Driven([.. Enumerable.Range(0, 16).Select(i => Shown(instance: $"I{i}"))]).Validate().ShouldBeNull();

        Driven([.. Enumerable.Range(0, 17).Select(i => Shown(instance: $"I{i}"))]).Validate()
            .ShouldBe("At most 16 monitors can report how they are driven.");
        foreach (var refreshHz in new[] { 0.99, 1000.01, 0, -60, double.NaN, double.PositiveInfinity })
            Driven(Shown(refreshHz)).Validate().ShouldBe("A refresh rate must be between 1 and 1000 Hz.");
        Driven(Shown(instance: "")).Validate().ShouldNotBeNull();
        Driven(Shown(instance: new string('I', 261))).Validate().ShouldNotBeNull();
        Driven(Shown(instance: null!)).Validate().ShouldNotBeNull();
        Driven([null!]).Validate().ShouldNotBeNull();
    }

    [Fact]
    public void Monitor_sources_keep_their_numbers_because_they_travel_as_numbers()
    {
        ((int)MonitorSource.Model).ShouldBe(0);
        ((int)MonitorSource.Estimate).ShouldBe(1);
        ((int)MonitorSource.Typed).ShouldBe(2);
    }

    [Fact]
    public void Monitor_power_states_keep_their_numbers_because_they_travel_as_numbers()
    {
        ((int)MonitorPowerState.Unknown).ShouldBe(0);
        ((int)MonitorPowerState.On).ShouldBe(1);
        ((int)MonitorPowerState.Standby).ShouldBe(2);
        ((int)MonitorPowerState.Off).ShouldBe(3);
    }

    [Fact]
    public void Monitor_choices_survive_the_wire_and_the_store()
    {
        var settings = ServiceSettings.Default with
        {
            Profile = MachineProfile.DefaultDesktop with
            {
                Monitors = [new MonitorChoice { Key = "DELA0B1-4C4A3833", Watts = 30 }, new MonitorChoice { Key = "GSM5B08-77", Counted = false }],
            },
        };
        PipeProtocol.Deserialize(Trim(PipeProtocol.Serialize(new SettingsReply(15, settings))))
            .ShouldBeOfType<SettingsReply>().Settings.ShouldBe(settings);
        PipeProtocol.DeserializeSettings(PipeProtocol.SerializeSettings(settings)).ShouldBe(settings);
    }

    [Fact]
    public void Settings_stored_before_monitors_were_detected_load_with_no_monitor_choices()
    {
        var settings = PipeProtocol.DeserializeSettings("""{"profile":{"chassis":0,"externalMonitors":2,"includeMonitors":true,"monitorWatts":30}}""")
            .ShouldNotBeNull();
        settings.Profile.Monitors.ShouldBeEmpty();
        settings.Profile.ExternalMonitors.ShouldBe(2);
        settings.Profile.CountMonitorsByDefault.ShouldBeTrue();
        settings.Validate().ShouldBeNull();
    }

    [Fact]
    public void Settings_stored_before_monitors_could_be_left_out_by_default_count_them_and_leaving_them_out_survives_the_wire_and_the_store()
    {
        var stored = PipeProtocol.DeserializeSettings("""{"profile":{"chassis":1,"monitors":[{"key":"DELA0B1-4C4A3833","counted":false}]}}""")
            .ShouldNotBeNull();
        stored.Profile.CountMonitorsByDefault.ShouldBeTrue();
        stored.Profile.Monitors.ShouldBe([new MonitorChoice { Key = "DELA0B1-4C4A3833", Counted = false }]);

        var leftOut = ServiceSettings.Default with { Profile = MachineProfile.DefaultLaptop with { CountMonitorsByDefault = false } };
        PipeProtocol.DeserializeSettings(PipeProtocol.SerializeSettings(leftOut)).ShouldNotBeNull().Profile.CountMonitorsByDefault.ShouldBeFalse();
        PipeProtocol.Deserialize(Trim(PipeProtocol.Serialize(new SettingsReply(18, leftOut))))
            .ShouldBeOfType<SettingsReply>().Settings.ShouldBe(leftOut);
    }

    [Fact]
    public void Settings_from_an_app_that_sends_no_monitor_choices_arrive_without_them_and_are_refused()
    {
        var older = JsonNode.Parse(Trim(PipeProtocol.Serialize(new SetSettingsRequest(16, ServiceSettings.Default))))!;
        older["settings"]!["profile"]!.AsObject().Remove("monitors").ShouldBeTrue();
        var request = PipeProtocol.Deserialize(Encoding.UTF8.GetBytes(older.ToJsonString())).ShouldBeOfType<SetSettingsRequest>();
        request.Settings.Profile.Monitors.ShouldBeNull();
        request.Settings.Validate().ShouldNotBeNull();
    }

    [Fact]
    public void The_type_comes_first_names_are_camel_case_and_enums_are_numbers()
    {
        Text(new GetStatusRequest(7)).ShouldBe("""{"type":"getStatus","id":7}""" + "\n");
        Text(Frame()).ShouldContain("\"quality\":2");
        Text(Frame()).ShouldNotContain("displayBandW");
    }

    [Theory]
    [InlineData("""{"type":"launchMissiles","id":1}""")]
    [InlineData("""{"id":1}""")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{"type":"getStatus","id":"seven"}""")]
    public void Anything_else_is_a_protocol_error(string line)
        => Should.Throw<PipeProtocolException>(() => PipeProtocol.Deserialize(Encoding.UTF8.GetBytes(line)));

    [Fact]
    public void A_message_over_64_KB_is_refused_before_it_is_sent()
        => Should.Throw<PipeProtocolException>(() => PipeProtocol.Serialize(new ErrorReply(1, new string('x', PipeProtocol.MaxMessageBytes))));

    [Fact]
    public void Stored_settings_missing_a_newer_value_take_its_default()
    {
        var settings = PipeProtocol.DeserializeSettings("""{"sampleIntervalSeconds":3}""").ShouldNotBeNull();
        settings.SampleIntervalSeconds.ShouldBe(3);
        settings.IdleThresholdSeconds.ShouldBe(300);
        settings.Profile.ShouldBe(MachineProfile.DefaultLaptop);
        PipeProtocol.DeserializeSettings("{ nope").ShouldBeNull();
    }

    private static ReportBrightnessRequest Report(params MonitorBrightness[] readings) => new(17, readings);

    private static ReportBrightnessRequest Report(MonitorBrightness[] readings, params MonitorPowerReading[] power) => new(17, readings, power);

    private static MonitorBrightness Reading(string instance = @"DISPLAY\DELA0B1\5&2F5A1B&0&UID4353", double brightness = 0.5)
        => new() { Instance = instance, Brightness = brightness };

    private static MonitorPowerReading Said(MonitorPowerState state = MonitorPowerState.On, string instance = @"DISPLAY\DELA0B1\5&2F5A1B&0&UID4353")
        => new() { Instance = instance, State = state };

    private static ReportBrightnessRequest Driven(params MonitorDisplayReading[] displays) => new(17, [], null, displays);

    private static MonitorDisplayReading Shown(double refreshHz = 60, bool hdr = false, string instance = @"DISPLAY\DELA0B1\5&2F5A1B&0&UID4353")
        => new() { Instance = instance, RefreshHz = refreshHz, Hdr = hdr };

    private static string Text(PipeMessage message) => Encoding.UTF8.GetString(PipeProtocol.Serialize(message));

    private static byte[] Trim(byte[] line) => line[..^1];
}
