using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

public sealed class PipeHandlerTests : IDisposable
{
    private static readonly DateTimeOffset Now = Samples.T0;
    private readonly TestDatabase _database = new();
    private readonly LoopCommands _commands = new();
    private readonly StatusBoard _board = new();
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly ServiceSignals _signals;
    private readonly MonitorBoard _monitors;
    private readonly PipeHandler _handler;

    public PipeHandlerTests()
    {
        _signals = new ServiceSignals(_clock);
        _monitors = new MonitorBoard(MonitorBoardTests.Catalogue, _clock);
        _handler = new PipeHandler(_commands, _board, _monitors, _signals, new TariffRepository(_database.Db), _clock);
    }

    [Fact]
    public async Task Status_and_settings_are_refused_until_the_loop_has_published_them()
    {
        (await Send(new GetStatusRequest(1))).ShouldBe(new ErrorReply(1, PipeHandler.Starting));
        (await Send(new GetSettingsRequest(2))).ShouldBe(new ErrorReply(2, PipeHandler.Starting));

        _board.Publish(ServiceSettings.Default);
        (await Send(new GetSettingsRequest(3))).ShouldBe(new SettingsReply(3, ServiceSettings.Default));
    }

    [Fact]
    public async Task Good_settings_go_to_the_loop_and_are_answered_once_it_has_applied_them()
    {
        var settings = ServiceSettings.Default with { IdleThresholdSeconds = 900 };
        var reply = Send(new SetSettingsRequest(4, settings));
        var command = (await _commands.Reader.ReadAsync()).ShouldBeOfType<ApplySettingsCommand>();
        command.Settings.ShouldBe(settings);
        reply.IsCompleted.ShouldBeFalse();
        command.Complete();
        (await reply).ShouldBe(new OkReply(4));
    }

    [Fact]
    public async Task Bad_settings_are_refused_without_bothering_the_loop()
    {
        var reply = await Send(new SetSettingsRequest(5, ServiceSettings.Default with { SampleIntervalSeconds = 60 }));
        reply.ShouldBeOfType<ErrorReply>().Message.ShouldContain("sample interval");
        _commands.Reader.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_loop_that_never_answers_gets_the_client_an_error_not_a_hang()
    {
        var reply = Send(new ResetCalibrationRequest(6));
        (await _commands.Reader.ReadAsync()).ShouldBeOfType<ResetCalibrationCommand>();
        _clock.Advance(PipeHandler.LoopTimeout);
        (await reply).ShouldBe(new ErrorReply(6, PipeHandler.NoAnswer));
    }

    [Fact]
    public async Task A_tariff_is_stored_now_or_backdated_and_nonsense_is_refused()
    {
        (await Send(new SetTariffRequest(7, 0.31m, "EUR", null))).ShouldBe(new OkReply(7));
        (await Send(new SetTariffRequest(8, 0.25m, "EUR", Now.AddDays(-30)))).ShouldBe(new OkReply(8));
        var tariffs = new TariffRepository(_database.Db).All();
        tariffs.Select(t => (t.EffectiveFrom, t.PricePerKwh)).ShouldBe(new[] { (Now.AddDays(-30), 0.25m), (Now, 0.31m) });

        (await Send(new SetTariffRequest(9, -1m, "EUR", null))).ShouldBeOfType<ErrorReply>();
        (await Send(new SetTariffRequest(10, 0.3m, "eur", null))).ShouldBeOfType<ErrorReply>();
        (await Send(new SetTariffRequest(11, 0.3m, "EURO", null))).ShouldBeOfType<ErrorReply>();
        (await Send(new SetTariffRequest(12, 0.3m, "EUR", Now.AddDays(3)))).ShouldBeOfType<ErrorReply>();
        (await Send(new SetTariffRequest(13, 0.3m, "EUR", new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero)))).ShouldBeOfType<ErrorReply>();
        new TariffRepository(_database.Db).All().Count.ShouldBe(2);
    }

