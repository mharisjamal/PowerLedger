using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Service.Sharing;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

public sealed class PipeHandlerTests : IDisposable
{
    private static readonly DateTimeOffset Now = Samples.T0;
    private readonly TestDatabase _database = new();
    private readonly LoopCommands _commands = new();
    private readonly SharingCommands _sharing = new();
    private readonly StatusBoard _board = new();
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly ServiceSignals _signals;
    private readonly MonitorBoard _monitors;
    private readonly PipeHandler _handler;

    public PipeHandlerTests()
    {
        _signals = new ServiceSignals(_clock);
        _monitors = new MonitorBoard(MonitorBoardTests.Catalogue, _clock);
        _handler = new PipeHandler(_commands, _board, _monitors, _signals, new TariffRepository(_database.Db), _clock, _sharing);
    }

    public static TheoryData<PipeRequest, Type> SharingRequests() => new()
    {
        { new SetConsentRequest(30, new Consent(ConsentText.Version, true, false, true, true)), typeof(SetConsentCommand) },
        { new PreviewUploadRequest(31), typeof(PreviewCommand) },
        { new SendNowRequest(32), typeof(SendNowCommand) },
        { new DeleteMyDataRequest(33), typeof(DeleteMyDataCommand) },
    };

    [Theory]
    [MemberData(nameof(SharingRequests))]
    public async Task A_sharing_request_goes_to_the_worker_and_is_answered_with_what_it_did(PipeRequest request, Type command)
    {
        var reply = Send(request);

        var queued = await _sharing.Reader.ReadAsync();
        queued.ShouldBeOfType(command);
        queued.Id.ShouldBe(request.Id);
        reply.IsCompleted.ShouldBeFalse();
        queued.Answer(true, "Done.", @"C:\ProgramData\PowerLedger\Sent\preview.json");
        (await reply).ShouldBe(new SharingReply(request.Id, true, "Done.", @"C:\ProgramData\PowerLedger\Sent\preview.json"));
    }

    [Fact]
    public async Task The_consent_the_app_sent_is_the_one_the_worker_gets()
    {
        var consent = new Consent(ConsentText.Version, false, true, false, false);
        var reply = Send(new SetConsentRequest(34, consent));

        var queued = (await _sharing.Reader.ReadAsync()).ShouldBeOfType<SetConsentCommand>();
        queued.Consent.ShouldBe(consent);
        queued.Answer(false, "No.");
        (await reply).ShouldBe(new SharingReply(34, false, "No."));
    }

