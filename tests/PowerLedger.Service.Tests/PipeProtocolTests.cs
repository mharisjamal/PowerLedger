using System.Text;
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

    [Fact]
    public void Status_and_settings_replies_survive_the_wire()
    {
        var status = new ServiceStatus(
            "0.1.0", At, 42,
            [new SourceStatus("battery", true, null, 0, null), new SourceStatus("nvidia-gpu", false, "no NVIDIA driver", 0, null)],
            3, 1, new CalibrationStatus(900, 1800, 2, 12), "64596c2b0f03e58f", 123456, null, null, Frame());
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

    private static string Text(PipeMessage message) => Encoding.UTF8.GetString(PipeProtocol.Serialize(message));

    private static byte[] Trim(byte[] line) => line[..^1];
}