    [Fact]
    public async Task Idle_reports_reach_the_signals_and_nonsense_is_refused()
    {
        (await Send(new ReportActivityRequest(14, 42))).ShouldBe(new OkReply(14));
        _signals.UserIdleSeconds().ShouldBe(42);
        (await Send(new ReportActivityRequest(15, double.NaN))).ShouldBeOfType<ErrorReply>();
    }

    [Fact]
    public async Task A_brightness_report_goes_to_the_monitor_board_without_bothering_the_loop()
    {
        _monitors.Detected([MonitorBoardTests.Dell]);

        // As an App that reads no power states sends it: the brightness only.
        (await Send(new ReportBrightnessRequest(18, [new MonitorBrightness { Instance = MonitorBoardTests.Dell.Instance, Brightness = 0.4 }])))
            .ShouldBe(new OkReply(18));

        var monitor = _monitors.Status(displayOn: true).ShouldHaveSingleItem();
        monitor.Brightness.ShouldBe(0.4);
        monitor.PowerState.ShouldBe(MonitorPowerState.Unknown);
        _commands.Reader.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_report_s_power_states_go_to_the_monitor_board_with_its_brightness()
    {
        _monitors.Detected([MonitorBoardTests.Dell]);

        (await Send(new ReportBrightnessRequest(22,
            [new MonitorBrightness { Instance = MonitorBoardTests.Dell.Instance, Brightness = 0.4 }],
            [new MonitorPowerReading { Instance = MonitorBoardTests.Dell.Instance, State = MonitorPowerState.Off }])))
            .ShouldBe(new OkReply(22));

        var monitor = _monitors.Status(displayOn: true).ShouldHaveSingleItem();
        monitor.Brightness.ShouldBe(0.4);
        monitor.PowerState.ShouldBe(MonitorPowerState.Off);
        monitor.WattsNow.ShouldBe(0.3);
        _commands.Reader.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_bad_brightness_report_is_refused_saying_what_is_wrong_and_changes_nothing()
    {
        _monitors.Detected([MonitorBoardTests.Dell]);

        (await Send(new ReportBrightnessRequest(19, [new MonitorBrightness { Instance = MonitorBoardTests.Dell.Instance, Brightness = 1.5 }])))
            .ShouldBe(new ErrorReply(19, "A brightness must be between 0 and 1."));
        (await Send(new ReportBrightnessRequest(20, [new MonitorBrightness { Instance = "", Brightness = 0.5 }])))
            .ShouldBe(new ErrorReply(20, "A monitor's instance must be between 1 and 260 characters."));
        (await Send(new ReportBrightnessRequest(21, null!))).ShouldBe(new ErrorReply(21, "The brightness readings are missing."));
        (await Send(new ReportBrightnessRequest(23,
            [new MonitorBrightness { Instance = MonitorBoardTests.Dell.Instance, Brightness = 0.5 }],
            [
                new MonitorPowerReading { Instance = MonitorBoardTests.Dell.Instance, State = MonitorPowerState.Off },
                new MonitorPowerReading { Instance = MonitorBoardTests.Dell.Instance, State = MonitorPowerState.Unknown },
            ])))
            .ShouldBe(new ErrorReply(23, "A monitor's power state must be on, standby or off."));

        var monitor = _monitors.Status(displayOn: true).ShouldHaveSingleItem();
        monitor.Brightness.ShouldBeNull();
        monitor.PowerState.ShouldBe(MonitorPowerState.Unknown);
    }

    [Fact]
    public async Task Subscribing_is_acknowledged_and_a_client_sending_replies_is_told_off()
    {
        (await Send(new SubscribeRequest(16))).ShouldBe(new OkReply(16));
        (await Send(new OkReply(17))).ShouldBeOfType<ErrorReply>().Id.ShouldBeNull();
    }

    public void Dispose() => _database.Dispose();

    private Task<PipeMessage> Send(PipeMessage message) => _handler.HandleAsync(message, "client-1", CancellationToken.None);
}
