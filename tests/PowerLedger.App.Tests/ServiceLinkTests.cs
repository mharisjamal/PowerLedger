using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class ServiceLinkTests : IAsyncLifetime
{
    private readonly string _name = $"PowerLedger.app-test.{Guid.NewGuid():N}";
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UnixEpoch);
    private readonly ConcurrentQueue<ReadingFrame> _frames = new();
    private readonly ConcurrentQueue<bool> _changes = new();
    private readonly FakeService _service;
    private readonly PipeServiceLink _link;

    public ServiceLinkTests()
    {
        _service = new FakeService(_name);
        _link = new PipeServiceLink(_name, new FixedIdle(42), _clock, new TrustAnyServer());
        _link.FrameReceived += _frames.Enqueue;
        _link.ConnectionChanged += _changes.Enqueue;
    }

    public async Task InitializeAsync()
    {
        _service.Start();
        _link.Start();
        await WaitFor.True(() => _link.IsConnected);
    }

    public async Task DisposeAsync()
    {
        await _link.DisposeAsync();
        await _service.DisposeAsync();
    }

    [Fact]
    public async Task Once_connected_the_link_has_subscribed_and_passes_frames_on()
    {
        _service.Requests.OfType<SubscribeRequest>().Count().ShouldBe(1);
        await _service.PushAsync(Frames.At(DateTimeOffset.UnixEpoch, totalW: 12));
        await WaitFor.True(() => !_frames.IsEmpty);
        _frames.Single().TotalW.ShouldBe(12);
        _changes.ToArray().ShouldBe(new[] { true });
    }

    [Fact]
    public async Task Status_and_settings_come_back_for_the_request_that_asked()
    {
        (await _link.GetStatusAsync()).ShouldNotBeNull().Version.ShouldBe("0.1.0+b688a18");
        (await _link.GetSettingsAsync()).ShouldBe(ServiceSettings.Default);
    }

    [Fact]
    public async Task Idle_time_is_reported_on_connecting_and_every_five_seconds()
    {
        await WaitFor.True(() => _service.Requests.OfType<ReportActivityRequest>().Any());
        _service.Requests.OfType<ReportActivityRequest>().Single().IdleSeconds.ShouldBe(42);
        _clock.Advance(PipeServiceLink.ActivityEvery);
        await WaitFor.True(() => _service.Requests.OfType<ReportActivityRequest>().Count() == 2);
    }

    [Fact]
    public async Task When_the_service_goes_away_the_link_says_so_and_comes_back_by_itself()
    {
        await _service.StopAsync();
        await WaitFor.True(() => !_link.IsConnected);
        (await _link.GetStatusAsync()).ShouldBeNull();

        _service.Start();
        await WaitFor.True(() =>
        {
            _clock.Advance(TimeSpan.FromSeconds(1));   // lets the backoff run out, however far it has got
            return _link.IsConnected;
        });
        _changes.ToArray().ShouldBe(new[] { true, false, true });
        _service.Requests.OfType<SubscribeRequest>().Count().ShouldBe(2);
    }

    private sealed class FixedIdle(double seconds) : IIdleSource
    {
        public double IdleSeconds() => seconds;
    }

    [Fact]
    public async Task Settings_a_tariff_and_a_reset_reach_the_service()
    {
        var settings = ServiceSettings.Default with { IdleThresholdSeconds = 600 };
        (await _link.SetSettingsAsync(settings)).ShouldBe(WriteResult.Done);
        (await _link.SetTariffAsync(0.17m, "USD", null)).ShouldBe(WriteResult.Done);
        (await _link.ResetCalibrationAsync()).ShouldBe(WriteResult.Done);

        _service.Requests.OfType<SetSettingsRequest>().Single().Settings.ShouldBe(settings);
        var tariff = _service.Requests.OfType<SetTariffRequest>().Single();
        (tariff.PricePerKwh, tariff.Currency, tariff.EffectiveFrom).ShouldBe((0.17m, "USD", (DateTimeOffset?)null));
        _service.Requests.OfType<ResetCalibrationRequest>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task Brightness_power_states_and_display_settings_reach_the_service()
    {
        MonitorBrightness[] monitors = [new() { Instance = Statuses.Dell.Instance, Brightness = 0.6 }, new() { Instance = Statuses.Aoc.Instance, Brightness = 0.35 }];
        MonitorPowerReading[] power = [new() { Instance = Statuses.Dell.Instance, State = MonitorPowerState.On }, new() { Instance = Statuses.Aoc.Instance, State = MonitorPowerState.Off }];
        MonitorDisplayReading[] displays = [new() { Instance = Statuses.Dell.Instance, RefreshHz = 143.998, Hdr = true }, new() { Instance = Statuses.Aoc.Instance, RefreshHz = 60 }];

        (await _link.ReportBrightnessAsync(monitors, power, displays)).ShouldBe(WriteResult.Done);

        var report = _service.Requests.OfType<ReportBrightnessRequest>().Single();
        report.Monitors.ShouldBe(monitors);
        report.Power.ShouldNotBeNull().ShouldBe(power);
        report.Displays.ShouldNotBeNull().ShouldBe(displays);
    }

    [Fact]
    public async Task A_refusal_comes_back_in_the_services_words()
    {
        _service.Refuse = "The idle threshold must be between 60 and 1800 seconds.";
        var result = await _link.SetSettingsAsync(ServiceSettings.Default);
        result.Succeeded.ShouldBeFalse();
        result.Problem.ShouldBe("The idle threshold must be between 60 and 1800 seconds.");
    }

    [Fact]
    public async Task A_server_that_fails_the_check_is_sent_nothing()
    {
        var name = $"PowerLedger.app-test.{Guid.NewGuid():N}";
        await using var service = new FakeService(name);
        await using var checkedLink = new PipeServiceLink(name, new FixedIdle(0), _clock, new RefuseAll());
        service.Start();
        checkedLink.Start();
        await WaitFor.True(() => checkedLink.IsConnected);

        (await checkedLink.SetTariffAsync(0.2m, "EUR", null)).Problem.ShouldBe(RefuseAll.Reason);
        (await checkedLink.ReportBrightnessAsync([new MonitorBrightness { Instance = Statuses.Dell.Instance, Brightness = 0.6 }], [], [])).Problem.ShouldBe(RefuseAll.Reason);
        service.Requests.OfType<SetTariffRequest>().ShouldBeEmpty();
        service.Requests.OfType<ReportBrightnessRequest>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Nothing_is_changed_while_the_service_is_away()
    {
        await using var alone = new PipeServiceLink($"PowerLedger.nobody.{Guid.NewGuid():N}", new FixedIdle(0), _clock, new TrustAnyServer());
        (await alone.ResetCalibrationAsync()).ShouldBe(WriteResult.NotConnected);
        (await alone.SetConsentAsync(Consent.Unanswered)).ShouldBe(SharingOutcome.NotConnected);
    }

    [Fact]
    public async Task Consent_usage_and_crash_reports_reach_the_service()
    {
        var consent = new Consent(ConsentText.Version, true, true, true, true);
        var counts = new UsageCounts("2026-09-24", 1, new Dictionary<string, int> { ["now"] = 2 }, new Dictionary<string, int>(), 0, 0, 3, "dark", "en-US");
        var crash = new CrashReport(DateTimeOffset.UnixEpoch, "app", "0.6.0", ["System.Exception"], "boom", "at X").Trimmed();

        (await _link.SetConsentAsync(consent)).ShouldBe(new SharingOutcome(true, "Saved."));
        (await _link.ReportUsageAsync(counts)).ShouldBe(WriteResult.Done);
        (await _link.ReportCrashAsync(crash)).ShouldBe(WriteResult.Done);

        _service.Requests.OfType<SetConsentRequest>().Single().Consent.ShouldBe(consent);
        var sent = _service.Requests.OfType<ReportUsageRequest>().Single().Counts;
        (sent.Day, sent.AppOpens, sent.DaysSinceFirstRun, sent.Theme, sent.Language).ShouldBe((counts.Day, counts.AppOpens, counts.DaysSinceFirstRun, counts.Theme, counts.Language));
        sent.Pages.ShouldBe(counts.Pages);
        var gotCrash = _service.Requests.OfType<ReportCrashRequest>().Single().Crash;
        (gotCrash.At, gotCrash.Component, gotCrash.Version, gotCrash.Message, gotCrash.Stack).ShouldBe((crash.At, crash.Component, crash.Version, crash.Message, crash.Stack));
        gotCrash.Types.ShouldBe(crash.Types);
    }

    [Fact]
    public async Task Preview_send_now_and_delete_reach_the_service_and_answer_in_words()
    {
        _service.PreviewPath = @"C:\ProgramData\PowerLedger\Sent\preview.json";

        (await _link.PreviewUploadAsync()).ShouldBe(new SharingOutcome(true, "Written.", _service.PreviewPath));
        (await _link.SendNowAsync()).ShouldBe(new SharingOutcome(true, "Sent."));
        (await _link.DeleteMyDataAsync()).ShouldBe(new SharingOutcome(true, "Your data has been deleted from the server."));

        _service.Requests.OfType<PreviewUploadRequest>().Count().ShouldBe(1);
        _service.Requests.OfType<SendNowRequest>().Count().ShouldBe(1);
        _service.Requests.OfType<DeleteMyDataRequest>().Count().ShouldBe(1);
    }

    /// <summary>The service serves one request at a time per pipe connection, and its own reply timeout starts when it
    /// queues a request; a second slow sharing request written while the first is still outstanding could be carried out
    /// after the App has already given up on it (finding 6). The App must wait for one to answer before writing the
    /// next.</summary>
    [Fact]
    public async Task A_second_sharing_request_is_not_written_until_the_first_has_answered()
    {
        _service.HoldReplyTo = request => request is SendNowRequest;

        var first = _link.SendNowAsync();
        await WaitFor.True(() => _service.Requests.OfType<SendNowRequest>().Any());   // the first reached the service

        var second = _link.DeleteMyDataAsync();
        await Task.Delay(300);   // give the App every chance to write the second, if it does not wait for the first
        _service.Requests.OfType<DeleteMyDataRequest>().ShouldBeEmpty();   // still waiting behind the unanswered first
        second.IsCompleted.ShouldBeFalse();

        _service.ReleaseHeldReplies();

        (await first).ShouldBe(new SharingOutcome(true, "Sent."));
        (await second).ShouldBe(new SharingOutcome(true, "Your data has been deleted from the server."));
        _service.Requests.OfType<DeleteMyDataRequest>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task A_sharing_refusal_comes_back_in_the_services_words()
    {
        _service.Refuse = "Sharing detailed data needs Hardware and power turned on.";
        (await _link.SetConsentAsync(new Consent(ConsentText.Version, false, false, false, true)))
            .ShouldBe(new SharingOutcome(false, "Sharing detailed data needs Hardware and power turned on."));
    }

    private sealed class RefuseAll : IServerCheck
    {
        public const string Reason = "Not the installed service.";

        public string? Refusal(Microsoft.Win32.SafeHandles.SafePipeHandle pipe) => Reason;
    }
}