    [Theory]
    [InlineData(ConsentText.Version, false, "Sharing detailed data needs Hardware and power turned on.")]
    [InlineData(ConsentText.Version - 1, true, "That answer is to an older wording of the choices. Please choose again.")]
    public async Task A_consent_that_cannot_be_recorded_is_refused_without_bothering_the_worker(int version, bool power, string problem)
    {
        (await Send(new SetConsentRequest(35, new Consent(version, true, true, power, Share: true)))).ShouldBe(new ErrorReply(35, problem));
        (await Send(new SetConsentRequest(36, null!))).ShouldBe(new ErrorReply(36, "The choices are missing."));
        _sharing.Reader.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Usage_counts_and_a_crash_are_acknowledged_at_once_and_left_to_the_worker()
    {
        var counts = new UsageCounts("2026-09-24", 1, new Dictionary<string, int> { ["now"] = 1 }, new Dictionary<string, int>(), 0, 0, 2, "dark", "en-US");
        var crash = new CrashReport(Now, "app", "0.6.0", ["System.Exception"], "Boom.", "   at X()");

        (await Send(new ReportUsageRequest(37, counts))).ShouldBe(new OkReply(37));
        (await Send(new ReportCrashRequest(38, crash))).ShouldBe(new OkReply(38));

        (await _sharing.Reader.ReadAsync()).ShouldBeOfType<ReportUsageCommand>().Counts.ShouldBeSameAs(counts);
        (await _sharing.Reader.ReadAsync()).ShouldBeOfType<ReportCrashCommand>().Crash.ShouldBeSameAs(crash);
    }

    [Fact]
    public async Task Bad_counts_and_a_bad_crash_are_refused_saying_what_is_wrong()
    {
        var counts = new UsageCounts("2026-09-24", 1, new Dictionary<string, int> { ["Now Page"] = 1 }, new Dictionary<string, int>(), 0, 0, 2, "dark", "en-US");
        var crash = new CrashReport(Now, "installer", "0.6.0", ["System.Exception"], "Boom.", "   at X()");

        (await Send(new ReportUsageRequest(39, counts))).ShouldBe(new ErrorReply(39, "A counted name must be camelCase letters and digits, at most 40."));
        (await Send(new ReportCrashRequest(40, crash))).ShouldBe(new ErrorReply(40, "A crash's component must be app or service."));
        (await Send(new ReportUsageRequest(41, null!))).ShouldBe(new ErrorReply(41, "The counts are missing."));
        (await Send(new ReportCrashRequest(42, null!))).ShouldBe(new ErrorReply(42, "The crash is missing."));
        _sharing.Reader.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_worker_that_never_answers_gets_the_client_an_error_not_a_hang()
    {
        var reply = Send(new SendNowRequest(43));
        (await _sharing.Reader.ReadAsync()).ShouldBeOfType<SendNowCommand>();

        _clock.Advance(PipeHandler.LoopTimeout);

        (await reply).ShouldBe(new ErrorReply(43, PipeHandler.NoAnswer));
    }

    [Fact]
    public async Task The_status_carries_how_sharing_stands_as_the_worker_last_published_it()
    {
        var status = new ServiceStatus("0.6.0", Now, 5, [], 0, 0, new CalibrationStatus(0, 0, 0, 0), "", 0, null, null, null);
        var sharing = new SharingStatus(new Consent(ConsentText.Version, true, true, false, false), SharingFakes.InstallId, Now, 41_000, null, false, 2);

        _board.Publish(sharing);
        (await Send(new GetStatusRequest(46))).ShouldBe(new ErrorReply(46, PipeHandler.Starting));   // the loop hasn't published yet

        _board.Publish(status);
        (await Send(new GetStatusRequest(47))).ShouldBeOfType<StatusReply>().Status.Sharing.ShouldBe(sharing);

        var later = sharing with { DaysWaiting = 0, LastSentAt = Now.AddDays(1) };
        _board.Publish(later);
        (await Send(new GetStatusRequest(48))).ShouldBeOfType<StatusReply>().Status.Sharing.ShouldBe(later);
        _board.Publish(status with { Ticks = 6 });
        var reply = (await Send(new GetStatusRequest(49))).ShouldBeOfType<StatusReply>();
        (reply.Status.Ticks, reply.Status.Sharing).ShouldBe((6L, later));
    }

    [Fact]
    public async Task While_the_service_stops_sharing_requests_are_answered_no()
    {
        _sharing.Close();

        (await Send(new SendNowRequest(44))).ShouldBe(new SharingReply(44, false, "The service is stopping."));
        (await Send(new ReportCrashRequest(45, new CrashReport(Now, "app", "0.6.0", ["X"], "", ""))))
            .ShouldBe(new ErrorReply(45, "The service is stopping."));
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
    public async Task A_report_s_power_states_and_display_readings_go_to_the_monitor_board_with_its_brightness()
    {
        _monitors.Detected([MonitorBoardTests.Dell]);

        (await Send(new ReportBrightnessRequest(22,
            [new MonitorBrightness { Instance = MonitorBoardTests.Dell.Instance, Brightness = 0.4 }],
            [new MonitorPowerReading { Instance = MonitorBoardTests.Dell.Instance, State = MonitorPowerState.Off }],
            [new MonitorDisplayReading { Instance = MonitorBoardTests.Dell.Instance, RefreshHz = 144, Hdr = true }])))
            .ShouldBe(new OkReply(22));

        var monitor = _monitors.Status(displayOn: true).ShouldHaveSingleItem();
        monitor.Brightness.ShouldBe(0.4);
        monitor.PowerState.ShouldBe(MonitorPowerState.Off);
        monitor.WattsNow.ShouldBe(0.3);
        monitor.RefreshHz.ShouldBe(144);
        monitor.Hdr.ShouldBe(true);
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
        (await Send(new ReportBrightnessRequest(24,
            [new MonitorBrightness { Instance = MonitorBoardTests.Dell.Instance, Brightness = 0.5 }],
            [new MonitorPowerReading { Instance = MonitorBoardTests.Dell.Instance, State = MonitorPowerState.Off }],
            [new MonitorDisplayReading { Instance = MonitorBoardTests.Dell.Instance, RefreshHz = 0, Hdr = true }])))
            .ShouldBe(new ErrorReply(24, "A refresh rate must be between 1 and 1000 Hz."));

        var monitor = _monitors.Status(displayOn: true).ShouldHaveSingleItem();
        monitor.Brightness.ShouldBeNull();
        monitor.PowerState.ShouldBe(MonitorPowerState.Unknown);
        monitor.Hdr.ShouldBeNull();
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
